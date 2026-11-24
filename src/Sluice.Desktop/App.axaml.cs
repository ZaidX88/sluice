using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
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
        else
            StartInDesktopTheme(askAgainLater: Arg(args, "--screenshot") is null);

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
        CrashGuard.Install(_host);   // from here an unexpected error stops capture before the app exits (#24)
        var vm = new MainViewModel(_host);
        _window = new MainWindow(vm, settings);
        desktop.MainWindow = _window;
        _tray = new AppTray(this, vm, ShowWindow, ExitApp);
        _host.TunnelNotice += (title, text, error) => OsPlatform.Current.Notify(title, text, error);
        _window.Show();
        base.OnFrameworkInitializationCompleted();

        // --profile opens that file as it is; otherwise the last profile, copied into Sluice's profiles folder once if it
        // was elsewhere (#134), or the first profile there, or a new "Default".
        // If it can't be opened (e.g. its master password is cancelled), Sluice runs an empty profile without a file:
        // that sends everything Direct, so capture is not started on launch and the banner says why (#248).
        var profileArg = Arg(args, "--profile") is { } p && File.Exists(p) ? p : null;
        var startCapture = args.Contains("--intercept") || settings.StartInterceptionOnLaunch;
        string? asked = null;
        string? notOpened = null;
        var profileOpen = false;
        try
        {
            var note = _host.OpenStartupProfile(profileArg, (name, confirm) =>
            {
                asked = name;
                return Dialogs.AskMasterPassword(name, confirm);
            });
            profileOpen = true;
            if (note is not null) vm.ShowInfo(note);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { notOpened = ex.Message; }
        if (!profileOpen) vm.ShowProfileNotOpened(asked, notOpened, startCapture);

        if (Arg(args, "--log-file") is { } diagDir) _host.StartDiagnosticLog(diagDir);
        _host.StartSocks();
        if (startCapture && profileOpen) vm.TurnOn();
        if (Arg(args, "--crash-test") is { } where) CrashTest(where);
        vm.StartUpdateChecks();   // about a minute from now, then daily, while the setting is on (#198)
    }

    /// <summary>
    /// Test only: an unexpected error 15 s after start (time to check capture is on), to see <see cref="CrashGuard"/>
    /// stop capture before the app exits. <c>ui</c>: on the UI thread; <c>thread</c>: on a thread of its own;
    /// <c>task</c>: a failed task nobody looks at (logged, the app keeps running).
    /// </summary>
    private static void CrashTest(string where) => DispatcherTimer.RunOnce(() =>
    {
        var error = new InvalidOperationException($"crash test ({where})");
        switch (where)
        {
            case "thread": new Thread(() => throw error) { IsBackground = true }.Start(); break;
            case "task": _ = Task.Run(() => throw error); Task.Delay(500).ContinueWith(_ => { GC.Collect(); GC.WaitForPendingFinalizers(); }); break;
            default: Dispatcher.UIThread.Post(() => throw error); break;   // a dispatcher operation, like a click handler
        }
    }, TimeSpan.FromSeconds(15));

    /// <summary>True when another Sluice of this user runs; it is asked to show its window (#171).</summary>
    private static async Task<bool> SignalRunningInstanceAsync()
    {
        await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        try
        {
            await client.ConnectAsync(300);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false;   // nobody listening: we are the first instance
        }
        catch (UnauthorizedAccessException)
        {
            return true;    // the pipe exists but we may not use it: another Sluice owns it
        }
        // Connected, so another Sluice runs, whatever happens to the request: a failed write must not start a second
        // copy (the running one may close the pipe early).
        try
        {
            await client.WriteAsync("show"u8.ToArray());
            await client.FlushAsync();
        }
        catch (IOException) { }
        return true;
    }

    private async Task ListenForShowRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = CreatePipeServer();
                await server.WaitForConnectionAsync(ct);
                // Read the request to its end before closing, so the other side's write never meets a closed pipe.
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(TimeSpan.FromSeconds(2));
                    var buffer = new byte[16];
                    try { while (await server.ReadAsync(buffer, wait.Token) > 0) { } }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    catch (IOException) { }
                }
                Dispatcher.UIThread.Post(ShowWindow);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500, ct);   // e.g. a second instance's pipe is still closing: try again
            }
        }
    }

    /// <summary>
    /// The single-instance pipe. On Windows it also lets the same user connect from a process without admin rights:
    /// with the default security a normal launch can't reach a Sluice running as administrator, and would start a
    /// second copy (#171).
    /// </summary>
    private static NamedPipeServerStream CreatePipeServer()
    {
        if (OperatingSystem.IsWindows() && WindowsIdentity.GetCurrent().User is { } user)
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }
        return new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
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

    /// <summary>Opens a web page or a download link in the user's browser.</summary>
    public static void OpenUrl(Uri uri)
    {
        if (Top?.Launcher is { } launcher) _ = launcher.LaunchUriAsync(uri);
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        if (Top?.Launcher is { } launcher) _ = launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    // ---------- the desktop's theme (#268, #253's Coordination) ----------

    /// <summary>Set once the desktop has changed its colours since start-up: from then on Avalonia follows it.</summary>
    private bool _followingDesktop;

    /// <summary>How long to wait before asking the platform a second time when its first answer was null.</summary>
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts in the theme the desktop asks for, as the platform reads it for the signed-in user
    /// (<see cref="Sluice.Core.Platform.IPlatformServices.PrefersDarkTheme"/>). With raised rights, Avalonia can't see it
    /// itself (Linux: the portal refuses a process with capabilities; Windows: an elevated administrator's own theme), so
    /// Sluice would open light on a dark desktop. On the first change of the desktop's colours the requested variant goes
    /// back to <see cref="ThemeVariant.Default"/>, so Avalonia follows the desktop as before. A platform that answers null
    /// changes nothing; at login the portal may not be up yet, so a null is asked again once, a few seconds later.
    /// </summary>
    private void StartInDesktopTheme(bool askAgainLater)
    {
        if (PlatformSettings is { } settings) settings.ColorValuesChanged += OnDesktopColorsChanged;
        var dark = Sluice.Core.Platform.PlatformServices.Current.PrefersDarkTheme();
        if (dark is not null) RequestedThemeVariant = dark.Value ? ThemeVariant.Dark : ThemeVariant.Light;
        else if (askAgainLater) _ = AskForDesktopThemeAgainAsync();
    }

    private async Task AskForDesktopThemeAgainAsync()
    {
        try
        {
            await Task.Delay(AskAgainAfter);
            var dark = await Task.Run(() => Sluice.Core.Platform.PlatformServices.Current.PrefersDarkTheme());
            if (dark is null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_followingDesktop && RequestedThemeVariant == ThemeVariant.Default)
                    RequestedThemeVariant = dark.Value ? ThemeVariant.Dark : ThemeVariant.Light;
            });
        }
        catch (Exception) { /* a convenience: the app keeps Avalonia's theme */ }
    }

    private void OnDesktopColorsChanged(object? sender, PlatformColorValues values)
    {
        if (PlatformSettings is { } settings) settings.ColorValuesChanged -= OnDesktopColorsChanged;
        Dispatcher.UIThread.Post(() =>
        {
            _followingDesktop = true;
            RequestedThemeVariant = ThemeVariant.Default;
        });
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
