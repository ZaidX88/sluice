using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Profiles;

namespace Sluice.Tests;

/// <summary>The UDP flow logic with a fake capture: datagram in → SOCKS5 UDP or a Direct socket → echo → reply out.</summary>
public class UdpFlowsTests
{
    private static readonly IPEndPoint App = new(IPAddress.Parse("192.168.50.10"), 50123);

    /// <summary>Records what the flows hand back to the app.</summary>
    private sealed class FakeSender : IDatagramSender
    {
        public readonly BlockingCollection<(IPEndPoint From, IPEndPoint App, string Payload, object? Context)> Sent = new();

        public void SendToApp(IPEndPoint from, IPEndPoint app, ReadOnlyMemory<byte> payload, object? context) =>
            Sent.Add((from, app, Encoding.ASCII.GetString(payload.Span), context));

        public (IPEndPoint From, IPEndPoint App, string Payload, object? Context) Next() =>
            Sent.TryTake(out var r, TimeSpan.FromSeconds(5)) ? r : throw new TimeoutException("no reply reached the app");
    }

    private sealed class NoSender : IDatagramSender
    {
        public void SendToApp(IPEndPoint from, IPEndPoint app, ReadOnlyMemory<byte> payload, object? context) =>
            throw new InvalidOperationException("no reply expected");
    }

    private static AppDatagram Datagram(IPEndPoint dst, string payload, object? ctx = null, IPEndPoint? app = null) =>
        new(app ?? App, dst, Encoding.ASCII.GetBytes(payload), ctx);

    private static AppDatagram To(string ip, int port) => Datagram(new IPEndPoint(IPAddress.Parse(ip), port), "x");

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
        var sender = new FakeSender();
        await using var flows = new UdpFlows(engine, fakes, sender, _ => 4242);

        var dst = new IPEndPoint(fakeIp, echo.Port);
        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(Datagram(dst, "hello", "ctx-1")));   // taken

        var reply = sender.Next();
        Assert.Equal(dst, reply.From);
        Assert.Equal(App, reply.App);
        Assert.Equal("hello", reply.Payload);
        Assert.Equal("ctx-1", reply.Context);
        Assert.Equal("game.example", socks.LastTargetHost);   // the name, not the fake address, went to the tunnel

        var entry = Assert.Single(engine.Tracker.Active);
        Assert.EndsWith("UDP", entry.Target);
        Assert.Equal(5, entry.BytesSent);

        // Same socket, second datagram: same association.
        flows.Handle(Datagram(dst, "again", "ctx-1"));
        Assert.Equal("again", sender.Next().Payload);
        Assert.Equal(1, socks.Associations);
        Assert.Equal(1, flows.FlowCount);
        Assert.Equal(2, flows.DatagramsTunneled);
    }

    [Fact]
    public async Task DirectBlockedDnsMulticastAndOwnTraffic()
    {
        var p = Profile.CreateDefault();
        p.Rules.Insert(0, new Rule { Name = "No", Targets = "203.0.113.9", Action = RuleAction.Block });
        await using var engine = new RoutingEngine(p);
        var pid = 4242;
        await using var flows = new UdpFlows(engine, new FakeIpPool(), new NoSender(), _ => pid);

        Assert.Equal(UdpFlows.Verdict.Pass, flows.Handle(To("203.0.113.1", 3478)));       // Default rule: Direct
        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(To("203.0.113.9", 3478)));       // Block rule
        Assert.Equal(UdpFlows.Verdict.Pass, flows.Handle(To("8.8.8.8", 53)));             // DNS is handled elsewhere
        Assert.Equal(UdpFlows.Verdict.Pass, flows.Handle(To("239.255.255.250", 5000)));   // multicast
        Assert.Equal(UdpFlows.Verdict.Pass, flows.Handle(To("127.0.0.1", 5000)));         // loopback
        Assert.Equal(1, flows.DatagramsDropped);
        pid = Environment.ProcessId;
        Assert.Equal(UdpFlows.Verdict.Pass, flows.Handle(Datagram(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 1), "x",
            app: new IPEndPoint(App.Address, 1))));                                          // Sluice itself, even to a blocked address
        Assert.Empty(engine.Tracker.Active);
    }

    private static readonly TimeSpan[] QuickRetries = [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(20), 5)];

    [Fact]
    public async Task OwnerFoundOnASecondLook_FlowFollowsItsRule_HeldDatagramsGoThroughTheTunnel()
    {
        using var echo = new UdpEchoServer();
        await using var socks = new Socks5UdpServer();
        socks.Hosts["203.0.113.5"] = IPAddress.Loopback;

        var p = Profile.CreateDefault();
        p.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = socks.Port });
        p.Rules.Insert(0, new Rule { Name = "Game", Targets = "203.0.113.5", Action = RuleAction.ViaTunnel(100) });
        await using var engine = new RoutingEngine(p);
        engine.Tracker.Level = LogLevel.Debug;
        var log = new ConcurrentQueue<string>();
        engine.Tracker.Log += (_, m) => log.Enqueue(m);
        var lookups = 0;
        var sender = new FakeSender();
        // The socket isn't listed yet on the first two looks, as on Windows right after it is created (#255).
        await using var flows = new UdpFlows(engine, new FakeIpPool(), sender, _ => Interlocked.Increment(ref lookups) <= 2 ? 0 : 4242)
        {
            OwnerRetryDelays = QuickRetries,
        };

        var dst = new IPEndPoint(IPAddress.Parse("203.0.113.5"), echo.Port);
        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(Datagram(dst, "one", "ctx-1")));   // held, not passed
        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(Datagram(dst, "two", "ctx-1")));

        var replies = new[] { sender.Next(), sender.Next() };
        Assert.Equal(new[] { "one", "two" }, replies.Select(r => r.Payload).Order());
        Assert.All(replies, r => Assert.Equal(dst, r.From));

        // Decided once: later datagrams go straight through the tunnel, no more looks.
        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(Datagram(dst, "three", "ctx-1")));
        Assert.Equal("three", sender.Next().Payload);
        Assert.Equal(3, lookups);
        Assert.Equal(1, flows.FlowCount);
        Assert.Equal(3, flows.DatagramsTunneled);
        var entry = Assert.Single(engine.Tracker.Active);
        Assert.Equal(4242, entry.ProcessId);
        Assert.Contains(log, m => m.Contains("pid 0 => Wait"));
        Assert.Contains(log, m => m.Contains("pid 4242 rule Game => Tunnel"));
    }

    [Fact]
    public async Task OwnerNeverFound_FlowPassesAfterTheRetries()
    {
        var p = Profile.CreateDefault();
        p.Rules.Single(r => r.IsDefault).Action = RuleAction.ViaTunnel(100);
        p.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = 9 });
        await using var engine = new RoutingEngine(p);
        engine.Tracker.Level = LogLevel.Debug;
        var log = new ConcurrentQueue<string>();
        engine.Tracker.Log += (_, m) => log.Enqueue(m);
        var lookups = 0;
        await using var flows = new UdpFlows(engine, new FakeIpPool(), new NoSender(), _ => { Interlocked.Increment(ref lookups); return 0; })
        {
            OwnerRetryDelays = QuickRetries,
        };

        var dst = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 3478);
        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(Datagram(dst, "x")));   // held while the owner is looked up again
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (flows.Handle(Datagram(dst, "x")) == UdpFlows.Verdict.Drop && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(UdpFlows.Verdict.Pass, flows.Handle(Datagram(dst, "x")));   // an unknown owner's flow, as before
        Assert.Equal(1 + QuickRetries.Length, lookups);
        Assert.True(flows.DatagramsDropped >= 1);   // the held datagrams can't be sent on; the app resends
        Assert.Empty(engine.Tracker.Active);
        Assert.Contains(log, m => m.Contains("pid 0 => Pass (owner unknown)"));
    }

    [Fact]
    public async Task OwnerNotFound_FlowEndedWhileWaiting_IsDecidedAgain()
    {
        var p = Profile.CreateDefault();   // Default rule: Direct
        await using var engine = new RoutingEngine(p);
        var pid = 0;
        await using var flows = new UdpFlows(engine, new FakeIpPool(), new NoSender(), _ => pid)
        {
            OwnerRetryDelays = [TimeSpan.FromMilliseconds(300)],
        };

        var dst = new IPEndPoint(IPAddress.Parse("203.0.113.8"), 3478);
        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(Datagram(dst, "x")));
        engine.Reload(Profile.CreateDefault());   // a new profile ends every flow, the waiting one too
        pid = 4242;
        Assert.Equal(UdpFlows.Verdict.Pass, flows.Handle(Datagram(dst, "x")));   // decided again at once: Direct
        await Task.Delay(500);   // the first look-up's late answer doesn't replace the new flow
        Assert.Equal(UdpFlows.Verdict.Pass, flows.Handle(Datagram(dst, "x")));
        Assert.Equal(1, flows.FlowCount);
    }

    [Fact]
    public async Task UnknownFakeAddress_IsDropped()
    {
        await using var engine = new RoutingEngine(Profile.CreateDefault());
        await using var flows = new UdpFlows(engine, new FakeIpPool(), new NoSender(), _ => 4242);
        // A placeholder from before a restart: nothing to send it to.
        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(To("198.18.0.77", 443)));
    }

    [Fact]
    public async Task DirectToFakeAddress_GoesToTheNamesRealAddress_AndRepliesFromTheFakeAddress()
    {
        using var echo = new UdpEchoServer();
        await using var engine = new RoutingEngine(Profile.CreateDefault());   // Default rule: Direct
        var looked = new ConcurrentQueue<string>();
        engine.HostResolver = (host, _) =>
        {
            looked.Enqueue(host);
            return Task.FromResult(host == "echo.example" ? new[] { IPAddress.Loopback } : Array.Empty<IPAddress>());
        };
        var fakes = new FakeIpPool();
        var dst = new IPEndPoint(fakes.GetOrAdd("echo.example"), echo.Port);
        var sender = new FakeSender();
        await using var flows = new UdpFlows(engine, fakes, sender, _ => 4242);

        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(Datagram(dst, "ping", "ctx-2")));   // taken: a placeholder can't go out as it is

        var reply = sender.Next();
        Assert.Equal(dst, reply.From);   // from the placeholder the app sent to
        Assert.Equal(App, reply.App);
        Assert.Equal("ping", reply.Payload);
        Assert.Equal("ctx-2", reply.Context);
        Assert.Equal(1, echo.Received);

        var entry = Assert.Single(engine.Tracker.Active);
        Assert.StartsWith("echo.example:", entry.Target);
        Assert.Equal("Direct", entry.Via);
        Assert.Equal(4, entry.BytesSent);
        Assert.Equal(4, entry.BytesReceived);

        // The flow keeps its socket: one lookup for both datagrams.
        flows.Handle(Datagram(dst, "pong", "ctx-2"));
        Assert.Equal("pong", sender.Next().Payload);
        Assert.Equal(new[] { "echo.example" }, looked.ToArray());
        Assert.Equal(2, flows.DatagramsTunneled);
    }

    [Fact]
    public async Task DirectToFakeAddress_NameNotFound_DropsAndRetriesLater()
    {
        await using var engine = new RoutingEngine(Profile.CreateDefault());
        var lookups = 0;
        engine.HostResolver = (_, _) => { Interlocked.Increment(ref lookups); return Task.FromResult(Array.Empty<IPAddress>()); };
        var errors = new List<string>();
        engine.Tracker.Log += (lvl, m) => { if (lvl == LogLevel.Error) lock (errors) errors.Add(m); };
        var fakes = new FakeIpPool();
        var dst = new IPEndPoint(fakes.GetOrAdd("gone.example"), 3478);
        await using var flows = new UdpFlows(engine, fakes, new NoSender(), _ => 4242);

        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(Datagram(dst, "x")));
        for (var i = 0; i < 50 && errors.Count == 0; i++) await Task.Delay(100);
        for (var i = 0; i < 20; i++) Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(Datagram(dst, "x")));
        await Task.Delay(300);
        Assert.Equal(1, lookups);   // not looked up again for every datagram
        lock (errors) Assert.Contains("gone.example", Assert.Single(errors));
    }

    [Fact]
    public async Task IdleFlows_Expire()
    {
        using var echo = new UdpEchoServer();
        await using var engine = new RoutingEngine(Profile.CreateDefault());
        engine.HostResolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback });
        var fakes = new FakeIpPool();
        var sender = new FakeSender();
        await using var flows = new UdpFlows(engine, fakes, sender, _ => 4242);
        var dst = new IPEndPoint(fakes.GetOrAdd("echo.example"), echo.Port);

        flows.Handle(Datagram(dst, "x"));
        sender.Next();
        flows.Handle(To("203.0.113.1", 3478));
        Assert.Equal(2, flows.FlowCount);

        flows.Sweep(TimeSpan.FromMinutes(1));   // nothing idle yet
        Assert.Equal(2, flows.FlowCount);
        flows.Sweep(TimeSpan.Zero);
        Assert.Equal(0, flows.FlowCount);
        Assert.Empty(engine.Tracker.Active);
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
        await using var flows = new UdpFlows(engine, new FakeIpPool(), new NoSender(), _ => 4242);
        var d = To("203.0.113.1", 443);
        Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(d));
        // Windows retries a refused loopback connect for about 2 s.
        for (var i = 0; i < 100 && errors.Count == 0; i++) await Task.Delay(100);
        for (var i = 0; i < 20; i++) Assert.Equal(UdpFlows.Verdict.Drop, flows.Handle(d));
        await Task.Delay(500);
        lock (errors) Assert.Single(errors);
    }
}
