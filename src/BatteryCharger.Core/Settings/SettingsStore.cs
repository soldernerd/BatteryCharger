using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatteryCharger.Core.Settings;

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON. Never throws on a missing or corrupt file.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public string Path { get; } = path;

    /// <summary>Default location: %LOCALAPPDATA%\BatteryCharger\settings.json.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryCharger", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return Fresh();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings();
            s.Normalize();
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Move(Path, Path + ".corrupt", true); } catch (Exception) { /* ignore */ }
            return Fresh();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, Path, true);
    }

    private static AppSettings Fresh()
    {
        var s = new AppSettings();
        s.Normalize();
        return s;
    }
}
