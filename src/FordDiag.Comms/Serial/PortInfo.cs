namespace FordDiag.Comms.Serial;

/// <summary>Kind of connection an entry in the port list represents.</summary>
public enum PortKind
{
    Serial,
    Usb,
    Bluetooth,
    /// <summary>WiFi ELM327 style TCP endpoint "ip:port".</summary>
    Tcp,
}

public enum PortStatus { Unknown, Online, Offline }

/// <summary>One selectable connection target (port, description, hardware id, status).</summary>
public sealed record PortInfo(
    string Name,
    string Description,
    string HardwareId,
    PortKind Kind,
    PortStatus Status = PortStatus.Unknown,
    string? SuggestedProfileKey = null,
    int? VendorId = null,
    int? ProductId = null);
