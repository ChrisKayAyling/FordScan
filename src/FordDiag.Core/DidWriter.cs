using FordDiag.Comms;

namespace FordDiag.Core;

public sealed class WriteOptions
{
    /// <summary>Minimum battery voltage; a low battery during a write can leave a module corrupt. Null reader skips the check.</summary>
    public double MinVoltage { get; init; } = 12.0;
    public Func<CancellationToken, ValueTask<double?>>? ReadVoltage { get; init; }
    public BackupStore Backups { get; init; } = new();
    /// <summary>When false (default) nothing is written: the plan is validated and reported only.</summary>
    public bool Commit { get; init; }
    public byte Session { get; init; } = 0x03;
}

public sealed record WriteResult(bool Written, string? BackupPath, byte[] Before, byte[] After, string Message);

public class WriteRefusedException : Exception { public WriteRefusedException(string m) : base(m) { } }

/// <summary>
/// Guarded UDS WriteDataByIdentifier (0x2E): checks voltage, backs up the current value, enters the diagnostic session,
/// writes and verifies by reading back. Security access (0x27) is not implemented: modules that demand it answer NRC 0x33.
/// </summary>
public static class DidWriter
{
    public static async Task<WriteResult> WriteAsync(IAddressableTransport t, ModuleSession module, ushort did, byte[] value,
        WriteOptions options, CancellationToken ct = default)
    {
        if (value.Length == 0) throw new WriteRefusedException("Refusing to write an empty value.");
        if (options.ReadVoltage is { } rv)
        {
            var v = await rv(ct).ConfigureAwait(false);
            if (v is double volts && volts < options.MinVoltage)
                throw new WriteRefusedException($"Battery voltage {volts:F1} V is below {options.MinVoltage:F1} V. Connect a charger/battery maintainer.");
        }

        var before = await module.ReadDidAsync(did, ct).ConfigureAwait(false);
        if (before.Length != value.Length)
            throw new WriteRefusedException($"Length mismatch: module holds {before.Length} bytes for DID {did:X4}, you supplied {value.Length}.");
        if (!options.Commit)
            return new WriteResult(false, null, before, value, "Dry run: nothing was written.");

        var backup = options.Backups.Save(module.Module, module.Bus, did, before);
        var sess = await module.RequestAsync(new byte[] { 0x10, options.Session }, ct).ConfigureAwait(false);
        if (sess.Length > 0 && sess[0] == 0x7F) throw new NegativeResponseException(sess[1], sess[2]);

        var req = new byte[3 + value.Length];
        req[0] = 0x2E; req[1] = (byte)(did >> 8); req[2] = (byte)did;
        value.CopyTo(req, 3);
        var rsp = await module.RequestAsync(req, ct).ConfigureAwait(false);
        if (rsp.Length >= 3 && rsp[0] == 0x7F)
            throw new WriteRefusedException($"Module rejected the write: NRC 0x{rsp[2]:X2} ({NegativeResponses.Describe(rsp[2])}). Backup: {backup}");

        var after = await module.ReadDidAsync(did, ct).ConfigureAwait(false);
        bool ok = after.AsSpan().SequenceEqual(value);
        return new WriteResult(true, backup, before, after, ok ? "Written and verified." : "Write accepted but read-back differs! Restore from the backup.");
    }
}
