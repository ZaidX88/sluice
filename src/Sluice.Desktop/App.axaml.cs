using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Sluice.Desktop.Services;
using Sluice.Desktop.ViewModels;

namespace Sluice.Desktop;

public partial class App : Application
{
    private EngineHost? _host;
    private MainWindow? _window;
    private AppTray? _tray;
    private CancellationTokenSource? _singleCts;

    private static string PipeName => $"Sluice-{Environment.UserName}";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        Sluice.Core.Platform.PlatformServices.Use(OsPlatform.Current);   // first: Core reaches the OS module through it (#104)
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var args = desktop.Args ?? [];

        if (Arg(args, "--theme") is { } theme)
            RequestedThemeVariant = theme.Equals("dark", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Dark : ThemeVariant.Light;

        if (Arg(args, "--screenshot") is { } shotDir)
        {
            base.OnFrameworkInitializationCompleted();
            await ScreenshotMode.RunAsync(shotDir, args.Contains("--direct-only"));
            desktop.Shutdown();
            return;
        }

        // One Sluice per user: a second launch asks the running one to show its window, then exits.
        if (await SignalRunningInstanceAsync())
        {
            desktop.Shutdown();
            return;
        }
        _singleCts = new CancellationTokenSource();
        _ = ListenForShowRequestsAsync(_singleCts.Token);

        OsPlatform.Current.OnAppStart();   // e.g. the app identity Windows names notifications after

        var settings = AppSettings.Load();
        _host = new EngineHost(settings);
        var vm = new MainViewModel(_host);
        _window = new MainWindow(vm, settings);
        desktop.MainWindow = _window;
        _tray = new AppTray(this, vm, ShowWindow, ExitApp);
        _host.TunnelNotice += (title, text, error) => OsPlatform.Current.Notify(title, text, error);
        _window.Show();
        base.OnFrameworkInitializationCompleted();

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

    private static async Task<bool> SignalRunningInstanceAsync()
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await client.ConnectAsync(300);
            await client.WriteAsync("show"u8.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;   // nobody listening: we are the first instance
        }
    }

    private async Task ListenForShowRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                await server.WaitForConnectionAsync(ct);
                Dispatcher.UIThread.Post(ShowWindow);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(500, ct); }
        }
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
        _singleCts?.Cancel();
        if (_host is not null) await _host.DisposeAsync();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // ---------------- helpers used by view models ----------------

    public static App? CurrentApp => Current as App;

    public static bool IsDark => Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>Use the window close button to exit when tray mode is off.</summary>
    public static void OnMainWindowClosed() => CurrentApp?.ExitApp();

    public static void Exit() => CurrentApp?.ExitApp();

    private static TopLevel? Top => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static void SetClipboardText(string text)
    {
        if (Top?.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
    }

    public static Task<string?> GetClipboardTextAsync() =>
        Top?.Clipboard is { } clipboard ? clipboard.TryGetTextAsync() : Task.FromResult<string?>(null);

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        if (Top?.Launcher is { } launcher) _ = launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
