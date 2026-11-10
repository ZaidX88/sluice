using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Sluice.App.Mvvm;
using Sluice.App.Services;
using Sluice.Core.Engine;

namespace Sluice.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private readonly DispatcherTimer _timer;
    private PageViewModel _current;
    private bool _busy;
    private long _lastSent, _lastReceived;
    private DateTime _lastTick = DateTime.UtcNow;
    private string _upRate = "0 B/s", _downRate = "0 B/s", _status = "";
    private string? _banner;

    public MainViewModel(EngineHost host)
    {
        _host = host;
        Connections = new ConnectionsViewModel(host);
        Statistics = new StatisticsViewModel(host);
        Log = new LogViewModel(host);
        Tunnels = new TunnelsViewModel(host);
        Rules = new RulesViewModel(host);
        Settings = new SettingsViewModel(host);
        Pages = [Connections, Statistics, Log, Tunnels, Rules, Settings];
        NavGroups =
        [
            new NavGroup("Monitor", [Connections, Statistics, Log]),
            new NavGroup("Configure", [Tunnels, Rules]),
            new NavGroup("App", [Settings]),
        ];
        _current = Connections;
        Connections.IsCurrent = true;
        NavigateCommand = new RelayCommand(p => { if (p is PageViewModel page) CurrentPage = page; });
        ToggleCaptureCommand = new RelayCommand(() => Intercepting = !Intercepting);
        DisconnectAllCommand = new RelayCommand(() => _host.Tracker.DisconnectAll());
        ShowLogCommand = new RelayCommand(() => CurrentPage = Log);

        host.ProfileChanged += () =>
        {
            foreach (var p in Pages) p.OnProfileChanged();
            Raise(nameof(ProfileName));
            Raise(nameof(ProfileInitial));
            Raise(nameof(ProfileHint));
        };
        host.StateChanged += () => RaiseCapture();
        host.Notice += ShowInfo;

        OpenCommand = new RelayCommand(Open);
        SaveCommand = new RelayCommand(Save);
        SaveAsCommand = new RelayCommand(SaveAs);
        ExportProxifierCommand = new RelayCommand(ExportProxifier);
        NewCommand = new RelayCommand(New);
        DismissBannerCommand = new RelayCommand(() => Banner = null);
        RestartElevatedCommand = new RelayCommand(() =>
        {
            EngineHost.RestartElevated("--intercept");
            Application.Current.Shutdown();
        });

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        UpdateStatus();
    }

    public ConnectionsViewModel Connections { get; }
    public StatisticsViewModel Statistics { get; }
    public LogViewModel Log { get; }
    public TunnelsViewModel Tunnels { get; }
    public RulesViewModel Rules { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public IReadOnlyList<NavGroup> NavGroups { get; }

    public PageViewModel CurrentPage
    {
        get => _current;
        set
        {
            var old = _current;
            if (!Set(ref _current, value)) return;
            old.IsCurrent = false;
            value.IsCurrent = true;
            value.Refresh();
        }
    }

    public ICommand NavigateCommand { get; }
    public ICommand ToggleCaptureCommand { get; }
    public ICommand DisconnectAllCommand { get; }
    public ICommand ShowLogCommand { get; }

    /// <summary>Top-bar search: filters live connections (and jumps to that page).</summary>
    public string Search
    {
        get => Connections.Filter;
        set
        {
            Connections.Filter = value;
            Raise();
            if (!string.IsNullOrEmpty(value)) CurrentPage = Connections;
        }
    }

    /// <summary>Play / pause glyph on the round capture button.</summary>
    public string CaptureGlyph => _host.Intercepting ? "" : "";
    public string CaptureTitle => _host.Intercepting ? "Capturing traffic" : "Capture is off";

    public string ProfileName => _host.ProfileName;
    public string ProfileInitial => ProfileName.Length > 0 ? ProfileName[..1].ToUpperInvariant() : "S";

    // ----- traffic sparkline (last 60 s, like the player's progress line) -----
    private const int SparkSamples = 60;
    public const double SparkWidth = 360, SparkHeight = 28;
    private readonly Queue<double> _rates = new(Enumerable.Repeat(0.0, SparkSamples));
    private System.Windows.Media.PointCollection _sparkLine = new();
    private System.Windows.Media.PointCollection _sparkArea = new();
    private string _peak = "";
    public System.Windows.Media.PointCollection SparkLine { get => _sparkLine; private set => Set(ref _sparkLine, value); }
    public System.Windows.Media.PointCollection SparkArea { get => _sparkArea; private set => Set(ref _sparkArea, value); }
    public string Peak { get => _peak; private set => Set(ref _peak, value); }
    public string Totals => $"↑ {Format.Bytes(_host.Tracker.TotalSent)}  ↓ {Format.Bytes(_host.Tracker.TotalReceived)}";

    private void PushRate(double bytesPerSecond)
    {
        _rates.Dequeue();
        _rates.Enqueue(bytesPerSecond);
        var max = Math.Max(1024, _rates.Max());
        var line = new System.Windows.Media.PointCollection();
        var i = 0;
        foreach (var r in _rates)
        {
            line.Add(new Point(i * SparkWidth / (SparkSamples - 1), SparkHeight - 2 - r / max * (SparkHeight - 4)));
            i++;
        }
        var area = new System.Windows.Media.PointCollection(line) { new Point(SparkWidth, SparkHeight), new Point(0, SparkHeight) };
        line.Freeze();
        area.Freeze();
        SparkLine = line;
        SparkArea = area;
        Peak = $"peak {Format.Rate(_rates.Max())}";
        Raise(nameof(Totals));
    }
    public string ProfileHint => _host.ProfilePath ?? "Not saved yet — use Save as";

    public string SocksText => _host.SocksEndPoint is { } ep ? $"SOCKS5 {ep}" : "SOCKS5 off";

    /// <summary>The big switch. Turning it on may require elevation.</summary>
    public bool Intercepting
    {
        get => _host.Intercepting;
        set
        {
            if (_busy || value == _host.Intercepting) return;
            if (value) TurnOn(); else _ = TurnOffAsync();
        }
    }

    public bool IsElevated => EngineHost.IsElevated;

    public string UpRate { get => _upRate; private set => Set(ref _upRate, value); }
    public string DownRate { get => _downRate; private set => Set(ref _downRate, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Message shown at the top of the content area (errors, elevation prompt).</summary>
    public string? Banner { get => _banner; set { Set(ref _banner, value); Raise(nameof(HasBanner)); } }
    public bool HasBanner => _banner is not null;
    public bool BannerOffersElevation { get; private set; }

    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand ExportProxifierCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand DismissBannerCommand { get; }
    public ICommand RestartElevatedCommand { get; }

    public void TurnOn()
    {
        if (!EngineHost.IsElevated)
        {
            ShowBanner("Capturing other applications' traffic needs administrator rights.", offerElevation: true);
            Raise(nameof(Intercepting));
            return;
        }
        var error = _host.StartInterception();
        if (error is not null) ShowBanner($"Interception could not start: {error}");
        else Banner = null;
        RaiseCapture();
    }

    private async Task TurnOffAsync()
    {
        _busy = true;
        try { await _host.StopInterceptionAsync(); }
        finally { _busy = false; RaiseCapture(); }
    }

    private void RaiseCapture()
    {
        Raise(nameof(Intercepting));
        Raise(nameof(CaptureGlyph));
        Raise(nameof(CaptureTitle));
        Raise(nameof(SocksText));
        UpdateStatus();
    }

    private void ShowBanner(string text, bool offerElevation = false)
    {
        BannerOffersElevation = offerElevation;
        BannerIsError = true;
        Raise(nameof(BannerOffersElevation));
        Raise(nameof(BannerIsError));
        Banner = text;
    }

    /// <summary>Neutral notice (e.g. "imported from Proxifier") rather than an error.</summary>
    public void ShowInfo(string text)
    {
        BannerOffersElevation = false;
        BannerIsError = false;
        Raise(nameof(BannerOffersElevation));
        Raise(nameof(BannerIsError));
        Banner = text;
    }

    public bool BannerIsError { get; private set; } = true;

    private void Tick()
    {
        Log.Pump();
        CurrentPage.Refresh();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var now = DateTime.UtcNow;
        var secs = Math.Max(0.2, (now - _lastTick).TotalSeconds);
        var sent = _host.Tracker.TotalSent;
        var recv = _host.Tracker.TotalReceived;
        UpRate = Format.Rate((sent - _lastSent) / secs);
        DownRate = Format.Rate((recv - _lastReceived) / secs);
        PushRate((sent - _lastSent + recv - _lastReceived) / secs);
        _lastSent = sent;
        _lastReceived = recv;
        _lastTick = now;

        var active = _host.Tracker.Active.Count;
        Status = _host.Intercepting
            ? $"{active} open · {ProfileName}"
            : $"{active} open via SOCKS5 · {ProfileName}";
    }

    // ----- profile commands -----

    private void Open()
    {
        var path = Dialogs.OpenProfile();
        if (path is null) return;
        try
        {
            var note = _host.LoadProfile(path, (name, confirm) => Dialogs.AskMasterPassword(name, confirm));
            if (note is not null) ShowInfo(note); else Banner = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowBanner($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private void Save()
    {
        if (_host.ProfilePath is null) SaveAs();
        else _host.TrySave(_host.ProfilePath);
    }

    private void SaveAs()
    {
        var path = Dialogs.SaveProfile(_host.ProfileName);
        if (path is not null && !_host.TrySave(path))
            ShowBanner("The profile could not be saved; see the Log page.");
    }

    private void ExportProxifier()
    {
        var path = Dialogs.ExportProxifier(_host.ProfileName);
        if (path is null) return;
        try
        {
            var lost = _host.ExportProxifier(path);
            ShowInfo(lost.Count == 0
                ? $"Exported {Path.GetFileName(path)} for Proxifier."
                : $"Exported {Path.GetFileName(path)} for Proxifier. Left out: {string.Join(" ", lost)}");
        }
        catch (Exception ex)
        {
            ShowBanner($"Export failed: {ex.Message}");
        }
    }

    private void New()
    {
        if (!Dialogs.Confirm("New profile", "Start a new profile with only the Localhost and Default rules?\n\nThe current profile stays on disk.", "New profile"))
            return;
        _host.NewProfile();
    }
}
