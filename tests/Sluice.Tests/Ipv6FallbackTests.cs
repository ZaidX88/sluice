using System.Net;
using System.Net.Sockets;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Tests;

/// <summary>
/// The shared IPv6 fallback tracker (#29, part of #4): only "can't reach" failures of IPv6 addresses before the
/// connection opened, on a route that otherwise works, mark it; marks expire; IPv4 and hostnames are never marked.
/// </summary>
public class Ipv6FallbackTests
{
    private const string Route = "SSH alice@ssh.example";
    private static readonly ProxyException GeneralFailure = new("SOCKS5: general SOCKS server failure") { Socks5Reply = 0x01 };
    private static readonly ProxyException HostUnreachable = new("SOCKS5: host unreachable") { Socks5Reply = 0x04 };
    private static readonly ProxyException Refused = new("SOCKS5: connection refused") { Socks5Reply = 0x05 };

    private sealed class Clock
    {
        public DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan t) => Now += t;
    }

    private static (Ipv6Fallback Tracker, Clock Clock, List<string> Marked) Create()
    {
        var clock = new Clock();
        var tracker = new Ipv6Fallback(() => clock.Now);
        var marked = new List<string>();
        tracker.Marked += (route, span) =>
        {
            Assert.Equal(Ipv6Fallback.MarkFor, span);
            marked.Add(route);
        };
        return (tracker, clock, marked);
    }

    [Fact]
    public void TwoIpv6AddressesUnreachable_WhileIpv4Works_MarksTheRouteOnce()
    {
        var (t, _, marked) = Create();
        t.Opened(Route, "192.0.2.10");
        t.FailedBeforeOpen(Route, "2001:db8::1", GeneralFailure);
        Assert.False(t.ShouldRefuse(Route));                 // one site may just be down
        t.FailedBeforeOpen(Route, "2001:db8::2", HostUnreachable);
        Assert.True(t.ShouldRefuse(Route));
        t.FailedBeforeOpen(Route, "2001:db8::3", GeneralFailure);
        Assert.Equal([Route], marked);                       // announced once
        Assert.False(t.ShouldRefuse("SOCKS5 198.51.100.7:1080"));   // other routes unaffected
    }

    [Fact]
    public void SuccessAfterTheFailures_AlsoCounts()
    {
        var (t, _, _) = Create();
        t.FailedBeforeOpen(Route, "2001:db8::1", GeneralFailure);
        t.FailedBeforeOpen(Route, "2001:db8::2", GeneralFailure);
        Assert.False(t.ShouldRefuse(Route));                 // no proof yet that the tunnel itself works
        t.Opened(Route, "example.com");
        Assert.True(t.ShouldRefuse(Route));
    }

    [Fact]
    public void SameAddressTwice_DoesNotMark()
    {
        var (t, _, _) = Create();
        t.Opened(Route, "192.0.2.10");
        t.FailedBeforeOpen(Route, "2001:db8::1", GeneralFailure);
        t.FailedBeforeOpen(Route, "2001:db8:0:0::1", GeneralFailure);   // same address, written differently
        Assert.False(t.ShouldRefuse(Route));
    }

    [Fact]
    public void TunnelDown_NothingOpened_DoesNotMark()
    {
        var (t, _, _) = Create();
        t.FailedBeforeOpen(Route, "2001:db8::1", GeneralFailure);
        t.FailedBeforeOpen(Route, "2001:db8::2", GeneralFailure);
        t.FailedBeforeOpen(Route, "2001:db8::3", GeneralFailure);
        Assert.False(t.ShouldRefuse(Route));
    }

    [Fact]
    public void OtherErrors_NeverCount()
    {
        var (t, _, _) = Create();
        t.Opened(Route, "192.0.2.10");
        Exception[] errors =
        [
            Refused,
            new ProxyException("SOCKS5: TTL expired") { Socks5Reply = 0x06 },
            new ProxyException("Tunnel \"x\" is turned off"),
            new TimeoutException("SSH tunnel x did not answer within 20 s"),
            new SocketException((int)SocketError.ConnectionRefused),
            new IOException("connection reset"),
        ];
        var i = 1;
        foreach (var e in errors) t.FailedBeforeOpen(Route, $"2001:db8::{i++}", e);
        Assert.False(t.ShouldRefuse(Route));
    }

    [Theory]
    [InlineData("192.0.2.1", "192.0.2.2")]
    [InlineData("a.example", "b.example")]
    [InlineData("::ffff:192.0.2.1", "::ffff:192.0.2.2")]   // IPv4 written as IPv6
    public void Ipv4AndHostnames_NeverMark(string a, string b)
    {
        var (t, _, _) = Create();
        t.Opened(Route, "192.0.2.10");
        t.FailedBeforeOpen(Route, a, GeneralFailure);
        t.FailedBeforeOpen(Route, b, HostUnreachable);
        Assert.False(t.ShouldRefuse(Route));
    }

    [Fact]
    public void Ipv6Success_ClearsTheEvidence()
    {
        var (t, _, _) = Create();
        t.Opened(Route, "192.0.2.10");
        t.FailedBeforeOpen(Route, "2001:db8::1", GeneralFailure);
        t.Opened(Route, "2001:db8::99");                     // this tunnel does reach IPv6
        t.FailedBeforeOpen(Route, "2001:db8::2", GeneralFailure);
        Assert.False(t.ShouldRefuse(Route));
    }

    [Fact]
    public void FailuresOutsideTheWindow_DoNotAddUp()
    {
        var (t, c, _) = Create();
        t.FailedBeforeOpen(Route, "2001:db8::1", GeneralFailure);
        c.Advance(Ipv6Fallback.Window + TimeSpan.FromSeconds(1));
        t.Opened(Route, "192.0.2.10");
        t.FailedBeforeOpen(Route, "2001:db8::2", GeneralFailure);
        Assert.False(t.ShouldRefuse(Route));
    }

    [Fact]
    public void OldIpv4Success_IsNotEnough()
    {
        var (t, c, _) = Create();
        t.Opened(Route, "192.0.2.10");
        c.Advance(Ipv6Fallback.Window + TimeSpan.FromSeconds(1));
        t.FailedBeforeOpen(Route, "2001:db8::1", GeneralFailure);
        t.FailedBeforeOpen(Route, "2001:db8::2", GeneralFailure);
        Assert.False(t.ShouldRefuse(Route));
    }

    [Fact]
    public void Mark_Expires_AndCanBeSetAgain()
    {
        var (t, c, marked) = Create();
        t.Opened(Route, "192.0.2.10");
        t.FailedBeforeOpen(Route, "2001:db8::1", GeneralFailure);
        t.FailedBeforeOpen(Route, "2001:db8::2", GeneralFailure);
        Assert.True(t.ShouldRefuse(Route));

        c.Advance(Ipv6Fallback.MarkFor - TimeSpan.FromSeconds(1));
        Assert.True(t.ShouldRefuse(Route));
        c.Advance(TimeSpan.FromSeconds(2));
        Assert.False(t.ShouldRefuse(Route));

        t.Opened(Route, "192.0.2.10");
        t.FailedBeforeOpen(Route, "2001:db8::3", GeneralFailure);
        Assert.False(t.ShouldRefuse(Route));                 // the old failures don't carry over
        t.FailedBeforeOpen(Route, "2001:db8::4", GeneralFailure);
        Assert.True(t.ShouldRefuse(Route));
        Assert.Equal(2, marked.Count);
    }

    [Fact]
    public void Clear_ForgetsMarks()
    {
        var (t, _, _) = Create();
        t.Opened(Route, "192.0.2.10");
        t.FailedBeforeOpen(Route, "2001:db8::1", GeneralFailure);
        t.FailedBeforeOpen(Route, "2001:db8::2", GeneralFailure);
        t.Clear();
        Assert.False(t.ShouldRefuse(Route));
    }

    [Fact]
    public async Task Socks5Handshake_KeepsTheReplyCode()
    {
        await using var upstream = new Socks5TestServer();
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, upstream.Port);
        var ex = await Assert.ThrowsAsync<ProxyException>(() =>
            ProxyHandshake.Socks5Async(c.GetStream(), "refuse.test", 80, null, null, CancellationToken.None));
        Assert.Equal((byte)0x05, ex.Socks5Reply);
    }

    /// <summary>A tunnel whose server has no IPv6: IPv6 targets get a general failure, everything else echoes.</summary>
    private sealed class NoIpv6Socks5Server : LoopbackServer
    {
        protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
        {
            var head = await Read(s, 2, ct);
            await Read(s, head[1], ct);
            await s.WriteAsync(new byte[] { 5, 0 }, ct);
            var req = await Read(s, 4, ct);
            var atyp = req[3];
            _ = atyp switch
            {
                1 => await Read(s, 4, ct),
                4 => await Read(s, 16, ct),
                _ => await Read(s, (await Read(s, 1, ct))[0], ct),
            };
            var pb = await Read(s, 2, ct);
            if (atyp == 4)
            {
                await s.WriteAsync(new byte[] { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
                return;
            }
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
            await RelayAsync(s, "127.0.0.1", (pb[0] << 8) | pb[1], ct);
        }
    }

    [Fact]
    public async Task Engine_MarksATunnelThatCannotReachIpv6_AndLogsIt()
    {
        await using var echo = new EchoServer();
        await using var tunnel = new NoIpv6Socks5Server();
        var profile = Profile.CreateDefault();
        profile.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = tunnel.Port });
        profile.Rules.Insert(0, new Rule { Name = "All", Action = RuleAction.ViaTunnel(100) });
        var engine = new RoutingEngine(profile);
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        engine.Tracker.Log += (_, line) => log.Enqueue(line);
        await using var inbound = new Socks5Inbound(engine, new IPEndPoint(IPAddress.Loopback, 0));
        inbound.Start();
        var route = engine.Describe(RuleAction.ViaTunnel(100));

        async Task<Exception?> Connect(string host)
        {
            using var c = new TcpClient();
            await c.ConnectAsync(inbound.LocalEndPoint);
            try { await ProxyHandshake.Socks5Async(c.GetStream(), host, echo.Port, null, null, CancellationToken.None); return null; }
            catch (Exception ex) { return ex; }
        }

        Assert.Null(await Connect("echo.test"));                 // the tunnel works for hostnames
        Assert.NotNull(await Connect("2001:db8::1"));
        Assert.False(engine.Ipv6Fallback.ShouldRefuse(route));
        Assert.NotNull(await Connect("2001:db8::2"));
        Assert.True(engine.Ipv6Fallback.ShouldRefuse(route));
        Assert.Contains(log, l => l.Contains("refusing IPv6 through"));

        engine.Reload(profile);                                  // a profile change forgets the marks
        Assert.False(engine.Ipv6Fallback.ShouldRefuse(route));
    }
}
