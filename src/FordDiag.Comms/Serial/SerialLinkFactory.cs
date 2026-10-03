namespace FordDiag.Comms.Serial;

/// <summary>Opens the right <see cref="ISerialLink"/> for a user-visible port name.</summary>
public static class SerialLinkFactory
{
    /// <summary>
    /// "host:port" opens a TCP link (WiFi ELM, e.g. 192.168.0.10:35000); everything else (COMx, /dev/ttyUSB0,
    /// /dev/rfcomm0, /dev/cu.*) opens a serial port. Bluetooth adapters are exposed by the OS as serial ports.
    /// </summary>
    public static async ValueTask<ISerialLink> OpenAsync(string portName, SerialLinkOptions? options = null, CancellationToken ct = default)
    {
        options ??= new SerialLinkOptions();
        portName = portName.Trim();
        if (PortEnumerator.TryParseEndpoint(portName, out var host, out var port))
            return await TcpSerialLink.ConnectAsync(host, port, options.ConnectTimeout, ct).ConfigureAwait(false);
        return await SystemSerialLink.OpenAsync(portName, options, ct).ConfigureAwait(false);
    }
}
