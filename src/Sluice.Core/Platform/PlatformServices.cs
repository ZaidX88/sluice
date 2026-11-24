using Sluice.Core.Interception;

namespace Sluice.Core.Platform;

/// <summary>
/// How Core code reaches the OS module (docs/architecture/platforms.md §6.1). The desktop app and the CLI call
/// <see cref="Use"/> once at start-up, before anything else; until then <see cref="Current"/> is a platform that does
/// nothing (tests, tools).
/// </summary>
public static class PlatformServices
{
    private static volatile IPlatformServices _current = new NoPlatform();

    /// <summary>The OS module in use.</summary>
    public static IPlatformServices Current => _current;

    /// <summary>Makes <paramref name="platform"/> the one in use and points <see cref="ProcessLookup.Backend"/> at its
    /// process lookup (when it has one).</summary>
    public static void Use(IPlatformServices platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        _current = platform;
        if (platform.ProcessLookup is { } lookup) ProcessLookup.Backend = lookup;
    }

    /// <summary>The default before <see cref="Use"/>: no OS features, capture unavailable.</summary>
    internal sealed class NoPlatform : IPlatformServices
    {
        public string Name => "none";
        public void OnAppStart() { }
        public void Notify(string title, string message, bool isError) { }
        public string? CaptureUnavailableReason => "No platform module is loaded.";
        public bool HasCaptureRights => false;
        public string CaptureRightsHint => "";
        public bool CanRestartElevated => false;
        public ITrafficCapture CreateCapture(Engine.RoutingEngine engine) =>
            throw new PlatformNotSupportedException("No platform module is loaded.");
        public bool? PrefersDarkTheme() => null;                  // not known: the app leaves the theme to Avalonia
    }
}
