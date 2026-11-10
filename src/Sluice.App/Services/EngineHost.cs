using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Principal;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Interception;
using Sluice.Core.Profiles;

namespace Sluice.App.Services;

/// <summary>
/// Owns the running engine for the UI: profile, local SOCKS listener, optional WinDivert
/// interception, statistics and file log. All methods are called on the UI thread.
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    private Socks5Inbound? _socks;
    private Interceptor? _interceptor;
    private FileLog? _fileLog;

    public AppSettings Settings { get; }
    public ConnectionTracker Tracker { get; } = new() { Level = LogLevel.Verbose };
    public Statistics Statistics { get; }
    public RoutingEngine Engine { get; }

    public Profile Profile { get; private set; }
    public string? ProfilePath { get; private set; }
    public bool IsDirty { get; private set; }

    public bool SocksRunning => _socks is not null;
    public IPEndPoint? SocksEndPoint => _socks?.LocalEndPoint;
    public bool Intercepting => _interceptor is not null;

    public event Action? StateChanged;
    public event Action? ProfileChanged;

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public EngineHost(AppSettings settings)
    {
        Settings = settings;
        Statistics = new Statistics(Tracker);
        Profile = Profile.CreateDefault();
        Engine = new RoutingEngine(Profile, Tracker);
        // Trust-on-first-use: remember an SSH server's host key in the profile (checked from then on).
        Engine.Backends.HostKeyLearned += (tunnel, key) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Profile.FindTunnel(tunnel.Id) is not { } t || t.Settings.ContainsKey("hostkey")) return;
            t.Settings["hostkey"] = key;
            CommitProfileEdit(notify: false);
        });
        ApplyFileLog();
    }

    public string ProfileName => ProfilePath is null ? "Untitled profile" : Path.GetFileNameWithoutExtension(ProfilePath);

    // ---------------- profile ----------------

    /// <summary>
    /// Opens a profile. A Proxifier .ppx is imported: it is converted into a new .sluice file in the
    /// Sluice profiles folder (autosaving back into .ppx would lose tunnels it cannot store).
    /// </summary>
    /// <returns>A message for the user when the file was imported, otherwise null.</returns>
    public string? LoadProfile(string path, MasterPasswordProvider? askMaster)
    {
        var profile = ProfileFile.Load(path, askMaster);
        string? note = null;
        if (ProfileFile.IsProxifier(path))
        {
            var target = UniquePath(Path.Combine(AppSettings.ProfilesFolder, Path.GetFileNameWithoutExtension(path) + ProfileFile.Extension));
            Directory.CreateDirectory(AppSettings.ProfilesFolder);
            SluiceProfileSerializer.Save(profile, target);
            note = $"Imported {Path.GetFileName(path)} from Proxifier as {Path.GetFileName(target)}. Changes are saved there; the original file is untouched.";
            Tracker.Write(LogLevel.Normal, note);
            path = target;
        }
        Profile = profile;
        ProfilePath = path;
        IsDirty = false;
        Engine.Reload(profile);
        Settings.LastProfilePath = path;
        Settings.Save();
        ProfileChanged?.Invoke();
        return note;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ProfileFile.Extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Writes a Proxifier-compatible copy; returns what could not be exported.</summary>
    public List<string> ExportProxifier(string path)
    {
        var warnings = new List<string>();
        ProfileFile.Save(Profile, path, warnings);
        Tracker.Write(LogLevel.Normal, $"Exported {Path.GetFileName(path)} for Proxifier" + (warnings.Count > 0 ? $" ({warnings.Count} item(s) left out)" : ""));
        return warnings;
    }

    /// <summary>Use an in-memory profile without a file (demo / screenshots).</summary>
    public void UseProfile(Profile profile)
    {
        Profile = profile;
        ProfilePath = null;
        Engine.Reload(profile);
        ProfileChanged?.Invoke();
    }

    public void NewProfile()
    {
        Profile = Profile.CreateDefault();
        ProfilePath = null;
        IsDirty = true;
        Engine.Reload(Profile);
        ProfileChanged?.Invoke();
    }

    /// <summary>Call after editing <see cref="Profile"/>: applies it live and autosaves if it has a file.</summary>
    /// <param name="notify">False when the caller already reflects the change (e.g. a toggle in a list row).</param>
    public void CommitProfileEdit(bool notify = true)
    {
        Engine.Reload(Profile);
        IsDirty = true;
        if (ProfilePath is not null) TrySave(ProfilePath, notify);
        else if (notify) ProfileChanged?.Invoke();
    }

    public bool TrySave(string path, bool notify = true)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // Saving always writes Sluice's format; Proxifier copies go through ExportProxifier.
            if (ProfileFile.IsProxifier(path)) path = Path.ChangeExtension(path, ProfileFile.Extension);
            SluiceProfileSerializer.Save(Profile, path);
            ProfilePath = path;
            IsDirty = false;
            Settings.LastProfilePath = path;
            Settings.Save();
            if (notify) ProfileChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Tracker.Write(LogLevel.Error, $"Could not save profile: {ex.Message}");
            return false;
        }
    }

    // ---------------- engine ----------------

    public void StartSocks()
    {
        if (_socks is not null) return;
        try
        {
            var s = new Socks5Inbound(Engine, new IPEndPoint(IPAddress.Loopback, Settings.SocksPort));
            s.Start();
            _socks = s;
        }
        catch (Exception ex)
        {
            Tracker.Write(LogLevel.Error, $"Local SOCKS5 server could not start on port {Settings.SocksPort}: {ex.Message}");
        }
        StateChanged?.Invoke();
    }

    public async Task RestartSocksAsync()
    {
        if (_socks is not null)
        {
            await _socks.DisposeAsync();
            _socks = null;
        }
        StartSocks();
    }

    /// <summary>Turns WinDivert interception on. Returns an error message, or null on success.</summary>
    public string? StartInterception()
    {
        if (_interceptor is not null) return null;
        if (!IsElevated) return "Interception needs administrator rights.";
        var i = new Interceptor(Engine);
        try
        {
            i.Start();
        }
        catch (Exception ex)
        {
            _ = i.DisposeAsync();
            return ex.Message;
        }
        _interceptor = i;
        StateChanged?.Invoke();
        return null;
    }

    public async Task StopInterceptionAsync()
    {
        if (_interceptor is null) return;
        var i = _interceptor;
        _interceptor = null;
        await i.DisposeAsync();
        StateChanged?.Invoke();
    }

    public void ApplyFileLog()
    {
        _fileLog?.Dispose();
        _fileLog = null;
        if (!Settings.FileLogEnabled) return;
        try
        {
            _fileLog = new FileLog(Tracker, Settings.LogDirectory, LogLevel.Verbose);
        }
        catch (Exception ex)
        {
            Tracker.Write(LogLevel.Error, $"File log disabled: {ex.Message}");
        }
    }

    public static void RestartElevated(string? extraArgs = null)
    {
        var exe = Environment.ProcessPath!;
        Process.Start(new ProcessStartInfo(exe, extraArgs ?? "") { UseShellExecute = true, Verb = "runas" });
    }

    public async ValueTask DisposeAsync()
    {
        Tracker.DisconnectAll();
        await Tracker.WaitIdleAsync(TimeSpan.FromSeconds(2));
        await StopInterceptionAsync();
        if (_socks is not null) await _socks.DisposeAsync();
        await Engine.DisposeAsync();   // closes SSH sessions
        _fileLog?.Dispose();
    }
}
