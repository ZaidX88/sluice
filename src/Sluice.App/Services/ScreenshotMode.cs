using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sluice.App.ViewModels;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.App.Services;

/// <summary>
/// "Sluice.exe --screenshot DIR [--theme dark]": builds a demo profile in memory, sends real
/// traffic through the app's own SOCKS5 listener, renders every page to PNG and exits.
/// Touches no user files and never turns on interception.
/// </summary>
internal static class ScreenshotMode
{
    /// <param name="directOnly">Demo profile with a proxy but only Direct rules (shows the routing hint).</param>
    public static async Task RunAsync(string dir, bool directOnly = false)
    {
        Directory.CreateDirectory(dir);
        var settings = new AppSettings { SocksPort = 0, MinimizeToTray = false };   // never saved
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
        if (!directOnly)
        {
            p.Rules.Insert(0, new Rule { Name = "Browsers", Applications = "chrome.exe; firefox.exe; msedge.exe", Action = RuleAction.ViaTunnel(100) });
            p.Rules.Insert(1, new Rule { Name = "Work sites", Targets = "*.corp.example; 10.0.0.0/8", Action = RuleAction.ViaTunnel(101) });
            p.Rules.Insert(2, new Rule { Name = "No trackers", Targets = "*.doubleclick.net; *.ads.example", Action = RuleAction.Block });
            p.Rules.Insert(3, new Rule { Name = "Old VPN", Enabled = false, Ports = "1194", Action = RuleAction.Direct });
        }
        host.UseProfile(p);
        host.StartSocks();

        var vm = new MainViewModel(host);
        var window = new MainWindow(vm, settings) { Width = 1240, Height = 780, ShowInTaskbar = false, Left = -10000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
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
        File.WriteAllLines(Path.Combine(dir, "resources.txt"),
            Application.Current.Resources.MergedDictionaries.Select((d, i) => $"{i}: {d.Source?.OriginalString ?? "(no source)"} ({d.Count} keys, {d.MergedDictionaries.Count} merged)")
                .Append($"ThemeMode={Application.Current.ThemeMode.Value}  Bg={Application.Current.TryFindResource("Bg")}"));
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
        }

        // Dialogs (shown non-modally just to render them).
        foreach (var (name, dialog) in new (string, Window)[]
                 {
                     ("dialog-tunnel", new Views.TunnelDialog(p.Tunnels[1])),
                     ("dialog-tunnel-ssh", new Views.TunnelDialog(p.Tunnels[2])),
                     ("dialog-rule", new Views.RuleDialog(p.Rules[1], p)),
                     ("dialog-password", new Views.PasswordDialog("Work", confirmNew: true)),
                 })
        {
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -10000;
            dialog.ShowInTaskbar = false;
            dialog.Show();
            await Pump(700);
            Save(dialog, Path.Combine(dir, name + ".png"));
            dialog.Close();
        }

        // The title bar is drawn by Windows, outside WPF: show the window on screen for a moment
        // and copy the real pixels to check its colour.
        vm.CurrentPage = vm.Connections;
        window.Left = 40;
        window.Top = 40;
        window.Topmost = true;
        await Pump(1200);
        CaptureScreen(window, Path.Combine(dir, "window-with-titlebar.png"));
        window.Left = -10000;

        foreach (var c in open) c.Dispose();
        window.AllowClose = true;
        window.Close();
    }

    private static async Task Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(50);
        }
    }

    private static void CaptureScreen(Window w, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        int x = (int)(w.Left * dpi.DpiScaleX), y = (int)(w.Top * dpi.DpiScaleY);
        int width = (int)(w.ActualWidth * dpi.DpiScaleX), height = (int)(Math.Min(w.ActualHeight, 220) * dpi.DpiScaleY);
        using var bmp = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Save(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(root);
        // Paint the window background, then the content at its margin offset (a plain Render would
        // drop both the background and the space taken by the margin).
        var m = root.Margin;
        var width = root.ActualWidth + m.Left + m.Right;
        var height = root.ActualHeight + m.Top + m.Bottom;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(m.Left, m.Top, root.ActualWidth, root.ActualHeight));
        }
        var bmp = new RenderTargetBitmap((int)(width * dpi.DpiScaleX), (int)(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
