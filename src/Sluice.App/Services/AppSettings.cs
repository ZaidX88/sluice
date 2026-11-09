using System.IO;
using System.Text.Json;

namespace Sluice.App.Services;

/// <summary>Per-user app settings in %APPDATA%\Sluice\settings.json (not part of the profile).</summary>
public sealed class AppSettings
{
    public string? LastProfilePath { get; set; }
    public int SocksPort { get; set; } = 1080;
    public bool StartInterceptionOnLaunch { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool FileLogEnabled { get; set; }
    public string LogDirectory { get; set; } = Path.Combine(Folder, "Logs");

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sluice");

    public static string ProfilesFolder => Path.Combine(Folder, "Profiles");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt settings: start fresh rather than refuse to launch.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
