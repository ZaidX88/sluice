using System.Diagnostics;
using System.Globalization;
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

    /// <summary>Process lookups (rules by application) read /proc. Notifications need no registration.</summary>
    public void OnAppStart() => ProcessLookup.Backend = Lookup;

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

    public ITrafficCapture CreateCapture(RoutingEngine engine) => new LinuxCapture(engine, Lookup);

    internal const string RightsHint =
        "Capturing traffic needs root, or these capabilities on the Sluice binary: " +
        "sudo setcap cap_net_admin,cap_setgid,cap_sys_ptrace,cap_dac_read_search+ep Sluice.Desktop (the Linux package's install.sh does this).";

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
