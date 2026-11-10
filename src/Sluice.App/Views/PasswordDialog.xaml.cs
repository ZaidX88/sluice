using System.Windows;

namespace Sluice.App.Views;

public partial class PasswordDialog : Window
{
    private readonly bool _confirmNew;

    public string? Password { get; private set; }

    public PasswordDialog(string profileName, bool confirmNew)
    {
        InitializeComponent();
        _confirmNew = confirmNew;
        Heading.Text = confirmNew ? "Set a master password" : "Profile is locked";
        Info.Text = confirmNew
            ? $"Tunnel passwords in \"{profileName}\" will be encrypted with this password. It is not stored anywhere — if you forget it, the passwords cannot be recovered."
            : $"Enter the master password for \"{profileName}\".";
        ConfirmPanel.Visibility = confirmNew ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Pass1.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Pass1.Password.Length == 0) { ShowError("Enter a password."); return; }
        if (_confirmNew && Pass1.Password != Pass2.Password) { ShowError("The passwords do not match."); return; }
        Password = Pass1.Password;
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
