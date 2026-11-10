using System.Windows;
using System.Windows.Controls;
using Sluice.Core.Profiles;

namespace Sluice.App.Views;

public partial class TunnelDialog : Window
{
    public Tunnel? Result { get; private set; }

    public TunnelDialog(Tunnel? existing)
    {
        InitializeComponent();
        var p = existing ?? new Tunnel { Type = TunnelType.Socks5, Port = 1080 };
        Heading.Text = existing is null ? "Add tunnel" : "Edit tunnel";
        TypeBox.SelectedIndex = p.Type switch { TunnelType.Socks4 => 1, TunnelType.Https => 2, TunnelType.Http => 3, _ => 0 };
        AddressBox.Text = p.Address;
        PortBox.Text = p.Port > 0 ? p.Port.ToString() : "";
        UserBox.Text = p.Username ?? "";
        PassBox.Password = p.Password ?? "";
        LabelBox.Text = p.Label ?? "";
        Socks4aBox.IsChecked = p.Socks4aRemoteResolve;
        Loaded += (_, _) => AddressBox.Focus();
    }

    private TunnelType SelectedType =>
        Enum.Parse<TunnelType>((string)((ComboBoxItem)TypeBox.SelectedItem).Tag);

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Socks4aBox is not null)
            Socks4aBox.Visibility = SelectedType == TunnelType.Socks4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        string? error = null;
        if (address.Length == 0) error = "Enter the server's address.";
        else if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535) error = "The port must be a number from 1 to 65535.";
        else if (SelectedType == TunnelType.Socks4 && PassBox.Password.Length > 0) error = "SOCKS4 has no passwords; use SOCKS5, or clear the password.";
        else
        {
            Result = new Tunnel
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
