using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Platform;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Platform.Linux;

/// <summary>Linux implementations of the platform services.</summary>
public sealed class LinuxPlatform : IPlatformServices
{
    public string Name => "Linux";

    private static readonly LinuxProcessLookup Lookup = new();

    /// <summary>Nothing to set up: notifications need no registration, and <see cref="PlatformServices.Use"/> sets the
    /// process lookup from <see cref="ProcessLookup"/>.</summary>
    public void OnAppStart() { }

    /// <summary>Process lookups (rules by application) read /proc (sock_diag when it can).</summary>
    public IProcessLookup? ProcessLookup => Lookup;

    // ---------- helpers end with Sluice (#57) ----------

    private static readonly Lazy<HelperTies?> Ties = new(HelperTies.ForThisProcess);
    private static int _exitHooked;
    private static PosixSignalRegistration[]? _exitSignals;

    /// <summary>
    /// Created once at start-up. First clears the capabilities the launcher passed on (ambient, inheritable) on this
    /// thread, so the threads Sluice creates from here start without them. That can't reach threads the runtime made
    /// earlier, so every program start clears them again (<see cref="Native.Libc.ClearPassedOn"/>). Then ends the
    /// sing-box helpers left by an earlier Sluice that was killed with SIGKILL or crashed (their SSH sessions would stay
    /// open), in the background: start-up doesn't wait.
    /// </summary>
    public LinuxPlatform()
    {
        Native.Libc.ClearPassedOn(out _);
        _ = Task.Run(() => Ties.Value?.EndLeftovers());
    }

    /// <summary>
    /// <paramref name="process"/> (a helper Sluice just started) gets SIGTERM when Sluice exits on a signal or an
    /// unhandled exception, and a Sluice killed with SIGKILL leaves a note so the next one ends it (<see cref="HelperTies"/>).
    /// </summary>
    public void TieChildProcess(Process process)
    {
        if (Ties.Value is not { } ties) return;
        int pid;
        try
        {
            if (process.HasExited) return;
            pid = process.Id;
        }
        catch (InvalidOperationException) { return; }   // not started
        HookExit(ties);
        ties.Tie(pid);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => ties.Untie(pid);
    }

    /// <summary>
    /// Right before a helper (sing-box) starts on this thread: clears the capabilities the launcher passed on, so the
    /// helper gets none. Throws when they can't be cleared, so the helper isn't started with them.
    /// </summary>
    public void PrepareHelperStart()
    {
        if (!Native.Libc.ClearPassedOn(out var problem))
            throw new InvalidOperationException($"Sluice's capabilities could not be kept from it ({problem}).");
    }

    /// <summary>On SIGTERM, SIGINT and SIGHUP (kill, logout, Ctrl+C), an unhandled exception, or an exit that didn't stop
    /// the tunnels first, the tied helpers get SIGTERM. The signal then ends Sluice as usual.</summary>
    private static void HookExit(HelperTies ties)
    {
        if (Interlocked.Exchange(ref _exitHooked, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ties.EndAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => ties.EndAll();
        try
        {
            _exitSignals = [.. new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP }
                .Select(s => PosixSignalRegistration.Create(s, _ => ties.EndAll()))];
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException) { /* ProcessExit still runs on SIGTERM */ }
    }

    /// <summary>Drops systemd-resolved's cached answers (<c>resolvectl flush-caches</c>). Never throws.</summary>
    public void FlushDnsCache() => LinuxCapture.FlushResolverCache();

    /// <summary>
    /// Desktop notification through notify-send (libnotify), which every common desktop provides; it talks to the
    /// org.freedesktop.Notifications service. Silently skipped when it is missing.
    /// </summary>
    public void Notify(string title, string message, bool isError)
    {
        try
        {
            var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("--app-name=Sluice");
            psi.ArgumentList.Add($"--urgency={(isError ? "critical" : "normal")}");
            psi.ArgumentList.Add("--icon=network-vpn");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(message);
            if (!Native.Libc.ClearPassedOn(out _)) return;   // notify-send gets no capabilities, or doesn't start
            using var p = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // notify-send not installed: notifications are a convenience
        }
    }

    // ---------- traffic capture ----------

    public string? CaptureUnavailableReason =>
        NftRules.FindNft() is null ? "Capturing traffic needs nftables: install the \"nftables\" package." : null;

    public bool HasCaptureRights => CheckCaptureRights();

    public string CaptureRightsHint
    {
        get
        {
            if (HasCaptureRights || AppBinary is null) return RightsHint;
            if (Launcher is not { } launcher)
                return "Capturing traffic needs Sluice's launcher (the \"sluice\" file next to Sluice.Desktop), which this copy doesn't have. " +
                    "Install Sluice with the Linux package's install.sh.";
            if (LinuxCaptureRights.HasFileCapabilities(launcher))
                return $"This copy of Sluice has capture rights: start it with {launcher} to use them.";
            if (CanGrantCaptureRights)
                return $"Capturing traffic needs capture rights for this copy of Sluice. Grant them (this asks for an administrator password), then start Sluice with {launcher}.";
            if (ChangeablePath is { } path)
                return $"Capturing traffic needs capture rights, and this copy of Sluice can't get them from here: {path} can be changed without " +
                    "administrator rights, so anything running as you could change what gets them. Install Sluice with the Linux package's install.sh.";
            return RightsHint;
        }
    }

    // ---------- capture rights and autostart (#69, platforms.md §6.6) ----------

    /// <summary>This copy's own binary (null when Sluice runs through <c>dotnet Sluice.Desktop.dll</c>).</summary>
    private static readonly string? AppBinary = LinuxCaptureRights.AppBinary(Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The launcher next to it, which gets the capabilities (null: this copy has none).</summary>
    private static readonly string? Launcher = AppBinary is { } b ? LinuxCaptureRights.Launcher(b) : null;

    /// <summary>
    /// The first path someone other than root could change, where the launcher would then refuse to run (the launcher
    /// itself, its folder, the folders above it, the .NET folder: <see cref="LinuxCaptureRights.FirstChangeablePath"/>).
    /// Read once for the banner; <see cref="TryGrantCaptureRightsAsync"/> checks again right before the prompt.
    /// </summary>
    private static readonly Lazy<string?> ChangeableAppPath = new(() => Launcher is { } launcher ? FirstChangeable(launcher) : null);

    private static string? FirstChangeable(string launcher) =>
        LinuxCaptureRights.FirstChangeablePath(launcher, LinuxCaptureRights.FirstLine, LinuxCaptureRights.FileSystemView.Real);

    private static string? ChangeablePath => ChangeableAppPath.Value;

    /// <summary>
    /// True when this copy has no capture rights yet and can get them safely: it runs from its own binary, the launcher
    /// is next to it (not a link), root alone can change its folder, the folders above it and the .NET folder, and pkexec
    /// and setcap exist.
    /// </summary>
    public bool CanGrantCaptureRights =>
        !HasCaptureRights && Launcher is { } launcher && ChangeablePath is null
        && LinuxCaptureRights.FindTool("pkexec") is not null && LinuxCaptureRights.FindTool("setcap") is not null
        && !LinuxCaptureRights.HasFileCapabilities(launcher);

    /// <summary>
    /// <c>pkexec setcap</c> on the launcher's real path, and <c>-r</c> on the apps if they still carry capabilities, after
    /// checking the folders again. Sluice must be started again through the launcher to use them. Never throws.
    /// </summary>
    public Task<bool> TryGrantCaptureRightsAsync()
    {
        try
        {
            if (!CanGrantCaptureRights || AppBinary is not { } binary || Launcher is not { } launcher
                || LinuxCaptureRights.FindTool("pkexec") is not { } pkexec || LinuxCaptureRights.FindTool("setcap") is not { } setcap)
                return Task.FromResult(false);
            // Again, right before the prompt: the banner can stay up for a long time. Once root alone can change every
            // folder on the way, nothing can be swapped between this check and setcap, which gets the real paths.
            if (FirstChangeable(launcher) is not null || LinuxCaptureRights.ResolveRealPath(launcher) is not { } real)
                return Task.FromResult(false);
            var clear = LinuxCaptureRights.AppsWithCapabilities(Path.Combine(Path.GetDirectoryName(real)!, Path.GetFileName(binary)),
                LinuxCaptureRights.HasAnyFileCapabilities);
            return LinuxCaptureRights.GrantAsync(pkexec, setcap, real, LinuxCaptureRights.SetcapArguments(real, clear));
        }
        catch (SystemException) { return Task.FromResult(false); }
    }

    /// <summary>The entry starts the launcher when there is one, so Sluice has its capture rights after login.</summary>
    private static readonly LinuxAutostart Login = new(
        LinuxAutostart.EntryPathFor(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        Launcher ?? AppBinary);

    /// <summary>Start at login through <c>~/.config/autostart/sluice.desktop</c>, when Sluice runs from its own binary.</summary>
    public bool AutostartSupported => Login.Supported;

    public bool Autostart
    {
        get => Login.Enabled;
        set => Login.Enabled = value;
    }

    /// <summary>A GUI cannot simply relaunch itself as root on Linux (sudo needs a terminal, and root apps lose the desktop session).</summary>
    public bool CanRestartElevated => false;

    public ITrafficCapture CreateCapture(RoutingEngine engine)
    {
        Lookup.Log = m => engine.Tracker.Write(LogLevel.Normal, m);     // e.g. sock_diag unavailable: owners through /proc
        return new LinuxCapture(engine, Lookup);
    }

    internal const string RightsHint =
        "Capturing traffic needs root, or Sluice started through its launcher (the sluice command) with these capabilities: " +
        "sudo setcap cap_net_admin,cap_setgid,cap_sys_ptrace,cap_dac_read_search+ep /opt/sluice/sluice (the Linux package's install.sh does this).";

    private const int CapSetGid = 6, CapNetAdmin = 12;

    /// <summary>Root, or CAP_NET_ADMIN (nftables, NFQUEUE) plus CAP_SETGID (Sluice's network group) in the effective set.</summary>
    internal static bool CheckCaptureRights()
    {
        if (Environment.IsPrivilegedProcess) return true;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("CapEff:", StringComparison.Ordinal)) continue;
                var caps = ulong.Parse(line["CapEff:".Length..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return (caps & (1UL << CapNetAdmin)) != 0 && (caps & (1UL << CapSetGid)) != 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { }
        return false;
    }
}
