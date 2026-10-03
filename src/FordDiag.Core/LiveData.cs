namespace FordDiag.Core;

/// <summary>An SAE J1979 mode 01 PID with its decoding formula.</summary>
public sealed record ObdPid(byte Pid, string Name, string Unit, int Bytes, Func<byte[], double> Decode, double Min, double Max, string Format = "0.#")
{
    public double Read(byte[] data) => Decode(data);
}

public static class ObdPids
{
    private static double U16(byte[] d) => d[0] * 256 + d[1];

    public static IReadOnlyList<ObdPid> All { get; } = new ObdPid[]
    {
        new(0x0C, "Engine speed", "rpm", 2, d => U16(d) / 4, 0, 7000, "0"),
        new(0x0D, "Vehicle speed", "km/h", 1, d => d[0], 0, 220, "0"),
        new(0x05, "Coolant temperature", "°C", 1, d => d[0] - 40, -40, 130, "0"),
        new(0x04, "Engine load", "%", 1, d => d[0] * 100.0 / 255, 0, 100),
        new(0x11, "Throttle position", "%", 1, d => d[0] * 100.0 / 255, 0, 100),
        new(0x0B, "Intake manifold pressure", "kPa", 1, d => d[0], 0, 255, "0"),
        new(0x0F, "Intake air temperature", "°C", 1, d => d[0] - 40, -40, 100, "0"),
        new(0x10, "Mass air flow", "g/s", 2, d => U16(d) / 100, 0, 150),
        new(0x0E, "Ignition timing advance", "°", 1, d => d[0] / 2.0 - 64, -64, 64),
        new(0x06, "Short-term fuel trim B1", "%", 1, d => d[0] * 100.0 / 128 - 100, -25, 25),
        new(0x07, "Long-term fuel trim B1", "%", 1, d => d[0] * 100.0 / 128 - 100, -25, 25),
        new(0x2F, "Fuel level", "%", 1, d => d[0] * 100.0 / 255, 0, 100),
        new(0x42, "Control module voltage", "V", 2, d => U16(d) / 1000, 9, 16),
        new(0x46, "Ambient air temperature", "°C", 1, d => d[0] - 40, -40, 60, "0"),
        new(0x5C, "Engine oil temperature", "°C", 1, d => d[0] - 40, -40, 150, "0"),
    };

    public static ObdPid? Find(byte pid) => All.FirstOrDefault(p => p.Pid == pid);

    /// <summary>Decodes a "41 pid data..." response. Returns null for negative or malformed answers.</summary>
    public static double? TryDecode(ObdPid pid, byte[] response)
    {
        if (response.Length < 2 + pid.Bytes || response[0] != 0x41 || response[1] != pid.Pid) return null;
        return pid.Read(response.AsSpan(2, pid.Bytes).ToArray());
    }
}
