using System.Text.Json;
using FordDiag.Comms;

namespace FordDiag.Core;

public sealed record DidBackup(DateTimeOffset Time, string Module, uint RequestId, FordBus Bus, string Did, string Hex);

/// <summary>Writes a JSON snapshot of a DID value before it is changed, so it can be restored.</summary>
public sealed class BackupStore
{
    public string Directory { get; }
    public BackupStore(string? directory = null) =>
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fordiag", "backups");

    public string Save(FordModule module, FordBus bus, ushort did, byte[] value)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var now = DateTimeOffset.Now;
        var path = Path.Combine(Directory, $"{now:yyyyMMdd-HHmmss-fff}_{module.Abbrev}_{did:X4}.json");
        var b = new DidBackup(now, module.Abbrev, module.RequestId, bus, did.ToString("X4"), Hex.ToString(value, true));
        File.WriteAllText(path, JsonSerializer.Serialize(b, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static DidBackup Load(string path) =>
        JsonSerializer.Deserialize<DidBackup>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty backup file.");
}
