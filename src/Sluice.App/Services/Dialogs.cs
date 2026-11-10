using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sluice.App.Views;
using Sluice.Core.Profiles;

namespace Sluice.App.Services;

/// <summary>Modal UI used by view models.</summary>
public static class Dialogs
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    public static bool Confirm(string title, string message, string confirmText) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
        == MessageBoxResult.OK;

    public static Tunnel? EditTunnel(Tunnel? existing)
    {
        var d = new TunnelDialog(existing) { Owner = Owner };
        return d.ShowDialog() == true ? d.Result : null;
    }

    public static Sluice.Core.Tunnels.ImportResult? Import(Profile profile, Sluice.Core.Tunnels.TunnelTransport.Resolver? resolve)
    {
        var d = new ImportDialog(profile, resolve, ImportDialog.ClipboardLinks()) { Owner = Owner };
        return d.ShowDialog() == true ? d.Result : null;
    }

    /// <summary>Yes / No / Cancel → true / false / null.</summary>
    public static bool? Ask(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public static Rule? EditRule(Rule? existing, Profile profile)
    {
        var d = new RuleDialog(existing, profile) { Owner = Owner };
        return d.ShowDialog() == true ? d.Result : null;
    }

    public static string? AskMasterPassword(string profileName, bool confirmNew)
    {
        var d = new PasswordDialog(profileName, confirmNew) { Owner = Owner };
        return d.ShowDialog() == true ? d.Password : null;
    }

    public static string? OpenProfile()
    {
        var d = new OpenFileDialog
        {
            Title = "Open profile",
            Filter = "Profiles (*.sluice; *.ppx)|*.sluice;*.ppx;*.prx|Sluice profiles (*.sluice)|*.sluice|Proxifier profiles — imported (*.ppx)|*.ppx;*.prx|All files (*.*)|*.*",
            InitialDirectory = DefaultFolder(),
        };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string? SaveProfile(string name)
    {
        var d = new SaveFileDialog
        {
            Title = "Save profile as",
            Filter = "Sluice profile (*.sluice)|*.sluice",
            FileName = name + ".sluice",
            InitialDirectory = DefaultFolder(),
        };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string? ExportProxifier(string name)
    {
        var d = new SaveFileDialog
        {
            Title = "Export for Proxifier",
            Filter = "Proxifier profile (*.ppx)|*.ppx",
            FileName = name + ".ppx",
            InitialDirectory = DefaultFolder(),
        };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static void Info(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private static string DefaultFolder()
    {
        Directory.CreateDirectory(AppSettings.ProfilesFolder);
        return AppSettings.ProfilesFolder;
    }
}
