using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Tests;



public class UdpPacketTests
{
    private static bool ChecksumOk(ReadOnlySpan<byte> pkt, bool v6)
    {
        var ipLen = v6 ? 40 : 20;
        uint sum = 0;
        void Add(ReadOnlySpan<byte> b) { for (var i = 0; i < b.Length; i += 2) sum += (uint)((b[i] << 8) | (i + 1 < b.Length ? b[i + 1] : 0)); }
        if (!v6)
        {
            Add(pkt[..20]);
            while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            if (sum != 0xFFFF) return false;
            sum = 0;
        }
        Add(v6 ? pkt[8..40] : pkt[12..20]);
        sum += 17u + (uint)(pkt.Length - ipLen);
        Add(pkt[ipLen..]);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return sum == 0xFFFF;
    }

    [Theory]
    [InlineData("198.18.0.7", "192.168.1.20", 5)]
    [InlineData("2001:db8::1", "2001:db8::2", 4)]
    public void BuildThenParse_RoundTrips_WithValidChecksums(string from, string to, int payloadLength)
    {
        var payload = Enumerable.Range(1, payloadLength).Select(i => (byte)i).ToArray();
        var src = new IPEndPoint(IPAddress.Parse(from), 443);
        var dst = new IPEndPoint(IPAddress.Parse(to), 51000);
        var pkt = UdpPacket.Build(src, dst, payload);
        Assert.True(ChecksumOk(pkt, src.AddressFamily == AddressFamily.InterNetworkV6));
        Assert.True(UdpPacket.TryParse(pkt, out var s, out var d, out var off, out var len));
        Assert.Equal((src, dst), (s, d));
        Assert.Equal(payload, pkt.AsSpan(off, len).ToArray());
    }

    [Fact]
    public void Parse_RejectsTcpAndFragments()
    {
        var pkt = UdpPacket.Build(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1), new IPEndPoint(IPAddress.Parse("10.0.0.2"), 2), new byte[10]);
        var frag = (byte[])pkt.Clone();
        frag[6] |= 0x20;   // more fragments
        Assert.False(UdpPacket.TryParse(frag, out _, out _, out _, out _));
        var tcp = (byte[])pkt.Clone();
        tcp[9] = 6;
        Assert.False(UdpPacket.TryParse(tcp, out _, out _, out _, out _));
    }

    [Fact]
    public void Socks5Header_Parses()
    {
        byte[] d = [0, 0, 0, 3, 4, (byte)'a', (byte)'.', (byte)'b', (byte)'c', 0x01, 0xBB, 9, 8];
        Assert.True(Socks5UdpAssociation.TryParse(d, out var host, out var port, out var off));
        Assert.Equal(("a.bc", 443, 11), (host, port, off));
        d[2] = 1;   // fragment
        Assert.False(Socks5UdpAssociation.TryParse(d, out _, out _, out _));
    }
}

public class Socks5UdpAssociationTests
{
    [Fact]
    public async Task EchoThroughSocks5Udp_WithHostnameTarget()
    {
        using var echo = new UdpEchoServer();
        await using var server = new Socks5UdpServer();
        server.Hosts["echo.test"] = IPAddress.Loopback;
        var got = new TaskCompletionSource<(string, int, byte[])>();
        var proxy = new Tunnel { Type = TunnelType.Socks5, Address = "127.0.0.1", Port = server.Port };
        await using var a = await Socks5UdpAssociation.OpenAsync(proxy, (h, p, d) => got.TrySetResult((h, p, d.ToArray())));
        Assert.Equal(IPAddress.Loopback, a.Relay.Address);
        await a.SendAsync("echo.test", echo.Port, "ping"u8.ToArray());
        var (host, port, data) = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("127.0.0.1", echo.Port, "ping"), (host, port, Encoding.ASCII.GetString(data)));
        Assert.Equal("echo.test", server.LastTargetHost);
    }

    [Fact]
    public async Task ServerWithoutUdp_IsAClearError()
    {
        await using var tcpOnly = new Socks5TestServer();
        var proxy = new Tunnel { Type = TunnelType.Socks5, Address = "127.0.0.1", Port = tcpOnly.Port };
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Socks5UdpAssociation.OpenAsync(proxy, (_, _, _) => { }, timeout: TimeSpan.FromSeconds(3)));
        Assert.Contains("does not support UDP", ex.Message);
    }
}

public class UdpRoutingTests
{
    private static (Profile, Tunnel socks, Tunnel ssh) Profile3()
    {
        var p = Profile.CreateDefault();
        var socks = new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = 1 };
        var ssh = new Tunnel { Id = 101, Type = TunnelType.Ssh, Address = "ssh.example", Port = 22 };
        p.Tunnels.Add(socks);
        p.Tunnels.Add(ssh);
        p.Rules.Insert(0, new Rule { Name = "Games", Applications = "game.exe", Action = RuleAction.ViaTunnel(101) });
        p.Rules.Insert(0, new Rule { Name = "Blocked", Targets = "blocked.example", Action = RuleAction.Block });
        p.Rules.Single(r => r.IsDefault).Action = RuleAction.ViaTunnel(100);
        return (p, socks, ssh);
    }

    [Theory]
    [InlineData(UdpMode.Tunnel, "x.example", 443, null, UdpVerdict.Tunnel)]
    [InlineData(UdpMode.Tunnel, "blocked.example", 443, null, UdpVerdict.Block)]
    [InlineData(UdpMode.BlockQuic, "x.example", 443, null, UdpVerdict.Block)]
    [InlineData(UdpMode.BlockQuic, "x.example", 3478, null, UdpVerdict.Pass)]
    [InlineData(UdpMode.BlockAll, "x.example", 3478, null, UdpVerdict.Block)]
    [InlineData(UdpMode.Bypass, "x.example", 443, null, UdpVerdict.Pass)]
    [InlineData(UdpMode.Tunnel, "localhost", 443, null, UdpVerdict.Pass)]                  // Localhost rule is Direct
    public async Task ClassifyUdp(UdpMode mode, string host, int port, string? app, UdpVerdict expected)
    {
        var (p, _, _) = Profile3();
        p.Options.Udp = mode;
        await using var engine = new RoutingEngine(p);
        Assert.Equal(expected, engine.ClassifyUdp(new InboundRequest(host, port, 1234, app)).Verdict);
    }

    [Fact]
    public async Task Chains_UseASingleUdpCapableHop()
    {
        var (p, _, _) = Profile3();
        var simple = new TunnelChain { Id = 1, Name = "two", Type = ChainType.Simple };
        simple.Hops.Add(new ChainHop { TunnelId = 100 });
        simple.Hops.Add(new ChainHop { TunnelId = 101 });
        var redundant = new TunnelChain { Id = 2, Name = "failover", Type = ChainType.Redundancy };
        redundant.Hops.Add(new ChainHop { TunnelId = 101 });
        redundant.Hops.Add(new ChainHop { TunnelId = 100 });
        p.Chains.Add(simple);
        p.Chains.Add(redundant);
        await using var engine = new RoutingEngine(p);
        p.Rules.Single(r => r.IsDefault).Action = RuleAction.ViaChain(1);
        engine.Reload(p);
        Assert.Equal(UdpVerdict.Block, engine.ClassifyUdp(new InboundRequest("x", 1, 1, null)).Verdict);
        p.Rules.Single(r => r.IsDefault).Action = RuleAction.ViaChain(2);
        engine.Reload(p);
        var r = engine.ClassifyUdp(new InboundRequest("x", 1, 1, null));
        Assert.Equal((UdpVerdict.Tunnel, 100), (r.Verdict, r.Tunnel!.Id));
    }
}

