using Avalonia.Threading;
using Sluice.Core.Engine;

namespace Sluice.Desktop.Services;

/// <summary>
/// The last resort for an error nothing else caught (#24): it is logged, capture is stopped cleanly (on Linux that
/// removes the nftables table, so name lookups and connections keep working), and the app exits. Without this a crash
/// during capture could leave the capture rules behind.
/// <list type="bullet">
/// <item>An exception on the UI thread (<see cref="Dispatcher.UnhandledException"/>): handled here, then the app exits
/// with code 1.</item>
/// <item>An exception on any other thread (<see cref="AppDomain.UnhandledException"/>): the runtime ends the process
/// after the handlers, so this only logs and stops capture first.</item>
/// <item>A task whose exception nobody looked at (<see cref="TaskScheduler.UnobservedTaskException"/>): .NET ignores it
/// and the app keeps working, so it is only logged; closing the app for it would turn a harmless background failure
/// into a crash.</item>
/// </list>
/// </summary>
internal static class CrashGuard
{
    private static EngineHost? _host;
    private static int _installed, _handling;

    public static void Install(EngineHost host)
    {
        _host = host;
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;   // exit in order below instead of tearing down the UI loop
            Handle(e.Exception, "on the UI thread");
            RemoveTrayIcon();
            Environment.Exit(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Handle(e.ExceptionObject as Exception, "in the background");
            RemoveTrayIcon();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log(LogLevel.Error, $"A background task failed and nothing handled it: {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };
    }

    /// <summary>Logs the error and stops capture, once (a second error while handling the first changes nothing).</summary>
    private static void Handle(Exception? ex, string where)
    {
        if (Interlocked.Exchange(ref _handling, 1) != 0) return;
        Log(LogLevel.Error, $"Sluice is closing after an unexpected error {where}: {ex}");
        if (_host is not { Intercepting: true } host) return;
        Log(LogLevel.Normal, host.StopInterceptionForCrash(TimeSpan.FromSeconds(5))
            ? "Capture stopped before closing."
            : "Capture could not be stopped in time; its rules may be left behind.");
    }

    /// <summary>
    /// The app ends without <c>ExitApp</c>, which would remove the tray icon: on Windows a dead icon would stay in the
    /// notification area until the mouse passes over it. The icon belongs to the UI thread, and Avalonia removes it in
    /// its next pass of pending UI work, so that pass is run here before the app ends. From another thread this waits
    /// at most a second.
    /// </summary>
    private static void RemoveTrayIcon()
    {
        static void Remove()
        {
            AppTray.Current?.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) Remove();
            else Dispatcher.UIThread.InvokeAsync(Remove).GetTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception) { /* closing anyway */ }
    }

    private static void Log(LogLevel level, string message)
    {
        try { _host?.Tracker.Write(level, message); }
        catch (Exception) { /* nothing more to do */ }
        Console.Error.WriteLine($"Sluice: {message}");
    }
}
