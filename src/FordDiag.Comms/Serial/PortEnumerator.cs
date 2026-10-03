using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.IO.Ports;

namespace FordDiag.Comms.Serial;

/// <summary>Enumerates serial ports (Windows/Linux/macOS), Bluetooth RFCOMM ports and well-known network endpoints.</summary>
public static partial class PortEnumerator
{
    /// <summary>Default WiFi ELM327 endpoint.</summary>
    public const string DefaultWifiEndpoint = "192.168.0.10:35000";

    [GeneratedRegex(@"^(?:tcp://)?(?<host>[A-Za-z0-9._\-]+):(?<port>\d{1,5})$")]
    private static partial Regex EndpointRegex();

    /// <summary>Parses "host:port" / "tcp://host:port".</summary>
    public static bool TryParseEndpoint(string name, out string host, out int port)
    {
        host = ""; port = 0;
        var m = EndpointRegex().Match(name.Trim());
        if (!m.Success) return false;
        port = int.Parse(m.Groups["port"].Value);
        if (port is < 1 or > 65535) return false;
        host = m.Groups["host"].Value;
        return true;
    }

    /// <summary>Classifies a user supplied port name.</summary>
    public static PortKind Classify(string name)
    {
        name = name.Trim();
        if (TryParseEndpoint(name, out _, out var port)) return PortKind.Tcp;
        var l = name.ToLowerInvariant();
        if (l.Contains("rfcomm") || l.Contains("bluetooth") || l.Contains("-bt") || l.EndsWith("bt")) return PortKind.Bluetooth;
        if (l.Contains("ttyusb") || l.Contains("ttyacm") || l.Contains("usbserial") || l.Contains("usbmodem")) return PortKind.Usb;
        return PortKind.Serial;
    }

    /// <summary>
    /// Lists serial ports. Fast (no hardware access) unless <paramref name="probe"/> is set, in which case each port
    /// is opened briefly in parallel to fill <see cref="PortInfo.Status"/>.
    /// </summary>
    public static IReadOnlyList<PortInfo> GetPorts(bool includeNetworkDefaults = false)
    {
        var list = new List<PortInfo>();
        foreach (var name in GetPortNamesCrossPlatform().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var (desc, hwid, vid, pid) = Describe(name);
            if (desc.Length == 0) desc = name;
            var profile = Elm.ElmProfiles.GuessFromDescription(desc, vid, pid)?.Key;
            var kind = Classify(name);
            list.Add(new PortInfo(name, Annotate(desc), hwid, kind, PortStatus.Unknown, profile, vid, pid));
        }
        if (includeNetworkDefaults)
        {
            list.Add(new PortInfo(DefaultWifiEndpoint, "WiFi ELM327 (" + DefaultWifiEndpoint + ")", "TCP", PortKind.Tcp, PortStatus.Unknown, "elm327"));
        }
        return list;
    }

    /// <summary>Lists ports and probes their availability concurrently.</summary>
    public static async Task<IReadOnlyList<PortInfo>> GetPortsAsync(bool probe, bool includeNetworkDefaults = false,
        TimeSpan? probeTimeout = null, CancellationToken ct = default)
    {
        var ports = GetPorts(includeNetworkDefaults);
        if (!probe) return ports;
        var timeout = probeTimeout ?? TimeSpan.FromMilliseconds(500);
        var tasks = ports.Select(async p => p with { Status = await ProbeAsync(p, timeout, ct).ConfigureAwait(false) });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Checks whether a port can be opened / endpoint accepts a TCP connection.</summary>
    public static async Task<PortStatus> ProbeAsync(PortInfo port, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            if (port.Kind is PortKind.Tcp && TryParseEndpoint(port.Name, out var host, out var tcpPort))
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                await client.ConnectAsync(host, tcpPort, cts.Token).ConfigureAwait(false);
                return PortStatus.Online;
            }
            return await Task.Run(() =>
            {
                using var sp = new SerialPort(port.Name);
                sp.Open();
                return PortStatus.Online;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return PortStatus.Offline; }
    }

    private static string Annotate(string desc)
    {
        var d = desc.ToUpperInvariant();
        if (d.Contains("ELM327")) return desc + " (ELM327 Compatible)";
        if (d.Contains("VLINKER") || d.Contains("OBDII")) return desc + " (Vlinker Compatible)";
        if (d.Contains("VGATE") || d.Contains("ICAR")) return desc + " (VGate Compatible)";
        if (d.Contains("OBDLINK") || d.Contains("SCANTOOL")) return desc + " (OBDLink Compatible)";
        if (d.Contains("FTDI") || d.Contains("FT232") || d.Contains("FT231X")) return desc + " (FTDI - Possible ELM327)";
        if (d.Contains("CH340") || d.Contains("CH341")) return desc + " (CH340 - Possible ELM327)";
        if (d.Contains("CP210")) return desc + " (CP210x - Possible ELM327)";
        if (d.Contains("PL2303")) return desc + " (PL2303 - Possible ELM327)";
        return desc;
    }

    private static IEnumerable<string> GetPortNamesCrossPlatform()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try { foreach (var n in SerialPort.GetPortNames()) names.Add(n); } catch { /* platform without serial support */ }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            foreach (var pattern in new[] { "ttyUSB*", "ttyACM*", "rfcomm*" })
                TryGlob("/dev", pattern, names);
            // Drop phantom built-in UARTs (ttyS* without a backing device).
            names.RemoveWhere(n => n.StartsWith("/dev/ttyS", StringComparison.Ordinal)
                                   && !Directory.Exists("/sys/class/tty/" + Path.GetFileName(n) + "/device"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            TryGlob("/dev", "cu.*", names);
        }
        return names;
    }

    private static void TryGlob(string dir, string pattern, HashSet<string> into)
    {
        try { foreach (var f in Directory.EnumerateFileSystemEntries(dir, pattern)) into.Add(f); } catch { /* ignore */ }
    }

    private static (string Desc, string HwId, int? Vid, int? Pid) Describe(string portName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return ("", "", null, null);
        try
        {
            var tty = Path.GetFileName(portName);
            var dev = "/sys/class/tty/" + tty + "/device";
            if (!Directory.Exists(dev)) return ("", "", null, null);
            var dir = new DirectoryInfo(Path.GetFullPath(dev, "/sys/class/tty/" + tty));
            var real = Directory.ResolveLinkTarget(dev, true)?.FullName ?? dir.FullName;
            for (var d = new DirectoryInfo(real); d is not null && d.FullName.Length > 5; d = d.Parent)
            {
                var idv = Path.Combine(d.FullName, "idVendor");
                if (!File.Exists(idv)) continue;
                string Read(string f) { var p = Path.Combine(d.FullName, f); return File.Exists(p) ? File.ReadAllText(p).Trim() : ""; }
                var vid = Read("idVendor"); var pid = Read("idProduct");
                var desc = (Read("manufacturer") + " " + Read("product")).Trim();
                int? v = int.TryParse(vid, System.Globalization.NumberStyles.HexNumber, null, out var vv) ? vv : null;
                int? p = int.TryParse(pid, System.Globalization.NumberStyles.HexNumber, null, out var pp) ? pp : null;
                return (desc, $"USB VID:PID={vid}:{pid}", v, p);
            }
        }
        catch { /* best effort */ }
        return ("", "", null, null);
    }
}
