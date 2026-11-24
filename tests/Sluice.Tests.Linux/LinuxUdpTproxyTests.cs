using System.Buffers.Binary;
using System.Net;
using Sluice.Core.Engine;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>The pieces of the UDP tproxy that need no privileges: the nft rules, the netlink messages, socket addresses
/// and control data, and the per-flow choice.</summary>
public class LinuxUdpTproxyTests
{
    [Fact]
    public void Nft_rules_mark_taken_udp_flows_and_send_them_to_the_transparent_socket()
    {
        var r = NftRules.Build(new NftRules.Settings(Queue: 1473, Mark: 0x5C1CE, Gid: 378318, RelayPort: 41000, DnsPort: 42000,
            QueueUdp: true, RejectMark: 0x5C1CF, UdpMark: 0x5C1D0, UdpPort: 43000));

        // Saved to conntrack when the verdict set it; put back on later packets (a route chain, so they are rerouted).
        Assert.Contains("type route hook output priority -149; policy accept;", r);
        Assert.Contains("meta l4proto udp meta mark 377296 ct mark set meta mark return", r);
        Assert.Contains("meta l4proto udp ct direction original ct mark 377296 meta mark set ct mark", r);
        // Back in through lo: to Sluice's transparent socket, destination unchanged.
        Assert.Contains("type filter hook prerouting priority mangle; policy accept;", r);
        Assert.Contains("meta l4proto udp meta mark 377296 tproxy to :43000 accept", r);
        // Sluice's own sockets (its replies) are never marked: the skgid return comes before the restore.
        var chain = r[r.IndexOf("chain udp_mark", StringComparison.Ordinal)..];
        Assert.True(chain.IndexOf("meta skgid 378318 return", StringComparison.Ordinal) < chain.IndexOf("meta mark set ct mark", StringComparison.Ordinal));
        Assert.DoesNotContain('\r', r);

        // Without a transparent socket (or without UDP capture) there is nothing to send marked packets to.
        Assert.DoesNotContain("tproxy", NftRules.Build(new NftRules.Settings(1473, 1, 2, 41000, null, QueueUdp: true)));
        Assert.DoesNotContain("tproxy", NftRules.Build(new NftRules.Settings(1473, 1, 2, 41000, null, QueueUdp: false, UdpMark: 3, UdpPort: 43000)));
    }

    [Theory]
    [InlineData(PolicyRoutes.AF_INET)]
    [InlineData(PolicyRoutes.AF_INET6)]
    public void Routing_rule_message_matches_the_mark_and_points_at_the_table(byte family)
    {
        var m = PolicyRoutes.RuleMessage(32, 0x600, seq: 7, family, mark: 0x5C1D0);   // RTM_NEWRULE, CREATE | EXCL

        Assert.Equal(m.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(m));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(4)));
        Assert.Equal(1 | 4 | 0x600, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(6)));   // REQUEST | ACK | CREATE | EXCL
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(8)));
        Assert.Equal(family, m[16]);                 // fib_rule_hdr.family
        Assert.Equal(0, m[20]);                      // table: in FRA_TABLE
        Assert.Equal(1, m[23]);                      // FR_ACT_TO_TBL
        var attrs = Attributes(m, 16 + 12);
        Assert.Equal((uint)PolicyRoutes.Priority, attrs[6]);   // FRA_PRIORITY
        Assert.Equal(0x5C1D0u, attrs[10]);                     // FRA_FWMARK
        Assert.Equal(0xFFFFFFFFu, attrs[16]);                  // FRA_FWMASK
        Assert.Equal((uint)PolicyRoutes.Table, attrs[15]);     // FRA_TABLE
        Assert.True(PolicyRoutes.Priority < 32766);            // before the main table
    }

    [Fact]
    public void Route_message_is_local_default_through_lo_in_the_table()
    {
        var m = PolicyRoutes.RouteMessage(24, 0x600, seq: 9, PolicyRoutes.AF_INET6, loopbackIndex: 1);   // RTM_NEWROUTE

        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(4)));
        Assert.Equal(PolicyRoutes.AF_INET6, m[16]);  // rtmsg.family
        Assert.Equal(0, m[17]);                      // dst_len 0: the default route
        Assert.Equal(254, m[22]);                    // RT_SCOPE_HOST
        Assert.Equal(2, m[23]);                      // RTN_LOCAL
        var attrs = Attributes(m, 16 + 12);
        Assert.Equal((uint)PolicyRoutes.Table, attrs[15]);   // RTA_TABLE
        Assert.Equal(1u, attrs[4]);                           // RTA_OIF: lo
    }

    private static Dictionary<int, uint> Attributes(byte[] m, int offset)
    {
        var d = new Dictionary<int, uint>();
        for (var a = offset; a + 8 <= m.Length; a += 8)
        {
            Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(a)));
            d[BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(a + 2))] = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(a + 4));
        }
        return d;
    }

    [Theory]
    [InlineData("198.18.0.7", 443)]
    [InlineData("203.0.113.5", 3478)]
    [InlineData("2001:db8::1", 123)]
    public void Socket_addresses_round_trip(string ip, int port)
    {
        var ep = new IPEndPoint(IPAddress.Parse(ip), port);
        Assert.Equal(ep, TransparentUdp.ParseSockaddr(TransparentUdp.Sockaddr(ep)));
    }

    [Fact]
    public void Ipv4_mapped_source_is_read_as_ipv4()
    {
        var mapped = TransparentUdp.Sockaddr(new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.10"), 5000));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 5000), TransparentUdp.ParseSockaddr(mapped));
        Assert.Null(TransparentUdp.ParseSockaddr([2, 0, 1]));
    }

    [Fact]
    public void Original_destination_is_found_among_control_messages()
    {
        var dst4 = new IPEndPoint(IPAddress.Parse("198.18.0.42"), 443);
        var dst6 = new IPEndPoint(IPAddress.Parse("2001:db8::53"), 5300);
        // Another control message first (IP_TTL, 4 bytes of data, padded to 8), then the original destination.
        var control = Cmsg(0, 2, [64, 0, 0, 0]).Concat(Cmsg(0, 20, TransparentUdp.Sockaddr(dst4))).ToArray();
        Assert.True(TransparentUdp.TryParseOriginalDestination(control, out var found));
        Assert.Equal(dst4, found);

        Assert.True(TransparentUdp.TryParseOriginalDestination(Cmsg(41, 74, TransparentUdp.Sockaddr(dst6)), out found));
        Assert.Equal(dst6, found);

        Assert.False(TransparentUdp.TryParseOriginalDestination(Cmsg(0, 2, [64, 0, 0, 0]), out _));       // none there
        Assert.False(TransparentUdp.TryParseOriginalDestination(control.AsSpan(0, control.Length - 8), out _));   // cut short
    }

    /// <summary>One 64-bit cmsghdr (len, level, type) with its data, padded to 8 bytes.</summary>
    private static byte[] Cmsg(int level, int type, byte[] data)
    {
        var len = 16 + data.Length;
        var b = new byte[(len + 7) & ~7];
        BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)len);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), level);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), type);
        data.CopyTo(b, 16);
        return b;
    }

    [Theory]
    [InlineData(UdpVerdict.Tunnel, false, true, "Take")]
    [InlineData(UdpVerdict.Tunnel, true, true, "Take")]
    [InlineData(UdpVerdict.Pass, true, true, "Take")]     // Direct to a placeholder: sent by name
    [InlineData(UdpVerdict.Pass, false, true, "Pass")]    // Direct to a real address: untouched
    [InlineData(UdpVerdict.Block, true, true, "Drop")]
    [InlineData(UdpVerdict.Block, false, true, "Drop")]
    [InlineData(UdpVerdict.Tunnel, false, false, "Drop")] // no tproxy: as before #60
    [InlineData(UdpVerdict.Pass, true, false, "Drop")]
    [InlineData(UdpVerdict.Pass, false, false, "Pass")]
    public void Udp_flow_choice(UdpVerdict verdict, bool fake, bool canTake, string expected) =>
        Assert.Equal(expected, LinuxCapture.Choose(verdict, fake, canTake).ToString());

    // #216: without IPv6 to the transparent socket (no IPv6 route or socket), an IPv6 flow isn't taken: marked, it would
    // go out past the tunnel. IPv4, placeholders included, is taken as before.
    [Theory]
    [InlineData(true, true, "2001:db8::1", true)]
    [InlineData(true, false, "2001:db8::1", false)]
    [InlineData(true, false, "198.51.100.7", true)]
    [InlineData(true, false, "198.18.0.42", true)]            // a placeholder
    [InlineData(true, false, "::ffff:198.51.100.7", true)]    // IPv4 seen through an IPv6 socket
    [InlineData(false, true, "198.51.100.7", false)]          // no transparent socket at all
    [InlineData(false, true, "2001:db8::1", false)]
    public void Ipv6_flows_are_taken_only_when_ipv6_reaches_the_transparent_socket(bool tproxyUp, bool ipv6Usable, string destination, bool expected) =>
        Assert.Equal(expected, LinuxCapture.CanTake(tproxyUp, ipv6Usable, IPAddress.Parse(destination)));

    [Theory]
    [InlineData("2001:db8::1", UdpVerdict.Tunnel, "Drop")]    // tunnel rule, IPv6 can't be taken: dropped, not leaked
    [InlineData("198.51.100.7", UdpVerdict.Tunnel, "Take")]
    [InlineData("2001:db8::1", UdpVerdict.Pass, "Pass")]      // Direct to a real IPv6 address: untouched, as before
    public void Without_ipv6_tunneled_ipv6_udp_is_dropped(string destination, UdpVerdict verdict, string expected) =>
        Assert.Equal(expected, LinuxCapture.Choose(verdict, fake: false,
            LinuxCapture.CanTake(tproxyUp: true, ipv6Usable: false, IPAddress.Parse(destination))).ToString());

    [Theory]
    [InlineData("8.8.8.8", 53, true)]
    [InlineData("192.168.1.1", 67, true)]
    [InlineData("224.0.0.251", 5353, true)]
    [InlineData("239.255.255.250", 1900, true)]
    [InlineData("255.255.255.255", 9999, true)]
    [InlineData("169.254.1.1", 9999, true)]
    [InlineData("ff02::1", 9999, true)]
    [InlineData("fe80::1", 9999, true)]
    [InlineData("198.18.0.7", 443, false)]
    [InlineData("203.0.113.5", 123, false)]
    [InlineData("2001:db8::1", 443, false)]
    public void Udp_destinations_never_taken(string ip, int port, bool never) =>
        Assert.Equal(never, LinuxCapture.IsNeverTaken(new IPEndPoint(IPAddress.Parse(ip), port)));
}
