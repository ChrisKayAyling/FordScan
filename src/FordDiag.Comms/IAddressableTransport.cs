using FordDiag.Comms;

namespace FordDiag.Comms;

/// <summary>
/// An <see cref="IEcuTransport"/> that can be (re)pointed at a particular ECU. Implemented by the ELM327 driver,
/// the simulation transports.
/// </summary>
public interface IAddressableTransport : IEcuTransport, IAsyncDisposable
{
    /// <summary>Currently selected ECU (null before the first <see cref="ConnectEcuAsync"/>).</summary>
    EcuAddress? CurrentEcu { get; }

    /// <summary>Selects an ECU: programs addressing/filters, performs K-line init where needed. Idempotent for the same address.</summary>
    ValueTask ConnectEcuAsync(EcuAddress ecu, CancellationToken ct = default);

    /// <summary>Releases the current ECU (ELM "ATPC" etc.) but keeps the adapter open.</summary>
    ValueTask CloseProtocolAsync(CancellationToken ct = default);
}
