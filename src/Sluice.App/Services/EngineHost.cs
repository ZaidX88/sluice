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

    /// <summary>A neutral notice for the banner (e.g. "3 tunnels imported").</summary>
    public event Action<string>? Notice;

    /// <summary>A tunnel connected, disconnected or gave up: (title, message, isError). Shown as a notification.</summary>
    public event Action<string, string, bool>? TunnelNotice;

    private readonly Dictionary<int, Sluice.Core.Tunnels.SessionState> _lastTunnelState = new();

    /// <summary>Turns state changes into notices, only on real transitions (a retry loop must not flood the user).</summary>
    private void OnTunnelStatus(Tunnel t, Sluice.Core.Tunnels.SessionStatus st)
    {
        string? title = null, text = null;
        var error = false;
        lock (_lastTunnelState)
        {
            _lastTunnelState.TryGetValue(t.Id, out var prev);
            _lastTunnelState[t.Id] = st.State;
            var name = t.Label ?? $"{t.Address}:{t.Port}";
            switch (st.State)
            {
                case Sluice.Core.Tunnels.SessionState.Connected when prev != Sluice.Core.Tunnels.SessionState.Connected:
                    var again = prev is Sluice.Core.Tunnels.SessionState.Reconnecting;
                    title = again ? $"{name} reconnected" : $"{name} connected";
                    text = $"{t.Type.DisplayName()} tunnel is up.";
                    break;
                case Sluice.Core.Tunnels.SessionState.Reconnecting when prev == Sluice.Core.Tunnels.SessionState.Connected:
                    title = $"{name} disconnected";
                    text = $"{st.Detail}. Reconnecting…";
                    error = true;
                    break;
                case Sluice.Core.Tunnels.SessionState.Reconnecting when prev is Sluice.Core.Tunnels.SessionState.Connecting && st.Detail?.StartsWith("attempt 1 ") == true:
                    title = $"{name} could not connect";
                    text = $"{st.Detail}. Retrying…";
                    error = true;
                    break;
                case Sluice.Core.Tunnels.SessionState.Failed:
                    title = $"{name} stopped";
                    text = $"{st.Detail}. Check the tunnel, or turn it off and on, to try again.";
                    error = true;
                    break;
                case Sluice.Core.Tunnels.SessionState.Idle when prev == Sluice.Core.Tunnels.SessionState.Connected:
                    title = $"{name} disconnected";
                    text = $"{st.Detail}.";
                    break;
            }
        }
        if (title is not null && Settings.TunnelNotifications)
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => TunnelNotice?.Invoke(title, text!, error));
    }

    /// <summary>Applies the tunnel settings (idle stop) to the engine.</summary>
    public void ApplyTunnelSettings() =>
        Engine.Backends.IdleTimeout = Settings.TunnelIdleMinutes > 0 ? TimeSpan.FromMinutes(Settings.TunnelIdleMinutes) : TimeSpan.Zero;

    /// <summary>Capture is redirecting connections but none arrive: Windows Firewall blocks Sluice.</summary>
    public event Action? FirewallBlocked;

    /// <summary>
    /// Adds a Windows Firewall rule allowing incoming connections to this Sluice.exe on every network type,
    /// replacing earlier allow/block choices for it. Only called when the user presses the button.
    /// Returns an error message, or null on success.
    /// </summary>
    public static string? AllowThroughFirewall()
    {
        var exe = Environment.ProcessPath!;
        string Run(string args)
        {
            var psi = new ProcessStartInfo("netsh.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? "" : output.Trim();
        }
        try
        {
            Run($"advfirewall firewall delete rule name=all program=\"{exe}\"");   // "no rules match" is fine
            var error = Run($"advfirewall firewall add rule name=\"Sluice\" dir=in action=allow program=\"{exe}\" enable=yes profile=any");
            return error.Length == 0 ? null : error;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public void Notify(string text) => Notice?.Invoke(text);

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
        Engine.Backends.HostKeyLearned += (tunnel, fingerprint, publicKey) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Profile.FindTunnel(tunnel.Id) is not { } t) return;
            var changed = false;
            if (!t.Settings.TryGetValue("hostkey", out var pinned)) { t.Settings["hostkey"] = fingerprint; pinned = fingerprint; changed = true; }
            // The full key (needed by sing-box's SSH client) is only added for the server already pinned.
            if (publicKey is not null && pinned == fingerprint && (!t.Settings.TryGetValue("hostpubkey", out var old) || old != publicKey))
            {
                t.Settings["hostpubkey"] = publicKey;
                changed = true;
            }
            if (changed) CommitProfileEdit(notify: false);
        });
        ApplyFileLog();
        Engine.Backends.StatusChanged += OnTunnelStatus;
        ApplyTunnelSettings();
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
        ReloadEngine();
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
        ReloadEngine();
        ProfileChanged?.Invoke();
    }

    public void NewProfile()
    {
        Profile = Profile.CreateDefault();
        ProfilePath = null;
        IsDirty = true;
        ReloadEngine();
        ProfileChanged?.Invoke();
    }

    /// <summary>Call after editing <see cref="Profile"/>: applies it live and autosaves if it has a file.</summary>
    /// <param name="notify">False when the caller already reflects the change (e.g. a toggle in a list row).</param>
    public void CommitProfileEdit(bool notify = true)
    {
        ReloadEngine();
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

    /// <summary>Applies <see cref="Profile"/> to the engine; restarts capture when the UDP mode needs another filter.</summary>
    private void ReloadEngine()
    {
        Engine.Reload(Profile);
        if (_interceptor is { } i && i.NeedsRestartFor(Profile)) _ = RestartInterceptionAsync();
        else if (_interceptor is not null) Engine.WarmUpTunnels();   // rules may now use other tunnels
    }

    private async Task RestartInterceptionAsync()
    {
        Tracker.Write(LogLevel.Normal, "UDP mode changed: restarting capture");
        await StopInterceptionAsync();
        if (StartInterception() is { } error) Tracker.Write(LogLevel.Error, $"Could not restart capture: {error}");
    }

    /// <summary>Turns WinDivert interception on. Returns an error message, or null on success.</summary>
    public string? StartInterception()
    {
        if (_interceptor is not null) return null;
        if (!IsElevated) return "Interception needs administrator rights.";
        var i = new Interceptor(Engine);
        try
        {
            i.RelayUnreachable += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => FirewallBlocked?.Invoke());
            i.Start();
        }
        catch (Exception ex)
        {
            _ = i.DisposeAsync();
            return ex.Message;
        }
        _interceptor = i;
        StateChanged?.Invoke();
        Engine.WarmUpTunnels();   // connect the rules' tunnels now, not on the first captured connection
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

    /// <summary>Extra diagnostic log file for this run only (--log-file DIR); not saved in settings.</summary>
    public void StartDiagnosticLog(string directory)
    {
        Tracker.Level = LogLevel.Debug;
        _diagLog = new FileLog(Tracker, directory, LogLevel.Debug);
    }

    private FileLog? _diagLog;

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
