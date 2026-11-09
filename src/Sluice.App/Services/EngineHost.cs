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
    public ProxyEngine Engine { get; }

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
        Engine = new ProxyEngine(Profile, Tracker);
        ApplyFileLog();
    }

    public string ProfileName => ProfilePath is null ? "Untitled profile" : Path.GetFileNameWithoutExtension(ProfilePath);

    // ---------------- profile ----------------

    public void LoadProfile(string path, MasterPasswordProvider? askMaster)
    {
        var profile = PpxSerializer.Load(path, askMaster);
        Profile = profile;
        ProfilePath = path;
        IsDirty = false;
        Engine.Reload(profile);
        Settings.LastProfilePath = path;
        Settings.Save();
        ProfileChanged?.Invoke();
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
            PpxSerializer.Save(Profile, path);
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
        _fileLog?.Dispose();
    }
}
