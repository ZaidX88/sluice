using System.Windows.Input;
using Sluice.App.Mvvm;
using Sluice.App.Services;
using Sluice.Core.Profiles;

namespace Sluice.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private string _socksPortText;

    public SettingsViewModel(EngineHost host)
        : base(host, "Settings", "", "Profile options are saved in the profile; app options apply to Sluice on this PC.")
    {
        _socksPortText = host.Settings.SocksPort.ToString();
        ApplySocksPortCommand = new AsyncCommand(ApplySocksPortAsync, () => SocksPortValid && SocksPortText != Host.Settings.SocksPort.ToString());
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            System.IO.Directory.CreateDirectory(Host.Settings.LogDirectory);
            System.Diagnostics.Process.Start("explorer.exe", Host.Settings.LogDirectory);
        });
    }

    private ProfileOptions O => Host.Profile.Options;

    private void Commit() => Host.CommitProfileEdit(notify: false);

    // ----- Name resolution -----
    public bool ResolveViaProxy { get => O.Resolve.ViaProxy; set { O.Resolve.ViaProxy = value; Commit(); Raise(); } }
    public bool OnlyListed { get => O.Resolve.OnlyFromListMode; set { O.Resolve.OnlyFromListMode = value; Commit(); Raise(); } }
    public string ExclusionList { get => O.Resolve.ExclusionList; set { O.Resolve.ExclusionList = value; Commit(); Raise(); } }
    public bool BlockNonATypes { get => O.Resolve.BlockNonATypes; set { O.Resolve.BlockNonATypes = value; Commit(); Raise(); } }
    public bool LeakPrevention { get => O.LeakPreventionMode; set { O.LeakPreventionMode = value; Commit(); Raise(); } }

    // ----- Connections -----
    public bool LoopDetection { get => O.ConnectionLoopDetection; set { O.ConnectionLoopDetection = value; Commit(); Raise(); } }
    public bool HandleDirect { get => O.HandleDirectConnections; set { O.HandleDirectConnections = value; Commit(); Raise(); } }
    public bool ProcessServices { get => O.ProcessServices; set { O.ProcessServices = value; Commit(); Raise(); } }

    // ----- Password encryption -----
    public string[] EncryptionModes { get; } =
    [
        "Disabled — plain text",
        "Basic — readable by anyone with Sluice or Proxifier",
        "Windows account — only you on this PC",
        "Master password — asked when the profile opens",
    ];

    public int EncryptionIndex
    {
        get => (int)O.PasswordEncryption;
        set
        {
            var mode = (PasswordEncryptionMode)value;
            if (mode == O.PasswordEncryption) return;
            if (mode == PasswordEncryptionMode.MasterPassword)
            {
                var pw = Dialogs.AskMasterPassword(Host.ProfileName, confirmNew: true);
                if (string.IsNullOrEmpty(pw)) { Raise(); return; }   // cancelled: keep the old mode
                Host.Profile.MasterPassword = pw;
            }
            O.PasswordEncryption = mode;
            Commit();
            Raise();
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

    public bool FileLog
    {
        get => Host.Settings.FileLogEnabled;
        set { Host.Settings.FileLogEnabled = value; Host.Settings.Save(); Host.ApplyFileLog(); Raise(); }
    }

    public string LogDirectory => Host.Settings.LogDirectory;

    public string SocksPortText
    {
        get => _socksPortText;
        set { Set(ref _socksPortText, value); Raise(nameof(SocksPortValid)); }
    }

    public bool SocksPortValid => int.TryParse(SocksPortText, out var p) && p is > 0 and < 65536;

    public ICommand ApplySocksPortCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    private async Task ApplySocksPortAsync()
    {
        Host.Settings.SocksPort = int.Parse(SocksPortText);
        Host.Settings.Save();
        await Host.RestartSocksAsync();
    }

    public override void OnProfileChanged()
    {
        foreach (var p in new[]
                 {
                     nameof(ResolveViaProxy), nameof(OnlyListed), nameof(ExclusionList), nameof(BlockNonATypes),
                     nameof(LeakPrevention), nameof(LoopDetection), nameof(HandleDirect), nameof(ProcessServices),
                     nameof(EncryptionIndex),
                 })
            Raise(p);
    }
}
