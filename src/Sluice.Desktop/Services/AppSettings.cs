using System.IO;
using System.Text.Json;
using Sluice.Core.Profiles;

namespace Sluice.Desktop.Services;

/// <summary>Per-user app settings in %APPDATA%\Sluice\settings.json (not part of the profile).</summary>
public sealed class AppSettings
{
    public string? LastProfilePath { get; set; }
    public int SocksPort { get; set; } = 1080;
    public bool StartInterceptionOnLaunch { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>On for new settings files (#40), so there is a log to attach after a problem; an existing file keeps
    /// what it says.</summary>
    public bool FileLogEnabled { get; set; } = true;

    /// <summary>The file log at Verbose (also every closed connection and relay statistics) instead of Normal.</summary>
    public bool FileLogDetailed { get; set; }

    /// <summary>Screenshot mode: show the settings, but write no log file. Never saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WriteNoFiles { get; init; }

    /// <summary>Stop a tunnel nothing has used for this many minutes (0 = never).</summary>
    public int TunnelIdleMinutes { get; set; } = 10;

    /// <summary>Windows notifications when tunnels connect, disconnect or give up.</summary>
    public bool TunnelNotifications { get; set; } = true;
    public string LogDirectory { get; set; } = Path.Combine(Folder, "Logs");

    /// <summary>Name resolution, connection and UDP options for every profile (#148). Null until the first profile
    /// Sluice opens (or the first change on the Settings page) supplies them: that profile's values carry over once,
    /// and from then on these win over what a profile file says.</summary>
    public NetworkSettings? Network { get; set; }

    /// <summary>The Settings page's Advanced section is open (#149); closed by default.</summary>
    public bool SettingsAdvancedOpen { get; set; }

    /// <summary><paramref name="path"/> for display: the user's folders shortened (%APPDATA%\…, ~/…), so the page
    /// and its screenshots don't show the account name.</summary>
    public static string ShortPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (appData.Length > 0 && path.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
                return "%APPDATA%" + path[appData.Length..];
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && path.StartsWith(home + Path.DirectorySeparatorChar, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return "~" + path[home.Length..];
        return path;
    }

    /// <summary>%APPDATA%\Sluice, ~/.config/Sluice. DoNotVerify: on Linux the folder is "" when ~/.config doesn't exist yet
    /// (a fresh account), which would put settings and logs in the current folder.</summary>
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Sluice");

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

    /// <summary>The app-wide network options, taken from <paramref name="current"/> the first time they're needed.</summary>
    public NetworkSettings NetworkFrom(ProfileOptions current) => Network ??= NetworkSettings.From(current);

    public void Save()
    {
        if (WriteNoFiles) return;   // screenshot mode: never saved
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The options the Settings page shows for name resolution, connections and UDP. They belong to the app, not to a
/// profile (#148), so switching profile never changes them. <see cref="ApplyTo"/> copies them into a profile's
/// options, which the engine reads and which are saved in the profile file, so <c>sluice-cli</c> running that file
/// behaves as the app did. Password protection is not here: it belongs to each profile file.
/// </summary>
public sealed class NetworkSettings
{
    public bool ResolveViaTunnel { get; set; }
    public string ResolveLocallyList { get; set; } = "%ComputerName%; localhost; *.local";
    public bool OnlyListedViaTunnel { get; set; }
    public bool LeakPrevention { get; set; }
    public bool BlockOtherQueryTypes { get; set; }
    public bool LoopDetection { get; set; } = true;
    public bool HandleDirectConnections { get; set; }
    public bool IncludeSystemServices { get; set; }
    public UdpMode Udp { get; set; } = UdpMode.Tunnel;

    public static NetworkSettings From(ProfileOptions o) => new()
    {
        ResolveViaTunnel = o.Resolve.ViaProxy,
        ResolveLocallyList = o.Resolve.ExclusionList,
        OnlyListedViaTunnel = o.Resolve.OnlyFromListMode,
        LeakPrevention = o.LeakPreventionMode,
        BlockOtherQueryTypes = o.Resolve.BlockNonATypes,
        LoopDetection = o.ConnectionLoopDetection,
        HandleDirectConnections = o.HandleDirectConnections,
        IncludeSystemServices = o.ProcessServices,
        Udp = o.Udp,
    };

    public void ApplyTo(ProfileOptions o)
    {
        o.Resolve.ViaProxy = ResolveViaTunnel;
        o.Resolve.ExclusionList = ResolveLocallyList;
        o.Resolve.OnlyFromListMode = OnlyListedViaTunnel;
        o.LeakPreventionMode = LeakPrevention;
        o.Resolve.BlockNonATypes = BlockOtherQueryTypes;
        o.ConnectionLoopDetection = LoopDetection;
        o.HandleDirectConnections = HandleDirectConnections;
        o.ProcessServices = IncludeSystemServices;
        o.Udp = Udp;
    }
}
