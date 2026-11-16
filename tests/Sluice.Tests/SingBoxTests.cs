using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Sluice.Core.Checker;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;
using Sluice.Core.Tunnels;

namespace Sluice.Tests;

internal static class TunnelTestExtensions
{
    public static Tunnel WithSettings(this Tunnel t, Tunnel from)
    {
        foreach (var (k, v) in from.Settings) t.Settings[k] = v;
        return t;
    }
}

public class SingBoxConfigTests
{
    private static JsonObject Out(Tunnel t) => SingBoxConfig.Outbound(t, "203.0.113.5");

    [Fact]
    public void VlessReality_Vision()
    {
        var t = new Tunnel { Type = TunnelType.VLess, Address = "edge.example", Port = 443 };
        t.Settings["uuid"] = "u-1";
        t.Settings["flow"] = "xtls-rprx-vision";
        t.Settings["security"] = "reality";
        t.Settings["sni"] = "www.microsoft.com";
        t.Settings["publickey"] = "PUB";
        t.Settings["shortid"] = "ab12";
        var o = Out(t);
        Assert.Equal("vless", (string?)o["type"]);
        Assert.Equal("203.0.113.5", (string?)o["server"]);          // pre-resolved by Sluice
        Assert.Equal("xtls-rprx-vision", (string?)o["flow"]);
        Assert.Equal("www.microsoft.com", (string?)o["tls"]!["server_name"]);
        Assert.Equal("PUB", (string?)o["tls"]!["reality"]!["public_key"]);
        Assert.Equal("chrome", (string?)o["tls"]!["utls"]!["fingerprint"]);   // REALITY needs uTLS
    }

    [Fact]
    public void CdnFronted_NoSni_UsesTheHttpHost_ForSniAndHostHeader()
    {
        // Typical share link behind Cloudflare: address = a CDN IP, host = the real site, no sni.
        var t = new Tunnel { Type = TunnelType.VMess, Address = "104.18.42.163", Port = 443 };
        t.Settings["uuid"] = "u";
        t.Settings["security"] = "tls";
        t.Settings["transport"] = "ws";
        t.Settings["host"] = "real.example";
        t.Settings["path"] = "/ws";
        var o = Out(t);
        Assert.Equal("real.example", (string?)o["tls"]!["server_name"]);
        Assert.Equal("real.example", (string?)o["transport"]!["headers"]!["Host"]);

        // ... and with only sni, the Host header follows it.
        var v = new Tunnel { Type = TunnelType.VLess, Address = "198.51.100.7", Port = 443 };
        v.Settings["uuid"] = "u";
        v.Settings["security"] = "tls";
        v.Settings["transport"] = "ws";
        v.Settings["sni"] = "site.example";
        Assert.Equal("site.example", (string?)Out(v)["transport"]!["headers"]!["Host"]);
    }

    [Theory]
    [InlineData("tls", "unsafe", null)]          // Xray: no uTLS — sing-box would refuse to start
    [InlineData("tls", "chrome_psk", null)]
    [InlineData("tls", "Firefox", "firefox")]
    [InlineData("reality", "unsafe", "chrome")]  // REALITY needs uTLS
    [InlineData("reality", null, "chrome")]
    public void Fingerprint_OnlyValuesSingBoxKnows(string security, string? fp, string? expected)
    {
        var t = new Tunnel { Type = TunnelType.VLess, Address = "edge.example", Port = 443 };
        t.Settings["uuid"] = "u";
        t.Settings["security"] = security;
        if (security == "reality") t.Settings["publickey"] = "PUB";
        if (fp is not null) t.Settings["fingerprint"] = fp;
        Assert.Equal(expected, (string?)Out(t)["tls"]!["utls"]?["fingerprint"]);
    }

    [Fact]
    public void Tls_DefaultsSniToTheHostName_AndTrojanAlwaysUsesTls()
    {
        var t = new Tunnel { Type = TunnelType.Trojan, Address = "tr.example", Port = 443, Password = "p" };
        var o = Out(t);
        Assert.Equal("tr.example", (string?)o["tls"]!["server_name"]);
        Assert.Null(o["tls"]!["reality"]);
    }

    [Fact]
    public void VmessWebSocket_AndGrpc()
    {
        var ws = new Tunnel { Type = TunnelType.VMess, Address = "1.2.3.4", Port = 80 };
        ws.Settings["uuid"] = "u";
        ws.Settings["transport"] = "ws";
        ws.Settings["path"] = "/ray";
        ws.Settings["host"] = "cdn.example";
        var o = Out(ws);
        Assert.Equal("auto", (string?)o["security"]);
        Assert.Null(o["tls"]);
        Assert.Equal("/ray", (string?)o["transport"]!["path"]);
        Assert.Equal("cdn.example", (string?)o["transport"]!["headers"]!["Host"]);

        ws.Settings["transport"] = "grpc";
        ws.Settings["servicename"] = "svc";
        Assert.Equal("svc", (string?)Out(ws)["transport"]!["service_name"]);
    }

    [Fact]
    public void Hysteria2_Obfs_AndTuicDefaults()
    {
        var h = new Tunnel { Type = TunnelType.Hysteria2, Address = "h.example", Port = 443, Password = "p" };
        h.Settings["obfspassword"] = "o";
        h.Settings["upmbps"] = "50";
        var o = Out(h);
        Assert.Equal("salamander", (string?)o["obfs"]!["type"]);
        Assert.Equal(50, (int)o["up_mbps"]!);

        var tu = new Tunnel { Type = TunnelType.Tuic, Address = "t.example", Port = 443, Password = "p" };
        tu.Settings["uuid"] = "u";
        var tuic = Out(tu);
        Assert.Equal("bbr", (string?)tuic["congestion_control"]);
        Assert.Equal("h3", (string?)tuic["tls"]!["alpn"]![0]);
    }

    [Theory]
    [InlineData(TunnelType.VLess, "uuid", "UUID")]
    [InlineData(TunnelType.Shadowsocks, "method", "cipher")]
    [InlineData(TunnelType.Trojan, null, "password")]
    public void MissingRequiredSettings_AreClearErrors(TunnelType type, string? present, string missingWord)
    {
        var t = new Tunnel { Type = type, Address = "x", Port = 1 };
        if (present == "method") t.Password = "p";
        var ex = Assert.Throws<ProxyException>(() => Out(t));
        Assert.Contains(missingWord, ex.Message);
    }

    [Fact]
    public void Shadowsocks_RejectsTls_AndHysteriaRejectsWs()
    {
        var ss = new Tunnel { Type = TunnelType.Shadowsocks, Address = "x", Port = 1, Password = "p" };
        ss.Settings["method"] = "aes-128-gcm";
        ss.Settings["security"] = "tls";
        Assert.Throws<ProxyException>(() => Out(ss));
        var hy = new Tunnel { Type = TunnelType.Hysteria2, Address = "x", Port = 1, Password = "p" };
        hy.Settings["transport"] = "ws";
        Assert.Throws<ProxyException>(() => Out(hy));
    }
}

/// <summary>
/// End to end: a throwaway sing-box *server* on 127.0.0.1 with every protocol, and Sluice's client side
/// (TunnelBackends → sing-box client → server → echo). Runs when sing-box.exe is next to the tests
/// (scripts/get-sing-box.ps1); REALITY additionally needs internet (SLUICE_TEST_INTERNET=1).
/// </summary>
public class LiveSingBoxTests : IAsyncLifetime
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
    private static bool Enabled => File.Exists(Exe);

    private readonly Dictionary<string, int> _ports = new();
    private string _dir = "";
    private Process? _server;
    private string _uuid = "", _ssKey = "", _realityPublic = "", _shortId = "0123abcd";

    private static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    private static int FreePort(ProtocolType proto = ProtocolType.Tcp)
    {
        if (proto == ProtocolType.Udp)
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
        }
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        _dir = Directory.CreateTempSubdirectory("sluice-singbox-server-").FullName;
        _uuid = Run("generate", "uuid");
        _ssKey = Run("generate", "rand", "--base64", "16");
        var pair = Run("generate", "reality-keypair").Split('\n');
        var realityPrivate = pair.First(l => l.StartsWith("PrivateKey")).Split(':', 2)[1].Trim();
        _realityPublic = pair.First(l => l.StartsWith("PublicKey")).Split(':', 2)[1].Trim();
        var tls = Run("generate", "tls-keypair", "test.local");
        var cert = tls[tls.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)..];
        var key = tls[..tls.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)].Trim();
        File.WriteAllText(Path.Combine(_dir, "cert.pem"), cert);
        File.WriteAllText(Path.Combine(_dir, "key.pem"), key);

        foreach (var n in new[] { "ss", "vmess", "vless-grpc", "trojan", "vless-reality" }) _ports[n] = FreePort();
        foreach (var n in new[] { "hy2", "tuic" }) _ports[n] = FreePort(ProtocolType.Udp);

        JsonObject Tls() => new() { ["enabled"] = true, ["certificate_path"] = Path.Combine(_dir, "cert.pem"), ["key_path"] = Path.Combine(_dir, "key.pem") };
        JsonObject In(string type, string name, JsonObject extra)
        {
            var o = new JsonObject { ["type"] = type, ["tag"] = name, ["listen"] = "127.0.0.1", ["listen_port"] = _ports[name] };
            foreach (var (k, v) in extra) o[k] = v?.DeepClone();
            return o;
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray(
                In("shadowsocks", "ss", new() { ["method"] = "2022-blake3-aes-128-gcm", ["password"] = _ssKey }),
                In("vmess", "vmess", new()
                {
                    ["users"] = new JsonArray(new JsonObject { ["uuid"] = _uuid, ["alterId"] = 0 }),
                    ["transport"] = new JsonObject { ["type"] = "ws", ["path"] = "/ray" },
                }),
                In("vless", "vless-grpc", new()
                {
                    ["users"] = new JsonArray(new JsonObject { ["uuid"] = _uuid }),
                    ["tls"] = Tls(),
                    ["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = "svc" },
                }),
                In("trojan", "trojan", new() { ["users"] = new JsonArray(new JsonObject { ["password"] = "tr-pass" }), ["tls"] = Tls() }),
                In("hysteria2", "hy2", new()
                {
                    ["users"] = new JsonArray(new JsonObject { ["password"] = "hy-pass" }),
                    ["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = "obfs-pass" },
                    ["tls"] = Tls(),
                }),
                In("tuic", "tuic", new()
                {
                    ["users"] = new JsonArray(new JsonObject { ["uuid"] = _uuid, ["password"] = "tuic-pass" }),
                    ["tls"] = new JsonObject
                    {
                        ["enabled"] = true, ["alpn"] = new JsonArray("h3"),
                        ["certificate_path"] = Path.Combine(_dir, "cert.pem"), ["key_path"] = Path.Combine(_dir, "key.pem"),
                    },
                }),
                In("vless", "vless-reality", new()
                {
                    ["users"] = new JsonArray(new JsonObject { ["uuid"] = _uuid, ["flow"] = "xtls-rprx-vision" }),
                    ["tls"] = new JsonObject
                    {
                        ["enabled"] = true, ["server_name"] = "www.microsoft.com",
                        ["reality"] = new JsonObject
                        {
                            ["enabled"] = true,
                            ["handshake"] = new JsonObject { ["server"] = "www.microsoft.com", ["server_port"] = 443 },
                            ["private_key"] = realityPrivate,
                            ["short_id"] = new JsonArray(_shortId),
                        },
                    },
                })),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
        };
        var path = Path.Combine(_dir, "server.json");
        File.WriteAllText(path, config.ToJsonString());
        var check = Run("check", "-c", path);
        Assert.True(check.Length == 0, "server config invalid: " + check);

        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(path);
        _server = Process.Start(psi)!;
        _server.BeginErrorReadLine();
        _server.BeginOutputReadLine();
        for (var i = 0; i < 100; i++)   // wait for the TCP inbounds
        {
            using var c = new TcpClient();
            try { await c.ConnectAsync(IPAddress.Loopback, _ports["trojan"]); break; }
            catch (SocketException) { await Task.Delay(50); }
        }
    }

    public async Task DisposeAsync()
    {
        try { _server?.Kill(true); } catch { }
        _server?.Dispose();
        if (_dir.Length > 0) try { Directory.Delete(_dir, true); } catch { }
        await Task.CompletedTask;
    }

    private Tunnel T(TunnelType type, string port, string? password = null, params (string, string)[] settings)
    {
        var t = new Tunnel { Id = 50, Type = type, Address = "127.0.0.1", Port = _ports[port], Password = password, Label = port };
        foreach (var (k, v) in settings) t.Settings[k] = v;
        return t;
    }

    private static async Task AssertTunnelWorks(Tunnel t)
    {
        await using var echo = new EchoServer();
        var profile = Profile.CreateDefault();
        profile.Tunnels.Add(t);
        var log = new List<string>();
        await using var backends = new TunnelBackends(l => { lock (log) log.Add(l); }) { SingBoxPath = Exe };
        var conn = new TunnelConnector(profile, backends);
        try
        {
            using var c = await conn.ConnectAsync(RuleAction.ViaTunnel(t.Id), "live", "127.0.0.1", echo.Port);
            var s = c.GetStream();
            var msg = Encoding.ASCII.GetBytes($"hello over {t.Type}");
            await s.WriteAsync(msg);
            var back = new byte[msg.Length];
            await s.ReadExactlyAsync(back).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(msg, back);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            throw new Exception($"{ex.Message}\nsing-box log:\n{string.Join("\n", log)}", ex);
        }
    }

    [Fact]
    public async Task Shadowsocks2022()
    {
        if (!Enabled) return;
        await AssertTunnelWorks(T(TunnelType.Shadowsocks, "ss", _ssKey, ("method", "2022-blake3-aes-128-gcm")));
    }

    [Fact]
    public async Task VmessOverWebSocket()
    {
        if (!Enabled) return;
        await AssertTunnelWorks(T(TunnelType.VMess, "vmess", null, ("uuid", _uuid), ("transport", "ws"), ("path", "/ray")));
    }

    [Fact]
    public async Task VlessTlsGrpc()
    {
        if (!Enabled) return;
        await AssertTunnelWorks(T(TunnelType.VLess, "vless-grpc", null, ("uuid", _uuid), ("security", "tls"), ("sni", "test.local"),
            ("insecure", "true"), ("transport", "grpc"), ("servicename", "svc")));
    }

    [Fact]
    public async Task Trojan()
    {
        if (!Enabled) return;
        await AssertTunnelWorks(T(TunnelType.Trojan, "trojan", "tr-pass", ("sni", "test.local"), ("insecure", "true")));
    }

    [Fact]
    public async Task Hysteria2WithObfs()
    {
        if (!Enabled) return;
        await AssertTunnelWorks(T(TunnelType.Hysteria2, "hy2", "hy-pass", ("sni", "test.local"), ("insecure", "true"), ("obfspassword", "obfs-pass")));
    }

    [Fact]
    public async Task Tuic()
    {
        if (!Enabled) return;
        await AssertTunnelWorks(T(TunnelType.Tuic, "tuic", "tuic-pass", ("uuid", _uuid), ("sni", "test.local"), ("insecure", "true")));
    }

    [Fact]
    public async Task VlessRealityVision()
    {
        if (!Enabled || Environment.GetEnvironmentVariable("SLUICE_TEST_INTERNET") != "1") return;
        await AssertTunnelWorks(T(TunnelType.VLess, "vless-reality", null, ("uuid", _uuid), ("flow", "xtls-rprx-vision"),
            ("security", "reality"), ("sni", "www.microsoft.com"), ("publickey", _realityPublic), ("shortid", _shortId)));
    }

    /// <summary>
    /// A base64 subscription of share links (as v2rayN / NekoRay would get) pointing at the local server:
    /// import it over HTTP, export every tunnel back to a link, re-import that, and carry traffic.
    /// </summary>
    [Fact]
    public async Task SubscriptionLinks_ImportExportAndConnect()
    {
        if (!Enabled) return;
        var vmess = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["v"] = "2", ["ps"] = "vmess", ["add"] = "127.0.0.1", ["port"] = _ports["vmess"].ToString(), ["id"] = _uuid,
            ["aid"] = "0", ["net"] = "ws", ["path"] = "/ray", ["tls"] = "",
        }.ToJsonString()));
        var links = string.Join('\n',
            $"ss://2022-blake3-aes-128-gcm:{Uri.EscapeDataString(_ssKey)}@127.0.0.1:{_ports["ss"]}#ss",
            $"vmess://{vmess}",
            $"vless://{_uuid}@127.0.0.1:{_ports["vless-grpc"]}?encryption=none&security=tls&sni=test.local&allowInsecure=1&type=grpc&serviceName=svc#vless",
            $"trojan://tr-pass@127.0.0.1:{_ports["trojan"]}?sni=test.local&allowInsecure=1#trojan",
            $"hy2://hy-pass@127.0.0.1:{_ports["hy2"]}?sni=test.local&insecure=1&obfs=salamander&obfs-password=obfs-pass#hy2",
            $"tuic://{_uuid}:tuic-pass@127.0.0.1:{_ports["tuic"]}?sni=test.local&alpn=h3&allow_insecure=1#tuic");
        await using var server = new SubscriptionTests.SubServer(Convert.ToBase64String(Encoding.UTF8.GetBytes(links)));
        var profile = Profile.CreateDefault();
        var sub = Subscriptions.AddSubscription(profile, $"http://127.0.0.1:{server.Port}/s");
        var result = await Subscriptions.UpdateAsync(profile, sub);
        Assert.Equal(6, result.Added.Count);
        Assert.Empty(result.Warnings);

        foreach (var t in profile.Tunnels)
        {
            var again = ShareLinks.Parse(ShareLinks.ToLink(t));
            Assert.True(again.Ok, $"{t}: {again.Error}");
            again.Tunnel!.Id = 50;
            await AssertTunnelWorks(again.Tunnel);
        }
    }

    /// <summary>UDP: a datagram to a local echo server through each protocol's sing-box session.</summary>
    [Theory]
    [InlineData("ss")]
    [InlineData("vmess")]
    [InlineData("vless-grpc")]
    [InlineData("trojan")]
    [InlineData("hy2")]
    [InlineData("tuic")]
    public async Task Udp_EchoThroughTunnel(string name)
    {
        if (!Enabled) return;
        var t = name switch
        {
            "ss" => T(TunnelType.Shadowsocks, "ss", _ssKey, ("method", "2022-blake3-aes-128-gcm")),
            "vmess" => T(TunnelType.VMess, "vmess", null, ("uuid", _uuid), ("transport", "ws"), ("path", "/ray")),
            "vless-grpc" => T(TunnelType.VLess, "vless-grpc", null, ("uuid", _uuid), ("security", "tls"), ("sni", "test.local"),
                ("insecure", "true"), ("transport", "grpc"), ("servicename", "svc")),
            "trojan" => T(TunnelType.Trojan, "trojan", "tr-pass", ("sni", "test.local"), ("insecure", "true")),
            "hy2" => T(TunnelType.Hysteria2, "hy2", "hy-pass", ("sni", "test.local"), ("insecure", "true"), ("obfspassword", "obfs-pass")),
            _ => T(TunnelType.Tuic, "tuic", "tuic-pass", ("uuid", _uuid), ("sni", "test.local"), ("insecure", "true")),
        };
        using var echo = new UdpEchoServer();
        var profile = Profile.CreateDefault();
        profile.Tunnels.Add(t);
        await using var engine = new Sluice.Core.Engine.RoutingEngine(profile);
        engine.Backends.SingBoxPath = Exe;
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var a = await engine.OpenUdpAsync(t, (h, p, d) => got.TrySetResult($"{h}:{p} {Encoding.ASCII.GetString(d.Span)}"), default);
        // UDP may be lost while the session warms up: retry a few times.
        for (var i = 0; i < 10 && !got.Task.IsCompleted; i++)
        {
            await a.SendAsync("127.0.0.1", echo.Port, Encoding.ASCII.GetBytes($"udp over {name}"));
            await Task.WhenAny(got.Task, Task.Delay(500));
        }
        Assert.True(got.Task.IsCompleted, $"no UDP reply through {name}");
        Assert.Equal($"127.0.0.1:{echo.Port} udp over {name}", await got.Task);
    }

    // ---------------- tunnel lifecycle (idle stop, leases, retries, giving up, off switch) ----------------

    private TunnelBackends Backends() => new() { SingBoxPath = Exe, HealthInterval = TimeSpan.Zero };

    private Tunnel TrojanTunnel(int? port = null) =>
        T(TunnelType.Trojan, "trojan", "tr-pass", ("sni", "test.local"), ("insecure", "true")) is var t && port is { } p
            ? new Tunnel { Id = t.Id, Type = t.Type, Address = t.Address, Port = p, Password = t.Password, Label = "dead" }.WithSettings(t)
            : t;

    [Fact]
    public async Task Lifecycle_IdleStop_ThenRestartOnUse()
    {
        if (!Enabled) return;
        await using var b = Backends();
        b.IdleTimeout = TimeSpan.FromSeconds(1);
        var t = TrojanTunnel();
        await b.GetLocalSocksAsync(t, default);
        Assert.Equal(SessionState.Connected, b.GetStatus(t.Id).State);
        var helper = Process.GetProcessesByName("sing-box").Select(p => p.Id).Single(b.IsHelperProcess);

        await Task.Delay(1300);
        await b.MonitorOnceAsync();
        Assert.Equal(SessionState.Idle, b.GetStatus(t.Id).State);
        Assert.False(b.IsHelperProcess(helper));

        await b.GetLocalSocksAsync(t, default);                     // next use starts it again
        Assert.Equal(SessionState.Connected, b.GetStatus(t.Id).State);
    }

    [Fact]
    public async Task Lifecycle_OpenConnectionsKeepItRunning()
    {
        if (!Enabled) return;
        await using var b = Backends();
        b.IdleTimeout = TimeSpan.FromSeconds(1);
        var t = TrojanTunnel();
        await b.GetLocalSocksAsync(t, default);
        using (b.Lease(t.Id))
        {
            await Task.Delay(1300);
            await b.MonitorOnceAsync();
            Assert.Equal(SessionState.Connected, b.GetStatus(t.Id).State);
        }
    }

    [Fact]
    public async Task Lifecycle_DeadServer_RetriesThenGivesUp_UntilReset()
    {
        if (!Enabled) return;
        await using var b = Backends();
        b.MaxAttempts = 3;
        b.Backoff = [TimeSpan.FromMilliseconds(200)];
        var changes = new List<SessionState>();
        b.StatusChanged += (_, st) => { lock (changes) changes.Add(st.State); };
        var dead = TrojanTunnel(FreePort());                               // nothing listens there

        for (var i = 1; i <= 3; i++)
        {
            var ex = await Assert.ThrowsAnyAsync<IOException>(() => b.GetLocalSocksAsync(dead, default));
            Assert.Contains("no test connection", ex.Message);       // started, but carries nothing
            if (i < 3)
            {
                Assert.Equal(SessionState.Reconnecting, b.GetStatus(dead.Id).State);
                // Within the back-off, connections fail at once instead of starting sing-box again.
                Assert.Contains("reconnecting", (await Assert.ThrowsAnyAsync<IOException>(() => b.GetLocalSocksAsync(dead, default))).Message);
                await Task.Delay(250);
            }
        }
        Assert.Equal(SessionState.Failed, b.GetStatus(dead.Id).State);
        Assert.Contains("stopped after", (await Assert.ThrowsAnyAsync<IOException>(() => b.GetLocalSocksAsync(dead, default))).Message);
        Assert.DoesNotContain(Process.GetProcessesByName("sing-box").Select(p => p.Id), b.IsHelperProcess);

        b.ResetFailures(dead.Id);                                    // Check / off-and-on
        Assert.Equal(SessionState.NotStarted, b.GetStatus(dead.Id).State);
        lock (changes) Assert.Contains(SessionState.Failed, changes);
    }

    [Fact]
    public async Task Lifecycle_HelperDies_ReconnectsByItself()
    {
        if (!Enabled) return;
        await using var b = Backends();
        var t = TrojanTunnel();
        await b.GetLocalSocksAsync(t, default);
        var helper = Process.GetProcessesByName("sing-box").Select(p => p.Id).Single(b.IsHelperProcess);
        var seen = new List<SessionState>();
        b.StatusChanged += (_, st) => { lock (seen) seen.Add(st.State); };
        Process.GetProcessById(helper).Kill();

        // The monitor (every 2 s) notices on its own; run a pass too so the test does not depend on timing.
        await Task.Delay(500);
        await b.MonitorOnceAsync();
        for (var i = 0; i < 50 && b.GetStatus(t.Id).State != SessionState.Connected; i++) await Task.Delay(200);
        Assert.Equal(SessionState.Connected, b.GetStatus(t.Id).State);
        lock (seen) Assert.Contains(SessionState.Reconnecting, seen);
        Assert.NotEqual(helper, Process.GetProcessesByName("sing-box").Select(p => p.Id).Single(b.IsHelperProcess));
        await AssertTunnelWorks(t);
    }

    [Fact]
    public async Task Lifecycle_TurnedOff_ClosesSession_AndRefusesUse()
    {
        if (!Enabled) return;
        await using var b = Backends();
        var t = TrojanTunnel();
        var profile = Profile.CreateDefault();
        profile.Tunnels.Add(t);
        await b.PruneAsync(profile);
        await b.GetLocalSocksAsync(t, default);

        t.Enabled = false;
        await b.PruneAsync(profile);
        Assert.Equal(SessionState.Disabled, b.GetStatus(t.Id).State);
        Assert.DoesNotContain(Process.GetProcessesByName("sing-box").Select(p => p.Id), b.IsHelperProcess);
        Assert.Contains("turned off", (await Assert.ThrowsAnyAsync<IOException>(() => b.GetLocalSocksAsync(t, default))).Message);
        var conn = new TunnelConnector(profile, b);
        Assert.Contains("turned off", (await Assert.ThrowsAnyAsync<IOException>(() => conn.ConnectAsync(RuleAction.ViaTunnel(t.Id), "r", "127.0.0.1", 9))).Message);

        t.Enabled = true;
        await b.PruneAsync(profile);
        Assert.Equal(SessionState.NotStarted, b.GetStatus(t.Id).State);
        await b.GetLocalSocksAsync(t, default);
        Assert.Equal(SessionState.Connected, b.GetStatus(t.Id).State);
    }

    // ---------------- dead-but-"connected" tunnels: a frozen server accepts TCP and never answers ----------------

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr process);

    private sealed class Frozen : IDisposable
    {
        private readonly Process _p;
        public Frozen(Process p) { _p = p; NtSuspendProcess(p.Handle); }
        public void Dispose() => NtResumeProcess(_p.Handle);
    }

    private TunnelBackends FastBackends() => new()
    {
        SingBoxPath = Exe,
        HealthInterval = TimeSpan.Zero,
        ProbeTimeout = TimeSpan.FromSeconds(2),
        RestartOnNetworkChange = false,
    };

    private static async Task WaitForState(TunnelBackends b, int id, SessionState state, int seconds = 20)
    {
        for (var i = 0; i < seconds * 5 && b.GetStatus(id).State != state; i++) await Task.Delay(200);
        Assert.Equal(state, b.GetStatus(id).State);
    }

    [Fact]
    public async Task DeadLink_RealConnectionTimeout_TriggersImmediateCheckAndRestart()
    {
        if (!Enabled) return;
        await using var b = FastBackends();
        var t = TrojanTunnel();
        var profile = Profile.CreateDefault();
        profile.Tunnels.Add(t);
        await b.PruneAsync(profile);
        await b.GetLocalSocksAsync(t, default);
        var seen = new List<SessionState>();
        b.StatusChanged += (_, st) => { lock (seen) seen.Add(st.State); };
        var conn = new TunnelConnector(profile, b) { DirectConnectTimeout = TimeSpan.FromSeconds(2) };
        await using var echo = new EchoServer();

        using (new Frozen(_server!))
        {
            var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                conn.ConnectAsync(RuleAction.ViaTunnel(t.Id), "r", "127.0.0.1", echo.Port));
            Assert.Contains("checking it now", ex.Message);
            // The failure itself starts the check; no scheduled check is involved (HealthInterval is off).
            await WaitForState(b, t.Id, SessionState.Reconnecting, 15);
        }
        // Server answers again: the monitor brings the tunnel back by itself.
        await WaitForState(b, t.Id, SessionState.Connected, 30);
        lock (seen) Assert.DoesNotContain(SessionState.NotResponding, seen);   // a real failure + failed check = restart at once
        using var c = await conn.ConnectAsync(RuleAction.ViaTunnel(t.Id), "r", "127.0.0.1", echo.Port);
    }

    [Fact]
    public async Task DeadLink_ScheduledCheck_NotRespondingThenReconnect()
    {
        if (!Enabled) return;
        await using var b = FastBackends();
        b.HealthInterval = TimeSpan.FromSeconds(1);
        var t = TrojanTunnel();
        await b.GetLocalSocksAsync(t, default);
        var seen = new List<SessionState>();
        b.StatusChanged += (_, st) => { lock (seen) seen.Add(st.State); };

        using (new Frozen(_server!))
        {
            await WaitForState(b, t.Id, SessionState.NotResponding, 15);    // first failed check: warn, check again soon
            await WaitForState(b, t.Id, SessionState.Reconnecting, 20);     // second failed check: restart
        }
        await WaitForState(b, t.Id, SessionState.Connected, 30);
        lock (seen) Assert.Equal([SessionState.NotResponding, SessionState.Reconnecting], seen.Where(x => x is SessionState.NotResponding or SessionState.Reconnecting).Distinct());
    }

    [Fact]
    public async Task BusyTunnel_FailedCheck_IsNotRestarted()
    {
        if (!Enabled) return;
        await using var b = FastBackends();
        b.HealthInterval = TimeSpan.FromSeconds(1);
        b.BusyGrace = TimeSpan.FromSeconds(3);
        var log = new List<string>();
        b.Log = m => { lock (log) log.Add(m); };
        var t = TrojanTunnel();
        await b.GetLocalSocksAsync(t, default);
        var seen = new List<SessionState>();
        b.StatusChanged += (_, st) => { lock (seen) seen.Add(st.State); };

        using (new Frozen(_server!))
        {
            // Checks time out, but data keeps arriving through the tunnel (a download filling it): keep it.
            var until = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < until)
            {
                b.NoteData(t.Id);
                await b.MonitorOnceAsync();
                await Task.Delay(200);
            }
            lock (log) Assert.Contains(log, m => m.Contains("still delivering data"));
            lock (seen) Assert.DoesNotContain(SessionState.Reconnecting, seen);

            // Data stops: the next failed checks restart it as before.
            await WaitForState(b, t.Id, SessionState.Reconnecting, 25);
        }
        await WaitForState(b, t.Id, SessionState.Connected, 30);
    }

    [Fact]
    public async Task NetworkChange_RestartsRunningTunnels()
    {
        if (!Enabled) return;
        await using var b = FastBackends();
        var t = TrojanTunnel();
        await b.GetLocalSocksAsync(t, default);
        var helper = Process.GetProcessesByName("sing-box").Select(p => p.Id).Single(b.IsHelperProcess);

        Assert.True(await b.CheckNetworkChangeAsync(force: true));
        Assert.Equal(SessionState.Reconnecting, b.GetStatus(t.Id).State);
        Assert.Equal("network changed", b.GetStatus(t.Id).Detail);
        await WaitForState(b, t.Id, SessionState.Connected, 30);
        Assert.NotEqual(helper, Process.GetProcessesByName("sing-box").Select(p => p.Id).Single(b.IsHelperProcess));
        Assert.False(await b.CheckNetworkChangeAsync());   // nothing changed since: no restart
    }

    [Fact]
    public async Task WrongPassword_FailsCleanly()
    {
        if (!Enabled) return;
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => AssertTunnelWorks(T(TunnelType.Trojan, "trojan", "WRONG", ("sni", "test.local"), ("insecure", "true"))));
        Assert.IsNotType<Xunit.Sdk.XunitException>(ex);
    }

    [Fact]
    public async Task Checker_WorksThroughSingBox_AndHelperIsTracked()
    {
        if (!Enabled) return;
        await using var web = new TinyHttpServer();
        await using var b = new TunnelBackends { SingBoxPath = Exe };
        var t = T(TunnelType.Trojan, "trojan", "tr-pass", ("sni", "test.local"), ("insecure", "true"));
        var report = await TunnelChecker.CheckAsync(t, new CheckSettings { TargetHost = "127.0.0.1", TargetPort = web.Port }, backends: b);
        Assert.True(report.Passed, string.Join(" | ", report.Steps.Select(s => $"{s.Number}:{s.Status}:{s.Detail}")));
        var helper = Process.GetProcessesByName("sing-box").Select(p => p.Id).Where(b.IsHelperProcess).ToList();
        Assert.Single(helper);
        await b.DisposeAsync();
        Assert.False(b.IsHelperProcess(helper[0]));
        Assert.True(HasExited(helper[0]), "helper should be stopped");
    }

    private static bool HasExited(int pid)
    {
        try { return Process.GetProcessById(pid).WaitForExit(5000); }
        catch (ArgumentException) { return true; }   // already gone
    }
}
