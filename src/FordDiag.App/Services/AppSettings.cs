using System.Text.Json;

namespace FordDiag.App.Services;

/// <summary>User settings, stored as JSON in the OS config directory (APPDATA / ~/Library/Application Support / ~/.config).</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public string AdapterKey { get; set; } = "vlinker-fs";
    public string Port { get; set; } = "";
    public int ResponseTimeoutMs { get; set; }
    public bool FullRangeScan { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public string? Path { get; set; }

    public static string ConfigDir => System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("FORDDIAG_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FordDiag");

    public static AppSettings Load(string? path = null)
    {
        path ??= System.IO.Path.Combine(ConfigDir, "settings.json");
        try
        {
            if (File.Exists(path))
                { var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings(); s.Path = path; return s; }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* corrupt or unreadable: start fresh */ }
        return new AppSettings { Path = path };
    }

    public void Save()
    {
        if (Path is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* settings are a convenience */ }
    }
}
