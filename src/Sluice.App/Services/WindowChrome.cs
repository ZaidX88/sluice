using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sluice.App.Services;

/// <summary>Black native title bar on Windows 11 (DWM caption colour); a dark title bar on Windows 10.</summary>
public static class WindowChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Apply to every window the app creates.</summary>
    public static void RegisterForAllWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => Apply((Window)s)));

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = 1, black = 0x000000, white = 0xFFFFFF;   // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));  // white caption buttons
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref black, sizeof(int));          // Windows 11 22000+
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref white, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref black, sizeof(int));
    }
}
