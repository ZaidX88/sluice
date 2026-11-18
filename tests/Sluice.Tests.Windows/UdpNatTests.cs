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
