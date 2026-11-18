using System.Net;
using Sluice.Core.Engine;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>
/// Linux capture refuses IPv6 connections meant for a route through which IPv6 destinations failed, so apps fall back
/// to IPv4 instead of retrying an IPv6 address the tunnel server cannot reach.
/// </summary>
public class LinuxIpv6FallbackTests
{
    private const string Ssh = "SSH alice@ssh.example";
    private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress V4 = IPAddress.Parse("192.0.2.10");

    private DateTime _now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private Ipv6Fallback New() => new(() => _now);

    [Fact]
    public void A_failed_ipv6_connection_marks_the_route_and_ipv6_is_refused_through_it()
    {
        var f = New();
        Assert.False(f.ShouldRefuse(V6, Ssh));

        Assert.True(f.Observe("[2001:db8::10]:443", Ssh, ConnectionState.Failed, "SOCKS5: general SOCKS server failure", 0));
        Assert.False(f.Observe("[2001:db8::11]:443", Ssh, ConnectionState.Failed, "SOCKS5: general SOCKS server failure", 0)); // already marked

        Assert.True(f.ShouldRefuse(V6, Ssh));
        Assert.True(f.ShouldRefuse(IPAddress.Parse("2001:db8::99"), Ssh));     // any IPv6 destination through that route
        Assert.False(f.ShouldRefuse(V4, Ssh));                                 // IPv4 still goes through
        Assert.False(f.ShouldRefuse(V4.MapToIPv6(), Ssh));                     // an IPv4 address, written as IPv6
        Assert.False(f.ShouldRefuse(V6, "SOCKS5 socks.example:1080"));         // other routes are not affected
    }

    [Fact]
    public void Ipv4_name_direct_and_blocked_failures_do_not_mark_anything()
    {
        var f = New();
        Assert.False(f.Observe("192.0.2.10:443", Ssh, ConnectionState.Failed, "x", 0));
        Assert.False(f.Observe("example.com:443", Ssh, ConnectionState.Failed, "x", 0));
        Assert.False(f.Observe("[::ffff:192.0.2.10]:443", Ssh, ConnectionState.Failed, "x", 0));
        Assert.False(f.Observe("[2001:db8::10]:443", "Direct", ConnectionState.Failed, "x", 0));
        Assert.False(f.Observe("[2001:db8::10]:443", "Block", ConnectionState.Blocked, "x", 0));

        Assert.False(f.ShouldRefuse(V6, Ssh));
        Assert.False(f.ShouldRefuse(V6, "Direct"));
    }

    [Fact]
    public void The_mark_expires_so_ipv6_is_tried_again()
    {
        var f = New();
        f.Observe("[2001:db8::10]:443", Ssh, ConnectionState.Failed, "x", 0);

        _now += Ipv6Fallback.Hold - TimeSpan.FromSeconds(1);
        Assert.True(f.ShouldRefuse(V6, Ssh));
        _now += TimeSpan.FromSeconds(2);
        Assert.False(f.ShouldRefuse(V6, Ssh));
    }

    [Fact]
    public void A_working_ipv6_connection_clears_the_mark()
    {
        var f = New();
        f.Observe("[2001:db8::10]:443", Ssh, ConnectionState.Failed, "x", 0);

        f.Observe("[2001:db8::20]:443", Ssh, ConnectionState.Closed, "disconnected", 0);   // cut by the user: says nothing
        Assert.True(f.ShouldRefuse(V6, Ssh));

        f.Observe("[2001:db8::20]:443", Ssh, ConnectionState.Closed, null, 5000);
        Assert.False(f.ShouldRefuse(V6, Ssh));
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
