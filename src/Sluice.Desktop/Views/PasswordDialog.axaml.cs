using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sluice.Desktop.Views;

public partial class PasswordDialog : Window
{
    private readonly bool _confirmNew;

    public string? Password { get; private set; }

    public PasswordDialog() : this("profile", false) { }   // designer

    public PasswordDialog(string profileName, bool confirmNew)
    {
        InitializeComponent();
        _confirmNew = confirmNew;
        Heading.Text = confirmNew ? "Set a master password" : "Profile is locked";
        Info.Text = confirmNew
            ? $"Tunnel passwords in \"{profileName}\" will be encrypted with this password. It is not stored anywhere — if you forget it, the passwords cannot be recovered."
            : $"Enter the master password for \"{profileName}\".";
        ConfirmPanel.IsVisible = confirmNew;
        Opened += (_, _) => Pass1.Focus();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Pass1.Text)) { ShowError("Enter a password."); return; }
        if (_confirmNew && Pass1.Text != Pass2.Text) { ShowError("The passwords do not match."); return; }
        Password = Pass1.Text;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }
}
