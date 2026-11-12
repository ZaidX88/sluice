using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sluice.Core.Profiles;
using Sluice.Core.Tunnels;

namespace Sluice.Desktop.Views;

public partial class TunnelDialog : Window
{
    private readonly Dictionary<string, string> _settings;
    private readonly string _originalAddress;
    private readonly int _originalPort;
    private bool _ready;

    public Tunnel? Result { get; private set; }

    public TunnelDialog() : this(null) { }   // designer

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
        PassBox.Text = t.Password ?? "";
        LabelBox.Text = t.Label ?? "";
        Socks4aBox.IsChecked = t.Socks4aRemoteResolve;

        // SSH
        KeyBox.Text = Get("privatekey");
        PassphraseBox.Text = Get("passphrase");
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
        ObfsBox.Text = Get("obfspassword");
        UpBox.Text = Get("upmbps");
        DownBox.Text = Get("downmbps");
        CongestionBox.Text = Get("congestion", "bbr");

        _ready = true;
        UpdateVisibility();
        Opened += (_, _) => AddressBox.Focus();
    }

    private static string T(TextBox box) => box.Text ?? "";
    private static string T(ComboBox box) => box.Text ?? "";

    private string Get(string key, string fallback = "") => _settings.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    private static void Select(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                           ?? box.Items[0];
    }

    private static string TagOf(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem!).Tag!;

    private TunnelType SelectedType => Enum.Parse<TunnelType>(TagOf(TypeBox));
    private TransportMode SelectedMode => (TransportMode)Math.Max(0, ModeBox.SelectedIndex);

    private static string DefaultSecurity(TunnelType t) => t is TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic ? "tls" : "none";

    private void OnTypeChanged(object? sender, SelectionChangedEventArgs e)
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

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_ready) UpdateVisibility();
    }

    private void OnSecurityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_ready) UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var type = SelectedType;
        var ssh = type == TunnelType.Ssh;
        var v2 = SingBoxConfig.Handles(type);

        SshPanel.IsVisible = (ssh);
        V2Panel.IsVisible = (v2);
        Socks4aBox.IsVisible = (type == TunnelType.Socks4);
        AuthTitle.Text = ssh ? "Login" : v2 ? "Credentials" : "Authentication (optional)";

        UuidPanel.IsVisible = (type is TunnelType.VMess or TunnelType.VLess or TunnelType.Tuic);
        UserPanel.IsVisible = (!v2);
        PassPanel.IsVisible = (!v2 || type is TunnelType.Shadowsocks or TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic);
        UserPassGrid.IsVisible = UserPanel.IsVisible || PassPanel.IsVisible;

        // SSH transport
        var o = new TransportOptions { Mode = SelectedMode };
        PayloadPanel.IsVisible = (o.UsesPayload);
        RemoteProxyPanel.IsVisible = (o.UsesPayload && SelectedMode != TransportMode.TlsPayload);
        SniPanel.IsVisible = (o.UsesTls);
        HostKeyPanel.IsVisible = (HostKeyText.Text.Length > 0);

        // sing-box protocols
        MethodPanel.IsVisible = (type == TunnelType.Shadowsocks);
        VmessCipherPanel.IsVisible = (type == TunnelType.VMess);
        FlowPanel.IsVisible = (type == TunnelType.VLess);
        var tlsFixed = type is TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic;
        SecurityPanel.IsVisible = (type is TunnelType.VMess or TunnelType.VLess);
        // REALITY exists for VLESS only.
        ((ComboBoxItem)SecurityBox.Items[2]).IsVisible = (type == TunnelType.VLess);
        if (type != TunnelType.VLess && TagOf(SecurityBox) == "reality") Select(SecurityBox, "tls");
        var security = tlsFixed ? "tls" : TagOf(SecurityBox);
        TlsPanel.IsVisible = (v2 && type != TunnelType.Shadowsocks && security != "none");
        RealityPanel.IsVisible = (type == TunnelType.VLess && security == "reality");
        TransportPanel.IsVisible = (type is TunnelType.VMess or TunnelType.VLess or TunnelType.Trojan);
        var transport = TagOf(TransportBox);
        PathHostGrid.IsVisible = (transport is "ws" or "http" or "httpupgrade");
        ServiceNamePanel.IsVisible = (transport == "grpc");
        HysteriaPanel.IsVisible = (type == TunnelType.Hysteria2);
        TuicPanel.IsVisible = (type == TunnelType.Tuic);
    }


    private async void OnLoadKey(object? sender, RoutedEventArgs e)
    {
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a private key",
            FileTypeFilter = [new FilePickerFileType("Private keys") { Patterns = ["id_*", "*.pem", "*.key"] }, FilePickerFileTypes.All],
            SuggestedStartLocation = Directory.Exists(ssh) ? await StorageProvider.TryGetFolderFromPathAsync(ssh) : null,
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var text = await File.ReadAllTextAsync(path);
        if (text.Contains("PuTTY-User-Key-File", StringComparison.Ordinal))
        {
            ShowError("PuTTY .ppk keys are not supported; export the key in OpenSSH format (PuTTYgen → Conversions → Export OpenSSH key).");
            return;
        }
        KeyBox.Text = text.Trim();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnForgetHostKey(object? sender, RoutedEventArgs e)
    {
        _settings.Remove("hostkey");
        _settings.Remove("hostpubkey");
        HostKeyText.Text = "";
        UpdateVisibility();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
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
            Address = T(AddressBox).Trim(),
            Port = port,
            Username = v2 || string.IsNullOrWhiteSpace(UserBox.Text) ? null : T(UserBox).Trim(),
            Password = T(PassBox).Length == 0 || !PassPanel.IsVisible ? null : T(PassBox),
            Label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : T(LabelBox).Trim(),
            Socks4aRemoteResolve = Socks4aBox.IsChecked == true,
        };
        void Put(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) t.Settings[k] = v.Trim(); }

        if (type == TunnelType.Ssh)
        {
            var mode = SelectedMode;
            var o = new TransportOptions { Mode = mode };
            Put("privatekey", KeyBox.Text);
            if (T(PassphraseBox).Length > 0) t.Settings["passphrase"] = T(PassphraseBox);
            if (mode != TransportMode.Direct) Put("mode", TransportOptions.ModeName(mode));
            if (o.UsesPayload)
            {
                if (T(PayloadBox).Length > 0) t.Settings["payload"] = PayloadBox.Text;   // keep exact spacing
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
            if (type == TunnelType.VMess && T(VmessCipherBox).Trim() != "auto") Put("cipher", VmessCipherBox.Text);
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
                if (T(ObfsBox).Length > 0) t.Settings["obfspassword"] = T(ObfsBox);
                Put("upmbps", UpBox.Text);
                Put("downmbps", DownBox.Text);
            }
            if (type == TunnelType.Tuic && T(CongestionBox).Trim() != "bbr") Put("congestion", CongestionBox.Text);
        }

        Result = t;
        Close(true);
    }

    private string? Validate(out int port)
    {
        port = 0;
        var type = SelectedType;
        if (T(AddressBox).Trim().Length == 0) return "Enter the server's address.";
        if (!int.TryParse(T(PortBox).Trim(), out port) || port is < 1 or > 65535) return "The port must be a number from 1 to 65535.";
        if (type == TunnelType.Socks4 && T(PassBox).Length > 0) return "SOCKS4 has no passwords; use SOCKS5, or clear the password.";
        if (type == TunnelType.Ssh)
        {
            if (string.IsNullOrWhiteSpace(UserBox.Text)) return "SSH needs a username.";
            if (T(PassBox).Length == 0 && string.IsNullOrWhiteSpace(KeyBox.Text)) return "SSH needs a password or a private key.";
            if (new TransportOptions { Mode = SelectedMode }.UsesPayload && string.IsNullOrWhiteSpace(PayloadBox.Text)) return "This mode needs a payload.";
            if (RemoteProxyBox.IsVisible && T(RemoteProxyBox).Trim() is { Length: > 0 } rp && TunnelTransport.SplitHostPort(rp, 0).Port == 0)
                return "Write the remote proxy as host:port.";
        }
        if (UuidPanel.IsVisible && !Guid.TryParse(T(UuidBox).Trim(), out _)) return $"{type.DisplayName()} needs a valid UUID (like 6c0a5c1e-9a3b-4b6f-8c2d-123456789abc).";
        if (type is TunnelType.Shadowsocks or TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic && T(PassBox).Length == 0)
            return $"{type.DisplayName()} needs a password.";
        if (type == TunnelType.Shadowsocks && string.IsNullOrWhiteSpace(MethodBox.Text)) return "Choose a Shadowsocks cipher.";
        if (RealityPanel.IsVisible && string.IsNullOrWhiteSpace(PublicKeyBox.Text)) return "REALITY needs the server's public key.";
        if (T(UpBox).Trim() is { Length: > 0 } up && !int.TryParse(up, out _)) return "Upload Mbps must be a number.";
        if (T(DownBox).Trim() is { Length: > 0 } down && !int.TryParse(down, out _)) return "Download Mbps must be a number.";
        return null;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }
}
