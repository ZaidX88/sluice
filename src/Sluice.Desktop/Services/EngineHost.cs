using System.Diagnostics;
using System.IO;
using System.Net;
using Avalonia.Threading;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Interception;
using Sluice.Core.Profiles;

namespace Sluice.Desktop.Services;

/// <summary>
/// Owns the running engine for the UI: profile, local SOCKS listener, optional WinDivert
/// interception, statistics and file log. All methods are called on the UI thread.
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    private Socks5Inbound? _socks;
    private ITrafficCapture? _interceptor;
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
                // Not responding then fine again: it never really dropped, so stay quiet.
                case Sluice.Core.Tunnels.SessionState.Connected when prev is not (Sluice.Core.Tunnels.SessionState.Connected or Sluice.Core.Tunnels.SessionState.NotResponding):
                    var again = prev is Sluice.Core.Tunnels.SessionState.Reconnecting;
                    title = again ? $"{name} reconnected" : $"{name} connected";
                    text = $"{t.Type.DisplayName()} tunnel is up.";
                    break;
                case Sluice.Core.Tunnels.SessionState.Reconnecting when prev is Sluice.Core.Tunnels.SessionState.Connected or Sluice.Core.Tunnels.SessionState.NotResponding:
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
                case Sluice.Core.Tunnels.SessionState.Idle when prev is Sluice.Core.Tunnels.SessionState.Connected or Sluice.Core.Tunnels.SessionState.NotResponding:
                    title = $"{name} disconnected";
                    text = $"{st.Detail}.";
                    break;
            }
        }
        if (title is not null && Settings.TunnelNotifications)
            Dispatcher.UIThread.Post(() => TunnelNotice?.Invoke(title, text!, error));
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
        if (!OperatingSystem.IsWindows()) return "only Windows Firewall is supported";
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

    /// <summary>The rights capture needs: administrator on Windows; root or capabilities on Linux.</summary>
    public static bool IsElevated => OsPlatform.Current.HasCaptureRights;

    /// <summary>Why this system cannot capture traffic at all, or null.</summary>
    public static string? CaptureUnavailable => OsPlatform.Current.CaptureUnavailableReason;

    public EngineHost(AppSettings settings)
    {
        Settings = settings;
        Statistics = new Statistics(Tracker);
        Profile = Profile.CreateDefault();
        Settings.Network?.ApplyTo(Profile.Options);
        Engine = new RoutingEngine(Profile, Tracker);
        // Trust-on-first-use: remember an SSH server's host key in the profile (checked from then on).
        Engine.Backends.HostKeyLearned += (tunnel, fingerprint, publicKey) => Dispatcher.UIThread.Post(() =>
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

    public string ProfileName => ProfilePath is null ? DemoProfileName ?? "Untitled profile" : Path.GetFileNameWithoutExtension(ProfilePath);

    // ---------------- profile ----------------

    /// <summary>Opens a Sluice profile (<c>.sluice</c>). Other files are refused, so autosave never writes Sluice's
    /// format into a file of another kind.</summary>
    public void LoadProfile(string path, MasterPasswordProvider? askMaster)
    {
        if (!IsSluiceProfile(path))
            throw new FormatException($"Not a Sluice profile. Sluice opens {ProfileFile.Extension} files only.");
        var profile = ProfileFile.Load(path, askMaster);
        // The app's network options win over the file's; the first profile opened supplies them once (#148).
        Settings.NetworkFrom(profile.Options).ApplyTo(profile.Options);
        Profile = profile;
        ProfilePath = path;
        IsDirty = false;
        ReloadEngine();
        Settings.LastProfilePath = path;
        Settings.Save();
        ProfileChanged?.Invoke();
    }

    private static bool IsSluiceProfile(string path) =>
        path.EndsWith(ProfileFile.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Use an in-memory profile without a file (demo / screenshots).</summary>
    public void UseProfile(Profile profile)
    {
        Settings.Network?.ApplyTo(profile.Options);
        Profile = profile;
        ProfilePath = null;
        ReloadEngine();
        ProfileChanged?.Invoke();
    }

    // ---------------- the profiles folder (#134) ----------------

    /// <summary>Sluice's own profiles folder; every profile the menu offers lives there.</summary>
    public ProfileStore Profiles { get; init; } = ProfileStore.Default;

    /// <summary>Screenshot mode: the names the profile menu shows instead of the real folder's, and the open one.</summary>
    public IReadOnlyList<string>? DemoProfileNames { get; set; }
    public string? DemoProfileName { get; set; }

    /// <summary>The profiles the menu lists, sorted by name.</summary>
    public IReadOnlyList<string> ProfileNames() => DemoProfileNames ?? Profiles.List();

    /// <summary>The current profile is a file in the profiles folder (rename and delete need it).</summary>
    public bool ProfileInFolder => ProfilePath is not null ? Profiles.Contains(ProfilePath) : DemoProfileName is not null;

    /// <summary>
    /// The profile Sluice starts with: <paramref name="argPath"/> (<c>--profile</c>) as it is; otherwise the last one
    /// used. A last profile outside the profiles folder (from before #134) is copied in once and the copy is used; the
    /// original stays untouched. With nothing to open, the first profile in the folder, or a new "Default".
    /// </summary>
    /// <returns>A message for the user when a profile was copied in, otherwise null.</returns>
    public string? OpenStartupProfile(string? argPath, MasterPasswordProvider? askMaster)
    {
        if (argPath is not null)
        {
            LoadProfile(argPath, askMaster);
            return null;
        }
        if (Settings.LastProfilePath is { } last && File.Exists(last) && IsSluiceProfile(last))
        {
            string? note = null;
            if (!Profiles.Contains(last))
            {
                var copy = Profiles.CopyIn(last);
                note = $"Sluice now keeps its profiles in a folder of its own, so it copied \"{Path.GetFileNameWithoutExtension(last)}\" "
                       + $"there as \"{Path.GetFileNameWithoutExtension(copy)}\". Switch profiles from the profile menu. The original file is untouched.";
                Tracker.Write(LogLevel.Normal, $"Copied the profile {Path.GetFileName(last)} into the profiles folder as {Path.GetFileName(copy)}");
                last = copy;
            }
            LoadProfile(last, askMaster);
            return note;
        }
        if (ProfileNames() is [var first, ..])
        {
            LoadProfile(Profiles.PathFor(first), askMaster);
            return null;
        }
        NewProfile("Default");
        return null;
    }

    /// <summary>Switches to the profile called <paramref name="name"/>. A wrong or cancelled master password throws, and the
    /// current profile keeps running.</summary>
    public void SwitchTo(string name, MasterPasswordProvider? askMaster) => LoadProfile(Profiles.PathFor(name), askMaster);

    /// <summary>A new profile with the default rules, saved in the folder as <paramref name="name"/>, and switched to.</summary>
    public bool NewProfile(string name)
    {
        Profile = Profile.CreateDefault();
        Settings.Network?.ApplyTo(Profile.Options);
        ProfilePath = null;
        ReloadEngine();
        return TrySave(Profiles.PathFor(name));
    }

    /// <summary>Saves the current profile as a new one called <paramref name="name"/> and carries on with the copy.</summary>
    public bool SaveProfileAs(string name) => TrySave(Profiles.PathFor(name));

    /// <summary>Renames the current profile (a file in the profiles folder).</summary>
    public void RenameProfile(string name)
    {
        if (!ProfileInFolder) throw new InvalidOperationException("Only a profile in Sluice's profiles folder can be renamed.");
        var target = Profiles.PathFor(name);
        File.Move(ProfilePath!, target);
        ProfilePath = target;
        Settings.LastProfilePath = target;
        Settings.Save();
        ProfileChanged?.Invoke();
    }

    /// <summary>Deletes a profile that isn't the current one.</summary>
    public void DeleteProfile(string name)
    {
        var path = Profiles.PathFor(name);
        if (ProfilePath is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(ProfilePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Switch to another profile before deleting this one.");
        File.Delete(path);
        Tracker.Write(LogLevel.Normal, $"Deleted the profile {name}");
    }

    /// <summary>Copies a <c>.sluice</c> file from anywhere into the folder and switches to the copy. If it can't be
    /// opened (a wrong or cancelled master password), the copy is removed again.</summary>
    public void ImportProfile(string path, MasterPasswordProvider? askMaster)
    {
        if (!IsSluiceProfile(path))
            throw new FormatException($"Not a Sluice profile. Sluice opens {ProfileFile.Extension} files only.");
        var copy = Profiles.CopyIn(path);
        try { LoadProfile(copy, askMaster); }
        catch { File.Delete(copy); throw; }
    }

    /// <summary>
    /// Call after changing <see cref="AppSettings.Network"/>: saves the settings, copies them into the current profile
    /// (so its file, and <c>sluice-cli</c> running it, match the app) and applies them live.
    /// </summary>
    public void ApplyNetworkSettings()
    {
        Settings.NetworkFrom(Profile.Options).ApplyTo(Profile.Options);
        Settings.Save();
        CommitProfileEdit(notify: false);
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
            // Saving always writes Sluice's format, so the file always gets its extension.
            if (!IsSluiceProfile(path)) path = Path.ChangeExtension(path, ProfileFile.Extension);
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

    /// <summary>Turns capture on (WinDivert / nftables). Returns an error message, or null on success.</summary>
    public string? StartInterception()
    {
        if (_interceptor is not null) return null;
        if (CaptureUnavailable is { } why) return why;
        if (!IsElevated) return OsPlatform.Current.CaptureRightsHint;
        var i = OsPlatform.Current.CreateCapture(Engine);
        try
        {
            i.RelayUnreachable += () => Dispatcher.UIThread.Post(() => FirewallBlocked?.Invoke());
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

    /// <summary>
    /// The last resort after an unexpected error (<see cref="CrashGuard"/>): stops capture like
    /// <see cref="StopInterceptionAsync"/> (on Linux that removes the nftables table), but off the UI thread, without
    /// telling the UI, and waiting at most <paramref name="timeout"/>. False when it didn't finish in time or failed.
    /// Never throws.
    /// </summary>
    internal bool StopInterceptionForCrash(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _interceptor, null) is not { } i) return true;
        try { return Task.Run(async () => await i.DisposeAsync()).Wait(timeout); }
        catch (Exception) { return false; }
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
        if (!Settings.FileLogEnabled || Settings.WriteNoFiles) return;
        try
        {
            _fileLog = new FileLog(Tracker, Settings.LogDirectory, Settings.FileLogDetailed ? LogLevel.Verbose : LogLevel.Normal);
        }
        catch (Exception ex)
        {
            Tracker.Write(LogLevel.Error, $"File log disabled: {ex.Message}");
        }
    }

    public static void RestartElevated(string? extraArgs = null)
    {
        if (!OperatingSystem.IsWindows()) return;   // Linux: start it with sudo/pkexec yourself
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
