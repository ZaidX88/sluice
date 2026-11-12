using Sluice.Core.Platform;

namespace Sluice.Desktop.Services;

/// <summary>The OS module this build was made for (chosen at compile time by the target framework).</summary>
public static class OsPlatform
{
    public static IPlatformServices Current { get; } =
#if WINDOWS
        new Sluice.Platform.Windows.WindowsPlatform();
#else
        new Sluice.Platform.Linux.LinuxPlatform();
#endif
}
