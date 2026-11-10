using System.Net.Sockets;
using System.Text;
using Sluice.Core.Profiles;
using Sluice.Core.Tunnels;

namespace Sluice.Tests;

public class ShareLinkTests
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";

    private static Tunnel P(string link)
    {
        var r = ShareLinks.Parse(link);
        Assert.True(r.Ok, r.Error);
        return r.Tunnel!;
    }

    private static string B64(string s, bool urlSafe = false)
    {
        var b = Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        return urlSafe ? b.TrimEnd('=').Replace('+', '-').Replace('/', '_') : b;
    }

    [Fact]
    public void Vless_RealityVision()
    {
        var t = P($"vless://{Uuid}@edge.example:443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.microsoft.com&fp=chrome&pbk=PUBKEY&sid=ab12&type=tcp#My%20Server%20%F0%9F%87%A9%F0%9F%87%AA");
        Assert.Equal(TunnelType.VLess, t.Type);
        Assert.Equal(("edge.example", 443), (t.Address, t.Port));
        Assert.Equal("My Server 🇩🇪", t.Label);
        Assert.Equal(Uuid, t.Settings["uuid"]);
        Assert.Equal("xtls-rprx-vision", t.Settings["flow"]);
        Assert.Equal("reality", t.Settings["security"]);
        Assert.Equal("www.microsoft.com", t.Settings["sni"]);
        Assert.Equal("chrome", t.Settings["fingerprint"]);
        Assert.Equal("PUBKEY", t.Settings["publickey"]);
        Assert.Equal("ab12", t.Settings["shortid"]);
        Assert.False(t.Settings.ContainsKey("transport"));
    }

    [Fact]
    public void Vless_WebSocketTls_Ipv6()
    {
        var t = P($"vless://{Uuid}@[2001:db8::1]:8443?security=tls&type=ws&path=%2Fws%3Fed%3D2048&host=cdn.example&allowInsecure=1");
        Assert.Equal(("2001:db8::1", 8443), (t.Address, t.Port));
        Assert.Equal("ws", t.Settings["transport"]);
        Assert.Equal("/ws?ed=2048", t.Settings["path"]);
        Assert.Equal("cdn.example", t.Settings["host"]);
        Assert.Equal("true", t.Settings["insecure"]);
        Assert.Null(t.Label);
    }

    [Fact]
    public void Vmess_Base64Json()
    {
        var json = $$"""{"v":"2","ps":"vm","add":"1.2.3.4","port":"8080","id":"{{Uuid}}","aid":"0","scy":"aes-128-gcm","net":"ws","type":"none","host":"h.example","path":"/ray","tls":"tls","sni":"s.example","alpn":"h2,http/1.1","fp":"firefox"}""";
        var t = P("vmess://" + B64(json));
        Assert.Equal(TunnelType.VMess, t.Type);
        Assert.Equal(("1.2.3.4", 8080, "vm"), (t.Address, t.Port, t.Label));
        Assert.Equal("aes-128-gcm", t.Settings["cipher"]);
        Assert.Equal("ws", t.Settings["transport"]);
        Assert.Equal("/ray", t.Settings["path"]);
        Assert.Equal("tls", t.Settings["security"]);
        Assert.Equal("h2,http/1.1", t.Settings["alpn"]);
        Assert.Equal("firefox", t.Settings["fingerprint"]);

        // numeric port / aid, grpc service name in "path"
        var grpc = P("vmess://" + B64($$"""{"add":"g.example","port":443,"id":"{{Uuid}}","aid":0,"net":"grpc","path":"svc"}""", urlSafe: true));
        Assert.Equal(443, grpc.Port);
        Assert.Equal("svc", grpc.Settings["servicename"]);
    }

    [Fact]
    public void Trojan_DefaultsToTls_Grpc()
    {
        var t = P("trojan://p%40ss@tr.example:443?type=grpc&serviceName=gun&sni=tr.example#tr");
        Assert.Equal(TunnelType.Trojan, t.Type);
        Assert.Equal("p@ss", t.Password);
        Assert.Equal("grpc", t.Settings["transport"]);
        Assert.Equal("gun", t.Settings["servicename"]);
        Assert.False(t.Settings.ContainsKey("security"));   // Trojan is always TLS
    }

    [Fact]
    public void Shadowsocks_AllThreeForms()
    {
        var sip002 = P($"ss://{B64("chacha20-ietf-poly1305:secret", urlSafe: true)}@ss.example:8388#ss1");
        Assert.Equal(("chacha20-ietf-poly1305", "secret", "ss1"), (sip002.Settings["method"], sip002.Password, sip002.Label));

        var plain2022 = P("ss://2022-blake3-aes-128-gcm:AAAAAAAAAAAAAAAAAAAAAA%3D%3D@1.2.3.4:443");
        Assert.Equal("2022-blake3-aes-128-gcm", plain2022.Settings["method"]);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAA==", plain2022.Password);

        var legacy = P("ss://" + B64("aes-256-gcm:pa:ss@legacy.example:8000") + "#old");
        Assert.Equal(("legacy.example", 8000, "pa:ss"), (legacy.Address, legacy.Port, legacy.Password));

        var plugin = P($"ss://{B64("aes-128-gcm:x", true)}@p.example:80/?plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dbing.com");
        Assert.Equal("obfs-local", plugin.Settings["plugin"]);
        Assert.Equal("obfs=http;obfs-host=bing.com", plugin.Settings["pluginopts"]);
    }

    [Fact]
    public void Hysteria2_AndAlias()
    {
        var t = P("hysteria2://pw@h.example:8443/?sni=s.example&insecure=1&obfs=salamander&obfs-password=ob&up=50%20mbps#hy");
        Assert.Equal(TunnelType.Hysteria2, t.Type);
        Assert.Equal(("pw", "s.example", "true", "ob", "50"), (t.Password, t.Settings["sni"], t.Settings["insecure"], t.Settings["obfspassword"], t.Settings["upmbps"]));
        Assert.Equal(TunnelType.Hysteria2, P("hy2://pw@h.example:443").Type);
    }

    [Fact]
    public void Tuic()
    {
        var t = P($"tuic://{Uuid}:p%3Aw@t.example:443?congestion_control=cubic&alpn=h3&sni=t.example&allow_insecure=1#tu");
        Assert.Equal((Uuid, "p:w", "cubic", "true"), (t.Settings["uuid"], t.Password, t.Settings["congestion"], t.Settings["insecure"]));
    }

    [Fact]
    public void PlainProxiesAndSsh()
    {
        var s = P("socks5://user:p%20w@10.0.0.1:1080#office");
        Assert.Equal((TunnelType.Socks5, "user", "p w", "office"), (s.Type, s.Username, s.Password, s.Label));
        var h = P("http://proxy.example:3128");
        Assert.Equal((TunnelType.Http, 3128, (string?)null), (h.Type, h.Port, h.Username));
        var ssh = P("ssh://root:pw@vps.example#vps");
        Assert.Equal((TunnelType.Ssh, 22, "root"), (ssh.Type, ssh.Port, ssh.Username));
        var b64auth = P($"socks://{B64("u:p")}@1.1.1.1:1080");
        Assert.Equal(("u", "p"), (b64auth.Username, b64auth.Password));
    }

    [Theory]
    [InlineData("wireguard://key@1.2.3.4:51820", "not supported")]
    [InlineData("vless://not-a-uuid@x:443", "UUID")]
    [InlineData("trojan://@x:443", "password")]
    [InlineData("vless://b831381d-6324-4d53-ad4f-8cda48b30811@x:443?type=kcp", "kcp")]
    [InlineData("vmess://!!!", "base64")]
    [InlineData("vless://b831381d-6324-4d53-ad4f-8cda48b30811@:443", "server")]
    public void UnusableLinks_GiveReasons(string link, string reason)
    {
        var r = ShareLinks.Parse(link);
        Assert.False(r.Ok);
        Assert.Contains(reason, r.Error, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string> RoundTripLinks => new()
    {
        $"vless://{Uuid}@edge.example:443?flow=xtls-rprx-vision&security=reality&sni=www.microsoft.com&fp=chrome&pbk=PUB&sid=ab#Reality",
        $"vless://{Uuid}@[2001:db8::1]:443?security=tls&type=grpc&serviceName=svc&allowInsecure=1",
        "trojan://p%2Bw@tr.example:443?type=ws&path=%2Fx&host=h#t%20r",
        "ss://2022-blake3-aes-128-gcm:AAAAAAAAAAAAAAAAAAAAAA%3D%3D@1.2.3.4:443#ss",
        $"ss://{Convert.ToBase64String("aes-128-gcm:x"u8)}@p.example:80/?plugin=v2ray-plugin%3Bmode%3Dwebsocket",
        "hysteria2://pw@h.example:443?sni=s&insecure=1&obfs=salamander&obfs-password=o&up=10&down=100#hy",
        $"tuic://{Uuid}:pw@t.example:443?congestion_control=bbr&alpn=h3#tu",
        "socks5://u:p@10.0.0.1:1080#s",
        "socks4a://10.0.0.1:1080",
        "https://u@proxy.example:443",
        "ssh://root:pw@vps.example:2222#vps",
    };

    [Theory]
    [MemberData(nameof(RoundTripLinks))]
    public void ToLink_RoundTrips(string link)
    {
        var a = P(link);
        var exported = ShareLinks.ToLink(a);
        var b = P(exported);
        Assert.Equal(TunnelBackends.Fingerprint(a), TunnelBackends.Fingerprint(b));
        Assert.Equal(a.Label, b.Label);
    }

    [Fact]
    public void Vmess_RoundTrips()
    {
        var a = P("vmess://" + B64($$"""{"ps":"vm","add":"1.2.3.4","port":"80","id":"{{Uuid}}","aid":"0","net":"ws","path":"/r","host":"h","tls":"tls","sni":"s"}"""));
        var b = P(ShareLinks.ToLink(a));
        Assert.Equal(TunnelBackends.Fingerprint(a), TunnelBackends.Fingerprint(b));
        Assert.Equal("vm", b.Label);
    }

    [Fact]
    public void ParseMany_Base64Subscription_AndPlainLists()
    {
        var lines = $"vless://{Uuid}@a.example:443#A\r\ntrojan://p@b.example:443#B\nwireguard://x@c:1\n";
        foreach (var body in new[] { lines, B64(lines), B64(lines, urlSafe: true), "# comment\n" + lines })
        {
            var r = ShareLinks.ParseMany(body);
            Assert.Equal(3, r.Count);
            Assert.Equal(["A", "B"], r.Where(x => x.Ok).Select(x => x.Tunnel!.Label!).ToArray());
        }
    }
}

public class SubscriptionTests
{
    private const string U1 = "11111111-1111-1111-1111-111111111111";
    private const string U2 = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void ImportLinks_AddsNewTunnels_SkipsDuplicates()
    {
        var p = Profile.CreateDefault();
        var r = Subscriptions.ImportLinks(p, $"vless://{U1}@a.example:443#A\ntrojan://p@b.example:443#B\nfoo://x");
        Assert.Equal(2, r.Added.Count);
        Assert.Single(r.Warnings);
        Assert.Equal([100, 101], p.Tunnels.Select(t => t.Id).ToArray());
        var again = Subscriptions.ImportLinks(p, $"vless://{U1}@a.example:443#A renamed");
        Assert.Empty(again.Added);
        Assert.Single(again.Unchanged);
        Assert.Equal(2, p.Tunnels.Count);
    }

    [Fact]
    public void Apply_KeepsIds_UpdatesMovedServers_AndRemovesStaleTunnels()
    {
        var p = Profile.CreateDefault();
        var sub = Subscriptions.AddSubscription(p, "https://sub.example/x");
        var first = Subscriptions.Apply(p, sub,
            $"vless://{U1}@a.example:443#Germany\nvless://{U2}@b.example:443#Japan\ntrojan://pw@c.example:443#US");
        Assert.Equal(3, first.Added.Count);
        var ids = p.Tunnels.ToDictionary(t => t.Label!, t => t.Id);
        Assert.All(p.Tunnels, t => Assert.Equal(sub.Id, t.SubscriptionId));
        p.Rules.Insert(0, new Rule { Name = "Use US", Action = RuleAction.ViaTunnel(ids["US"]) });
        p.Tunnels.First(t => t.Label == "Japan").Settings["hostkey"] = "ignored-for-vless";

        // Germany unchanged, Japan moved to a new host (same name), US dropped, France new.
        var second = Subscriptions.Apply(p, sub,
            $"vless://{U1}@a.example:443#Germany\nvless://{U2}@b2.example:443#Japan\nvless://{U1}@f.example:443#France");
        Assert.Equal(["Germany"], second.Unchanged.Select(t => t.Label!).ToArray());
        Assert.Equal(["Japan"], second.Updated.Select(t => t.Label!).ToArray());
        Assert.Equal(["France"], second.Added.Select(t => t.Label!).ToArray());
        Assert.Equal(["US"], second.Removed.Select(t => t.Label!).ToArray());
        Assert.Equal(ids["Germany"], p.Tunnels.Single(t => t.Label == "Germany").Id);
        var japan = p.Tunnels.Single(t => t.Label == "Japan");
        Assert.Equal((ids["Japan"], "b2.example"), (japan.Id, japan.Address));
        Assert.False(japan.Settings.ContainsKey("hostkey"));     // server moved: forget its key
        Assert.Equal(RuleActionType.Direct, p.Rules[0].Action.Type);
        Assert.Contains(second.Warnings, w => w.Contains("Use US"));
    }

    [Fact]
    public void Apply_BrokenBody_TouchesNothing()
    {
        var p = Profile.CreateDefault();
        var sub = Subscriptions.AddSubscription(p, "https://sub.example/x");
        Subscriptions.Apply(p, sub, $"vless://{U1}@a.example:443#A");
        var r = Subscriptions.Apply(p, sub, "<html>502 Bad Gateway</html>");
        Assert.Single(p.Tunnels);
        Assert.Contains("web page", r.Warnings.Single());
        Assert.Contains("Clash", Subscriptions.Apply(p, sub, "proxies:\n  - name: x").Warnings.Single());
    }

    [Fact]
    public void ManualTunnels_AreNotTouchedBySubscriptions()
    {
        var p = Profile.CreateDefault();
        Subscriptions.ImportLinks(p, $"vless://{U1}@a.example:443#Mine");
        var sub = Subscriptions.AddSubscription(p, "https://sub.example/x");
        Subscriptions.Apply(p, sub, $"vless://{U1}@a.example:443#Theirs");
        Subscriptions.Apply(p, sub, $"trojan://x@z.example:443#Other");
        Assert.Contains(p.Tunnels, t => t.Label == "Mine" && t.SubscriptionId is null);
        Assert.DoesNotContain(p.Tunnels, t => t.Label == "Theirs");
        Assert.Throws<FormatException>(() => Subscriptions.AddSubscription(p, "ftp://x"));
    }

    [Fact]
    public void SluiceProfile_StoresSubscriptions()
    {
        var p = Profile.CreateDefault();
        var sub = Subscriptions.AddSubscription(p, "https://sub.example/token", "Provider");
        sub.LastUpdated = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
        Subscriptions.Apply(p, sub, $"vless://{U1}@a.example:443#A");
        var json = SluiceProfileSerializer.Serialize(p);
        Assert.DoesNotContain(U1, json);   // uuid stays encrypted
        var back = SluiceProfileSerializer.Parse(json);
        var s = Assert.Single(back.Subscriptions);
        Assert.Equal(("Provider", "https://sub.example/token", sub.LastUpdated), (s.Name, s.Url, s.LastUpdated));
        Assert.Equal(s.Id, back.Tunnels.Single().SubscriptionId);
    }

    /// <summary>Serves a fixed body and remembers the User-Agent.</summary>
    internal sealed class SubServer(string body, int status = 200) : LoopbackServer
    {
        public string? UserAgent;

        protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
        {
            var buf = new byte[4096];
            var got = 0;
            while (!Encoding.ASCII.GetString(buf, 0, got).Contains("\r\n\r\n"))
            {
                var n = await s.ReadAsync(buf.AsMemory(got), ct);
                if (n == 0) return;
                got += n;
            }
            UserAgent = Encoding.ASCII.GetString(buf, 0, got).Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))?[11..].Trim();
            var bytes = Encoding.UTF8.GetBytes(body);
            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), ct);
            await s.WriteAsync(bytes, ct);
        }
    }

    [Fact]
    public async Task UpdateAsync_FetchesOverHttp()
    {
        var links = $"vless://{U1}@a.example:443#A\ntrojan://p@b.example:443#B";
        await using var server = new SubServer(Convert.ToBase64String(Encoding.UTF8.GetBytes(links)));
        var p = Profile.CreateDefault();
        var sub = Subscriptions.AddSubscription(p, $"http://127.0.0.1:{server.Port}/sub");
        var resolverCalls = 0;
        var r = await Subscriptions.UpdateAsync(p, sub, (h, ct) => { resolverCalls++; return Task.FromResult(System.Net.Dns.GetHostAddresses(h)); });
        Assert.Equal(2, r.Added.Count);
        Assert.NotNull(sub.LastUpdated);
        Assert.Null(sub.LastError);
        Assert.StartsWith("Sluice/", server.UserAgent);
        Assert.Equal(0, resolverCalls);   // IP literal: no lookup needed

        await using var bad = new SubServer("nope", 403);
        var sub2 = Subscriptions.AddSubscription(p, $"http://127.0.0.1:{bad.Port}/sub");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Subscriptions.UpdateAsync(p, sub2));
        Assert.Contains("403", ex.Message);
        Assert.Contains("403", sub2.LastError);
    }
}
