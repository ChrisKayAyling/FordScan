using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FordDiag.Comms.Serial;

namespace FordDiag.Comms.Elm;

/// <summary>Identification gathered while connecting to an adapter.</summary>
public sealed record ElmAdapterInfo(ElmProfile Profile, string Version, string? Description, string? StnId, bool IsStn, int BaudRate);

/// <summary>
/// ELM327-family driver implementing <see cref="FordDiag.Comms.IEcuTransport"/>.
/// Handles adapter init, baud negotiation, CAN (11/29 bit, extended addressing) and K-line (KWP2000 fast/slow, ISO8) setup,
/// ISO-TP segmentation, response-pending (NRC 0x78) waiting, retries and keep-alive.
/// </summary>
public sealed partial class ElmTransport : IAddressableTransport
{
    private readonly ElmConnection _conn;
    private readonly ElmOptions _opt;
    private EcuAddress? _ecu;
    private bool _canReady, _klineReady, _cea;
    private int _rxBlock;
    private byte[] _sessionRequest = Array.Empty<byte>();
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;

    private ElmTransport(ElmConnection conn, ElmOptions options, ElmAdapterInfo info)
    {
        _conn = conn; _opt = options; Info = info;
        _rxBlock = Math.Clamp(options.ReceiveBlockSize, 1, 15);
    }

    /// <summary>Raw adapter access (AT commands, monitor mode).</summary>
    public ElmConnection Connection => _conn;
    public ElmAdapterInfo Info { get; private set; }
    public ElmProfile Profile => Info.Profile;
    public ElmCounters Counters => _conn.Counters;
    public EcuAddress? CurrentEcu => _ecu;
    public ElmOptions Options => _opt;

    #region connect

    /// <summary>Opens <paramref name="portName"/> ("COM3", "/dev/ttyUSB0", "192.168.0.10:35000") and connects.</summary>
    public static async Task<ElmTransport> OpenAsync(string portName, ElmOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ElmOptions();
        var baud = options.InitialBaud ?? options.Profile.DefaultBaud;
        var link = await SerialLinkFactory.OpenAsync(portName, new SerialLinkOptions
        {
            BaudRate = baud,
            RtsCts = options.Profile.RtsCts,
            DsrDtr = options.Profile.DsrDtr,
        }, ct).ConfigureAwait(false);
        try { return await ConnectAsync(link, options, ct).ConfigureAwait(false); }
        catch { await link.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Identifies the adapter on an already open link (negotiating the baud rate) and prepares it for diagnostics.</summary>
    public static async Task<ElmTransport> ConnectAsync(ISerialLink link, ElmOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ElmOptions();
        var conn = new ElmConnection(link, options.CommandTimeout ?? options.Profile.Timeout, options.Trace);
        try
        {
            using var lease = await conn.AcquireAsync(ct).ConfigureAwait(false);
            var info = await HandshakeAsync(conn, options, ct).ConfigureAwait(false);
            var t = new ElmTransport(conn, options, info);
            await t.PostConnectAsync(ct).ConfigureAwait(false);
            return t;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<ElmAdapterInfo> HandshakeAsync(ElmConnection conn, ElmOptions o, CancellationToken ct)
    {
        var link = conn.Link;
        bool uart = link.BaudRate > 0;
        var bauds = new List<int>();
        if (!uart) bauds.Add(0);
        else
        {
            int first = o.InitialBaud ?? o.Profile.DefaultBaud;
            bauds.Add(first);
            if (o.AutoBaud) foreach (var b in o.Profile.ProbeBauds.Concat(ElmProfiles.Elm327.ProbeBauds)) if (!bauds.Contains(b)) bauds.Add(b);
        }

        string lastError = "no answer";
        for (int i = 0; i < bauds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (uart) await conn.SetHostBaudAsync(bauds[i], ct).ConfigureAwait(false);
            var probe = i == 0 ? o.FirstProbeTimeout : o.FallbackProbeTimeout;
            conn.Trace?.Invoke($"# probing {link.Name} @ {bauds[i]}");

            // 1. wake up / purge
            var r0 = await conn.SendLockedAsync("", probe, ct).ConfigureAwait(false);
            if (r0.TimedOut) { lastError = $"no prompt at {bauds[i]} baud"; continue; }

            // 2. reset + identify
            var z = await conn.SendLockedAsync("ATZ", TimeSpan.FromSeconds(Math.Max(3, probe.TotalSeconds)), ct).ConfigureAwait(false);
            var ati = await conn.SendLockedAsync("ATI", probe, ct).ConfigureAwait(false);
            string identity = z.Text + "\n" + ati.Text;
            string? desc = null;
            if (!LooksLikeElm(identity))
            {
                var at1 = await conn.SendLockedAsync("AT@1", probe, ct).ConfigureAwait(false);
                desc = at1.Text;
                identity += "\n" + desc;
            }
            if (!LooksLikeElm(identity)) { lastError = $"unrecognised answer at {bauds[i]} baud"; continue; }

            string version = ati.Lines.Count > 0 && !ati.TimedOut ? ati.Lines[0] : z.Lines.LastOrDefault() ?? "ELM327";

            // 3. standard settings
            await conn.SendLockedAsync("ATE0", null, ct).ConfigureAwait(false);
            await conn.SendLockedAsync("ATL0", null, ct).ConfigureAwait(false);
            await conn.SendLockedAsync("ATS0", null, ct).ConfigureAwait(false);
            await conn.SendLockedAsync("ATH0", null, ct).ConfigureAwait(false);

            // 4. STN detection
            string? stnId = null; bool stn = false;
            bool mayBeStn = o.Profile.IsStn || identity.Contains("STN", StringComparison.OrdinalIgnoreCase) ||
                            identity.Contains("OBDLINK", StringComparison.OrdinalIgnoreCase) ||
                            o.Profile.Key is "vgate" or "vlinker" or "obdlink" or "obdlink_ex";
            if (mayBeStn)
            {
                var sti = await conn.SendLockedAsync("STI", null, ct).ConfigureAwait(false);
                if (!sti.TimedOut && sti.Error is null && sti.Lines.Count > 0)
                {
                    stnId = sti.Lines[0];
                    stn = StnRegex().IsMatch(stnId);
                }
            }

            // 5. profile
            var profile = o.Profile;
            if (o.AutoDetectProfile && profile.Key is "elm327" or "elm327_usb" or "unknown" or "elm327_clone")
            {
                var detected = ElmProfiles.DetectFromVersion(identity + "\n" + stnId);
                if (detected.Key != "unknown" && detected.Key != "elm327") profile = detected;
                else if (stn) profile = ElmProfiles.ObdLinkSx;
            }
            if (stn && !profile.IsStn) profile = profile with { IsStn = true, SpeedSwitch = SpeedSwitchMode.StSbr };
            o.Profile = profile;
            return new ElmAdapterInfo(profile, version, desc, stnId, stn, uart ? link.BaudRate : 0);
        }
        throw new EcuTimeoutException($"No ELM327-compatible adapter found on {link.Name}: {lastError}.");
    }

    private static bool LooksLikeElm(string identity)
    {
        var u = identity.ToUpperInvariant();
        return u.Contains("ELM") || u.Contains("OBDII") || u.Contains("STN") || u.Contains("OBDLINK") || u.Contains("VGATE")
               || u.Contains("VLINKER");
    }

    [GeneratedRegex(@"\bSTN\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex StnRegex();

    private async Task PostConnectAsync(CancellationToken ct)
    {
        _conn.DefaultTimeout = _opt.CommandTimeout ?? Profile.Timeout;
        if (_opt.TargetBaud is int target && target > 0 && _conn.Link.BaudRate > 0 && target != _conn.Link.BaudRate)
            await SwitchBaudCoreAsync(target, ct).ConfigureAwait(false);
        foreach (var cmd in Profile.PostInitCommands)
            await _conn.SendLockedAsync(cmd, null, ct).ConfigureAwait(false);
    }

    #endregion

    #region baud switch

    /// <summary>Raises/lowers the UART speed using ATBRD (ELM327) or ST SBR (STN adapters).</summary>
    public async ValueTask SwitchBaudAsync(int baud, CancellationToken ct = default)
    {
        using var _ = await _conn.AcquireAsync(ct).ConfigureAwait(false);
        await SwitchBaudCoreAsync(baud, ct).ConfigureAwait(false);
    }

    private async ValueTask SwitchBaudCoreAsync(int baud, CancellationToken ct)
    {
        int old = _conn.Link.BaudRate;
        if (old == 0 || baud == old) return;
        bool stn = Info.IsStn || Profile.SpeedSwitch == SpeedSwitchMode.StSbr;
        string cmd;
        if (stn) cmd = "ST SBR " + baud.ToString(CultureInfo.InvariantCulture);
        else
        {
            int div = (int)Math.Round(4_000_000.0 / baud);
            if (div is < 1 or > 255) throw new ProtocolException($"Baud rate {baud} not supported by ATBRD.");
            cmd = "ATBRD " + div.ToString("X2");
        }

        var (ok, text) = await _conn.WriteAndWaitTokenAsync(cmd + "\r", "OK", TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        if (!ok)
        {
            _conn.MarkDirty();
            await RecoverAsync(ct).ConfigureAwait(false);
            throw new ProtocolException($"{Profile.DisplayName} refused speed switch to {baud}: {text.Trim()}");
        }

        await _conn.SetHostBaudAsync(baud, ct).ConfigureAwait(false);
        if (stn)
        {
            // STN prints the prompt at the new speed.
            var r = await _conn.ReadPromptAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            var chk = await _conn.SendLockedAsync("ATI", TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (r.TimedOut && chk.TimedOut) { await RevertAsync(old, ct).ConfigureAwait(false); throw new ProtocolException("No answer after speed switch."); }
        }
        else
        {
            // ELM327: the adapter sends its ID at the new speed and wants a CR within ATBRT; it answers "OK" and a prompt.
            var (ok2, _) = await _conn.WriteAndWaitTokenAsync("\r", "OK", TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (!ok2) { await RevertAsync(old, ct).ConfigureAwait(false); throw new ProtocolException("Adapter did not confirm the speed switch."); }
            await _conn.ReadPromptAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        Info = Info with { BaudRate = baud };
    }

    private async ValueTask RevertAsync(int old, CancellationToken ct)
    {
        await _conn.SetHostBaudAsync(old, ct).ConfigureAwait(false);
        await RecoverAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask RecoverAsync(CancellationToken ct)
    {
        _conn.MarkDirty();
        var r = await _conn.SendLockedAsync("ATI", TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        if (r.TimedOut) throw new LinkClosedException("Lost the adapter during speed switch.");
    }

    #endregion

    #region AT helpers

    /// <summary>Sends an AT/ST command (exclusive) and returns the parsed reply. For terminal-style use.</summary>
    public ValueTask<ElmReply> SendAtAsync(string command, CancellationToken ct = default) => _conn.SendAsync(command, null, ct);

    /// <summary>Battery voltage via ATRV, or null if unavailable.</summary>
    public async ValueTask<double?> ReadVoltageAsync(CancellationToken ct = default)
    {
        var r = await _conn.SendAsync("ATRV", null, ct).ConfigureAwait(false);
        var text = r.Lines.FirstOrDefault()?.TrimEnd('V', 'v');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private async ValueTask AtOkAsync(string cmd, CancellationToken ct)
    {
        var r = await _conn.SendLockedAsync(cmd, null, ct).ConfigureAwait(false);
        if (r.TimedOut) throw new EcuTimeoutException($"Adapter did not answer '{cmd}'.");
        if (r.Error is not null) throw new ElmErrorException(r.Error, cmd);
    }

    private ValueTask<ElmReply> AtAsync(string cmd, CancellationToken ct) => _conn.SendLockedAsync(cmd, null, ct);

    private TimeSpan RequestTimeout => _opt.RequestTimeout ?? TimeSpan.FromSeconds(Math.Max(Profile.TimeoutSeconds, 1.5));

    #endregion

    #region addressing

    /// <inheritdoc/>
    public async ValueTask ConnectEcuAsync(EcuAddress ecu, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ecu);
        using var _ = await _conn.AcquireAsync(ct).ConfigureAwait(false);
        if (_ecu is not null && _ecu.Equals(ecu)) return;
        _ecu = null;
        _sessionRequest = Array.Empty<byte>();
        switch (ecu.Protocol)
        {
            case EcuProtocol.Can:
                await ConfigureCanAsync(ecu, ct).ConfigureAwait(false);
                break;
            case EcuProtocol.Kwp2000Fast:
            case EcuProtocol.Kwp2000Slow:
            case EcuProtocol.Iso8:
                await ConfigureKLineAsync(ecu, ct).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"{ecu.Protocol} is not reachable through an ELM adapter.");
        }
        _ecu = ecu;
        if (!ecu.StartSession.IsEmpty)
        {
            var sess = ecu.StartSession.ToArray();
            var rsp = await RequestLockedAsync(sess, ct).ConfigureAwait(false);
            if (rsp.Length > 0 && rsp[0] == 0x7F) throw new NegativeResponseException(rsp.Length > 1 ? rsp[1] : sess[0], rsp.Length > 2 ? rsp[2] : (byte)0);
            _sessionRequest = sess;
        }
    }

    /// <inheritdoc/>
    public async ValueTask CloseProtocolAsync(CancellationToken ct = default)
    {
        using var _ = await _conn.AcquireAsync(ct).ConfigureAwait(false);
        await AtAsync("ATPC", ct).ConfigureAwait(false);
        _ecu = null; _canReady = false; _klineReady = false; _sessionRequest = Array.Empty<byte>();
    }

    /// <summary>Common CAN settings. Called automatically by <see cref="ConnectEcuAsync"/>.</summary>
    private async ValueTask InitCanAsync(CancellationToken ct)
    {
        await AtAsync("ATE0", ct).ConfigureAwait(false);
        await AtAsync("ATS0", ct).ConfigureAwait(false);
        await AtAsync("ATH0", ct).ConfigureAwait(false);
        await AtAsync("ATL0", ct).ConfigureAwait(false);
        await AtAsync("ATAL", ct).ConfigureAwait(false);
        await AtAsync(_opt.IsoTp == IsoTpMode.Manual ? "ATCAF0" : "ATCAF1", ct).ConfigureAwait(false);
        await AtAsync("ATCFC1", ct).ConfigureAwait(false);
        _canReady = true;
        _klineReady = false;
    }

    private async ValueTask ConfigureCanAsync(EcuAddress e, CancellationToken ct)
    {
        if (e.TxId == 0 && e.RxId == 0) throw new ArgumentException("CAN address without TX/RX identifiers.", nameof(e));
        if (!_canReady) await InitCanAsync(ct).ConfigureAwait(false);

        if (e.Is29Bit)
        {
            await AtOkAsync("ATCP " + (e.TxId >> 24).ToString("X2"), ct).ConfigureAwait(false);
            await AtOkAsync("ATSH " + (e.TxId & 0xFFFFFF).ToString("X6"), ct).ConfigureAwait(false);
        }
        else await AtOkAsync("ATSH " + e.TxId.ToString("X3"), ct).ConfigureAwait(false);

        await AtAsync("ATFC SH " + e.TxHex, ct).ConfigureAwait(false);
        await AtAsync("ATFC SD 30 00 00", ct).ConfigureAwait(false); // status, block size, STmin
        await AtAsync("ATFC SM 1", ct).ConfigureAwait(false);
        await AtAsync("ATST FF", ct).ConfigureAwait(false);
        await AtAsync("ATAT0", ct).ConfigureAwait(false);

        switch (e.Speed)
        {
            case CanSpeed.Kbps125:
                // Ford MS-CAN. STN: preset 53 = ISO 15765 11-bit 125k on the MS-CAN pins (OBDLink FRPM). ELM: user protocol B (11-bit, 125k).
                if (e.Is29Bit) throw new NotSupportedException("29-bit MS-CAN is not supported.");
                if (Info.IsStn) await AtOkAsync("STP 53", ct).ConfigureAwait(false);
                else
                {
                    await AtOkAsync("ATPB E004", ct).ConfigureAwait(false); // options E0 (11-bit, var. DLC), divisor 04 = 500/4 kbps
                    await AtOkAsync("ATSP B", ct).ConfigureAwait(false);
                }
                break;
            case CanSpeed.Kbps250: await AtOkAsync("ATSP " + (e.Is29Bit ? "9" : "8"), ct).ConfigureAwait(false); break;
            case CanSpeed.Auto:
                await AtOkAsync("ATSP " + (e.Is29Bit ? "9" : "8"), ct).ConfigureAwait(false);
                var probe = await AtAsync("0210C0", ct).ConfigureAwait(false); // any frame: CAN ERROR means wrong speed
                if (probe.Contains("CAN ERROR")) await AtOkAsync("ATSP " + (e.Is29Bit ? "7" : "6"), ct).ConfigureAwait(false);
                break;
            default: await AtOkAsync("ATSP " + (e.Is29Bit ? "7" : "6"), ct).ConfigureAwait(false); break;
        }

        // Some clones reset formatting on ATSP: re-apply.
        await AtAsync(_opt.IsoTp == IsoTpMode.Manual ? "ATCAF0" : "ATCAF1", ct).ConfigureAwait(false);
        await AtAsync("ATS0", ct).ConfigureAwait(false);
        await AtAsync("ATAL", ct).ConfigureAwait(false);
        if (_opt.CanTimeoutMs > 0)
            await AtAsync("ATST " + Math.Min(255, _opt.CanTimeoutMs / 4).ToString("X2"), ct).ConfigureAwait(false);
        await AtAsync("ATAT1", ct).ConfigureAwait(false);

        if (e.ExtAddress is byte ext && _opt.IsoTp == IsoTpMode.ElmAutomatic)
        {
            await AtOkAsync("ATCEA " + ext.ToString("X2"), ct).ConfigureAwait(false); _cea = true;
        }
        else if (_cea) { await AtAsync("ATCEA", ct).ConfigureAwait(false); _cea = false; }

        await AtAsync("ATCRA " + e.RxHex, ct).ConfigureAwait(false);
    }

    private async ValueTask ConfigureKLineAsync(EcuAddress e, CancellationToken ct)
    {
        if (e.FuncAddress is not byte addr) throw new ArgumentException("K-line address without FuncAddress.", nameof(e));
        string a = addr.ToString("X2");
        if (!_klineReady)
        {
            await AtAsync("ATE0", ct).ConfigureAwait(false);
            await AtAsync("ATS0", ct).ConfigureAwait(false);
            await AtAsync("ATL0", ct).ConfigureAwait(false);
            await AtAsync("ATH0", ct).ConfigureAwait(false);
            _klineReady = true; _canReady = false;
        }
        await AtAsync($"ATSH 81 {a} F1", ct).ConfigureAwait(false);
        await AtAsync("ATSW 96", ct).ConfigureAwait(false);          // wake-up period 3 s
        await AtAsync($"ATWM 81 {a} F1 3E", ct).ConfigureAwait(false); // wake-up (tester present) message
        await AtAsync("ATIB 10", ct).ConfigureAwait(false);          // 10400 baud
        await AtAsync("ATST FF", ct).ConfigureAwait(false);
        await AtAsync("ATAT0", ct).ConfigureAwait(false);

        ElmReply init;
        if (e.Protocol == EcuProtocol.Iso8)
        {
            await AtAsync("ATSP 3", ct).ConfigureAwait(false);
            init = await SlowInitAsync(a, withAddress: false, ct).ConfigureAwait(false);
        }
        else if (e.Protocol == EcuProtocol.Kwp2000Slow)
        {
            await AtAsync("ATSP 4", ct).ConfigureAwait(false);
            init = await SlowInitAsync(a, withAddress: true, ct).ConfigureAwait(false);
            if (!InitOk(init) && _opt.FallbackToFastInit)
            {
                await AtAsync("ATSP 5", ct).ConfigureAwait(false);
                init = await FastInitAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            await AtAsync("ATSP 5", ct).ConfigureAwait(false);
            init = await FastInitAsync(ct).ConfigureAwait(false);
        }
        await AtAsync("ATAT1", ct).ConfigureAwait(false);
        if (!InitOk(init)) throw new ElmErrorException(init.Error ?? (init.TimedOut ? "BUS INIT: TIMEOUT" : init.Text), "K-line init");

        if (e.Protocol != EcuProtocol.Iso8 && _opt.KwpStartCommunication)
            await AtAsync("81", ct).ConfigureAwait(false); // startCommunication
    }

    private async ValueTask<ElmReply> SlowInitAsync(string addr, bool withAddress, CancellationToken ct)
    {
        if (withAddress) await AtAsync("ATIIA " + addr, ct).ConfigureAwait(false);
        return await _conn.SendLockedAsync("ATSI", TimeSpan.FromSeconds(Math.Max(10, Profile.TimeoutSeconds * 2)), ct).ConfigureAwait(false);
    }

    private ValueTask<ElmReply> FastInitAsync(CancellationToken ct) =>
        _conn.SendLockedAsync("ATFI", TimeSpan.FromSeconds(Math.Max(5, Profile.TimeoutSeconds)), ct);

    private static bool InitOk(ElmReply r) => !r.TimedOut && r.Error is null && r.Contains("OK");

    #endregion

    #region request

    /// <inheritdoc/>
    public async ValueTask<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default)
    {
        if (request.IsEmpty) throw new ArgumentException("Empty request.", nameof(request));
        using var _ = await _conn.AcquireAsync(ct).ConfigureAwait(false);
        return await RequestLockedAsync(request.ToArray(), ct).ConfigureAwait(false);
    }

    private async ValueTask<byte[]> RequestLockedAsync(byte[] req, CancellationToken ct)
    {
        var ecu = _ecu ?? throw new InvalidOperationException("No ECU selected: call ConnectEcuAsync first.");

        // Keep-alive: after silence re-send the session request before the next command.
        if (_sessionRequest.Length > 0 && ecu.Protocol == EcuProtocol.Can && !ReferenceEquals(req, _sessionRequest)
            && _conn.IdleTime > _opt.KeepAliveInterval)
        {
            _opt.Trace?.Invoke("# keep-alive: re-sending session request");
            try { await RequestCoreAsync(ecu, _sessionRequest, ct).ConfigureAwait(false); }
            catch (CommsException) { /* the real request will report the problem */ }
        }

        int attempt = 0;
        while (true)
        {
            try
            {
                return await RequestCoreAsync(ecu, req, ct).ConfigureAwait(false);
            }
            catch (ElmErrorException e) when (attempt < _opt.Retries && IsTransient(e.ElmMessage))
            {
                attempt++;
                if (e.ElmMessage == "BUFFER FULL") _rxBlock = Math.Max(1, _rxBlock / 2);
                _opt.Trace?.Invoke($"# retry {attempt} after {e.ElmMessage}");
            }
            catch (ProtocolException) when (attempt < _opt.Retries)
            {
                attempt++; _conn.Counters.FrameErrors++;
                _opt.Trace?.Invoke($"# retry {attempt} after framing error");
            }
            catch (EcuTimeoutException) when (_opt.RetryOnNoData && attempt < _opt.Retries)
            {
                attempt++;
            }
        }
    }

    private static bool IsTransient(string m) =>
        m is "BUFFER FULL" or "CAN ERROR" or "RX ERROR" or "DATA ERROR" or "BUS BUSY" or "FB ERROR" or "?" or "BUS ERROR";

    private ValueTask<byte[]> RequestCoreAsync(EcuAddress ecu, byte[] req, CancellationToken ct) => ecu.Protocol switch
    {
        EcuProtocol.Can when _opt.IsoTp == IsoTpMode.Manual => CanManualAsync(ecu, req, ct),
        EcuProtocol.Can => CanAutomaticAsync(ecu, req, ct),
        _ => KLineAsync(req, ct),
    };

    private static void ThrowIfError(ElmReply r, string context)
    {
        if (r.TimedOut) throw new EcuTimeoutException($"Adapter did not answer ({context}).");
        if (r.Error == "NO DATA") throw new EcuTimeoutException($"No response from ECU ({context}).");
        if (r.Error is not null) throw new ElmErrorException(r.Error, context);
    }

    // ---- CAN, adapter in CAF0: we do ISO-TP ----

    private async ValueTask<ElmReply> SendFrameAsync(byte[] frame, bool expectResponse, CancellationToken ct)
    {
        string hex = Hex.ToString(frame);
        if (expectResponse && hex.Length < 16) hex += "1";    // "wait for one response" suffix (and avoids a clone bug on 8-byte frames)
        var r = await _conn.SendLockedAsync(hex, RequestTimeout, ct).ConfigureAwait(false);
        if (expectResponse) ThrowIfError(r, "CAN frame");
        return r;
    }

    private static List<byte[]> ParseFrames(ElmReply r)
    {
        var list = new List<byte[]>(r.Lines.Count);
        foreach (var line in r.Lines)
            if (Hex.IsHexLine(line) && Hex.TryParse(line, out var b) && b.Length > 0) list.Add(b);
        return list;
    }

    private async ValueTask<byte[]> CanManualAsync(EcuAddress ecu, byte[] req, CancellationToken ct)
    {
        byte? ext = ecu.ExtAddress;
        int extOff = ext.HasValue ? 1 : 0;
        var frames = IsoTp.Segment(req, ext, _opt.PadByte);
        int sid = req[0];
        bool responsesOff = false;

        try
        {
            var reply = await SendFrameAsync(frames[0], true, ct).ConfigureAwait(false);
            int next = 1;
            while (next < frames.Count)
            {
                var fcFrame = ParseFrames(reply).FirstOrDefault(f => f.Length > extOff && IsoTp.TypeOf(f, extOff) == IsoTpFrameType.FlowControl);
                if (fcFrame is null) throw new EcuTimeoutException("No ISO-TP flow control from ECU.");
                var fc = IsoTp.ParseFlowControl(fcFrame, extOff);
                if (fc.Overflow) throw new ProtocolException("ECU reported ISO-TP buffer overflow.");
                if (fc.Wait)
                {
                    var more = await WaitForFramesAsync(ecu, f => IsoTp.TypeOf(f, extOff) == IsoTpFrameType.FlowControl, ct).ConfigureAwait(false);
                    fc = IsoTp.ParseFlowControl(more[^1], extOff);
                    if (!fc.ClearToSend) throw new ProtocolException("ECU flow control did not clear to send.");
                }

                int block = fc.BlockSize == 0 ? frames.Count - next : Math.Min(fc.BlockSize, frames.Count - next);
                bool quiet = block > 1;
                if (quiet && !responsesOff) { await AtAsync("ATR0", ct).ConfigureAwait(false); responsesOff = true; }
                for (int i = 0; i < block; i++)
                {
                    if (fc.SeparationTime > TimeSpan.Zero) await Task.Delay(fc.SeparationTime, ct).ConfigureAwait(false);
                    bool last = i == block - 1;
                    if (last && responsesOff) { await AtAsync("ATR1", ct).ConfigureAwait(false); responsesOff = false; }
                    var rep = await SendFrameAsync(frames[next], last, ct).ConfigureAwait(false);
                    next++;
                    if (last) reply = rep;
                }
            }
            return await CollectCanResponseAsync(ecu, reply, sid, ct).ConfigureAwait(false);
        }
        finally
        {
            if (responsesOff)
            {
                try { await AtAsync("ATR1", CancellationToken.None).ConfigureAwait(false); } catch { _conn.MarkDirty(); }
            }
        }
    }

    private static bool IsOurs(byte[] payload, int sid) =>
        payload.Length > 0 && (payload[0] == sid + 0x40 || (payload[0] == 0x7F && payload.Length > 1 && payload[1] == sid));

    private async ValueTask<byte[]> CollectCanResponseAsync(EcuAddress ecu, ElmReply first, int sid, CancellationToken ct)
    {
        int extOff = ecu.ExtAddress.HasValue ? 1 : 0;
        var rx = new IsoTpReassembler(extOff);
        var frames = ParseFrames(first);
        byte[]? fallback = null;
        int pendingSeen = 0;
        bool triedUnmatched = false;

        while (true)
        {
            bool pending = false;
            foreach (var f in frames)
            {
                IsoTpFrameType type;
                try { type = IsoTp.TypeOf(f, extOff); } catch (ProtocolException) { continue; }
                if (type == IsoTpFrameType.FlowControl) continue;

                if (rx.InProgress)
                {
                    if (type == IsoTpFrameType.Consecutive)
                    {
                        try { rx.Feed(f); } catch (ProtocolException) { _conn.Counters.FrameErrors++; throw; }
                        if (rx.IsComplete) return rx.Payload;
                    }
                    continue; // anything else while reassembling: foreign traffic
                }

                if (type == IsoTpFrameType.Single)
                {
                    var one = new IsoTpReassembler(extOff);
                    one.Feed(f);
                    var payload = one.Payload;
                    if (IsoTp.IsPending(payload, sid)) { pending = true; continue; }
                    if (IsOurs(payload, sid)) return payload;
                    fallback ??= payload;      // broadcast frame; used only if nothing better arrives
                }
                else if (type == IsoTpFrameType.First)
                {
                    var probe = new IsoTpReassembler(extOff);
                    probe.Feed(f);
                    var head = f.AsSpan(extOff + 2);
                    if (head.Length > 0 && !IsOurs(head.ToArray(), sid) && head[0] != 0x7F) continue; // broadcast multi-frame
                    rx.Reset(); rx.Feed(f);
                }
            }

            if (rx.InProgress)
            {
                while (!rx.IsComplete)
                {
                    int n = Math.Min(rx.FramesRemaining, _rxBlock);
                    var fcBytes = IsoTp.BuildFlowControl(0, n, 0, ecu.ExtAddress);
                    string hex = Hex.ToString(fcBytes) + n.ToString("X");   // trailing digit: expect n frames
                    var r = await _conn.SendLockedAsync(hex, RequestTimeout, ct).ConfigureAwait(false);
                    if (r.TimedOut) throw new EcuTimeoutException("Adapter did not answer flow control.");
                    if (r.Error == "BUFFER FULL") throw new ElmErrorException("BUFFER FULL", "multi-frame receive");
                    int got = 0;
                    foreach (var cf in ParseFrames(r))
                    {
                        if (IsoTp.TypeOf(cf, extOff) != IsoTpFrameType.Consecutive) continue;
                        try { rx.Feed(cf); } catch (ProtocolException) { _conn.Counters.FrameErrors++; throw; }
                        got++;
                        if (rx.IsComplete) break;
                    }
                    if (got == 0)
                    {
                        if (r.Error is not null && r.Error != "NO DATA") throw new ElmErrorException(r.Error, "multi-frame receive");
                        throw new EcuTimeoutException("Multi-frame response incomplete.");
                    }
                }
                return rx.Payload;
            }

            if (pending)
            {
                if (++pendingSeen > _opt.MaxPendingResponses) throw new EcuTimeoutException("Too many 'response pending' messages.");
                _opt.Trace?.Invoke($"# response pending (0x78) for service 0x{sid:X2}");
                frames = await WaitForFramesAsync(ecu, f => IsFinalCandidate(f, extOff, sid), ct).ConfigureAwait(false);
                continue;
            }

            if (fallback is not null)
            {
                // Only unrelated (broadcast) frames so far: the real answer may still be queued behind them.
                if (!triedUnmatched)
                {
                    triedUnmatched = true;
                    try
                    {
                        frames = await WaitForFramesAsync(ecu, f => IsFinalCandidate(f, extOff, sid), ct, _opt.UnmatchedResponseWait).ConfigureAwait(false);
                        continue;
                    }
                    catch (EcuTimeoutException) { /* nothing better arrived */ }
                }
                return fallback;
            }
            throw new ProtocolException("No valid ISO-TP response frames received.");
        }
    }

    /// <summary>A frame that ends the wait after "response pending": FF, or an SF that is not itself a pending notice.</summary>
    private static bool IsFinalCandidate(byte[] f, int extOff, int sid)
    {
        try
        {
            var t = IsoTp.TypeOf(f, extOff);
            if (t == IsoTpFrameType.First) return true;
            if (t != IsoTpFrameType.Single) return false;
            var r = new IsoTpReassembler(extOff); r.Feed(f);
            return !IsoTp.IsPending(r.Payload, sid) && IsOurs(r.Payload, sid);
        }
        catch (ProtocolException) { return false; }
    }

    /// <summary>
    /// Listens with ATMA (no transmit) until a frame satisfying <paramref name="stop"/> arrives, bounded by P2*.
    /// Every pending notice restarts the timer. Returns all frames collected, the last being the matching one.
    /// </summary>
    private async ValueTask<List<byte[]>> WaitForFramesAsync(EcuAddress ecu, Func<byte[], bool> stop, CancellationToken ct, TimeSpan? wait = null)
    {
        var window = wait ?? _opt.PendingTimeout;
        var collected = new List<byte[]>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        try
        {
            await foreach (var line in _conn.MonitorLockedAsync(Profile.IsStn ? "STMA" : "ATMA", cts.Token).ConfigureAwait(false))
            {
                if (!Hex.IsHexLine(line) || !Hex.TryParse(line, out var f) || f.Length == 0) continue;
                collected.Add(f);
                if (stop(f)) return collected;
                cts.CancelAfter(window); // some other frame / pending notice: keep waiting
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EcuTimeoutException("Timed out waiting for the ECU's final response after 'response pending'.");
        }
        throw new EcuTimeoutException("Monitor ended before the ECU responded.");
    }

    // ---- CAN, adapter in CAF1: the ELM does ISO-TP ----

    private static readonly Regex MultiLine = new(@"^[0-9A-F]:\s*", RegexOptions.Compiled);

    private async ValueTask<byte[]> CanAutomaticAsync(EcuAddress ecu, byte[] req, CancellationToken ct)
    {
        int cap = ecu.ExtAddress.HasValue ? 6 : 7;
        if (req.Length > cap)
            throw new ProtocolException($"Automatic ISO-TP mode only sends up to {cap} bytes; use IsoTpMode.Manual for longer requests.");
        var r = await _conn.SendLockedAsync(Hex.ToString(req), RequestTimeout, ct).ConfigureAwait(false);
        ThrowIfError(r, "CAN request");

        int sid = req[0];
        var segments = new SortedDictionary<int, byte[]>();
        int declared = -1;
        var singles = new List<byte[]>();
        foreach (var line in r.Lines)
        {
            if (MultiLine.IsMatch(line))
            {
                int idx = Convert.ToInt32(line[0].ToString(), 16);
                if (Hex.TryParse(line[(line.IndexOf(':') + 1)..], out var b)) segments[idx] = b;
            }
            else if (Hex.IsHexLine(line))
            {
                var compact = line.Replace(" ", "");
                if (compact.Length == 3 && line.Length == 3) { declared = Convert.ToInt32(compact, 16); continue; }   // "00B" length header
                if (Hex.TryParse(line, out var b) && b.Length > 0) singles.Add(b);
            }
        }
        if (segments.Count > 0)
        {
            var all = segments.Values.SelectMany(x => x).ToArray();
            if (declared > 0 && all.Length >= declared) all = all[..declared];
            return all;
        }
        foreach (var s in singles) if (IsOurs(s, sid) && !IsoTp.IsPending(s, sid)) return s;
        if (singles.Any(s => IsoTp.IsPending(s, sid)))
            throw new ProtocolException("ECU answered 'response pending'; automatic ISO-TP mode cannot wait for the final response (use Manual).");
        if (singles.Count > 0) return singles[0];
        throw new EcuTimeoutException("No response from ECU.");
    }

    // ---- K-line ----

    private async ValueTask<byte[]> KLineAsync(byte[] req, CancellationToken ct)
    {
        int sid = req[0];
        var r = await _conn.SendLockedAsync(Hex.ToString(req), RequestTimeout, ct).ConfigureAwait(false);
        ThrowIfError(r, "K-line request");
        var msgs = ParseFrames(r);
        int pendingSeen = 0;
        while (true)
        {
            var real = msgs.Where(m => !IsoTp.IsPending(m, sid)).ToList();
            if (real.Count > 0)
            {
                if (real.Count == 1) return real[0];
                return real.SelectMany(m => m).ToArray(); // multi-line answer, concatenated
            }
            if (msgs.Count == 0) throw new EcuTimeoutException("No response from ECU.");
            if (++pendingSeen > _opt.MaxPendingResponses) throw new EcuTimeoutException("Too many 'response pending' messages.");
            msgs = await WaitForFramesAsync(_ecu!, m => !IsoTp.IsPending(m, sid), ct).ConfigureAwait(false);
        }
    }

    #endregion

    /// <summary>Forgets the selected ECU so the next <see cref="ConnectEcuAsync"/> re-programs the adapter (after monitor mode etc.).</summary>
    internal void InvalidateAddressing()
    {
        _ecu = null; _canReady = false; _klineReady = false; _cea = false; _sessionRequest = Array.Empty<byte>();
    }

    #region keep-alive

    /// <summary>
    /// Starts a background tester-present loop: whenever the bus has been idle for <paramref name="interval"/> the
    /// configured payload is sent (skipped if a request is in flight). Failures are ignored.
    /// </summary>
    public void StartKeepAlive(TimeSpan? interval = null)
    {
        StopKeepAlive();
        var period = interval ?? _opt.KeepAliveInterval;
        var cts = new CancellationTokenSource();
        _keepAliveCts = cts;
        _keepAliveTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period / 2 > TimeSpan.FromMilliseconds(10) ? period / 2 : period);
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    if (_ecu is null || _conn.IdleTime < period) continue;
                    if (!_conn.TryAcquire(out var lease)) continue;
                    using (lease)
                    {
                        try { await RequestCoreAsync(_ecu, _opt.TesterPresent, cts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { /* ignore: the next real request reports problems */ }
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public void StopKeepAlive()
    {
        var cts = Interlocked.Exchange(ref _keepAliveCts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    #endregion

    /// <summary>Resets the adapter (ATZ) politely and closes the link.</summary>
    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopKeepAlive();
        if (_keepAliveTask is not null) { try { await _keepAliveTask.ConfigureAwait(false); } catch { } }
        try
        {
            if (_conn.Link.IsOpen && _conn.TryAcquire(out var lease))
            {
                using (lease)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await _conn.WriteRawAsync("ATZ\r"u8.ToArray(), cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch { /* best effort */ }
        await _conn.DisposeAsync().ConfigureAwait(false);
    }
}
