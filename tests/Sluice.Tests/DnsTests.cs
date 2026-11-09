using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Interception;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Tests;

public class DnsTests
{
    // ---------------- wire format ----------------

    [Fact]
    public void Query_RoundTrip_And_ResponseParses()
    {
        var q = DnsMessage.BuildQuery(0xBEEF, "www.Example.com.", DnsType.A);
        var parsed = DnsMessage.TryParseQuery(q)!;
        Assert.Equal((0xBEEF, "www.Example.com", (ushort)1, (ushort)1, true),
            (parsed.Id, parsed.Name, parsed.Type, parsed.Class, parsed.RecursionDesired));

        var resp = DnsMessage.BuildResponse(q, parsed, [IPAddress.Parse("198.18.0.7"), IPAddress.Parse("2001:db8::1")], 5);
        var addrs = DnsMessage.ParseAddresses(resp, out var id, out var rcode);
        Assert.Equal(0xBEEF, id);
        Assert.Equal(0, rcode);
        Assert.Equal([IPAddress.Parse("198.18.0.7"), IPAddress.Parse("2001:db8::1")], addrs);
        Assert.Null(DnsMessage.TryParseQuery(resp)); // responses are not queries
    }

    [Fact]
    public void ParseAddresses_FollowsCompressionAndSkipsCname()
    {
        // Hand-built reply: www.a.com CNAME a.com ; a.com A 1.2.3.4 (names compressed).
        var q = DnsMessage.BuildQuery(1, "www.a.com", DnsType.A);
        var body = new List<byte>(q);
        body[2] = 0x81; body[3] = 0x80;               // response, RD, RA
        body[7] = 2;                                   // ANCOUNT = 2
        body.AddRange([0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60, 0, 2, 0xC0, 0x10]);   // CNAME -> ptr to "a.com"
        body.AddRange([0xC0, 0x10, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 1, 2, 3, 4]);   // A 1.2.3.4
        var addrs = DnsMessage.ParseAddresses(body.ToArray(), out _, out var rcode);
        Assert.Equal(0, rcode);
        Assert.Equal([IPAddress.Parse("1.2.3.4")], addrs);
    }

    [Fact]
    public void MalformedInput_IsRejected()
    {
        Assert.Null(DnsMessage.TryParseQuery(new byte[5]));
        var loop = DnsMessage.BuildQuery(1, "a.b", DnsType.A);
        loop[12] = 0xC0; loop[13] = 12;                // pointer to itself in a query
        Assert.Null(DnsMessage.TryParseQuery(loop));
        Assert.Empty(DnsMessage.ParseAddresses(new byte[3], out _, out _));
    }

    // ---------------- fake IP pool ----------------

    [Fact]
    public void FakePool_StableBidirectional_CaseInsensitive()
    {
        var pool = new FakeIpPool();
        var a = pool.GetOrAdd("Example.com.");
        Assert.True(FakeIpPool.IsFake(a));
        Assert.Equal(a, pool.GetOrAdd("example.com"));
        Assert.NotEqual(a, pool.GetOrAdd("other.com"));
        Assert.True(pool.TryGetHost(a, out var h));
        Assert.Equal("example.com", h);
        Assert.True(pool.TryGetHost(IPAddress.Parse("::ffff:" + a), out _));
        Assert.False(FakeIpPool.IsFake(IPAddress.Parse("198.20.0.1")));
        Assert.True(FakeIpPool.IsFake(IPAddress.Parse("198.19.255.255")));
    }

    [Fact]
    public void FakePool_RecyclesOldestWhenFull()
    {
        var pool = new FakeIpPool(capacity: 2);
        var a = pool.GetOrAdd("a");
        pool.GetOrAdd("b");
        var c = pool.GetOrAdd("c");                     // reuses a's slot
        Assert.Equal(a, c);
        Assert.True(pool.TryGetHost(c, out var h));
        Assert.Equal("c", h);
        Assert.Equal(2, pool.Count);
    }

    // ---------------- policy ----------------

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("printer.local", false)]     // *.local
    [InlineData("localhost", false)]
    [InlineData("MYPC", false)]              // %ComputerName%
    [InlineData("4.3.2.1.in-addr.arpa", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("wsl.localhost", false)]    // RFC 6761, always local
    [InlineData("nas.home.arpa", false)]    // RFC 8375, always local
    public void Policy_ExclusionList(string name, bool fake)
    {
        var o = new ResolveOptions { ViaProxy = true };
        Assert.Equal(fake, new DnsPolicy(o, leakPrevention: false, computerName: "MYPC").ShouldFake(name));
    }

    [Fact]
    public void Policy_OnlyListedMode_And_Disabled()
    {
        var only = new ResolveOptions { ViaProxy = true, OnlyFromListMode = true, ExclusionList = "*.corp" };
        var p = new DnsPolicy(only, false);
        Assert.True(p.ShouldFake("git.corp"));
        Assert.False(p.ShouldFake("example.com"));

        Assert.False(new DnsPolicy(new ResolveOptions(), false).ShouldFake("example.com"));
        // Built-in local names stay local even when the user lists them for proxy resolution.
        var listed = new DnsPolicy(new ResolveOptions { ViaProxy = true, OnlyFromListMode = true, ExclusionList = "*.localhost" }, false);
        Assert.False(listed.ShouldFake("wsl.localhost"));
        Assert.True(new DnsPolicy(new ResolveOptions(), leakPrevention: true).ShouldFake("example.com"));
    }

    // ---------------- redirector (packet level) ----------------

    private static byte[] UdpPacket(IPAddress src, ushort sport, IPAddress dst, ushort dport, byte[] payload)
    {
        var v6 = src.AddressFamily == AddressFamily.InterNetworkV6;
        var ip = v6 ? 40 : 20;
        var p = new byte[ip + 8 + payload.Length];
        if (v6)
        {
            p[0] = 0x60; p[6] = 17; p[7] = 64;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)(8 + payload.Length));
            src.GetAddressBytes().CopyTo(p, 8);
            dst.GetAddressBytes().CopyTo(p, 24);
        }
        else
        {
            p[0] = 0x45; p[8] = 64; p[9] = 17;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
            src.GetAddressBytes().CopyTo(p, 12);
            dst.GetAddressBytes().CopyTo(p, 16);
        }
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(ip), sport);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(ip + 2), dport);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(ip + 4), (ushort)(8 + payload.Length));
        payload.CopyTo(p, ip + 8);
        return p;
    }

    private static readonly IPAddress Me = IPAddress.Parse("192.168.1.10");
    private static readonly IPAddress Dns = IPAddress.Parse("192.168.1.1");

    private static DnsRedirector Redirector(FakeIpPool pool, int ownPort = 7777, bool blockNonA = false) =>
        new(pool, () => new DnsPolicy(new ResolveOptions { ViaProxy = true, BlockNonATypes = blockNonA }, false, "MYPC"), ownPort);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redirector_AnswersA_WithFakeIp_AsSwappedReply(bool v6)
    {
        var (me, dns) = v6 ? (IPAddress.Parse("2001:db8::10"), IPAddress.Parse("2001:db8::1")) : (Me, Dns);
        var pool = new FakeIpPool();
        var query = DnsMessage.BuildQuery(42, "example.com", DnsType.A);
        var reply = Redirector(pool).TryAnswer(UdpPacket(me, 55555, dns, 53, query))!;
        Assert.NotNull(reply);

        var ip = v6 ? 40 : 20;
        var (src, dst) = v6
            ? (new IPAddress(reply.AsSpan(8, 16)), new IPAddress(reply.AsSpan(24, 16)))
            : (new IPAddress(reply.AsSpan(12, 4)), new IPAddress(reply.AsSpan(16, 4)));
        Assert.Equal((dns, me), (src, dst));
        Assert.Equal(53, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(ip)));
        Assert.Equal(55555, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(ip + 2)));
        Assert.Equal(reply.Length - ip, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(ip + 4)));
        if (!v6) Assert.Equal(reply.Length, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)));

        var addrs = DnsMessage.ParseAddresses(reply.AsSpan(ip + 8), out var id, out _);
        Assert.Equal(42, id);
        Assert.Equal([pool.GetOrAdd("example.com")], addrs);
    }

    [Fact]
    public void Redirector_AAAA_And_HTTPS_GetNoData()
    {
        var r = Redirector(new FakeIpPool());
        foreach (var t in new[] { DnsType.AAAA, DnsType.HTTPS })
        {
            var reply = r.TryAnswer(UdpPacket(Me, 1, Dns, 53, DnsMessage.BuildQuery(1, "example.com", t)))!;
            Assert.Empty(DnsMessage.ParseAddresses(reply.AsSpan(28), out _, out var rcode));
            Assert.Equal(0, rcode);
        }
    }

    [Fact]
    public void Redirector_PassesWhatItShouldNotTouch()
    {
        var r = Redirector(new FakeIpPool(), ownPort: 7777);
        var a = DnsMessage.BuildQuery(1, "example.com", DnsType.A);
        Assert.Null(r.TryAnswer(UdpPacket(Me, 7777, Dns, 53, a)));                                         // Sluice's own resolver
        Assert.Null(r.TryAnswer(UdpPacket(Me, 1, Dns, 5353, a)));                                          // not port 53
        Assert.Null(r.TryAnswer(UdpPacket(Me, 1, Dns, 53, DnsMessage.BuildQuery(1, "printer.local", DnsType.A)))); // excluded
        Assert.Null(r.TryAnswer(UdpPacket(Me, 1, Dns, 53, DnsMessage.BuildQuery(1, "example.com", (DnsType)16))));  // TXT passes...
        Assert.NotNull(Redirector(new FakeIpPool(), blockNonA: true)
            .TryAnswer(UdpPacket(Me, 1, Dns, 53, DnsMessage.BuildQuery(1, "example.com", (DnsType)16))));    // ...unless blocked
    }

    // ---------------- own resolver against a fake DNS server ----------------

    private sealed class FakeDnsServer : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public int Queries;

        public FakeDnsServer(Dictionary<string, IPAddress> zone)
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    UdpReceiveResult r;
                    try { r = await _udp.ReceiveAsync(); } catch { return; }
                    Interlocked.Increment(ref Queries);
                    var q = DnsMessage.TryParseQuery(r.Buffer);
                    if (q is null) continue;
                    var hit = zone.TryGetValue(q.Name, out var ip) && (q.Type == 1) == DnsMessage.IsIPv4(ip);
                    var known = zone.ContainsKey(q.Name);
                    var resp = DnsMessage.BuildResponse(r.Buffer, q, hit ? [ip!] : [], 60, rcode: (byte)(known ? 0 : 3));
                    await _udp.SendAsync(resp, r.RemoteEndPoint);
                }
            });
        }

        public void Dispose() => _udp.Dispose();
    }

    // DnsResolver always queries port 53, so tests point it at a loopback server via a port-forwarding shim.
    private static DnsResolver ResolverFor(FakeDnsServer server) =>
        new(() => [IPAddress.Loopback]) { Timeout = TimeSpan.FromSeconds(2), PortOverride = server.Port };

    [Fact]
    public async Task Resolver_ResolvesA_ThenCaches()
    {
        using var server = new FakeDnsServer(new() { ["example.com"] = IPAddress.Parse("93.184.216.34") });
        using var resolver = ResolverFor(server);

        Assert.Equal([IPAddress.Parse("93.184.216.34")], await resolver.ResolveAsync("example.com"));
        Assert.Equal([IPAddress.Parse("93.184.216.34")], await resolver.ResolveAsync("EXAMPLE.com."));
        Assert.Equal(1, server.Queries);
    }

    [Fact]
    public async Task Resolver_FallsBackToAAAA_And_ReportsNxdomain()
    {
        using var server = new FakeDnsServer(new() { ["v6only.test"] = IPAddress.Parse("2001:db8::5") });
        using var resolver = ResolverFor(server);

        Assert.Equal([IPAddress.Parse("2001:db8::5")], await resolver.ResolveAsync("v6only.test"));
        var ex = await Assert.ThrowsAsync<SocketException>(() => resolver.ResolveAsync("missing.test"));
        Assert.Equal(SocketError.HostNotFound, ex.SocketErrorCode);
    }

    [Fact]
    public async Task DirectConnection_ByHostname_UsesEngineResolver()
    {
        // Fake-IP flows reach the engine with a hostname; a Direct rule must resolve it
        // with Sluice's resolver, not the OS (whose cache would hold the fake address).
        await using var echo = new EchoServer();
        using var server = new FakeDnsServer(new() { ["echo.test"] = IPAddress.Loopback });
        using var resolver = ResolverFor(server);
        var engine = new ProxyEngine(Profile.CreateDefault()) { HostResolver = resolver.ResolveAsync };
        await using var inbound = new Socks5Inbound(engine, new IPEndPoint(IPAddress.Loopback, 0));
        inbound.Start();

        using var c = new TcpClient();
        await c.ConnectAsync(inbound.LocalEndPoint);
        await ProxyHandshake.Socks5Async(c.GetStream(), "echo.test", echo.Port, null, null, CancellationToken.None);
        var s = c.GetStream();
        await s.WriteAsync("dns!"u8.ToArray());
        var back = new byte[4];
        await s.ReadExactlyAsync(back).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("dns!"u8.ToArray(), back);
        Assert.Equal(1, server.Queries);
    }
}
