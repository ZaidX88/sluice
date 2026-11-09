using System.Windows;
using System.Windows.Controls;
using Sluice.Core.Profiles;

namespace Sluice.App.Views;

public partial class ProxyDialog : Window
{
    public ProxyServer? Result { get; private set; }

    public ProxyDialog(ProxyServer? existing)
    {
        InitializeComponent();
        var p = existing ?? new ProxyServer { Type = ProxyType.Socks5, Port = 1080 };
        Heading.Text = existing is null ? "Add proxy server" : "Edit proxy server";
        TypeBox.SelectedIndex = p.Type switch { ProxyType.Socks4 => 1, ProxyType.Https => 2, ProxyType.Http => 3, _ => 0 };
        AddressBox.Text = p.Address;
        PortBox.Text = p.Port > 0 ? p.Port.ToString() : "";
        UserBox.Text = p.Username ?? "";
        PassBox.Password = p.Password ?? "";
        LabelBox.Text = p.Label ?? "";
        Socks4aBox.IsChecked = p.Socks4aRemoteResolve;
        Loaded += (_, _) => AddressBox.Focus();
    }

    private ProxyType SelectedType =>
        Enum.Parse<ProxyType>((string)((ComboBoxItem)TypeBox.SelectedItem).Tag);

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Socks4aBox is not null)
            Socks4aBox.Visibility = SelectedType == ProxyType.Socks4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        string? error = null;
        if (address.Length == 0) error = "Enter the proxy server's address.";
        else if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535) error = "The port must be a number from 1 to 65535.";
        else if (SelectedType == ProxyType.Socks4 && PassBox.Password.Length > 0) error = "SOCKS4 has no passwords; use SOCKS5, or clear the password.";
        else
        {
            Result = new ProxyServer
            {
                Type = SelectedType,
                Address = address,
                Port = port,
                Username = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim(),
                Password = PassBox.Password.Length == 0 ? null : PassBox.Password,
                Label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim(),
                Socks4aRemoteResolve = Socks4aBox.IsChecked == true,
            };
            DialogResult = true;
            return;
        }
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
    }
}
