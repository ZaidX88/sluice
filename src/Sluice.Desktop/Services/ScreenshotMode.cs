using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Platform;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;
using Sluice.Desktop.ViewModels;

namespace Sluice.Desktop.Services;

/// <summary>
/// "Sluice.Desktop --screenshot DIR [--theme dark]": builds a demo profile in memory, sends real
/// traffic through the app's own SOCKS5 listener, renders every page to PNG and exits.
/// Touches no user files and never turns on interception.
/// </summary>
internal static class ScreenshotMode
{
    /// <param name="directOnly">Demo profile with a proxy but only Direct rules (shows the routing hint).</param>
    public static async Task RunAsync(string dir, bool directOnly = false)
    {
        Directory.CreateDirectory(dir);
        OsPlatform.Current = new DemoPlatform(OsPlatform.Current);   // shows the grant button and the autostart switch
        var settings = new AppSettings { SocksPort = 0, MinimizeToTray = false, WriteNoFiles = true };   // never saved
        await using var host = new EngineHost(settings);

        var p = Profile.CreateDefault();
        p.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = 6002, Label = "Local tunnel" });
        p.Tunnels.Add(new Tunnel { Id = 101, Type = TunnelType.Https, Address = "proxy.corp.example", Port = 3128, Username = "alice", Password = "x" });
        var ssh = new Tunnel { Id = 102, Type = TunnelType.Ssh, Address = "ssh.example.net", Port = 443, Username = "alice", Password = "x", Label = "SSH over WebSocket" };
        ssh.Settings["mode"] = "tls-payload";
        ssh.Settings["sni"] = "cdn.example.net";
        ssh.Settings["payload"] = "GET / [protocol][crlf]Host: cdn.example.net[crlf]Upgrade: websocket[crlf][crlf]";
        ssh.Settings["hostkey"] = "ssh-ed25519 SHA256:1o9IWCf9i53pEF2bhPW204/arxb+ZCkGAIz7w4w2PyQ";
        p.Tunnels.Add(ssh);
        var vless = new Tunnel { Id = 103, Type = TunnelType.VLess, Address = "reality.example.net", Port = 443, Label = "VLESS REALITY" };
        vless.Settings["uuid"] = "6c0a5c1e-9a3b-4b6f-8c2d-123456789abc";
        vless.Settings["flow"] = "xtls-rprx-vision";
        vless.Settings["security"] = "reality";
        vless.Settings["sni"] = "www.microsoft.com";
        vless.Settings["fingerprint"] = "chrome";
        vless.Settings["publickey"] = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";
        vless.Settings["shortid"] = "0123abcd";
        p.Tunnels.Add(vless);
        var sub = new Subscription { Id = 1, Name = "Provider", Url = "https://sub.example/x" };
        p.Subscriptions.Add(sub);
        var hy = new Tunnel { Id = 104, Type = TunnelType.Hysteria2, Address = "de1.provider.example", Port = 443, Password = "x", Label = "Germany 01", SubscriptionId = 1 };
        p.Tunnels.Add(hy);
        p.Tunnels[1].Enabled = false;   // show the switch in both positions
        if (!directOnly)
        {
            p.Rules.Insert(0, new Rule { Name = "Browsers", Applications = "chrome.exe; firefox.exe; msedge.exe", Action = RuleAction.ViaTunnel(100) });
            p.Rules.Insert(1, new Rule { Name = "Work sites", Targets = "*.corp.example; 10.0.0.0/8", Action = RuleAction.ViaTunnel(101) });
            p.Rules.Insert(2, new Rule { Name = "No trackers", Targets = "*.doubleclick.net; *.ads.example", Action = RuleAction.Block });
            p.Rules.Insert(3, new Rule { Name = "Old VPN", Enabled = false, Ports = "1194", Action = RuleAction.Direct });
        }
        host.DemoProfileNames = ["Home", "Travel", "Work"];   // never the real profiles folder
        host.DemoProfileName = "Work";
        host.UseProfile(p);
        host.StartSocks();

        var vm = new MainViewModel(host);
        var window = new MainWindow(vm, settings) { Width = 1240, Height = 780, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-10000, 0) };
        window.Show();

        // Real traffic through our own listener: some finished, some kept open.
        var ep = host.SocksEndPoint!;
        using var http = new HttpClient(new HttpClientHandler { Proxy = new WebProxy($"socks5://{ep}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(10) };
        foreach (var url in new[] { "https://example.com/", "https://www.google.com/", "http://neverssl.com/" })
        {
            try { await http.GetAsync(url); } catch { /* offline is fine */ }
        }
        var open = new List<TcpClient>();
        foreach (var (h, port) in new[] { ("example.com", 443), ("www.google.com", 443), ("x.doubleclick.net", 443) })
        {
            var c = new TcpClient();
            try
            {
                await c.ConnectAsync(ep);
                await ProxyHandshake.Socks5Async(c.GetStream(), h, port, null, null, CancellationToken.None);
                open.Add(c);
            }
            catch { c.Dispose(); }
        }

        await Pump(1500);
        foreach (var page in vm.Pages)
        {
            vm.CurrentPage = page;
            if (page is TunnelsViewModel proxies)
            {
                proxies.Selected = proxies.Rows.FirstOrDefault();
                proxies.CheckCommand.Execute(null);
                await Pump(4000);
            }
            if (page is RulesViewModel rules) rules.Selected = rules.Rows.Skip(1).FirstOrDefault();
            await Pump(1300);
            Save(window, Path.Combine(dir, $"{page.Title.ToLowerInvariant()}.png"));
            if (page is SettingsViewModel settingsPage)
            {
                // The Tunnels section with the UDP choice (#200), below the first screen.
                if (window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => ReferenceEquals(c.ItemsSource, settingsPage.UdpModes)) is { } udp)
                {
                    udp.BringIntoView(new Rect(0, 0, udp.Bounds.Width, 200));
                    await Pump(500);
                    Save(window, Path.Combine(dir, "settings-tunnels.png"));
                }
                // Advanced opened (#149): its top, then the end of the page.
                settingsPage.AdvancedOpen = true;
                await Pump(500);
                if (window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "AdvancedToggle") is { } toggle)
                {
                    toggle.BringIntoView(new Rect(0, 0, toggle.Bounds.Width, 560));
                    await Pump(500);
                    Save(window, Path.Combine(dir, "settings-advanced.png"));
                }
                if (window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Extent.Height > s.Viewport.Height) is { } scroll)
                {
                    scroll.ScrollToEnd();
                    await Pump(500);
                    Save(window, Path.Combine(dir, "settings-end.png"));
                }
                settingsPage.AdvancedOpen = false;
            }
        }

        // The capture-rights banner, as on a platform that can grant them (#70).
        vm.CurrentPage = vm.Pages[0];
        vm.ShowRightsBanner();
        await Pump(700);
        Save(window, Path.Combine(dir, "banner-rights.png"));
        vm.DismissBannerCommand.Execute(null);

        // The startup profile's master password was cancelled, with capture set to start on launch (#248).
        vm.ShowProfileNotOpened("Work", null, captureHeld: true);
        await Pump(700);
        Save(window, Path.Combine(dir, "banner-profile-not-opened.png"));
        vm.DismissBannerCommand.Execute(null);

        // The update notice (#198), as with a newer release out; nothing is fetched.
        vm.ShowUpdateForScreenshot(new Sluice.Core.Updates.UpdateInfo("1.1.0",
            new Uri("https://github.com/ZaidX88/sluice/releases/tag/v1.1.0"), null));
        await Pump(700);
        Save(window, Path.Combine(dir, "banner-update.png"));
        vm.ShowUpdateForScreenshot(null);

        // The profile menu (#134), drawn in a window of its own: a flyout's popup can't be rendered off-screen.
        window.BuildProfileMenu();
        var menuItems = window.ProfileMenu.Items.ToList();
        window.ProfileMenu.Items.Clear();
        var menuWindow = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-10000, 0),
            Content = new MenuFlyoutPresenter { ItemsSource = menuItems, MinWidth = 220 },
        };
        menuWindow.Show();
        await Pump(700);
        Save(menuWindow, Path.Combine(dir, "profile-menu.png"));
        menuWindow.Close();

        // Dialogs (shown non-modally just to render them).
        foreach (var (name, dialog) in new (string, Window)[]
                 {
                     ("dialog-tunnel", new Views.TunnelDialog(p.Tunnels[1])),
                     ("dialog-tunnel-ssh", new Views.TunnelDialog(p.Tunnels[2])),
                     ("dialog-tunnel-vless", new Views.TunnelDialog(p.Tunnels[3])),
                     ("dialog-rule", new Views.RuleDialog(p.Rules[1], p)),
                     ("dialog-password", new Views.PasswordDialog("Work", confirmNew: true)),
                     ("dialog-password-protection", new Views.PasswordProtectionDialog(host)),
                     ("dialog-profile-name", new NameDialog("New profile",
                         "A new profile starts with the Localhost and Default rules. The current one stays as it is.", "New profile", "Create", _ => null)),
                     ("dialog-file-error", MessageDialog.Create(Dialogs.FileDialogFailedTitle,
                         Dialogs.FileDialogFailedText("the system's file chooser didn't answer."), ["OK"], primary: 0)),
                     ("dialog-import", new Views.ImportDialog(Profile.CreateDefault(), null,
                         "vless://b831381d-6324-4d53-ad4f-8cda48b30811@edge.example:443?security=reality&pbk=PUB&sni=www.microsoft.com#Edge\ntrojan://secret@tr.example:443#Trojan")),
                     ("dialog-import-error", ImportWithStatus("No share links found.", error: true)),
                 })
        {
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Position = new PixelPoint(-10000, 0);
            dialog.ShowInTaskbar = false;
            dialog.Show();
            await Pump(700);
            Save(dialog, Path.Combine(dir, name + ".png"));
            dialog.Close();
        }

        foreach (var c in open) c.Dispose();
        window.AllowClose = true;
        window.Close();
    }

    /// <summary>
    /// The real platform, except that it offers what the screenshots should show: granting capture rights (without
    /// having them) and starting at login. Never grants anything and changes no login items.
    /// </summary>
    private sealed class DemoPlatform(IPlatformServices real) : IPlatformServices
    {
        private bool _autostart = true;
        public string Name => real.Name;
        public void OnAppStart() { }
        public void Notify(string title, string message, bool isError) { }
        public string? CaptureUnavailableReason => null;
        public bool HasCaptureRights => false;
        public string CaptureRightsHint => "Capturing traffic needs rights this copy of Sluice doesn't have yet.";
        public bool CanRestartElevated => false;
        public ITrafficCapture CreateCapture(RoutingEngine engine) => throw new NotSupportedException("screenshots never capture");
        public bool CanGrantCaptureRights => true;
        public bool AutostartSupported => true;
        public bool Autostart { get => _autostart; set => _autostart = value; }
        public bool? PrefersDarkTheme() => null;            // the screenshots' theme comes from --theme (#268)
    }

    /// <summary>The import dialog showing a status line once it's open, the path that crashed in #181.</summary>
    private static Views.ImportDialog ImportWithStatus(string text, bool error)
    {
        var d = new Views.ImportDialog(Profile.CreateDefault(), null, "not a link");
        d.Opened += (_, _) => d.ShowStatus(text, error);
        return d;
    }

    private static async Task Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(50);
        }
    }

    private static void Save(TopLevel w, string path)
    {
        var size = w.ClientSize;
        var scale = Environment.GetEnvironmentVariable("SLUICE_SHOT_SCALE") is { } v ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : 1.0;   // RenderTargetBitmap mis-scales DataGrid content at >1 DPI; 1x is faithful
        using var bmp = new RenderTargetBitmap(new PixelSize((int)(size.Width * scale), (int)(size.Height * scale)), new Vector(96 * scale, 96 * scale));
        bmp.Render(w);
        bmp.Save(path);
    }
}
