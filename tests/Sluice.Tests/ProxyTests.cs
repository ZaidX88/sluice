using System.Net.Sockets;
using System.Text;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Tests;

public class ProxyTests
{
    private static async Task AssertEchoes(TcpClient c)
    {
        var s = c.GetStream();
        var msg = Encoding.ASCII.GetBytes("hello through proxy");
        await s.WriteAsync(msg);
        var back = new byte[msg.Length];
        await s.ReadExactlyAsync(back).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(msg, back);
    }

    private static (Profile, ProxyConnector) Setup(params ProxyServer[] proxies)
    {
        var p = Profile.CreateDefault();
        p.Proxies.AddRange(proxies);
        return (p, new ProxyConnector(p));
    }

    [Fact]
    public async Task Socks5_NoAuth_Hostname()
    {
        await using var echo = new EchoServer();
        await using var socks = new Socks5TestServer();
        var (_, conn) = Setup(new ProxyServer { Id = 100, Type = ProxyType.Socks5, Address = "127.0.0.1", Port = socks.Port });

        using var c = await conn.ConnectAsync(RuleAction.ViaProxy(100), "t", "echo.test", echo.Port);
        await AssertEchoes(c);
        Assert.Equal($"echo.test:{echo.Port}", socks.LastTarget);
    }

    [Fact]
    public async Task Socks5_UserPass()
    {
        await using var echo = new EchoServer();
        await using var socks = new Socks5TestServer("alice", "s3cret");
        var (_, conn) = Setup(new ProxyServer { Id = 1, Type = ProxyType.Socks5, Address = "127.0.0.1", Port = socks.Port, Username = "alice", Password = "s3cret" });

        using var c = await conn.ConnectAsync(RuleAction.ViaProxy(1), "t", "127.0.0.1", echo.Port);
        await AssertEchoes(c);
    }

    [Fact]
    public async Task Socks5_WrongPassword_Fails()
    {
        await using var socks = new Socks5TestServer("alice", "s3cret");
        var (_, conn) = Setup(new ProxyServer { Id = 1, Type = ProxyType.Socks5, Address = "127.0.0.1", Port = socks.Port, Username = "alice", Password = "nope" });

        var ex = await Assert.ThrowsAsync<ProxyException>(() => conn.ConnectAsync(RuleAction.ViaProxy(1), "t", "127.0.0.1", 1));
        Assert.Contains("authentication failed", ex.Message);
    }

    [Fact]
    public async Task Socks5_RefusedReply_Surfaces()
    {
        await using var socks = new Socks5TestServer();
        var (_, conn) = Setup(new ProxyServer { Id = 1, Type = ProxyType.Socks5, Address = "127.0.0.1", Port = socks.Port });

        var ex = await Assert.ThrowsAsync<ProxyException>(() => conn.ConnectAsync(RuleAction.ViaProxy(1), "t", "refuse.test", 80));
        Assert.Contains("connection refused", ex.Message);
    }

    [Fact]
    public async Task Socks4a_Hostname()
    {
        await using var echo = new EchoServer();
        await using var socks = new Socks4TestServer();
        var (_, conn) = Setup(new ProxyServer { Id = 1, Type = ProxyType.Socks4, Address = "127.0.0.1", Port = socks.Port });

        using var c = await conn.ConnectAsync(RuleAction.ViaProxy(1), "t", "echo.test", echo.Port);
        await AssertEchoes(c);
        Assert.Equal($"echo.test:{echo.Port}", socks.LastTarget);
    }

    [Fact]
    public async Task Socks4_Ip()
    {
        await using var echo = new EchoServer();
        await using var socks = new Socks4TestServer();
        var (_, conn) = Setup(new ProxyServer { Id = 1, Type = ProxyType.Socks4, Address = "127.0.0.1", Port = socks.Port, Socks4aRemoteResolve = false });

        using var c = await conn.ConnectAsync(RuleAction.ViaProxy(1), "t", "127.0.0.1", echo.Port);
        await AssertEchoes(c);
    }

    [Fact]
    public async Task HttpsConnect_WithBasicAuth()
    {
        await using var echo = new EchoServer();
        await using var http = new HttpConnectTestServer("bob", "pw");
        var (_, conn) = Setup(new ProxyServer { Id = 1, Type = ProxyType.Https, Address = "127.0.0.1", Port = http.Port, Username = "bob", Password = "pw" });

        using var c = await conn.ConnectAsync(RuleAction.ViaProxy(1), "t", "echo.test", echo.Port);
        await AssertEchoes(c);
    }

    [Fact]
    public async Task HttpsConnect_407_Fails()
    {
        await using var http = new HttpConnectTestServer("bob", "pw");
        var (_, conn) = Setup(new ProxyServer { Id = 1, Type = ProxyType.Https, Address = "127.0.0.1", Port = http.Port });

        var ex = await Assert.ThrowsAsync<ProxyException>(() => conn.ConnectAsync(RuleAction.ViaProxy(1), "t", "echo.test", 1));
        Assert.Contains("407", ex.Message);
    }

    [Fact]
    public async Task SimpleChain_Socks5_Then_Http_Then_Socks4()
    {
        await using var echo = new EchoServer();
        await using var a = new Socks5TestServer();
        await using var b = new HttpConnectTestServer();
        await using var c4 = new Socks4TestServer();
        var (profile, conn) = Setup(
            new ProxyServer { Id = 1, Type = ProxyType.Socks5, Address = "127.0.0.1", Port = a.Port },
            new ProxyServer { Id = 2, Type = ProxyType.Https, Address = "127.0.0.1", Port = b.Port },
            new ProxyServer { Id = 3, Type = ProxyType.Socks4, Address = "127.0.0.1", Port = c4.Port });
        var chain = new ProxyChain { Id = 200, Name = "three-hop" };
        chain.Hops.AddRange([new ChainHop { ProxyId = 1 }, new ChainHop { ProxyId = 2 }, new ChainHop { ProxyId = 3 }]);
        profile.Chains.Add(chain);

        using var c = await conn.ConnectAsync(RuleAction.ViaChain(200), "t", "echo.test", echo.Port);
        await AssertEchoes(c);
        Assert.Equal($"127.0.0.1:{b.Port}", a.LastTarget);
        Assert.Equal($"127.0.0.1:{c4.Port}", b.LastTarget);
        Assert.Equal($"echo.test:{echo.Port}", c4.LastTarget);
    }

    [Fact]
    public async Task RedundancyChain_SkipsDeadProxy()
    {
        await using var echo = new EchoServer();
        await using var live = new Socks5TestServer();
        int deadPort;
        using (var l = new TcpListener(System.Net.IPAddress.Loopback, 0)) { l.Start(); deadPort = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }

        var (profile, conn) = Setup(
            new ProxyServer { Id = 1, Type = ProxyType.Socks5, Address = "127.0.0.1", Port = deadPort },
            new ProxyServer { Id = 2, Type = ProxyType.Socks5, Address = "127.0.0.1", Port = live.Port });
        var chain = new ProxyChain { Id = 300, Name = "failover", Type = ChainType.Redundancy, RedundancyTimeoutSeconds = 2 };
        chain.Hops.AddRange([new ChainHop { ProxyId = 1 }, new ChainHop { ProxyId = 2 }]);
        profile.Chains.Add(chain);

        using var c = await conn.ConnectAsync(RuleAction.ViaChain(300), "t", "echo.test", echo.Port);
        await AssertEchoes(c);
        Assert.Equal(1, live.Accepted);
    }

    [Fact]
    public async Task BlockAction_Throws()
    {
        var conn = new ProxyConnector(Profile.CreateDefault());
        await Assert.ThrowsAsync<ConnectionBlockedException>(() => conn.ConnectAsync(RuleAction.Block, "ads", "x", 1));
    }
}
