using System.Net;
using Sluice.Core.Engine;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>
/// Linux capture refuses new IPv6 connections through a route that Core marked as unable to reach IPv6
/// (<see cref="RoutingEngine.Ipv6Fallback"/>, #30, part of #4), so apps fall back to IPv4 at once. Linux only asks;
/// marking is Core's job.
/// </summary>
public class LinuxIpv6RefuseTests
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
        var unreachable = new ProxyException("SOCKS5: host unreachable") { Socks5Reply = 0x04 };
        engine.Ipv6Fallback.Opened(route, "example.com");
        engine.Ipv6Fallback.FailedBeforeOpen(route, "2001:db8::1", unreachable);
        engine.Ipv6Fallback.FailedBeforeOpen(route, "2001:db8::2", unreachable);
        Assert.True(engine.Ipv6Fallback.ShouldRefuse(route));
    }

    [Fact]
    public void Ipv6_through_a_marked_tunnel_is_refused()
    {
        var engine = Engine();
        var route = engine.Describe(ViaTunnel);

        Assert.False(LinuxCapture.ShouldRefuseIpv6(engine, V6, ViaTunnel, out _));   // not marked yet
        Mark(engine, route);

        Assert.True(LinuxCapture.ShouldRefuseIpv6(engine, V6, ViaTunnel, out var via));
        Assert.Equal(route, via);
    }

    [Fact]
    public void Ipv4_and_ipv4_mapped_addresses_are_never_refused()
    {
        var engine = Engine();
        Mark(engine, engine.Describe(ViaTunnel));

        Assert.False(LinuxCapture.ShouldRefuseIpv6(engine, V4, ViaTunnel, out _));
        Assert.False(LinuxCapture.ShouldRefuseIpv6(engine, V4.MapToIPv6(), ViaTunnel, out _));
    }

    [Fact]
    public void Direct_and_blocked_routes_are_never_refused()
    {
        var engine = Engine();
        Mark(engine, engine.Describe(ViaTunnel));

        Assert.False(LinuxCapture.ShouldRefuseIpv6(engine, V6, RuleAction.Direct, out _));
        Assert.False(LinuxCapture.ShouldRefuseIpv6(engine, V6, RuleAction.Block, out _));
    }

    [Fact]
    public void Only_the_marked_route_is_refused()
    {
        var engine = Engine();
        Mark(engine, engine.Describe(ViaTunnel));

        Assert.False(LinuxCapture.ShouldRefuseIpv6(engine, V6, RuleAction.ViaTunnel(101), out _));
    }

    [Fact]
    public void Ruleset_refuses_syns_with_the_reject_mark_after_the_other_chains()
    {
        var r = NftRules.Build(new NftRules.Settings(1473, 0x5C1CE, 378318, 41000, 42000, QueueUdp: true, RejectMark: 0x5C1CF));

        Assert.Contains("chain refuse {", r);
        Assert.Contains("type filter hook output priority filter; policy accept;", r);
        Assert.Contains("meta mark 377295 meta l4proto tcp reject with tcp reset", r);
        Assert.True(r.IndexOf("chain refuse", StringComparison.Ordinal) > r.IndexOf("chain to_sluice", StringComparison.Ordinal));
        Assert.DoesNotContain('\r', r);

        var without = NftRules.Build(new NftRules.Settings(1473, 0x5C1CE, 378318, 41000, 42000, QueueUdp: true));
        Assert.DoesNotContain("refuse", without);
        Assert.DoesNotContain("reject", without);
    }
}
