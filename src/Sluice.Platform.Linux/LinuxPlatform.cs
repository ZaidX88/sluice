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

    public string CaptureRightsHint => RightsHint;

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
