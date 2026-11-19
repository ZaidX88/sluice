namespace Sluice.Core.Platform;

/// <summary>
/// What differs between operating systems. Each OS has its own module (Sluice.Platform.Windows,
/// Sluice.Platform.Linux) implementing this; everything else lives in Sluice.Core and is shared.
/// More services move here as the platform split continues (traffic capture, process lookup, secret storage).
/// </summary>
public interface IPlatformServices
{
    /// <summary>"Windows", "Linux".</summary>
    string Name { get; }

    /// <summary>One-time setup when the desktop app starts (e.g. registering the app identity used by notifications).</summary>
    void OnAppStart();

    /// <summary>A desktop notification titled with the app's name and icon.</summary>
    void Notify(string title, string message, bool isError);

    // ---------- traffic capture ----------

    /// <summary>Null when this system can capture other apps' traffic; otherwise a sentence saying what is missing.</summary>
    string? CaptureUnavailableReason { get; }

    /// <summary>True when this process has the rights capture needs (administrator / root or capabilities).</summary>
    bool HasCaptureRights { get; }

    /// <summary>Tells the user how to get those rights, e.g. "…needs administrator rights."</summary>
    string CaptureRightsHint { get; }

    /// <summary>True when the app can relaunch itself with those rights (Windows UAC).</summary>
    bool CanRestartElevated { get; }

    /// <summary>A new, not yet started capture feeding <paramref name="engine"/>.</summary>
    Sluice.Core.Interception.ITrafficCapture CreateCapture(Sluice.Core.Engine.RoutingEngine engine);

    // ---------- the Core split (docs/architecture/platforms.md §6.2) ----------
    // Default members, so a platform that doesn't implement one yet keeps building and behaves as before.

    /// <summary>"Which app owns this connection" for this OS. Null: no lookups (connections have PID 0).</summary>
    IProcessLookup? ProcessLookup => null;

    /// <summary>Makes <paramref name="process"/> (a helper Sluice just started) end when Sluice ends, even on a crash.</summary>
    void TieChildProcess(System.Diagnostics.Process process) { }

    /// <summary>Drops the OS's cached DNS answers. Never throws.</summary>
    void FlushDnsCache() { }

    /// <summary>Protects secrets with the current OS account (Windows DPAPI). Null where there is none.</summary>
    Sluice.Core.Profiles.ISecretProtector? SecretProtector => null;
}
