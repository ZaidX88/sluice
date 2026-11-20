using Avalonia;
using Avalonia.Dialogs;
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
        if (UseManagedFileDialogs())
            builder = builder.With(new X11PlatformOptions { UseDBusFilePicker = false }).UseManagedSystemDialogs();
        return builder;
    }

    /// <summary>
    /// True on Linux when the app runs with capture rights (the capabilities the launcher passes on, or root). Then
    /// file dialogs are Avalonia's own (#126), drawn by Avalonia in the app's theme:
    /// <list type="bullet">
    /// <item>the xdg-desktop-portal file chooser refuses Sluice ("Unable to open /proc/&lt;pid&gt;/root"): it can't
    /// look at a process with more capabilities than itself;</item>
    /// <item>GTK's dialog (used before, #25) must not be loaded into a process with capture rights: it loads modules
    /// named in the user's own GTK settings, and it refuses to start or aborts there anyway.</item>
    /// </list>
    /// Without capture rights the portal is kept.
    /// </summary>
    internal static bool UseManagedFileDialogs()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (Environment.GetEnvironmentVariable("SLUICE_MANAGED_DIALOGS") == "1") return true;   // testing without rights
        try { return OsPlatform.Current.HasCaptureRights; }
        catch (Exception) { return false; }      // never keep the app from starting over a dialog choice
    }
}
