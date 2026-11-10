using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sluice.App.Services;
using Sluice.App.ViewModels;

namespace Sluice.App;

public partial class App : Application
{
    private const string MutexName = "Local\\Sluice.SingleInstance";
    private const string ShowEventName = "Local\\Sluice.ShowWindow";

    private Mutex? _single;
    private EventWaitHandle? _showEvent;
    private EngineHost? _host;
    private TrayIcon? _tray;
    private MainWindow? _window;

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Windows names notifications by the app identity; without one it shows a random GUID.
        try { SetCurrentProcessExplicitAppUserModelID("Sluice"); } catch { /* cosmetic */ }
        base.OnStartup(e);
        var args = e.Args;
        string? screenshotDir = Arg(args, "--screenshot");
        var forcedTheme = Arg(args, "--theme");

        ApplyTheme(forcedTheme is null ? SystemUsesDarkTheme() : forcedTheme.Equals("dark", StringComparison.OrdinalIgnoreCase));
        WindowChrome.RegisterForAllWindows();

        if (screenshotDir is not null)
        {
            await ScreenshotMode.RunAsync(screenshotDir, args.Contains("--direct-only"));
            Shutdown();
            return;
        }

        // One Sluice per user session. An elevated restart may briefly overlap the old instance.
        _single = new Mutex(false, MutexName);
        bool owned;
        try { owned = _single.WaitOne(TimeSpan.FromSeconds(args.Contains("--intercept") ? 8 : 0)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            // Already running: ask that instance to show its window, then quietly exit.
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            show.Set();
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        var settings = AppSettings.Load();
        _host = new EngineHost(settings);
        var vm = new MainViewModel(_host);
        _window = new MainWindow(vm, settings);
        MainWindow = _window;
        _tray = new TrayIcon(vm, ShowWindow, ExitApp);
        _host.TunnelNotice += (title, text, error) => _tray?.Notify(title, text, error);

        _window.Show();

        var profilePath = Arg(args, "--profile") ?? settings.LastProfilePath;
        if (profilePath is not null && File.Exists(profilePath))
        {
            try
            {
                var note = _host.LoadProfile(profilePath, (name, confirm) => Dialogs.AskMasterPassword(name, confirm));
                if (note is not null) vm.ShowInfo(note);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { vm.Banner = $"Could not open {Path.GetFileName(profilePath)}: {ex.Message}"; }
        }

        if (Arg(args, "--log-file") is { } diagDir) _host.StartDiagnosticLog(diagDir);
        _host.StartSocks();
        if (args.Contains("--intercept") || settings.StartInterceptionOnLaunch) vm.TurnOn();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void ExitApp()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        _tray?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
        _single?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ExitApp();
        base.OnSessionEnding(e);
    }

    /// <summary>Use the window close button to exit when tray mode is off.</summary>
    public static void OnMainWindowClosed() => (Current as App)?.ExitApp();

    // ---------------- theme ----------------

    public static bool IsDark { get; private set; }

    public static void ApplyTheme(bool dark)
    {
        IsDark = dark;
        var app = Current;
        // Order matters (later dictionaries win): Fluent theme for standard controls, then our palette, then our styles.
        var mode = dark ? "Dark" : "Light";
        var merged = app.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{mode}.xaml", UriKind.Absolute),
        });
        merged.Add(new ResourceDictionary { Source = new Uri($"Theme/{mode}.xaml", UriKind.Relative) });
        merged.Add(new ResourceDictionary { Source = new Uri("Theme/Styles.xaml", UriKind.Relative) });
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
