using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;
using Sluice.Core.Tunnels;

namespace Sluice.Tests;

public class TunnelTransportTests
{
    [Fact]
    public void Payload_PlaceholdersAndSplit()
    {
        var parts = TunnelTransport.RenderPayload(
            "[raw][crlf]Host: bug.example[crlf]User-Agent: [ua][crlf][crlf][split]GET / [protocol][lf]",
            "ssh.example", 443, "UA/1");
        Assert.Equal(2, parts.Count);
        Assert.Equal("CONNECT ssh.example:443 HTTP/1.1\r\nHost: bug.example\r\nUser-Agent: UA/1\r\n\r\n", parts[0]);
        Assert.Equal("GET / HTTP/1.1\n", parts[1]);
        Assert.Equal(["x:22 22 x"], TunnelTransport.RenderPayload("[HOST_PORT] [Port] [host]", "x", 22, ""));
    }

    [Theory]
    [InlineData("direct", TransportMode.Direct)]
    [InlineData("payload", TransportMode.Payload)]
    [InlineData("SSL", TransportMode.Tls)]
    [InlineData("tls_payload", TransportMode.TlsPayload)]
    [InlineData("payload-ssl", TransportMode.PayloadTls)]
    [InlineData("nonsense", TransportMode.Direct)]
    public void Modes_Parse(string text, TransportMode mode) => Assert.Equal(mode, TransportOptions.ParseMode(text));

    [Theory]
    [InlineData("proxy.example:8080", "proxy.example", 8080)]
    [InlineData("proxy.example", "proxy.example", 3128)]
    [InlineData("[2001:db8::1]:8443", "2001:db8::1", 8443)]
    [InlineData("2001:db8::1", "2001:db8::1", 3128)]
    public void HostPort_Split(string s, string host, int port) => Assert.Equal((host, port), TunnelTransport.SplitHostPort(s, 3128));

    // ---------- live transports against in-process servers ----------

    /// <summary>Accepts one connection; optional TLS; optionally answers one HTTP request; then echoes.</summary>
    private sealed class FakeFront : IAsyncDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly Task _run;
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public string? ReceivedRequest;
        public string? ReceivedSni;

        public FakeFront(bool tls, string? httpReply)
        {
            _l.Start();
            _run = Task.Run(async () =>
            {
                using var c = await _l.AcceptTcpClientAsync();
                Stream s = c.GetStream();
                if (tls)
                {
                    var ssl = new SslStream(s);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateSelectionCallback = (_, name) => { ReceivedSni = name; return Cert.Value; },
                    });
                    s = ssl;
                }
                if (httpReply is not null)
                {
                    var sb = new StringBuilder();
                    var b = new byte[1];
                    while (!sb.ToString().EndsWith("\r\n\r\n") && await s.ReadAsync(b) == 1) sb.Append((char)b[0]);
                    ReceivedRequest = sb.ToString();
                    await s.WriteAsync(Encoding.ASCII.GetBytes(httpReply));
                }
                var buf = new byte[256];
                int n;
                while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n));
            });
        }

        public async ValueTask DisposeAsync()
        {
            _l.Stop();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private static readonly Lazy<X509Certificate2> Cert = new(() =>
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);
    });

    private static async Task AssertEchoes(Stream s)
    {
        var msg = "SSH-2.0-test\r\n"u8.ToArray();
        await s.WriteAsync(msg);
        var back = new byte[msg.Length];
        await s.ReadExactlyAsync(back).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(msg, back);
    }

    [Fact]
    public async Task Payload_ThroughRemoteProxy_ThenStreamIsClean()
    {
        await using var front = new FakeFront(tls: false, httpReply: "HTTP/1.1 200 Connection established\r\n\r\n");
        var o = new TransportOptions { Mode = TransportMode.Payload, Payload = "CONNECT [host_port] [protocol][crlf][crlf]", RemoteProxy = $"127.0.0.1:{front.Port}" };
        await using var s = await TunnelTransport.ConnectAsync("ssh.example", 22, o, null, null, CancellationToken.None);
        await AssertEchoes(s);
        Assert.Equal("CONNECT ssh.example:22 HTTP/1.1\r\n\r\n", front.ReceivedRequest);
    }

    [Fact]
    public async Task Payload_Refused_IsReported()
    {
        await using var front = new FakeFront(tls: false, httpReply: "HTTP/1.1 403 Forbidden\r\n\r\n");
        var o = new TransportOptions { Mode = TransportMode.Payload, Payload = "GET / [protocol][crlf][crlf]" };
        var ex = await Assert.ThrowsAsync<ProxyException>(() => TunnelTransport.ConnectAsync("127.0.0.1", front.Port, o, null, null, CancellationToken.None));
        Assert.Contains("403 Forbidden", ex.Message);
    }

    [Fact]
    public async Task Tls_UsesTheChosenSni()
    {
        await using var front = new FakeFront(tls: true, httpReply: null);
        var o = new TransportOptions { Mode = TransportMode.Tls, Sni = "cdn.example.com" };
        await using var s = await TunnelTransport.ConnectAsync("127.0.0.1", front.Port, o, null, null, CancellationToken.None);
        await AssertEchoes(s);
        Assert.Equal("cdn.example.com", front.ReceivedSni);
    }

    [Fact]
    public async Task TlsThenPayload_WebSocketStyle()
    {
        await using var front = new FakeFront(tls: true, httpReply: "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n\r\n");
        var o = new TransportOptions
        {
            Mode = TransportMode.TlsPayload, Sni = "front.example",
            Payload = "GET / [protocol][crlf]Host: front.example[crlf]Upgrade: websocket[crlf][crlf]",
        };
        await using var s = await TunnelTransport.ConnectAsync("127.0.0.1", front.Port, o, null, null, CancellationToken.None);
        await AssertEchoes(s);
        Assert.Equal("front.example", front.ReceivedSni);
        Assert.StartsWith("GET / HTTP/1.1\r\nHost: front.example", front.ReceivedRequest);
    }

    [Fact]
    public void Settings_RoundTripThroughOptions()
    {
        var t = new Tunnel { Type = TunnelType.Ssh };
        t.Settings["mode"] = "tls-payload";
        t.Settings["payload"] = "x";
        t.Settings["sni"] = " a.example ";
        t.Settings["skipresponse"] = "TRUE";
        var o = TransportOptions.From(t.Settings);
        Assert.Equal((TransportMode.TlsPayload, "x", "a.example", true, true, true),
            (o.Mode, o.Payload, o.Sni, o.SkipResponse, o.UsesPayload, o.UsesTls));
    }

    [Fact]
    public void BackendFingerprint_IgnoresLearnedHostKey_ButNotCredentials()
    {
        var t = new Tunnel { Id = 1, Type = TunnelType.Ssh, Address = "h", Port = 22, Username = "u", Password = "p" };
        var a = TunnelBackends.Fingerprint(t);
        t.Settings["hostkey"] = "ssh-ed25519 SHA256:abc";
        Assert.Equal(a, TunnelBackends.Fingerprint(t));
        t.Password = "changed";
        Assert.NotEqual(a, TunnelBackends.Fingerprint(t));
    }

    [Fact]
    public async Task SshTunnel_AsLaterChainHop_IsRejectedClearly()
    {
        var p = Profile.CreateDefault();
        p.Tunnels.Add(new Tunnel { Id = 1, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = 1 });
        p.Tunnels.Add(new Tunnel { Id = 2, Type = TunnelType.Ssh, Address = "h", Port = 22, Username = "u", Password = "p" });
        var chain = new TunnelChain { Id = 9, Name = "c" };
        chain.Hops.AddRange([new ChainHop { TunnelId = 1 }, new ChainHop { TunnelId = 2 }]);
        p.Chains.Add(chain);
        await using var backends = new TunnelBackends();
        var conn = new TunnelConnector(p, backends);
        var ex = await Assert.ThrowsAsync<ProxyException>(() => conn.ConnectAsync(RuleAction.ViaChain(9), "r", "example.com", 80));
        Assert.Contains("first hop", ex.Message);
    }
}
