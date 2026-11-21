using System.Windows.Input;
using Sluice.Desktop.Mvvm;
using Sluice.Desktop.Services;
using Sluice.Core;
using Sluice.Core.Profiles;

namespace Sluice.Desktop.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private string _socksPortText;

    public SettingsViewModel(EngineHost host)
        : base(host, "Settings", "settings", "These apply to Sluice on this PC, whichever profile is open.")
    {
        _socksPortText = host.Settings.SocksPort.ToString();
        ToggleAdvancedCommand = new RelayCommand(() => AdvancedOpen = !AdvancedOpen);
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            System.IO.Directory.CreateDirectory(Host.Settings.LogDirectory);
            App.OpenFolder(Host.Settings.LogDirectory);
        });
        CopyVersionCommand = new RelayCommand(() => App.SetClipboardText(SluiceVersion.Text));
    }

    /// <summary>The build's version (#96), for bug reports; <see cref="CopyVersionCommand"/> copies it.</summary>
    public string VersionText => $"Version {SluiceVersion.Text}";
    public ICommand CopyVersionCommand { get; }

    /// <summary>The current profile's options. The network ones always hold the app's settings (copied in by
    /// <see cref="EngineHost"/>), so the page reads them here; reading never creates the app's settings.</summary>
    private ProfileOptions O => Host.Profile.Options;

    /// <summary>Changes an app-wide network option (#148): the app's settings, then the current profile and the engine.</summary>
    private void SetNetwork(Action<NetworkSettings> change, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        change(Host.Settings.NetworkFrom(O));
        Host.ApplyNetworkSettings();
        Raise(name);
    }

    // ----- Privacy: one choice on the main page -----
    public string[] ResolveChoices { get; } =
    [
        "Through the tunnel (recommended, no DNS leaks)",
        "On this PC",
    ];

    public int ResolveIndex
    {
        get => O.Resolve.ViaProxy ? 0 : 1;
        set
        {
            if (value < 0 || (value == 0) == O.Resolve.ViaProxy) return;
            ResolveViaProxy = value == 0;
            Raise();
        }
    }

    // ----- Advanced: closed by default, remembered -----
    public bool AdvancedOpen
    {
        get => Host.Settings.SettingsAdvancedOpen;
        set { Host.Settings.SettingsAdvancedOpen = value; Host.Settings.Save(); Raise(); }
    }

    public ICommand ToggleAdvancedCommand { get; }

    // ----- Name resolution (app-wide) -----
    public bool ResolveViaProxy { get => O.Resolve.ViaProxy; set => SetNetwork(n => n.ResolveViaTunnel = value); }
    public bool OnlyListed { get => O.Resolve.OnlyFromListMode; set => SetNetwork(n => n.OnlyListedViaTunnel = value); }
    public string ExclusionList { get => O.Resolve.ExclusionList; set => SetNetwork(n => n.ResolveLocallyList = value); }
    public bool BlockNonATypes { get => O.Resolve.BlockNonATypes; set => SetNetwork(n => n.BlockOtherQueryTypes = value); }
    public bool LeakPrevention { get => O.LeakPreventionMode; set => SetNetwork(n => n.LeakPrevention = value); }

    // ----- Connections (app-wide) -----
    public bool LoopDetection { get => O.ConnectionLoopDetection; set => SetNetwork(n => n.LoopDetection = value); }
    public bool HandleDirect { get => O.HandleDirectConnections; set => SetNetwork(n => n.HandleDirectConnections = value); }
    public bool ProcessServices { get => O.ProcessServices; set => SetNetwork(n => n.IncludeSystemServices = value); }

    // ----- Tunnels (app settings, not part of the profile) -----
    private static readonly int[] IdleChoices = [5, 10, 30, 60, 0];

    public string[] IdleOptions { get; } =
    [
        "After 5 minutes unused",
        "After 10 minutes unused",
        "After 30 minutes unused",
        "After 1 hour unused",
        "Never — keep tunnels connected",
    ];

    public int IdleIndex
    {
        get => Math.Max(0, Array.IndexOf(IdleChoices, Host.Settings.TunnelIdleMinutes));
        set
        {
            if (value < 0) return;
            Host.Settings.TunnelIdleMinutes = IdleChoices[value];
            Host.Settings.Save();
            Host.ApplyTunnelSettings();
            Raise();
        }
    }

    public bool TunnelNotifications
    {
        get => Host.Settings.TunnelNotifications;
        set { Host.Settings.TunnelNotifications = value; Host.Settings.Save(); Raise(); }
    }

    // ----- UDP (app-wide), worded without "UDP" or "QUIC" -----
    // Linux can't tunnel UDP yet (#47): there "through the tunnel" blocks it, so the choice is shown as "Block it".
    private static readonly UdpMode[] UdpOrder = OperatingSystem.IsLinux()
        ? [UdpMode.BlockAll, UdpMode.BlockQuic, UdpMode.Bypass]
        : [UdpMode.Tunnel, UdpMode.BlockQuic, UdpMode.BlockAll, UdpMode.Bypass];

    private static string UdpLabel(UdpMode mode) => mode switch
    {
        UdpMode.Tunnel => "Through the tunnel (blocked if the tunnel can't carry it)",
        UdpMode.BlockQuic => "Make browsers use regular HTTPS; the rest goes direct",
        UdpMode.BlockAll => "Block it",
        _ => "Let it go direct, outside the tunnel",
    };

    public string[] UdpModes { get; } = UdpOrder.Select(UdpLabel).ToArray();

    private static UdpMode Shown(UdpMode mode) =>
        OperatingSystem.IsLinux() && mode == UdpMode.Tunnel ? UdpMode.BlockAll : mode;

    public int UdpIndex
    {
        get => Array.IndexOf(UdpOrder, Shown(O.Udp));
        set
        {
            if (value < 0 || UdpOrder[value] == Shown(O.Udp)) return;
            SetNetwork(n => n.Udp = UdpOrder[value]);
        }
    }

    // ----- App -----
    public bool StartInterceptionOnLaunch
    {
        get => Host.Settings.StartInterceptionOnLaunch;
        set { Host.Settings.StartInterceptionOnLaunch = value; Host.Settings.Save(); Raise(); }
    }

    public bool MinimizeToTray
    {
        get => Host.Settings.MinimizeToTray;
        set { Host.Settings.MinimizeToTray = value; Host.Settings.Save(); Raise(); }
    }

    /// <summary>Shown only where the platform can start Sluice at login (#70; Linux: #69).</summary>
    public bool AutostartSupported => OsPlatform.Current.AutostartSupported;

    public bool StartAtLogin
    {
        get => OsPlatform.Current.Autostart;
        set { OsPlatform.Current.Autostart = value; Raise(); }   // read back: the platform may not have managed it
    }

    public bool FileLog
    {
        get => Host.Settings.FileLogEnabled;
        set { Host.Settings.FileLogEnabled = value; Host.Settings.Save(); Host.ApplyFileLog(); Raise(); }
    }

    public string LogDirectory => AppSettings.ShortPath(Host.Settings.LogDirectory);

    public string LogKeptText =>
        $"One file a day; the last {Sluice.Core.Engine.FileLog.DefaultRetainDays} days are kept. The log lists the apps and sites Sluice handled, so share it with care.";

    public string[] LogLevels { get; } =
    [
        "Normal: tunnels, rules applied, blocked connections and errors",
        "Detailed: also every closed connection and relay statistics (larger files)",
    ];

    public int LogLevelIndex
    {
        get => Host.Settings.FileLogDetailed ? 1 : 0;
        set
        {
            if (value < 0 || (value == 1) == Host.Settings.FileLogDetailed) return;
            Host.Settings.FileLogDetailed = value == 1;
            Host.Settings.Save();
            Host.ApplyFileLog();
            Raise();
        }
    }

    public string SocksPortText
    {
        get => _socksPortText;
        set { Set(ref _socksPortText, value); Raise(nameof(SocksPortValid)); Raise(nameof(SocksPortError)); }
    }

    public bool SocksPortValid => int.TryParse(SocksPortText, out var p) && p is > 0 and < 65536;

    /// <summary>Shown only for a port the user typed, not for the one in use (screenshot mode runs on port 0).</summary>
    public bool SocksPortError => !SocksPortValid && SocksPortText != Host.Settings.SocksPort.ToString();

    public ICommand OpenLogFolderCommand { get; }

    /// <summary>Applies the port when the field loses focus or on Enter (no Apply button). An invalid port keeps the
    /// current one; the page says why.</summary>
    public async Task CommitSocksPortAsync()
    {
        if (!SocksPortValid) return;
        var port = int.Parse(SocksPortText);
        if (port == Host.Settings.SocksPort) return;
        Host.Settings.SocksPort = port;
        Host.Settings.Save();
        await Host.RestartSocksAsync();
    }

    public override void OnProfileChanged()
    {
        foreach (var p in new[]
                 {
                     nameof(ResolveViaProxy), nameof(OnlyListed), nameof(ExclusionList), nameof(BlockNonATypes),
                     nameof(LeakPrevention), nameof(LoopDetection), nameof(HandleDirect), nameof(ProcessServices),
                     nameof(UdpIndex), nameof(ResolveIndex),
                 })
            Raise(p);
    }
}
