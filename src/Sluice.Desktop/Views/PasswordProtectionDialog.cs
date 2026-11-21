using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Sluice.Core.Profiles;
using Sluice.Desktop.Controls;
using Sluice.Desktop.Services;

namespace Sluice.Desktop.Views;

/// <summary>
/// How the current profile stores its tunnel passwords (#150), opened from the profile menu: password protection
/// belongs to each profile file, not to the app. One switch for a master password; without one, passwords are only
/// hidden (basic), and on Windows they can be tied to the Windows account instead. Changes apply at once and the
/// profile is saved, like any other edit. Plain text is no longer offered; a profile saved that way is saved as basic.
/// </summary>
public sealed class PasswordProtectionDialog : SluiceWindow
{
    private readonly EngineHost _host;
    private readonly CheckBox _master;
    private readonly Button _change;
    private readonly StackPanel _offPanel;
    private readonly CheckBox? _account;
    private bool _updating;

    private ProfileOptions Options => _host.Profile.Options;

    public PasswordProtectionDialog(EngineHost host)
    {
        _host = host;
        Title = "Password protection";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        ControlTheme Theme(string key) => (ControlTheme)Application.Current!.FindResource(key)!;
        TextBlock Muted(string text) => new() { Text = text, Classes = { "Muted" }, TextWrapping = TextWrapping.Wrap };

        _master = new CheckBox
        {
            Theme = Theme("Switch"),
            Margin = new Thickness(0, 18, 0, 0),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Protect tunnel passwords with a master password" },
                    Muted("Asked once when the profile opens. If you forget it, the passwords can't be recovered."),
                },
            },
        };
        _master.IsCheckedChanged += (_, _) => OnMasterChanged();

        _change = new Button { Content = "Change password…", Theme = Theme("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        _change.Click += (_, _) => ChangePassword();

        _offPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var warning = Muted("Without a master password, tunnel passwords are hidden but not secure: anyone with this file and Sluice can read them.");
        _offPanel.Children.Add(warning);
        if (OperatingSystem.IsWindows())
        {
            _account = new CheckBox
            {
                Content = "Tie them to my Windows account instead (only you, on this PC, can read them)",
                Margin = new Thickness(0, 10, 0, 0),
            };
            _account.IsCheckedChanged += (_, _) => OnAccountChanged();
            _offPanel.Children.Add(_account);
        }

        var close = new Button { Content = "Close", MinWidth = 90, Theme = Theme("PrimaryButton"), IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        close.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Children =
            {
                new TextBlock { Text = "Password protection", Classes = { "PageTitle" }, FontSize = 17 },
                new TextBlock { Text = $"How tunnel passwords are stored in \"{host.ProfileName}\".", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) },
                _master,
                _change,
                _offPanel,
                close,
            },
        };
        ShowMode(Options.PasswordEncryption);
    }

    /// <summary>Shows <paramref name="mode"/> without reacting to it.</summary>
    private void ShowMode(PasswordEncryptionMode mode)
    {
        _updating = true;
        _master.IsChecked = mode == PasswordEncryptionMode.MasterPassword;
        if (_account is not null) _account.IsChecked = mode == PasswordEncryptionMode.CurrentUser;
        _change.IsVisible = mode == PasswordEncryptionMode.MasterPassword;
        _offPanel.IsVisible = mode != PasswordEncryptionMode.MasterPassword;
        _updating = false;
    }

    private void OnMasterChanged()
    {
        if (_updating) return;
        if (_master.IsChecked == true)
        {
            if (Dialogs.AskMasterPassword(_host.ProfileName, confirmNew: true) is not { Length: > 0 } password)
            {
                ShowMode(Options.PasswordEncryption);   // cancelled: nothing changes
                return;
            }
            _host.Profile.MasterPassword = password;
            Apply(PasswordEncryptionMode.MasterPassword);
        }
        else
        {
            _host.Profile.MasterPassword = null;
            Apply(_account?.IsChecked == true ? PasswordEncryptionMode.CurrentUser : PasswordEncryptionMode.Basic);
        }
    }

    private void OnAccountChanged()
    {
        if (_updating || Options.PasswordEncryption == PasswordEncryptionMode.MasterPassword) return;
        Apply(_account?.IsChecked == true ? PasswordEncryptionMode.CurrentUser : PasswordEncryptionMode.Basic);
    }

    private void ChangePassword()
    {
        if (Dialogs.AskMasterPassword(_host.ProfileName, confirmNew: true) is not { Length: > 0 } password) return;
        _host.Profile.MasterPassword = password;
        _host.CommitProfileEdit(notify: false);
    }

    /// <summary>Stores the mode in the profile and saves it, as any edit does.</summary>
    private void Apply(PasswordEncryptionMode mode)
    {
        Options.PasswordEncryption = mode;
        _host.CommitProfileEdit(notify: false);
        ShowMode(mode);
    }
}
