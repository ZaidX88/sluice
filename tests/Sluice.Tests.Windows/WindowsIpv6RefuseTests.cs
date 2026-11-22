using System.Buffers.Binary;
using System.Net;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Platform.Windows.Capture;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Tests;

/// <summary>
/// Windows capture refuses new IPv6 connections through a route that Core marked as unable to reach IPv6
/// (<see cref="RoutingEngine.Ipv6Fallback"/>, #31, part of #4): the SYN is answered with a TCP reset so the app falls
/// back to IPv4 at once. Windows only asks; marking is Core's job.
/// </summary>
public class WindowsIpv6RefuseTests
{
    private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress V4 = IPAddress.Parse("192.0.2.10");
    private static readonly RuleAction ViaTunnel = RuleAction.ViaTunnel(100);

    private static RoutingEngine Engine()
    {
        var profile = Profile.CreateDefault();
        profile.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "192.0.2.1", Port = 1080 });
        profile.Tunnels.Add(new Tunnel { Id = 101, Type = TunnelType.Socks5, Address = "198.51.100.1", Port = 1080 });
        return new RoutingEngine(profile);
    }

    /// <summary>What Core sees when the tunnel opens names but not two IPv6 addresses: the route gets marked.</summary>
    private static void Mark(RoutingEngine engine, string route)
    {
        var failure = new ProxyException("SOCKS5: general SOCKS server failure") { Socks5Reply = 0x01 };
        engine.Ipv6Fallback.Opened(route, "example.com");
        engine.Ipv6Fallback.FailedBeforeOpen(route, "2001:db8::1", failure);
        engine.Ipv6Fallback.FailedBeforeOpen(route, "2001:db8::2", failure);
        Assert.True(engine.Ipv6Fallback.ShouldRefuse(route));
    }

    [Fact]
    public void Ipv6_through_a_marked_tunnel_is_refused()
    {
        var engine = Engine();
        var route = engine.Describe(ViaTunnel);
        Assert.False(Interceptor.ShouldRefuseIpv6(engine, V6, ViaTunnel, out _));   // not marked yet
        Mark(engine, route);
        Assert.True(Interceptor.ShouldRefuseIpv6(engine, V6, ViaTunnel, out var via));
        Assert.Equal(route, via);
    }

    [Fact]
    public void Ipv4_ipv4_mapped_direct_block_and_other_routes_are_never_refused()
    {
        var engine = Engine();
        Mark(engine, engine.Describe(ViaTunnel));
        Assert.False(Interceptor.ShouldRefuseIpv6(engine, V4, ViaTunnel, out _));
        Assert.False(Interceptor.ShouldRefuseIpv6(engine, V4.MapToIPv6(), ViaTunnel, out _));
        Assert.False(Interceptor.ShouldRefuseIpv6(engine, V6, RuleAction.Direct, out _));
        Assert.False(Interceptor.ShouldRefuseIpv6(engine, V6, RuleAction.Block, out _));
        Assert.False(Interceptor.ShouldRefuseIpv6(engine, V6, RuleAction.ViaTunnel(101), out _));
    }

    // ------------------------------------------------------------ the reset packet

    private static byte[] Syn6(IPAddress src, ushort sport, IPAddress dst, ushort dport, uint seq, byte flags = 0x02)
    {
        var p = new byte[40 + 40];                           // IPv6 + TCP with 20 bytes of options, as a real SYN has
        p[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), 40);
        p[6] = 6;
        p[7] = 128;
        src.GetAddressBytes().CopyTo(p, 8);
        dst.GetAddressBytes().CopyTo(p, 24);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(40), sport);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(42), dport);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(44), seq);
        p[52] = 10 << 4;
        p[53] = flags;
        return p;
    }

    private static byte[] Syn4(IPAddress src, ushort sport, IPAddress dst, ushort dport, uint seq)
    {
        var p = new byte[20 + 20];
        p[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 40);
        p[8] = 128;
        p[9] = 6;
        src.GetAddressBytes().CopyTo(p, 12);
        dst.GetAddressBytes().CopyTo(p, 16);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), sport);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), dport);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(24), seq);
        p[32] = 5 << 4;
        p[33] = 0x02;
        return p;
    }

    [Fact]
    public void Ipv6_reset_answers_the_syn_from_its_destination()
    {
        var local = IPAddress.Parse("2001:db8::abcd");
        var r = Interceptor.BuildSynReset(Syn6(local, 50123, V6, 443, 0xFFFFFFFF))!;   // seq + 1 wraps to 0

        Assert.Equal(60, r.Length);
        Assert.Equal(6, r[0] >> 4);
        Assert.Equal(20, BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(4)));
        Assert.Equal(6, r[6]);
        Assert.Equal(V6, new IPAddress(r.AsSpan(8, 16)));        // from the site …
        Assert.Equal(local, new IPAddress(r.AsSpan(24, 16)));   // … to the app
        Assert.Equal(443, BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(40)));
        Assert.Equal(50123, BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(42)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(44)));   // seq
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(48)));   // ack = 0xFFFFFFFF + 1
        Assert.Equal(5 << 4, r[52]);
        Assert.Equal(0x14, r[53]);                               // RST + ACK
    }

    [Fact]
    public void Ipv4_reset_has_the_same_shape()
    {
        var local = IPAddress.Parse("192.0.2.50");
        var r = Interceptor.BuildSynReset(Syn4(local, 50123, V4, 443, 1000))!;

        Assert.Equal(40, r.Length);
        Assert.Equal(0x45, r[0]);
        Assert.Equal(40, BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2)));
        Assert.Equal(6, r[9]);
        Assert.Equal(V4, new IPAddress(r.AsSpan(12, 4)));
        Assert.Equal(local, new IPAddress(r.AsSpan(16, 4)));
        Assert.Equal(443, BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(20)));
        Assert.Equal(50123, BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(22)));
        Assert.Equal(1001u, BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(28)));
        Assert.Equal(0x14, r[33]);
    }

    [Theory]
    [InlineData((byte)0x12)]   // SYN+ACK
    [InlineData((byte)0x10)]   // ACK
    [InlineData((byte)0x06)]   // SYN+RST
    public void Only_a_plain_syn_gets_a_reset(byte flags) =>
        Assert.Null(Interceptor.BuildSynReset(Syn6(IPAddress.Parse("2001:db8::abcd"), 50123, V6, 443, 1, flags)));

    [Fact]
    public void Other_packets_get_no_reset()
    {
        var syn = Syn6(IPAddress.Parse("2001:db8::abcd"), 50123, V6, 443, 1);
        syn[6] = 17;                                             // UDP
        Assert.Null(Interceptor.BuildSynReset(syn));
        Assert.Null(Interceptor.BuildSynReset(new byte[10]));
        Assert.Null(Interceptor.BuildSynReset(syn.AsSpan(0, 50)));   // truncated TCP header
    }
}
