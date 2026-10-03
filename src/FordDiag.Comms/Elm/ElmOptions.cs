namespace FordDiag.Comms.Elm;

/// <summary>How ISO-TP segmentation/reassembly is performed on CAN.</summary>
public enum IsoTpMode
{
    /// <summary>
    /// Adapter in ATCAF0: the driver builds PCI bytes, performs flow control and reassembles multi-frame responses itself.
    /// Supports requests of any size (&gt; 7 bytes) and ECU "response pending". Default.
    /// </summary>
    Manual,
    /// <summary>
    /// Adapter in ATCAF1: the ELM does ISO-TP. Requests are limited to 7 bytes (6 with extended addressing);
    /// multi-line "0:/1:" responses are reassembled by the driver. Faster but less capable.
    /// </summary>
    ElmAutomatic,
}

/// <summary>Tuning knobs for <see cref="ElmTransport"/>.</summary>
public sealed class ElmOptions
{
    /// <summary>Adapter profile. When <see cref="AutoDetectProfile"/> is set it is refined from the ATI/STI answer.</summary>
    public ElmProfile Profile { get; set; } = ElmProfiles.Elm327;
    public bool AutoDetectProfile { get; set; } = true;

    /// <summary>First baud rate to try (default: profile default).</summary>
    public int? InitialBaud { get; set; }

    /// <summary>Raise the UART speed after identification (ATBRD / ST SBR). Null: stay.</summary>
    public int? TargetBaud { get; set; }

    /// <summary>Try the other speeds of <see cref="ElmProfile.ProbeBauds"/> when the first one gets no answer.</summary>
    public bool AutoBaud { get; set; } = true;

    /// <summary>Wait for the very first answer at the requested baud.</summary>
    public TimeSpan FirstProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Wait at fallback bauds.</summary>
    public TimeSpan FallbackProbeTimeout { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>Host timeout for plain AT commands. Null: profile timeout.</summary>
    public TimeSpan? CommandTimeout { get; set; }

    /// <summary>Host timeout for a diagnostic exchange (the ELM itself returns after ATST/adaptive timing). Null: profile timeout.</summary>
    public TimeSpan? RequestTimeout { get; set; }

    /// <summary>How long to wait for the final answer after "7F xx 78" (P2*). Default 5 s.</summary>
    public TimeSpan PendingTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to keep listening (ATMA) when only unrelated frames (e.g. an ECU broadcasting on its reply id) were received,
    /// before falling back to the first such frame.
    /// </summary>
    public TimeSpan UnmatchedResponseWait { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on successive "response pending" messages for one request.</summary>
    public int MaxPendingResponses { get; set; } = 64;

    /// <summary>Retries for transient adapter errors (CAN ERROR, BUFFER FULL, RX ERROR, '?', ...).</summary>
    public int Retries { get; set; } = 1;

    /// <summary>Also retry once when the ECU stays silent (NO DATA).</summary>
    public bool RetryOnNoData { get; set; }

    /// <summary>ELM "ATST" value in ms for CAN.</summary>
    public int CanTimeoutMs { get; set; }

    public IsoTpMode IsoTp { get; set; } = IsoTpMode.Manual;

    /// <summary>Consecutive frames the ECU may send per flow-control block (manual mode). ELM buffers are small: max 7.</summary>
    public int ReceiveBlockSize { get; set; } = 7;

    /// <summary>When set, CAN frames are padded to 8 bytes with this value (some ECUs require 0x00/0xAA/0x55 padding).</summary>
    public byte? PadByte { get; set; }

    /// <summary>After this much silence the session request of the current ECU is re-sent before the next request.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Payload sent by the background keep-alive ("tester present").</summary>
    public byte[] TesterPresent { get; set; } = { 0x3E, 0x00 };

    /// <summary>For slow-init requests: fall back to fast init when the 5-baud init fails.</summary>
    public bool FallbackToFastInit { get; set; } = true;

    /// <summary>Send "81" (startCommunication) after a KWP init.</summary>
    public bool KwpStartCommunication { get; set; } = true;

    /// <summary>Optional trace sink: "&gt; command" / "&lt; reply" lines (for the app's log view).</summary>
    public Action<string>? Trace { get; set; }
}
