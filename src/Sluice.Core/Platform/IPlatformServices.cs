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

    /// <summary>
    /// Called on the thread that is about to start a helper program (sing-box), right before <c>Process.Start</c> and
    /// with nothing awaited in between, so the platform can set up state that the new process takes from the thread
    /// that starts it. Throwing stops the start: the helper isn't started and the tunnel fails with the message.
    /// </summary>
    void PrepareHelperStart() { }

    /// <summary>Drops the OS's cached DNS answers. Never throws.</summary>
    void FlushDnsCache() { }

    /// <summary>Protects secrets with the current OS account (Windows DPAPI). Null where there is none.</summary>
    Sluice.Core.Profiles.ISecretProtector? SecretProtector => null;

    // ---------- capture rights and autostart (#68; #50's Coordination) ----------
    // Default members: a platform that doesn't offer these keeps building, and the UI hides them.

    /// <summary>True when <see cref="TryGrantCaptureRightsAsync"/> can give this copy of Sluice the rights capture needs
    /// without the installer (Linux: pkexec + setcap on the running binary). The UI shows its button only then.</summary>
    bool CanGrantCaptureRights => false;

    /// <summary>Asks the user (the OS's own password prompt) to give this copy the rights capture needs. True when it
    /// worked; the app may have to restart to use them, which the platform's <see cref="CaptureRightsHint"/> says.
    /// False when the user cancelled, it failed or it isn't supported. Never throws.</summary>
    System.Threading.Tasks.Task<bool> TryGrantCaptureRightsAsync() => System.Threading.Tasks.Task.FromResult(false);

    /// <summary>True when <see cref="Autostart"/> can start Sluice at login on this OS. The UI shows its switch only then.</summary>
    bool AutostartSupported => false;

    /// <summary>Whether Sluice starts at login for this user (Linux: <c>~/.config/autostart/sluice.desktop</c>). Setting
    /// it where <see cref="AutostartSupported"/> is false does nothing.</summary>
    bool Autostart { get => false; set { } }

    // ---------- the desktop's theme (#265; #253's Coordination) ----------
    // A default member, like the ones above: until a platform reads its desktop's theme (Linux #266, Windows #267), it
    // keeps building and answers null, so the app looks as it does today.

    /// <summary>The theme the desktop asks for, read the way an unprivileged process of the signed-in user would:
    /// true dark, false light, null not known (leave it to Avalonia). Never throws; returns within about a second.</summary>
    bool? PrefersDarkTheme() => null;
}
