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

/// <summary>UDP echo on 127.0.0.1.</summary>
public sealed class UdpEchoServer : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public int Received;

    public UdpEchoServer() => _ = Loop();

    private async Task Loop()
    {
        try
        {
            while (true)
            {
                var r = await _udp.ReceiveAsync();
                Interlocked.Increment(ref Received);
                await _udp.SendAsync(r.Buffer, r.RemoteEndPoint);
            }
        }
        catch { /* disposed */ }
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>
/// Minimal SOCKS5 server supporting only UDP ASSOCIATE (no auth). Hostnames are resolved through
/// <see cref="Hosts"/> (so tests can use names that only exist here).
/// </summary>
public sealed class Socks5UdpServer : LoopbackServer
{
    public Dictionary<string, IPAddress> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Associations;
    public string? LastTargetHost;

    protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[512];
        await s.ReadExactlyAsync(buf.AsMemory(0, 2), ct);
        await s.ReadExactlyAsync(buf.AsMemory(0, buf[1]), ct);
        await s.WriteAsync(new byte[] { 5, 0 }, ct);
        await s.ReadExactlyAsync(buf.AsMemory(0, 4), ct);
        var cmd = buf[1];
        var skip = buf[3] switch { 1 => 4, 4 => 16, _ => -1 };
        if (skip < 0) { await s.ReadExactlyAsync(buf.AsMemory(0, 1), ct); skip = buf[0]; }
        await s.ReadExactlyAsync(buf.AsMemory(0, skip + 2), ct);
        if (cmd != 3)
        {
            await s.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.LocalEndPoint!).Port;
        // Reply "0.0.0.0": the client must use the address it connected to.
        await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, (byte)(port >> 8), (byte)port }, ct);
        Interlocked.Increment(ref Associations);

        IPEndPoint? client = null;
        var pump = Task.Run(async () =>
        {
            var d = new byte[65536];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                var r = await udp.ReceiveFromAsync(d, SocketFlags.None, any, ct);
                var from = (IPEndPoint)r.RemoteEndPoint;
                if (client is null || from.Equals(client))
                {
                    client = from;
                    if (!Socks5UdpAssociation.TryParse(d.AsSpan(0, r.ReceivedBytes), out var host, out var p, out var off)) continue;
                    LastTargetHost = host;
                    var ip = IPAddress.TryParse(host, out var lit) ? lit : Hosts[host];
                    await udp.SendToAsync(d.AsMemory(off, r.ReceivedBytes - off), SocketFlags.None, new IPEndPoint(ip, p), ct);
                }
                else
                {
                    var reply = new byte[10 + r.ReceivedBytes];
                    reply[3] = 1;
                    from.Address.GetAddressBytes().CopyTo(reply, 4);
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), (ushort)from.Port);
                    d.AsSpan(0, r.ReceivedBytes).CopyTo(reply.AsSpan(10));
                    await udp.SendToAsync(reply, SocketFlags.None, client, ct);
                }
            }
        }, ct);
        // The association lives as long as the control connection.
        try { while (await s.ReadAsync(buf, ct) > 0) { } } catch { }
        udp.Dispose();
        try { await pump; } catch { }
    }
}

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
    [InlineData(UdpMode.Tunnel, "x.example", 443, @"C:\g\game.exe", UdpVerdict.Block)]   // SSH cannot carry UDP
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

/// <summary>The NAT end to end with synthetic packets: capture → SOCKS5 UDP → echo → injected reply.</summary>
public class UdpNatTests
{
    private static readonly IPEndPoint App = new(IPAddress.Parse("192.168.50.10"), 50123);

    [Fact]
    public async Task TunneledFlow_ToFakeAddress_RepliesFromTheFakeAddress()
    {
        using var echo = new UdpEchoServer();
        await using var socks = new Socks5UdpServer();
        socks.Hosts["game.example"] = IPAddress.Loopback;

        var p = Profile.CreateDefault();
        p.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = socks.Port });
        p.Rules.Single(r => r.IsDefault).Action = RuleAction.ViaTunnel(100);
        await using var engine = new RoutingEngine(p);
        var fakes = new FakeIpPool();
        var fakeIp = fakes.GetOrAdd("game.example");

        var injected = new TaskCompletionSource<(byte[] Packet, object? Ctx)>();
        await using var nat = new UdpNat(engine, fakes, (pkt, ctx) => injected.TrySetResult((pkt, ctx)), _ => 4242);

        var dst = new IPEndPoint(fakeIp, echo.Port);
        var verdict = nat.ProcessOutbound(UdpPacket.Build(App, dst, "hello"u8), "ctx-1");
        Assert.Equal(UdpNat.Verdict.Drop, verdict);   // taken out of the network

        var (reply, ctx) = await injected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ctx-1", ctx);
        Assert.True(UdpPacket.TryParse(reply, out var from, out var to, out var off, out var len));
        Assert.Equal(dst, from);
        Assert.Equal(App, to);
        Assert.Equal("hello", Encoding.ASCII.GetString(reply, off, len));
        Assert.Equal("game.example", socks.LastTargetHost);   // the name, not the fake address, went to the tunnel

        var entry = Assert.Single(engine.Tracker.Active);
        Assert.EndsWith("UDP", entry.Target);
        Assert.Equal(5, entry.BytesSent);

        // Same socket, second datagram: same association.
        nat.ProcessOutbound(UdpPacket.Build(App, dst, "again"u8), "ctx-1");
        await Task.Delay(300);
        Assert.Equal(1, socks.Associations);
        Assert.Equal(1, nat.FlowCount);
        Assert.Equal(1, nat.Sweep(TimeSpan.Zero));
        Assert.Empty(engine.Tracker.Active);
    }

    [Fact]
    public async Task DirectBlockedOwnAndSystemTraffic()
    {
        var p = Profile.CreateDefault();
        p.Rules.Insert(0, new Rule { Name = "No", Targets = "203.0.113.9", Action = RuleAction.Block });
        await using var engine = new RoutingEngine(p);
        var fakes = new FakeIpPool();
        var pid = 4242;
        await using var nat = new UdpNat(engine, fakes, (_, _) => throw new InvalidOperationException("no injection expected"), _ => pid);

        byte[] To(string ip, int port) => UdpPacket.Build(App, new IPEndPoint(IPAddress.Parse(ip), port), "x"u8);
        Assert.Equal(UdpNat.Verdict.Pass, nat.ProcessOutbound(To("203.0.113.1", 3478), null));        // Default rule: Direct
        Assert.Equal(UdpNat.Verdict.Drop, nat.ProcessOutbound(To("203.0.113.9", 3478), null));        // Block rule
        Assert.Equal(UdpNat.Verdict.Pass, nat.ProcessOutbound(To("8.8.8.8", 53), null));              // DNS is handled elsewhere
        Assert.Equal(UdpNat.Verdict.Pass, nat.ProcessOutbound(To("239.255.255.250", 5000), null));    // multicast
        Assert.Equal(UdpNat.Verdict.Drop, nat.ProcessOutbound(UdpPacket.Build(App, new IPEndPoint(fakes.GetOrAdd("x.example"), 443), "x"u8), null)); // fake + Direct
        pid = Environment.ProcessId;
        Assert.Equal(UdpNat.Verdict.Pass, nat.ProcessOutbound(UdpPacket.Build(new IPEndPoint(App.Address, 1), new IPEndPoint(IPAddress.Parse("203.0.113.9"), 1), "x"u8), null)); // Sluice itself
    }

    [Fact]
    public async Task DeadTunnel_DropsWithoutHammering()
    {
        var p = Profile.CreateDefault();
        p.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = 1 });   // nothing listens
        p.Rules.Single(r => r.IsDefault).Action = RuleAction.ViaTunnel(100);
        await using var engine = new RoutingEngine(p);
        var errors = new List<string>();
        engine.Tracker.Log += (lvl, m) => { if (lvl == LogLevel.Error) lock (errors) errors.Add(m); };
        await using var nat = new UdpNat(engine, new FakeIpPool(), (_, _) => { }, _ => 4242);
        var pkt = UdpPacket.Build(App, new IPEndPoint(IPAddress.Parse("203.0.113.1"), 443), "x"u8);
        Assert.Equal(UdpNat.Verdict.Drop, nat.ProcessOutbound(pkt, null));
        // Windows retries a refused loopback connect for about 2 s.
        for (var i = 0; i < 100 && errors.Count == 0; i++) await Task.Delay(100);
        for (var i = 0; i < 20; i++) Assert.Equal(UdpNat.Verdict.Drop, nat.ProcessOutbound(pkt, null));
        await Task.Delay(500);
        lock (errors) Assert.Single(errors);
    }
}
