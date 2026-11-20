using Sluice.Core.Platform;

namespace Sluice.Desktop.Services;

/// <summary>The OS module this build was made for (chosen at compile time by the target framework).</summary>
public static class OsPlatform
{
    /// <summary>Replaced only by <see cref="ScreenshotMode"/> (a demo platform), before any view model reads it.</summary>
    public static IPlatformServices Current { get; internal set; } =
#if WINDOWS
        new Sluice.Platform.Windows.WindowsPlatform();
#else
        new Sluice.Platform.Linux.LinuxPlatform();
#endif
}
