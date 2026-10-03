using System.Globalization;
using System.Text;
using FordDiag.Comms.Serial;

namespace FordDiag.Comms.Simulation;

/// <summary>Behaviour switches of the software ELM327.</summary>
public sealed class SimulatedElmOptions
{
    /// <summary>Text printed by ATZ/ATI, e.g. "ELM327 v1.5" or "ELM327 v2.2".</summary>
    public string Version { get; set; } = "ELM327 v1.5";
    /// <summary>AT@1 answer.</summary>
    public string Description { get; set; } = "OBDII to RS232 Interpreter";
    /// <summary>When not null the adapter behaves like an STN chip (STI answers this, ST SBR / STMA supported).</summary>
    public string? StnId { get; set; }
    /// <summary>If &gt; 0 the device only understands this UART speed (bytes at other speeds are lost).</summary>
    public int RequiredBaud { get; set; }
    /// <summary>Speeds accepted by ATBRD/ST SBR.</summary>
    public IReadOnlyList<int> SupportedBauds { get; set; } = new[] { 57600, 115200, 230400, 500000 };
    public double Voltage { get; set; } = 12.4;
    /// <summary>Real delay before answering each command (exercise host timeouts).</summary>
    public TimeSpan Latency { get; set; }
    /// <summary>Picks the physical bus for the active protocol letter (e.g. 'B' -> MS-CAN bus). Null: the single bus given to the constructor.</summary>
    public Func<char, SimulatedBus>? BusSelector { get; set; }
}

/// <summary>
/// Software ELM327 (+ basic STN extensions) speaking the real AT protocol over an <see cref="ISerialLink"/>,
/// backed by a <see cref="SimulatedBus"/> of scripted ECUs. Deterministic and instantaneous (ECU delays are virtual).
/// </summary>
public sealed class SimulatedElm : IAsyncDisposable
{
    private readonly ISerialLink _link;       // device end
    private readonly InMemorySerialLink? _hostEnd;
    private readonly Task _loop;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _cmdLock = new();

    // ---- ELM state ----
    private bool _echo = true, _linefeed = true, _spaces = true, _headers, _dlc, _caf = true, _responses = true;
    private char _protocol = 'A';
    private int _stMs = 200;
    private string _sh = "7DF";
    private string _cp = "18";
    private string? _cra;
    private string _fcSh = "", _fcSd = "300000";
    private string _iia = "";
    private byte? _kInitAddress;
    private byte? _cea;
    private Task? _monitor;
    private CancellationTokenSource? _monitorCts;
    private string? _injectedError;
    private int _dropNext;
    private volatile bool _mute;
    private volatile bool _pendingBaudOk;

    public SimulatedElm(InMemorySerialLink deviceEnd, SimulatedBus bus, SimulatedElmOptions? options = null)
    {
        _link = deviceEnd; _hostEnd = deviceEnd.Peer; _bus = bus; Options = options ?? new SimulatedElmOptions();
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Creates a connected (host link, simulator) pair. Give the host link to <c>ElmTransport.ConnectAsync</c>.</summary>
    public static (InMemorySerialLink Host, SimulatedElm Device) Create(SimulatedBus bus, SimulatedElmOptions? options = null, int baud = 38400)
    {
        var (host, dev) = InMemorySerialLink.CreatePair(baud);
        return (host, new SimulatedElm(dev, bus, options));
    }

    private readonly SimulatedBus _bus;
    public SimulatedBus Bus => Options.BusSelector?.Invoke(_protocol) ?? _bus;
    public SimulatedElmOptions Options { get; }

    /// <summary>Every command line received (upper-cased, as typed), for test assertions.</summary>
    public List<string> Commands { get; } = new();

    /// <summary>Stop answering entirely (a hung adapter).</summary>
    public bool Mute { get => _mute; set => _mute = value; }

    /// <summary>The next data command answers with this adapter error (e.g. "CAN ERROR", "BUFFER FULL") instead of data.</summary>
    public void InjectErrorOnNextData(string error) => _injectedError = error;

    /// <summary>Ignore the next <paramref name="count"/> commands completely (lost on the wire).</summary>
    public void DropNextCommands(int count) => _dropNext = count;

    public bool IsMonitoring => _monitor is { IsCompleted: false };
    public int CurrentBaud => _hostEnd?.BaudRate ?? 0;

    // ------------------------------------------------------------------------------------------------
    private async Task RunAsync()
    {
        var buf = new byte[256];
        var line = new StringBuilder();
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _link.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n <= 0) break;
                if (Options.RequiredBaud > 0 && CurrentBaud != Options.RequiredBaud) { line.Clear(); continue; } // garbage at wrong speed
                for (int i = 0; i < n; i++)
                {
                    char c = (char)buf[i];
                    if (_monitor is { IsCompleted: false })
                    {
                        await StopMonitorAsync().ConfigureAwait(false);
                        await WriteAsync("\r>").ConfigureAwait(false);
                        line.Clear();
                        continue;
                    }
                    if (c == '\r')
                    {
                        string cmd = line.ToString();
                        line.Clear();
                        await HandleLineAsync(cmd).ConfigureAwait(false);
                    }
                    else if (c != '\n') line.Append(c);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private async Task StopMonitorAsync()
    {
        var m = _monitor; var cts = _monitorCts;
        cts?.Cancel();
        if (m is not null) { try { await m.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        _monitor = null;
    }

    private ValueTask WriteAsync(string s)
    {
        if (_mute) return ValueTask.CompletedTask;
        return _link.WriteAsync(Encoding.ASCII.GetBytes(s), _stop.Token);
    }

    private string Eol => _linefeed ? "\r\n" : "\r";

    private async Task HandleLineAsync(string raw)
    {
        string typed = raw.Trim();
        lock (Commands) Commands.Add(typed.ToUpperInvariant());
        if (_mute) return;
        if (_pendingBaudOk && typed.Length == 0) { _pendingBaudOk = false; await WriteAsync("OK\r\r>").ConfigureAwait(false); return; }
        if (_dropNext > 0) { _dropNext--; return; }
        if (Options.Latency > TimeSpan.Zero) await Task.Delay(Options.Latency, _stop.Token).ConfigureAwait(false);

        var sb = new StringBuilder();
        if (_echo) sb.Append(raw).Append('\r');

        string cmd = typed.Replace(" ", "").ToUpperInvariant();
        List<string> output;
        bool prompt = true;
        if (cmd.Length == 0) output = new List<string>();
        else if (cmd.StartsWith("AT", StringComparison.Ordinal)) output = HandleAt(cmd[2..], ref prompt, sb);
        else if (cmd.StartsWith("ST", StringComparison.Ordinal) && Options.StnId is not null) output = HandleSt(cmd[2..], ref prompt);
        else if (IsHex(cmd)) output = HandleData(cmd);
        else output = new List<string> { "?" };

        foreach (var l in output) sb.Append(l).Append(Eol);
        if (prompt)
        {
            if (cmd.Length > 0) sb.Append('\r');
            sb.Append('>');
        }
        await WriteAsync(sb.ToString()).ConfigureAwait(false);
    }

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);

    private List<string> Ok() => new() { "OK" };

    // ------------------------------------------------------------------------------------------------
    // AT commands
    private List<string> HandleAt(string c, ref bool prompt, StringBuilder sb)
    {
        switch (c)
        {
            case "Z": case "WS": { Reset(c == "Z"); return new List<string> { "", Options.Version }; }
            case "D": Reset(false); return Ok();
            case "I": return new List<string> { Options.Version };
            case "@1": return new List<string> { Options.Description };
            case "@2": return new List<string> { "?" };
            case "RV": return new List<string> { Options.Voltage.ToString("0.0", CultureInfo.InvariantCulture) + "V" };
            case "E0": _echo = false; return Ok();
            case "E1": _echo = true; return Ok();
            case "L0": _linefeed = false; return Ok();
            case "L1": _linefeed = true; return Ok();
            case "S0": _spaces = false; return Ok();
            case "S1": _spaces = true; return Ok();
            case "H0": _headers = false; return Ok();
            case "H1": _headers = true; return Ok();
            case "D0": _dlc = false; return Ok();
            case "D1": _dlc = true; return Ok();
            case "CAF0": _caf = false; return Ok();
            case "CAF1": _caf = true; return Ok();
            case "CFC0": case "CFC1": case "AT0": case "AT1": case "AT2": case "NL": case "AR": case "BI": case "FE": case "KW0": case "KW1":
                return Ok();
            case "AL": return Ok();
            case "R0": _responses = false; return Ok();
            case "R1": _responses = true; return Ok();
            case "PC": _kInitAddress = null; return Ok();
            case "DPN": return new List<string> { _protocol.ToString() };
            case "DP": return new List<string> { DescribeProtocol() };
            case "MA": return StartMonitor(ref prompt, sb);
            case "CEA": _cea = null; return Ok();
        }
        if (c.StartsWith("ST", StringComparison.Ordinal) && c.Length > 2 && byte.TryParse(c[2..], NumberStyles.HexNumber, null, out var st))
        { _stMs = st == 0 ? 0 : st * 4; return Ok(); }
        if (c.StartsWith("SP", StringComparison.Ordinal) || c.StartsWith("TP", StringComparison.Ordinal))
        {
            var a = c[2..].TrimStart('A');
            if (a.Length == 1 && Uri.IsHexDigit(a[0])) { _protocol = a[0]; _kInitAddress = null; return Ok(); }
            if (c[2..] == "A") { _protocol = '6'; return Ok(); }
            return new List<string> { "?" };
        }
        if (c.StartsWith("SH", StringComparison.Ordinal)) { _sh = c[2..]; return IsHex(_sh) && _sh.Length is 3 or 6 or 8 ? Ok() : new List<string> { "?" }; }
        if (c.StartsWith("PB", StringComparison.Ordinal)) return Ok();
        if (c.StartsWith("CP", StringComparison.Ordinal)) { _cp = c[2..]; return Ok(); }
        if (c.StartsWith("CRA", StringComparison.Ordinal)) { _cra = c.Length > 3 ? c[3..] : null; return Ok(); }
        if (c.StartsWith("CF", StringComparison.Ordinal) || c.StartsWith("CM", StringComparison.Ordinal)) return Ok();
        if (c.StartsWith("FCSH", StringComparison.Ordinal)) { _fcSh = c[4..]; return Ok(); }
        if (c.StartsWith("FCSD", StringComparison.Ordinal)) { _fcSd = c[4..]; return Ok(); }
        if (c.StartsWith("FCSM", StringComparison.Ordinal)) return Ok();
        if (c.StartsWith("CEA", StringComparison.Ordinal))
        {
            if (byte.TryParse(c[3..], NumberStyles.HexNumber, null, out var ea)) { _cea = ea; return Ok(); }
            return new List<string> { "?" };
        }
        if (c.StartsWith("IIA", StringComparison.Ordinal)) { _iia = c[3..]; return Ok(); }
        if (c.StartsWith("IB", StringComparison.Ordinal) || c.StartsWith("SW", StringComparison.Ordinal) || c.StartsWith("WM", StringComparison.Ordinal)
            || c.StartsWith("BRT", StringComparison.Ordinal) || c.StartsWith("CV", StringComparison.Ordinal) || c.StartsWith("IFR", StringComparison.Ordinal))
            return Ok();
        if (c == "SI") return KInit(slow: true);
        if (c == "FI") return KInit(slow: false);
        if (c.StartsWith("BRD", StringComparison.Ordinal)) return BaudSwitchElm(c[3..], ref prompt, sb);
        return new List<string> { "?" };
    }

    private void Reset(bool full)
    {
        _echo = true; _linefeed = true; _spaces = true; _headers = false; _dlc = false; _caf = true; _responses = true;
        _protocol = 'A'; _stMs = 200; _cra = null; _kInitAddress = null; _cea = null; _sh = "7DF"; _cp = "18";
    }

    private string DescribeProtocol() => _protocol switch
    {
        '3' => "ISO 9141-2",
        '4' => "ISO 14230-4 (KWP 5BAUD)",
        '5' => "ISO 14230-4 (KWP FAST)",
        '6' => "ISO 15765-4 (CAN 11/500)",
        '7' => "ISO 15765-4 (CAN 29/500)",
        '8' => "ISO 15765-4 (CAN 11/250)",
        '9' => "ISO 15765-4 (CAN 29/250)",
        'B' => "USER1 CAN (11* bit ID, 125* Kbaud)",
        _ => "AUTOMATIC",
    };

    private bool IsCan => _protocol is '6' or '7' or '8' or '9' or 'A' or '0' or 'B';
    private int ProtocolSpeed => _protocol is '8' or '9' ? 250 : _protocol == 'B' ? 125 : 500;

    // ---- speed switching ----
    private List<string> BaudSwitchElm(string divHex, ref bool prompt, StringBuilder sb)
    {
        if (!int.TryParse(divHex, NumberStyles.HexNumber, null, out int div) || div is < 1 or > 255) return new List<string> { "?" };
        int baud = (int)Math.Round(4_000_000.0 / div);
        int match = Options.SupportedBauds.FirstOrDefault(b => Math.Abs(b - baud) <= b / 20);
        if (match == 0) return new List<string> { "?" };
        // ELM: OK at the old speed, ID string at the new one, expects CR within the timeout, then OK + prompt; else reverts.
        prompt = false;
        sb.Append("OK\r");
        string okText = sb.ToString();
        sb.Clear();
        if (Options.RequiredBaud > 0) Options.RequiredBaud = match;
        _pendingBaudOk = true;
        _ = Task.Run(() => FinishElmBaudSwitchAsync(okText, match));
        return new List<string>();
    }

    private async Task FinishElmBaudSwitchAsync(string okText, int newBaud)
    {
        await WriteAsync(okText).ConfigureAwait(false);
        int old = Options.RequiredBaud;
        // wait for the host to change its speed
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (CurrentBaud != newBaud && sw.ElapsedMilliseconds < 500) await Task.Delay(1).ConfigureAwait(false);
        if (CurrentBaud != newBaud) return;
        if (Options.RequiredBaud > 0) Options.RequiredBaud = newBaud;
        await WriteAsync(Options.Version + "\r").ConfigureAwait(false);
        _pendingBaudOk = true;
    }


    // ------------------------------------------------------------------------------------------------
    // STN extensions
    private List<string> HandleSt(string c, ref bool prompt)
    {
        if (c == "I") return new List<string> { Options.StnId! };
        if (c.StartsWith("SBR", StringComparison.Ordinal) && int.TryParse(c[3..], out int baud) && Options.SupportedBauds.Contains(baud))
        {
            prompt = false;
            _ = Task.Run(async () =>
            {
                await WriteAsync("OK\r").ConfigureAwait(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (CurrentBaud != baud && sw.ElapsedMilliseconds < 500) await Task.Delay(1).ConfigureAwait(false);
                if (Options.RequiredBaud > 0) Options.RequiredBaud = baud;
                await WriteAsync("\r>").ConfigureAwait(false);
            });
            return new List<string>();
        }
        if (c.Replace(" ", "") == "P53") { _protocol = 'B'; return Ok(); }
        if (c == "MA") { var sb = new StringBuilder(); return StartMonitor(ref prompt, sb); }
        return new List<string> { "?" };
    }

    // ------------------------------------------------------------------------------------------------
    // K-line
    private byte? KAddressFromHeader()
    {
        // header "81 aa F1" (6 hex) -> aa
        var h = _sh;
        if (h.Length >= 6 && byte.TryParse(h.AsSpan(2, 2), NumberStyles.HexNumber, null, out var a)) return a;
        return null;
    }

    private List<string> KInit(bool slow)
    {
        if (_protocol is not ('3' or '4' or '5')) return new List<string> { "?" };
        if (slow && _protocol == '5') return new List<string> { "BUS INIT: ERROR" };
        if (!slow && _protocol != '5') return new List<string> { "BUS INIT: ERROR" };
        byte? addr = _protocol == '4' && _iia.Length > 0 && byte.TryParse(_iia, NumberStyles.HexNumber, null, out var ia) ? ia : KAddressFromHeader();
        var ecu = addr is byte a ? Bus.FindKLine(a) : null;
        bool ok = ecu is not null && (slow ? ecu.SupportsSlowInit : ecu.SupportsFastInit);
        if (!ok) return new List<string> { "BUS INIT: ...ERROR" };
        _kInitAddress = ecu!.Address;
        return new List<string> { "BUS INIT: OK", "OK" };
    }

    // ------------------------------------------------------------------------------------------------
    // data commands
    private List<string> HandleData(string hex)
    {
        if (_injectedError is { } err) { _injectedError = null; return new List<string> { err }; }

        int? count = null;
        if (hex.Length % 2 == 1)
        {
            count = Convert.ToInt32(hex[^1].ToString(), 16);
            hex = hex[..^1];
        }
        if (hex.Length == 0) return new List<string> { "?" };
        var data = Convert.FromHexString(hex);
        return IsCan ? HandleCanData(data, count) : HandleKLineData(data, count);
    }

    private List<string> HandleKLineData(byte[] data, int? count)
    {
        if (_kInitAddress is not byte addr) return new List<string> { "UNABLE TO CONNECT" };
        var ecu = Bus.FindKLine(addr);
        if (ecu is null) return new List<string> { "NO DATA" };
        var replies = ecu.Handle(data);
        Bus.ClearRx();
        foreach (var r in replies) Bus.Enqueue(new BusFrame(0, false, r.Payload, r.Delay));
        var lines = new List<string>();
        int max = count ?? int.MaxValue;
        while (lines.Count < max && Bus.TryTake(TimeSpan.FromMilliseconds(_stMs), null, out var f))
            lines.Add(FormatBytes(f.Data));
        return lines.Count == 0 ? new List<string> { "NO DATA" } : lines;
    }

    private uint TxId(out bool ext29)
    {
        ext29 = _protocol is '7' or '9' || _sh.Length == 8;
        if (_sh.Length == 3) { ext29 = false; return uint.Parse(_sh, NumberStyles.HexNumber); }
        if (_sh.Length == 8) return uint.Parse(_sh, NumberStyles.HexNumber);
        uint low = uint.Parse(_sh.PadLeft(6, '0'), NumberStyles.HexNumber);
        return (uint.Parse(_cp, NumberStyles.HexNumber) << 24) | low;
    }

    private bool PassesFilter(BusFrame f)
    {
        if (_cra is null) return true;
        return uint.TryParse(_cra, NumberStyles.HexNumber, null, out var id) && id == f.Id;
    }

    private string FormatId(BusFrame f) => f.Extended ? f.Id.ToString("X8") : f.Id.ToString("X3");

    private string FormatBytes(ReadOnlySpan<byte> b) => Hex.ToString(b, _spaces);

    private string FormatFrame(BusFrame f)
    {
        var sb = new StringBuilder();
        if (_headers) sb.Append(FormatId(f)).Append(_spaces ? " " : "");
        if (_dlc) sb.Append(f.Data.Length.ToString("X")).Append(_spaces ? " " : "");
        sb.Append(FormatBytes(f.Data));
        return sb.ToString();
    }

    private List<string> HandleCanData(byte[] data, int? count)
    {
        if (!_headers && _sh.Length is not (3 or 6 or 8)) return new List<string> { "?" };
        if (Bus.SpeedKbps != ProtocolSpeed && _protocol is not ('A' or '0'))
            return new List<string> { "CAN ERROR" };
        if (!Bus.HasCanEcus) return new List<string> { "CAN ERROR" };

        uint id = TxId(out bool ext29);
        Bus.ClearRx();
        if (_caf) return CanAutomatic(id, ext29, data);

        if (data.Length > 8) return new List<string> { "?" };
        Bus.Transmit(new BusFrame(id, ext29, data));
        if (!_responses) return new List<string>();
        var lines = new List<string>();
        int max = count ?? int.MaxValue;
        var within = TimeSpan.FromMilliseconds(_stMs);
        while (lines.Count < max && Bus.TryTake(within, PassesFilter, out var f)) lines.Add(FormatFrame(f));
        return lines.Count == 0 ? new List<string> { "NO DATA" } : lines;
    }

    /// <summary>ATCAF1: the "ELM" builds PCI bytes, sends flow control and prints reassembled messages.</summary>
    private List<string> CanAutomatic(uint id, bool ext29, byte[] data)
    {
        int ext = _cea.HasValue ? 1 : 0;
        if (data.Length > 7 - ext) return new List<string> { "?" };
        var frame = new byte[ext + 1 + data.Length];
        if (ext == 1) frame[0] = _cea!.Value;
        frame[ext] = (byte)data.Length;
        data.CopyTo(frame, ext + 1);
        Bus.Transmit(new BusFrame(id, ext29, frame));

        var lines = new List<string>();
        var within = TimeSpan.FromMilliseconds(_stMs);
        var rx = new IsoTpReassembler(ext);
        bool any = false;
        while (Bus.TryTake(within, PassesFilter, out var f))
        {
            any = true;
            var type = IsoTp.TypeOf(f.Data, ext);
            if (type == IsoTpFrameType.Single) { rx.Reset(); rx.Feed(f.Data); lines.Add(FormatBytes(rx.Payload)); }
            else if (type == IsoTpFrameType.First)
            {
                rx.Reset(); rx.Feed(f.Data);
                var fcId = _fcSh.Length > 0 ? uint.Parse(_fcSh, NumberStyles.HexNumber) : id;
                var fc = IsoTp.BuildFlowControl(0, 0, 0, _cea);
                Bus.Transmit(new BusFrame(fcId, ext29, fc));
                while (!rx.IsComplete && Bus.TryTake(within, PassesFilter, out var cf))
                    if (IsoTp.TypeOf(cf.Data, ext) == IsoTpFrameType.Consecutive) rx.Feed(cf.Data);
                if (!rx.IsComplete) return new List<string> { "DATA ERROR" };
                var all = rx.Payload;
                lines.Add(all.Length.ToString("X3"));
                int pos = 0, idx = 0, first = 6 - ext;
                while (pos < all.Length)
                {
                    int n = Math.Min(idx == 0 ? first : 7 - ext, all.Length - pos);
                    lines.Add((idx & 0xF).ToString("X") + ": " + Hex.ToString(all.AsSpan(pos, n), true));
                    pos += n; idx++;
                }
            }
        }
        return any ? lines : new List<string> { "NO DATA" };
    }

    // ------------------------------------------------------------------------------------------------
    // monitor
    private List<string> StartMonitor(ref bool prompt, StringBuilder sb)
    {
        prompt = false;
        // flush echo + nothing else, monitor task prints frames until the host sends a byte
        string pre = sb.ToString();
        sb.Clear();
        var cts = new CancellationTokenSource();
        _monitorCts = cts;
        Bus.MonitorStarted();
        _monitor = Task.Run(async () =>
        {
            try
            {
                if (pre.Length > 0) await WriteAsync(pre).ConfigureAwait(false);
                while (!cts.IsCancellationRequested)
                {
                    await Bus.WaitAsync(cts.Token).ConfigureAwait(false);
                    while (Bus.TryTakeAny(PassesFilter, out var f))
                        await WriteAsync(FormatFrame(f) + Eol).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { Bus.MonitorStopped(); }
        });
        return new List<string>();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await StopMonitorAsync().ConfigureAwait(false); } catch { }
        await _link.DisposeAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); } catch { }
        _stop.Dispose();
    }
}
