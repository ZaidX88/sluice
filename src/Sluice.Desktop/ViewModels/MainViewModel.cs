using System.IO;
using System.Windows.Input;
using Avalonia.Threading;
using Sluice.Desktop.Mvvm;
using Sluice.Desktop.Services;
using Sluice.Core;
using Sluice.Core.Engine;
using Sluice.Core.Updates;

namespace Sluice.Desktop.ViewModels;

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
            Raise(nameof(CanManageProfile));
            // A profile is open now: the "wasn't opened" notice (#248) no longer applies.
            if (_profileNotOpenedBanner is not null && _host.ProfilePath is not null)
            {
                if (Banner == _profileNotOpenedBanner) Banner = null;
                _profileNotOpenedBanner = null;
            }
        };
        host.StateChanged += () => RaiseCapture();
        host.Notice += ShowInfo;
        host.FirewallBlocked += () =>
        {
            ShowBanner("Windows Firewall is blocking Sluice on this network, so captured connections never reach it. Allow Sluice to receive incoming connections.");
            BannerOffersFirewall = true;
            Raise(nameof(BannerOffersFirewall));
        };
        AllowFirewallCommand = new RelayCommand(() =>
        {
            if (EngineHost.AllowThroughFirewall() is { } error) ShowBanner($"Could not add the firewall rule: {error}");
            else ShowInfo("Sluice is now allowed in Windows Firewall. Reload the page in your browser.");
        });

        // The profile menu's items open file pickers and other dialogs that wait until they close. Run them after the
        // click has been handled, so the menu closes first instead of staying on top of the dialog.
        NewProfileCommand = new RelayCommand(AfterMenuCloses(NewProfile));
        SaveAsCommand = new RelayCommand(AfterMenuCloses(SaveAs));
        RenameProfileCommand = new RelayCommand(AfterMenuCloses(Rename));
        DeleteProfileCommand = new RelayCommand(AfterMenuCloses(Delete));
        ImportProfileCommand = new RelayCommand(AfterMenuCloses(Import));
        PasswordProtectionCommand = new RelayCommand(AfterMenuCloses(() => Dialogs.EditPasswordProtection(_host)));
        ShowProfilesFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(_host.Profiles.Folder);
            App.OpenFolder(_host.Profiles.Folder);
        });
        DismissBannerCommand = new RelayCommand(() => Banner = null);
        DownloadUpdateCommand = new RelayCommand(() => { if (_update is { } u) App.OpenUrl(u.Download ?? u.ReleasePage); });
        UpdateNotesCommand = new RelayCommand(() => { if (_update is { } u) App.OpenUrl(u.ReleasePage); });
        DismissUpdateCommand = new RelayCommand(() =>
        {
            if (_update is null) return;
            _host.Settings.DismissedUpdate = _update.Version;
            _host.Settings.Save();
            Update = null;
        });
        GrantRightsCommand = new AsyncCommand(GrantRightsAsync);
        RestartElevatedCommand = new RelayCommand(() =>
        {
            EngineHost.RestartElevated("--intercept");
            App.Exit();
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
    public string CaptureGlyph => _host.Intercepting ? "pause" : "play";
    public string CaptureTitle => _host.Intercepting ? "Capturing traffic" : "Capture is off";

    public string ProfileName => _host.ProfileName;
    public string ProfileInitial => ProfileName.Length > 0 ? ProfileName[..1].ToUpperInvariant() : "S";

    // ----- traffic sparkline (last 60 s, like the player's progress line) -----
    private const int SparkSamples = 60;
    public const double SparkWidth = 360, SparkHeight = 28;
    private readonly Queue<double> _rates = new(Enumerable.Repeat(0.0, SparkSamples));
    private IList<Avalonia.Point> _sparkLine = new List<Avalonia.Point>();
    private IList<Avalonia.Point> _sparkArea = new List<Avalonia.Point>();
    private string _peak = "";
    public IList<Avalonia.Point> SparkLine { get => _sparkLine; private set => Set(ref _sparkLine, value); }
    public IList<Avalonia.Point> SparkArea { get => _sparkArea; private set => Set(ref _sparkArea, value); }
    public string Peak { get => _peak; private set => Set(ref _peak, value); }
    public string Totals => $"↑ {Format.Bytes(_host.Tracker.TotalSent)}  ↓ {Format.Bytes(_host.Tracker.TotalReceived)}";

    private void PushRate(double bytesPerSecond)
    {
        _rates.Dequeue();
        _rates.Enqueue(bytesPerSecond);
        var max = Math.Max(1024, _rates.Max());
        var line = new List<Avalonia.Point>();
        var i = 0;
        foreach (var r in _rates)
        {
            line.Add(new Avalonia.Point(i * SparkWidth / (SparkSamples - 1), SparkHeight - 2 - r / max * (SparkHeight - 4)));
            i++;
        }
        var area = new List<Avalonia.Point>(line) { new(SparkWidth, SparkHeight), new(0, SparkHeight) };
        SparkLine = line;
        SparkArea = area;
        Peak = $"peak {Format.Rate(_rates.Max())}";
        Raise(nameof(Totals));
    }
    public string ProfileHint => "Switch, create or manage profiles";

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

    public ICommand NewProfileCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand RenameProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand ImportProfileCommand { get; }
    public ICommand PasswordProtectionCommand { get; }
    public ICommand ShowProfilesFolderCommand { get; }
    public ICommand DismissBannerCommand { get; }
    public ICommand RestartElevatedCommand { get; }
    public ICommand GrantRightsCommand { get; }
    public bool BannerOffersGrant { get; private set; }
    public ICommand AllowFirewallCommand { get; private set; } = null!;
    public bool BannerOffersFirewall { get; private set; }

    public void TurnOn()
    {
        if (EngineHost.CaptureUnavailable is { } why)
        {
            ShowBanner(why);
            Raise(nameof(Intercepting));
            return;
        }
        if (!EngineHost.IsElevated)
        {
            ShowRightsBanner();
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

    /// <summary>Capture rights are missing: the platform's hint, with what this platform can do about it (#70).</summary>
    internal void ShowRightsBanner(string? before = null)
    {
        var platform = OsPlatform.Current;
        ShowBanner(before + platform.CaptureRightsHint, offerElevation: platform.CanRestartElevated, offerGrant: platform.CanGrantCaptureRights);
    }

    /// <summary>The OS's own password prompt (Linux: pkexec). The running app usually can't use new rights until it
    /// starts again; if it already can, capture starts at once. Otherwise the platform's hint says how to start it with
    /// them: through the launcher on Linux, which reopening the app directly doesn't do (#129).</summary>
    private async Task GrantRightsAsync()
    {
        var platform = OsPlatform.Current;
        if (!await platform.TryGrantCaptureRightsAsync())
        {
            ShowRightsBanner("Capture rights were not granted. ");
            return;
        }
        if (platform.HasCaptureRights) TurnOn();
        else ShowInfo(platform.CaptureRightsHint is { Length: > 0 } how
            ? how                                                    // e.g. "…start it with /opt/sluice/sluice" (#129)
            : "Capture rights granted. Quit Sluice and open it again to start capturing.");
    }

    private void ShowBanner(string text, bool offerElevation = false, bool offerGrant = false)
    {
        BannerOffersGrant = offerGrant;
        Raise(nameof(BannerOffersGrant));
        BannerOffersFirewall = false;
        Raise(nameof(BannerOffersFirewall));
        BannerOffersElevation = offerElevation;
        BannerIsError = true;
        Raise(nameof(BannerOffersElevation));
        Raise(nameof(BannerIsError));
        Banner = text;
    }

    /// <summary>Neutral notice (e.g. "Sluice is now allowed in Windows Firewall") rather than an error.</summary>
    public void ShowInfo(string text)
    {
        BannerOffersGrant = false;
        Raise(nameof(BannerOffersGrant));
        BannerOffersFirewall = false;
        Raise(nameof(BannerOffersFirewall));
        BannerOffersElevation = false;
        BannerIsError = false;
        Raise(nameof(BannerOffersElevation));
        Raise(nameof(BannerIsError));
        Banner = text;
    }

    public bool BannerIsError { get; private set; } = true;

    /// <summary>
    /// The startup profile could not be opened (a cancelled or wrong master password, an unreadable file): Sluice runs
    /// an empty profile without a file, so it says so instead of quietly sending everything Direct (#248).
    /// </summary>
    /// <param name="name">The profile that wasn't opened; null when it is not known.</param>
    /// <param name="why">The error, or null when the master password was cancelled.</param>
    /// <param name="captureHeld">True when capture would have started on launch and was held back.</param>
    public void ShowProfileNotOpened(string? name, string? why, bool captureHeld)
    {
        var what = name is null ? "Your profile" : $"\"{name}\"";
        var text = why is null ? $"{what} wasn't opened" : $"{what} could not be opened: {why.TrimEnd('.')}";
        text += ". Choose it from the profile menu.";
        if (captureHeld) text += " Capture stays off until a profile is open.";
        ShowBanner(text);
        _profileNotOpenedBanner = text;
    }

    private string? _profileNotOpenedBanner;

    // ---------------- update notice (#198) ----------------

    private UpdateInfo? _update;
    private DispatcherTimer? _updateTimer;
    private DateTime _lastUpdateCheck = DateTime.MinValue;
    private bool _checkingUpdate;

    /// <summary>A newer release the user hasn't dismissed; shown above the page and in the tray menu.</summary>
    public UpdateInfo? Update
    {
        get => _update;
        private set { Set(ref _update, value); Raise(nameof(HasUpdate)); Raise(nameof(UpdateText)); }
    }

    public bool HasUpdate => _update is not null;
    public string UpdateText => _update is { } u ? $"Sluice {u.Version} is available." : "";
    public ICommand DownloadUpdateCommand { get; }
    public ICommand UpdateNotesCommand { get; }
    public ICommand DismissUpdateCommand { get; }

    /// <summary>
    /// Checks about a minute after start and then once a day while Sluice runs, while the setting is on. One request
    /// to GitHub's releases API (<see cref="UpdateCheck"/>); a failure is a log line, never a message.
    /// </summary>
    public void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(1);   // after the first minute, look hourly whether a day has passed
            if (DateTime.UtcNow - _lastUpdateCheck >= TimeSpan.FromDays(1)) await CheckForUpdateAsync();
        };
        _updateTimer.Start();
    }

    private async Task CheckForUpdateAsync()
    {
        if (_checkingUpdate || !_host.Settings.CheckForUpdates) return;
        _checkingUpdate = true;
        try
        {
            _lastUpdateCheck = DateTime.UtcNow;
            // Release candidates are offered only to someone running one (or a development build).
            var preRelease = SluiceVersion.Text.Split('+')[0].Contains('-');
            var found = await UpdateCheck.GetNewerAsync(preRelease, CancellationToken.None, m => _host.Tracker.Write(LogLevel.Verbose, m));
            if (found is null) return;
            _host.Tracker.Write(LogLevel.Normal, $"Sluice {found.Version} is available: {found.ReleasePage}");
            if (found.Version != _host.Settings.DismissedUpdate) Update = found;
        }
        finally { _checkingUpdate = false; }
    }

    /// <summary>Screenshots only: the notice as it looks with a release out.</summary>
    internal void ShowUpdateForScreenshot(UpdateInfo? info) => Update = info;

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

    private static Action AfterMenuCloses(Action action) =>
        () => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    private string? AskMaster(string name, bool confirm) => Dialogs.AskMasterPassword(name, confirm);

    /// <summary>The profiles the menu lists, and which one is open (#134).</summary>
    public IReadOnlyList<string> ProfileNames => _host.ProfileNames();

    public bool CanManageProfile => _host.ProfileInFolder;

    /// <summary>Switches to another profile at once; capture keeps running. A cancelled or wrong master password
    /// leaves the current profile as it is.</summary>
    public void SwitchProfile(string name)
    {
        if (name == ProfileName && _host.ProfileInFolder) return;
        Dispatcher.UIThread.Post(() => RunProfileAction(() => { _host.SwitchTo(name, AskMaster); Banner = null; },
            $"Could not open \"{name}\""), DispatcherPriority.Background);
    }

    private void NewProfile()
    {
        var name = Dialogs.AskName("New profile", "A new profile starts with the Localhost and Default rules. The current one stays as it is.",
            _host.Profiles.UniqueName("New profile"), "Create", n => _host.Profiles.CheckName(n));
        if (name is not null && !_host.NewProfile(name)) ShowBanner("The profile could not be created; see the Log page.");
    }

    private void SaveAs()
    {
        var name = Dialogs.AskName("Save as", $"Saves a copy of \"{ProfileName}\" under a new name and carries on with the copy.",
            _host.Profiles.UniqueName(ProfileName + " copy"), "Save", n => _host.Profiles.CheckName(n));
        if (name is not null && !_host.SaveProfileAs(name)) ShowBanner("The profile could not be saved; see the Log page.");
    }

    private void Rename()
    {
        if (!_host.ProfileInFolder) return;
        var current = ProfileName;
        var name = Dialogs.AskName("Rename profile", $"A new name for \"{current}\".", current, "Rename",
            n => _host.Profiles.CheckName(n, current));
        if (name is null || name == current) return;
        RunProfileAction(() => _host.RenameProfile(name), "Could not rename the profile");
    }

    /// <summary>Deletes the current profile after switching to another one; the last profile can't be deleted.</summary>
    private void Delete()
    {
        if (!_host.ProfileInFolder) return;
        var current = ProfileName;
        var other = _host.ProfileNames().FirstOrDefault(n => !string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
        if (other is null)
        {
            Dialogs.Info("Delete profile", $"\"{current}\" is the only profile, and Sluice needs one. Create another profile first.");
            return;
        }
        if (!Dialogs.Confirm("Delete profile",
                $"Delete \"{current}\" with its tunnels and rules? This can't be undone.\n\nSluice switches to \"{other}\".", "Delete"))
            return;
        RunProfileAction(() =>
        {
            _host.SwitchTo(other, AskMaster);
            _host.DeleteProfile(current);
            ShowInfo($"Deleted \"{current}\". Now using \"{other}\".");
        }, "Could not delete the profile");
    }

    private void Import()
    {
        if (Dialogs.PickProfileToImport() is not { } path) return;
        RunProfileAction(() =>
        {
            _host.ImportProfile(path, AskMaster);
            ShowInfo($"Imported {Path.GetFileName(path)} as \"{ProfileName}\". The original file is untouched.");
        }, $"Could not import {Path.GetFileName(path)}");
    }

    /// <summary>Runs a profile action; a cancelled master password does nothing, any other error shows in the banner.</summary>
    private void RunProfileAction(Action action, string failure)
    {
        try { action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowBanner($"{failure}: {ex.Message}"); }
    }
}
