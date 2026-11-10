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
