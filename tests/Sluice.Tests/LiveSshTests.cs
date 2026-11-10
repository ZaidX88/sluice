using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Sluice.Core.Checker;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;
using Sluice.Core.Tunnels;

namespace Sluice.Tests;

/// <summary>
/// End-to-end SSH tests against a real sshd. Opt-in: they run only when these are set, otherwise pass trivially.
///   SLUICE_LIVE_SSH_PORT  (sshd on 127.0.0.1)
///   SLUICE_LIVE_SSH_USER
///   SLUICE_LIVE_SSH_KEY   (path to an authorized private key)
///   SLUICE_LIVE_SSH_BADKEY (optional: a key the server does NOT accept)
/// The "fronts" below stand in for a remote HTTP proxy, a TLS (stunnel) server and a WebSocket-style
/// front, each forwarding to the real sshd.
/// </summary>
public class LiveSshTests
{
    private static readonly int? Port = int.TryParse(Environment.GetEnvironmentVariable("SLUICE_LIVE_SSH_PORT"), out var p) ? p : null;
    private static readonly string? User = Environment.GetEnvironmentVariable("SLUICE_LIVE_SSH_USER");
    private static readonly string? KeyPath = Environment.GetEnvironmentVariable("SLUICE_LIVE_SSH_KEY");
    private static readonly string? BadKeyPath = Environment.GetEnvironmentVariable("SLUICE_LIVE_SSH_BADKEY");
    private static bool Enabled => Port is not null && User is not null && KeyPath is not null;

    private static Tunnel SshTunnel(string address, int port, Dictionary<string, string>? settings = null, string? keyPath = null)
    {
        var t = new Tunnel { Id = 7, Type = TunnelType.Ssh, Address = address, Port = port, Username = User, Label = "live ssh" };
        t.Settings["privatekey"] = File.ReadAllText(keyPath ?? KeyPath!);
        foreach (var (k, v) in settings ?? []) t.Settings[k] = v;
        return t;
    }

    /// <summary>Opens a connection through the tunnel to an echo server and checks bytes come back.</summary>
    private static async Task AssertTunnelWorks(Tunnel t, TunnelBackends? backends = null)
    {
        await using var echo = new EchoServer();
        var profile = Profile.CreateDefault();
        profile.Tunnels.Add(t);
        var own = backends is null;
        backends ??= new TunnelBackends();
        try
        {
            var conn = new TunnelConnector(profile, backends);
            using var c = await conn.ConnectAsync(RuleAction.ViaTunnel(t.Id), "live", "127.0.0.1", echo.Port);
            var s = c.GetStream();
            var msg = Encoding.ASCII.GetBytes($"through ssh {t.Settings.GetValueOrDefault("mode", "direct")}");
            await s.WriteAsync(msg);
            var back = new byte[msg.Length];
            await s.ReadExactlyAsync(back).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(msg, back);
        }
        finally
        {
            if (own) await backends.DisposeAsync();
        }
    }

    [Fact]
    public async Task Direct()
    {
        if (!Enabled) return;
        await AssertTunnelWorks(SshTunnel("127.0.0.1", Port!.Value));
    }

    [Fact]
    public async Task Payload_ThroughRemoteHttpProxy()
    {
        if (!Enabled) return;
        await using var front = new Front(Port!.Value, httpBefore: "HTTP/1.1 200 Connection established\r\n\r\n");
        await AssertTunnelWorks(SshTunnel("127.0.0.1", Port.Value, new()
        {
            ["mode"] = "payload",
            ["remoteproxy"] = $"127.0.0.1:{front.Port}",
            ["payload"] = "CONNECT [host_port] [protocol][crlf]Host: bug.example[crlf][crlf]",
        }));
        Assert.StartsWith($"CONNECT 127.0.0.1:{Port} HTTP/1.1\r\nHost: bug.example", front.Requests[0]);
    }

    [Fact]
    public async Task Tls_WithSni()
    {
        if (!Enabled) return;
        await using var front = new Front(Port!.Value, tls: true);
        await AssertTunnelWorks(SshTunnel("127.0.0.1", front.Port, new() { ["mode"] = "tls", ["sni"] = "www.example.org" }));
        Assert.Equal("www.example.org", front.Sni);
    }

    [Fact]
    public async Task TlsThenPayload_WebSocketStyle()
    {
        if (!Enabled) return;
        await using var front = new Front(Port!.Value, tls: true, httpAfterTls: "HTTP/1.1 101 Switching Protocols\r\n\r\n");
        await AssertTunnelWorks(SshTunnel("127.0.0.1", front.Port, new()
        {
            ["mode"] = "tls-payload",
            ["sni"] = "cdn.example.net",
            ["payload"] = "GET /ws [protocol][crlf]Host: cdn.example.net[crlf]Upgrade: websocket[crlf][crlf]",
        }));
        Assert.Equal("cdn.example.net", front.Sni);
        Assert.StartsWith("GET /ws HTTP/1.1", front.Requests[0]);
    }

    [Fact]
    public async Task PayloadThenTls_ConnectThenSsl()
    {
        if (!Enabled) return;
        await using var front = new Front(Port!.Value, httpBefore: "HTTP/1.1 200 OK\r\n\r\n", tls: true);
        await AssertTunnelWorks(SshTunnel("127.0.0.1", front.Port, new()
        {
            ["mode"] = "payload-tls",
            ["sni"] = "sni.example",
            ["payload"] = "[raw][crlf][crlf]",
        }));
        Assert.Equal("sni.example", front.Sni);
    }

    [Fact]
    public async Task HostKey_TrustedOnFirstUse_ThenEnforced()
    {
        if (!Enabled) return;
        string? learned = null;
        await using (var b = new TunnelBackends())
        {
            b.HostKeyLearned += (_, k) => learned = k;
            await AssertTunnelWorks(SshTunnel("127.0.0.1", Port!.Value), b);
        }
        Assert.Matches("^ssh-[a-z0-9-]+ SHA256:", learned);

        // The same key is accepted silently …
        var known = SshTunnel("127.0.0.1", Port!.Value, new() { ["hostkey"] = learned! });
        await AssertTunnelWorks(known);

        // … a different one is refused.
        var wrong = SshTunnel("127.0.0.1", Port.Value, new() { ["hostkey"] = "ssh-ed25519 SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" });
        var ex = await Assert.ThrowsAsync<ProxyException>(() => AssertTunnelWorks(wrong));
        Assert.Contains("host key changed", ex.Message);
    }

    [Fact]
    public async Task WrongKey_IsAnAuthenticationFailure()
    {
        if (!Enabled || BadKeyPath is null) return;
        var ex = await Assert.ThrowsAsync<ProxyException>(() => AssertTunnelWorks(SshTunnel("127.0.0.1", Port!.Value, keyPath: BadKeyPath)));
        Assert.Contains("authentication failed", ex.Message);
    }

    [Fact]
    public async Task Checker_RunsAllThreeTestsThroughSsh()
    {
        if (!Enabled) return;
        await using var web = new TinyHttpServer();
        await using var b = new TunnelBackends();
        var report = await TunnelChecker.CheckAsync(SshTunnel("127.0.0.1", Port!.Value),
            new CheckSettings { TargetHost = "127.0.0.1", TargetPort = web.Port }, backends: b);
        Assert.True(report.Passed, string.Join(" | ", report.Steps.Select(s => $"{s.Number}:{s.Status}:{s.Detail}")));
        Assert.Contains("SSH session", report.Steps[0].Detail);
        Assert.Contains("HTTP/1.1 200 OK", report.Steps[1].Detail);
    }

    [Fact]
    public async Task Session_IsReused_AndRebuiltAfterEdit()
    {
        if (!Enabled) return;
        await using var b = new TunnelBackends();
        var t = SshTunnel("127.0.0.1", Port!.Value);
        var first = await b.GetLocalSocksAsync(t, CancellationToken.None);
        Assert.Equal(first, await b.GetLocalSocksAsync(t, CancellationToken.None));     // same live session
        t.Settings["sni"] = "changed";                                                  // any setting change → new session
        Assert.NotEqual(first, await b.GetLocalSocksAsync(t, CancellationToken.None));
    }

    // ---------------- stand-ins for remote proxy / stunnel / CDN fronts ----------------

    private sealed class Front : IAsyncDisposable
    {
        private static readonly Lazy<X509Certificate2> Cert = new(() =>
        {
            // RSA: Windows' server-side TLS (SChannel) rejects ephemeral in-memory ECDSA keys.
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=front", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);
        });

        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public List<string> Requests { get; } = new();
        public string? Sni;

        public Front(int sshdPort, string? httpBefore = null, bool tls = false, string? httpAfterTls = null)
        {
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        {
                            try
                            {
                                Stream s = c.GetStream();
                                if (httpBefore is not null) await AnswerHttp(s, httpBefore);
                                if (tls)
                                {
                                    var ssl = new SslStream(s);
                                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                                    {
                                        ServerCertificateSelectionCallback = (_, name) => { Sni = name; return Cert.Value; },
                                    });
                                    s = ssl;
                                }
                                if (httpAfterTls is not null) await AnswerHttp(s, httpAfterTls);
                                using var up = new TcpClient();
                                await up.ConnectAsync(IPAddress.Loopback, sshdPort);
                                var u = up.GetStream();
                                await Task.WhenAny(s.CopyToAsync(u), u.CopyToAsync(s));
                            }
                            catch { /* test front */ }
                        }
                    });
                }
            });
        }

        private async Task AnswerHttp(Stream s, string reply)
        {
            var sb = new StringBuilder();
            var b = new byte[1];
            while (!sb.ToString().EndsWith("\r\n\r\n") && await s.ReadAsync(b) == 1) sb.Append((char)b[0]);
            lock (Requests) Requests.Add(sb.ToString());
            await s.WriteAsync(Encoding.ASCII.GetBytes(reply));
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _l.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
