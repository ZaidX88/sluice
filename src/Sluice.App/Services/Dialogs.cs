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

    public static ProxyServer? EditProxy(ProxyServer? existing)
    {
        var d = new ProxyDialog(existing) { Owner = Owner };
        return d.ShowDialog() == true ? d.Result : null;
    }

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
            Filter = "Proxifier / Sluice profiles (*.ppx)|*.ppx|All files (*.*)|*.*",
            InitialDirectory = DefaultFolder(),
        };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string? SaveProfile(string name)
    {
        var d = new SaveFileDialog
        {
            Title = "Save profile as",
            Filter = "Profile (*.ppx)|*.ppx",
            FileName = name + ".ppx",
            InitialDirectory = DefaultFolder(),
        };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    private static string DefaultFolder()
    {
        Directory.CreateDirectory(AppSettings.ProfilesFolder);
        return AppSettings.ProfilesFolder;
    }
}
