using Avalonia;
using Sluice.Desktop.Services;

namespace Sluice.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        if (UseInProcessFilePicker())
            builder = builder.With(new X11PlatformOptions { UseDBusFilePicker = false });
        return builder;
    }

    /// <summary>
    /// True on Linux when the app runs with capture rights (the <c>setcap</c> capabilities <c>install.sh</c> gives it, or
    /// root). The xdg-desktop-portal file chooser then refuses Sluice ("Unable to open /proc/&lt;pid&gt;/root"): a
    /// process with file capabilities isn't dumpable, so the portal can't look at it. Avalonia's in-process (GTK) file
    /// dialog doesn't need that, so it is used instead (#25). Without capture rights the portal is kept.
    /// </summary>
    private static bool UseInProcessFilePicker()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { return OsPlatform.Current.HasCaptureRights; }
        catch (Exception) { return false; }      // never keep the app from starting over a dialog choice
    }
}
