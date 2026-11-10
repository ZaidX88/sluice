using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sluice.Core.Profiles;
using Sluice.Core.Tunnels;

namespace Sluice.App.Views;

public partial class TunnelDialog : Window
{
    private readonly Dictionary<string, string> _settings;
    private readonly string _originalAddress;
    private readonly int _originalPort;
    private bool _ready;

    public Tunnel? Result { get; private set; }

    public TunnelDialog(Tunnel? existing)
    {
        InitializeComponent();
        var t = existing ?? new Tunnel { Type = TunnelType.Ssh, Port = 22 };
        _settings = new Dictionary<string, string>(t.Settings, StringComparer.OrdinalIgnoreCase);
        _originalAddress = t.Address;
        _originalPort = t.Port;
        Heading.Text = existing is null ? "Add tunnel" : "Edit tunnel";

        TypeBox.SelectedIndex = t.Type switch
        {
            TunnelType.Ssh => 0, TunnelType.Socks5 => 1, TunnelType.Socks4 => 2, TunnelType.Https => 3, TunnelType.Http => 4, _ => 1,
        };
        AddressBox.Text = t.Address;
        PortBox.Text = t.Port > 0 ? t.Port.ToString() : "";
        UserBox.Text = t.Username ?? "";
        PassBox.Password = t.Password ?? "";
        LabelBox.Text = t.Label ?? "";
        Socks4aBox.IsChecked = t.Socks4aRemoteResolve;

        KeyBox.Text = Get("privatekey");
        PassphraseBox.Password = Get("passphrase");
        var o = TransportOptions.From(_settings);
        ModeBox.SelectedIndex = (int)o.Mode;
        PayloadBox.Text = o.Payload;
        RemoteProxyBox.Text = o.RemoteProxy ?? "";
        SniBox.Text = o.Sni ?? "";
        SkipResponseBox.IsChecked = o.SkipResponse;
        HostKeyText.Text = Get("hostkey");

        _ready = true;
        UpdateVisibility();
        Loaded += (_, _) => AddressBox.Focus();
    }

    private string Get(string key) => _settings.TryGetValue(key, out var v) ? v : "";

    private TunnelType SelectedType => Enum.Parse<TunnelType>((string)((ComboBoxItem)TypeBox.SelectedItem).Tag);

    private TransportMode SelectedMode => (TransportMode)Math.Max(0, ModeBox.SelectedIndex);

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        // Sensible default ports when switching protocol.
        if (PortBox.Text is "" or "22" or "1080" or "8080" or "3128")
            PortBox.Text = SelectedType switch { TunnelType.Ssh => "22", TunnelType.Https or TunnelType.Http => "8080", _ => "1080" };
        UpdateVisibility();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var ssh = SelectedType == TunnelType.Ssh;
        SshPanel.Visibility = Show(ssh);
        Socks4aBox.Visibility = Show(SelectedType == TunnelType.Socks4);
        AuthTitle.Text = ssh ? "Login" : "Authentication (optional)";

        var o = new TransportOptions { Mode = SelectedMode };
        PayloadPanel.Visibility = Show(o.UsesPayload);
        RemoteProxyPanel.Visibility = Show(o.UsesPayload && SelectedMode != TransportMode.TlsPayload);
        SniPanel.Visibility = Show(o.UsesTls);
        HostKeyPanel.Visibility = Show(HostKeyText.Text.Length > 0);
    }

    private static Visibility Show(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    private void OnLoadKey(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog
        {
            Title = "Choose a private key",
            Filter = "Private keys (id_*, *.pem, *.key)|id_*;*.pem;*.key|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (d.ShowDialog(this) != true) return;
        var text = File.ReadAllText(d.FileName);
        if (text.Contains("PuTTY-User-Key-File", StringComparison.Ordinal))
        {
            ShowError("PuTTY .ppk keys are not supported; export the key in OpenSSH format (PuTTYgen → Conversions → Export OpenSSH key).");
            return;
        }
        KeyBox.Text = text.Trim();
    }

    private void OnForgetHostKey(object sender, RoutedEventArgs e)
    {
        _settings.Remove("hostkey");
        HostKeyText.Text = "";
        UpdateVisibility();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        var type = SelectedType;
        var ssh = type == TunnelType.Ssh;
        string? error = null;
        if (address.Length == 0) error = "Enter the server's address.";
        else if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535) error = "The port must be a number from 1 to 65535.";
        else if (type == TunnelType.Socks4 && PassBox.Password.Length > 0) error = "SOCKS4 has no passwords; use SOCKS5, or clear the password.";
        else if (ssh && string.IsNullOrWhiteSpace(UserBox.Text)) error = "SSH needs a username.";
        else if (ssh && PassBox.Password.Length == 0 && string.IsNullOrWhiteSpace(KeyBox.Text)) error = "SSH needs a password or a private key.";
        else if (ssh && new TransportOptions { Mode = SelectedMode }.UsesPayload && string.IsNullOrWhiteSpace(PayloadBox.Text)) error = "This mode needs a payload.";
        else if (ssh && RemoteProxyBox.IsVisible && RemoteProxyBox.Text.Trim() is { Length: > 0 } rp && TunnelTransport.SplitHostPort(rp, 0).Port == 0)
            error = "Write the remote proxy as host:port.";
        else
        {
            var t = new Tunnel
            {
                Type = type,
                Address = address,
                Port = port,
                Username = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim(),
                Password = PassBox.Password.Length == 0 ? null : PassBox.Password,
                Label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim(),
                Socks4aRemoteResolve = Socks4aBox.IsChecked == true,
            };
            if (ssh)
            {
                void Put(string k, string? v) { if (!string.IsNullOrEmpty(v)) t.Settings[k] = v; }
                var mode = SelectedMode;
                var o = new TransportOptions { Mode = mode };
                Put("privatekey", KeyBox.Text.Trim());
                Put("passphrase", PassphraseBox.Password);
                if (mode != TransportMode.Direct) Put("mode", TransportOptions.ModeName(mode));
                if (o.UsesPayload)
                {
                    Put("payload", PayloadBox.Text);
                    if (SkipResponseBox.IsChecked == true) Put("skipresponse", "true");
                    if (mode != TransportMode.TlsPayload) Put("remoteproxy", RemoteProxyBox.Text.Trim());
                }
                if (o.UsesTls) Put("sni", SniBox.Text.Trim());
                // Keep the trusted host key unless the server changed or the user pressed Forget.
                if (_settings.TryGetValue("hostkey", out var hk)
                    && string.Equals(_originalAddress, address, StringComparison.OrdinalIgnoreCase) && _originalPort == port)
                    Put("hostkey", hk);
            }
            Result = t;
            DialogResult = true;
            return;
        }
        ShowError(error);
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
