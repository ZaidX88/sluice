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

        Select(TypeBox, t.Type.ToString());
        AddressBox.Text = t.Address;
        PortBox.Text = t.Port > 0 ? t.Port.ToString() : "";
        UserBox.Text = t.Username ?? "";
        PassBox.Password = t.Password ?? "";
        LabelBox.Text = t.Label ?? "";
        Socks4aBox.IsChecked = t.Socks4aRemoteResolve;

        // SSH
        KeyBox.Text = Get("privatekey");
        PassphraseBox.Password = Get("passphrase");
        var o = TransportOptions.From(_settings);
        ModeBox.SelectedIndex = (int)o.Mode;
        PayloadBox.Text = o.Payload;
        RemoteProxyBox.Text = o.RemoteProxy ?? "";
        SniBox.Text = o.Sni ?? "";
        SkipResponseBox.IsChecked = o.SkipResponse;
        HostKeyText.Text = Get("hostkey");

        // sing-box protocols
        UuidBox.Text = Get("uuid");
        MethodBox.Text = Get("method", "2022-blake3-aes-128-gcm");
        VmessCipherBox.Text = Get("cipher", "auto");
        FlowBox.Text = Get("flow");
        Select(SecurityBox, Get("security", DefaultSecurity(t.Type)));
        V2SniBox.Text = Get("sni");
        FingerprintBox.Text = Get("fingerprint");
        AlpnBox.Text = Get("alpn");
        InsecureBox.IsChecked = Get("insecure") is "true" or "1";
        PublicKeyBox.Text = Get("publickey");
        ShortIdBox.Text = Get("shortid");
        Select(TransportBox, Get("transport", "tcp"));
        PathBox.Text = Get("path");
        HostBox.Text = Get("host");
        ServiceNameBox.Text = Get("servicename");
        ObfsBox.Password = Get("obfspassword");
        UpBox.Text = Get("upmbps");
        DownBox.Text = Get("downmbps");
        CongestionBox.Text = Get("congestion", "bbr");

        _ready = true;
        UpdateVisibility();
        Loaded += (_, _) => AddressBox.Focus();
    }

    private string Get(string key, string fallback = "") => _settings.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    private static void Select(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, tag, StringComparison.OrdinalIgnoreCase))
                           ?? box.Items[0];
    }

    private static string TagOf(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    private TunnelType SelectedType => Enum.Parse<TunnelType>(TagOf(TypeBox));
    private TransportMode SelectedMode => (TransportMode)Math.Max(0, ModeBox.SelectedIndex);

    private static string DefaultSecurity(TunnelType t) => t is TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic ? "tls" : "none";

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var type = SelectedType;
        // Sensible default ports when switching protocol.
        if (PortBox.Text is "" or "22" or "1080" or "8080" or "443" or "8388")
            PortBox.Text = type switch
            {
                TunnelType.Ssh => "22",
                TunnelType.Https or TunnelType.Http => "8080",
                TunnelType.Socks4 or TunnelType.Socks5 => "1080",
                TunnelType.Shadowsocks => "8388",
                _ => "443",
            };
        if (type is TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic) Select(SecurityBox, "tls");
        UpdateVisibility();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) UpdateVisibility();
    }

    private void OnSecurityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var type = SelectedType;
        var ssh = type == TunnelType.Ssh;
        var v2 = SingBoxConfig.Handles(type);

        SshPanel.Visibility = Show(ssh);
        V2Panel.Visibility = Show(v2);
        Socks4aBox.Visibility = Show(type == TunnelType.Socks4);
        AuthTitle.Text = ssh ? "Login" : v2 ? "Credentials" : "Authentication (optional)";

        UuidPanel.Visibility = Show(type is TunnelType.VMess or TunnelType.VLess or TunnelType.Tuic);
        UserPanel.Visibility = Show(!v2);
        PassPanel.Visibility = Show(!v2 || type is TunnelType.Shadowsocks or TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic);
        UserPassGrid.Visibility = Show(UserPanel.Visibility == Visibility.Visible || PassPanel.Visibility == Visibility.Visible);

        // SSH transport
        var o = new TransportOptions { Mode = SelectedMode };
        PayloadPanel.Visibility = Show(o.UsesPayload);
        RemoteProxyPanel.Visibility = Show(o.UsesPayload && SelectedMode != TransportMode.TlsPayload);
        SniPanel.Visibility = Show(o.UsesTls);
        HostKeyPanel.Visibility = Show(HostKeyText.Text.Length > 0);

        // sing-box protocols
        MethodPanel.Visibility = Show(type == TunnelType.Shadowsocks);
        VmessCipherPanel.Visibility = Show(type == TunnelType.VMess);
        FlowPanel.Visibility = Show(type == TunnelType.VLess);
        var tlsFixed = type is TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic;
        SecurityPanel.Visibility = Show(type is TunnelType.VMess or TunnelType.VLess);
        // REALITY exists for VLESS only.
        ((ComboBoxItem)SecurityBox.Items[2]).Visibility = Show(type == TunnelType.VLess);
        if (type != TunnelType.VLess && TagOf(SecurityBox) == "reality") Select(SecurityBox, "tls");
        var security = tlsFixed ? "tls" : TagOf(SecurityBox);
        TlsPanel.Visibility = Show(v2 && type != TunnelType.Shadowsocks && security != "none");
        RealityPanel.Visibility = Show(type == TunnelType.VLess && security == "reality");
        TransportPanel.Visibility = Show(type is TunnelType.VMess or TunnelType.VLess or TunnelType.Trojan);
        var transport = TagOf(TransportBox);
        PathHostGrid.Visibility = Show(transport is "ws" or "http" or "httpupgrade");
        ServiceNamePanel.Visibility = Show(transport == "grpc");
        HysteriaPanel.Visibility = Show(type == TunnelType.Hysteria2);
        TuicPanel.Visibility = Show(type == TunnelType.Tuic);
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
        _settings.Remove("hostpubkey");
        HostKeyText.Text = "";
        UpdateVisibility();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var error = Validate(out var port);
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        var type = SelectedType;
        var v2 = SingBoxConfig.Handles(type);
        var t = new Tunnel
        {
            Type = type,
            Address = AddressBox.Text.Trim(),
            Port = port,
            Username = v2 || string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim(),
            Password = PassBox.Password.Length == 0 || PassPanel.Visibility != Visibility.Visible ? null : PassBox.Password,
            Label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim(),
            Socks4aRemoteResolve = Socks4aBox.IsChecked == true,
        };
        void Put(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) t.Settings[k] = v.Trim(); }

        if (type == TunnelType.Ssh)
        {
            var mode = SelectedMode;
            var o = new TransportOptions { Mode = mode };
            Put("privatekey", KeyBox.Text);
            if (PassphraseBox.Password.Length > 0) t.Settings["passphrase"] = PassphraseBox.Password;
            if (mode != TransportMode.Direct) Put("mode", TransportOptions.ModeName(mode));
            if (o.UsesPayload)
            {
                if (PayloadBox.Text.Length > 0) t.Settings["payload"] = PayloadBox.Text;   // keep exact spacing
                if (SkipResponseBox.IsChecked == true) Put("skipresponse", "true");
                if (mode != TransportMode.TlsPayload) Put("remoteproxy", RemoteProxyBox.Text);
            }
            if (o.UsesTls) Put("sni", SniBox.Text);
            // Keep the trusted host key unless the server changed or the user pressed Forget.
            if (_settings.TryGetValue("hostkey", out var hk)
                && string.Equals(_originalAddress, t.Address, StringComparison.OrdinalIgnoreCase) && _originalPort == port)
            {
                Put("hostkey", hk);
                if (_settings.TryGetValue("hostpubkey", out var pub)) Put("hostpubkey", pub);
            }
            if (_settings.TryGetValue("sshclient", out var sshClient)) Put("sshclient", sshClient);
        }
        else if (v2)
        {
            if (UuidPanel.IsVisible) Put("uuid", UuidBox.Text);
            if (type == TunnelType.Shadowsocks) Put("method", MethodBox.Text);
            if (type == TunnelType.VMess && VmessCipherBox.Text.Trim() != "auto") Put("cipher", VmessCipherBox.Text);
            if (type == TunnelType.VLess) Put("flow", FlowBox.Text);
            if (SecurityPanel.IsVisible && TagOf(SecurityBox) != "none") Put("security", TagOf(SecurityBox));
            if (TlsPanel.IsVisible)
            {
                Put("sni", V2SniBox.Text);
                Put("fingerprint", FingerprintBox.Text);
                Put("alpn", AlpnBox.Text);
                if (InsecureBox.IsChecked == true) Put("insecure", "true");
            }
            if (RealityPanel.IsVisible)
            {
                Put("publickey", PublicKeyBox.Text);
                Put("shortid", ShortIdBox.Text);
            }
            if (TransportPanel.IsVisible && TagOf(TransportBox) != "tcp")
            {
                Put("transport", TagOf(TransportBox));
                if (PathHostGrid.IsVisible) { Put("path", PathBox.Text); Put("host", HostBox.Text); }
                if (ServiceNamePanel.IsVisible) Put("servicename", ServiceNameBox.Text);
            }
            if (type == TunnelType.Hysteria2)
            {
                if (ObfsBox.Password.Length > 0) t.Settings["obfspassword"] = ObfsBox.Password;
                Put("upmbps", UpBox.Text);
                Put("downmbps", DownBox.Text);
            }
            if (type == TunnelType.Tuic && CongestionBox.Text.Trim() != "bbr") Put("congestion", CongestionBox.Text);
        }

        Result = t;
        DialogResult = true;
    }

    private string? Validate(out int port)
    {
        port = 0;
        var type = SelectedType;
        if (AddressBox.Text.Trim().Length == 0) return "Enter the server's address.";
        if (!int.TryParse(PortBox.Text.Trim(), out port) || port is < 1 or > 65535) return "The port must be a number from 1 to 65535.";
        if (type == TunnelType.Socks4 && PassBox.Password.Length > 0) return "SOCKS4 has no passwords; use SOCKS5, or clear the password.";
        if (type == TunnelType.Ssh)
        {
            if (string.IsNullOrWhiteSpace(UserBox.Text)) return "SSH needs a username.";
            if (PassBox.Password.Length == 0 && string.IsNullOrWhiteSpace(KeyBox.Text)) return "SSH needs a password or a private key.";
            if (new TransportOptions { Mode = SelectedMode }.UsesPayload && string.IsNullOrWhiteSpace(PayloadBox.Text)) return "This mode needs a payload.";
            if (RemoteProxyBox.IsVisible && RemoteProxyBox.Text.Trim() is { Length: > 0 } rp && TunnelTransport.SplitHostPort(rp, 0).Port == 0)
                return "Write the remote proxy as host:port.";
        }
        if (UuidPanel.IsVisible && !Guid.TryParse(UuidBox.Text.Trim(), out _)) return $"{type.DisplayName()} needs a valid UUID (like 6c0a5c1e-9a3b-4b6f-8c2d-123456789abc).";
        if (type is TunnelType.Shadowsocks or TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic && PassBox.Password.Length == 0)
            return $"{type.DisplayName()} needs a password.";
        if (type == TunnelType.Shadowsocks && string.IsNullOrWhiteSpace(MethodBox.Text)) return "Choose a Shadowsocks cipher.";
        if (RealityPanel.IsVisible && string.IsNullOrWhiteSpace(PublicKeyBox.Text)) return "REALITY needs the server's public key.";
        if (UpBox.Text.Trim() is { Length: > 0 } up && !int.TryParse(up, out _)) return "Upload Mbps must be a number.";
        if (DownBox.Text.Trim() is { Length: > 0 } down && !int.TryParse(down, out _)) return "Download Mbps must be a number.";
        return null;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
