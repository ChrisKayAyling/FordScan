namespace FordDiag.Comms;

/// <summary>Anything that can send a diagnostic request to the currently selected ECU and return its response payload.</summary>
public interface IEcuTransport
{
    /// <summary>Send a diagnostic request; return the complete positive/negative response payload.</summary>
    ValueTask<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default);
}
