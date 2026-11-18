using System.Net;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>
/// The capture reads the first packet of each new flow from NFQUEUE. IPv6 packets may carry extension headers between
/// the IPv6 header and TCP/UDP (nftables' <c>meta l4proto</c> looks past them, so such packets are queued too); the
/// parser has to follow the chain to find the ports, or the connection passes uncaptured.
/// </summary>
public class LinuxPacketInfoTests
{
    private static readonly IPAddress Src = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress Dst = IPAddress.Parse("2001:db8::2");
    private const int HopByHop = 0, Tcp = 6, Udp = 17, Routing = 43, Fragment = 44, Esp = 50, Ah = 51, NoNext = 59, DestOpts = 60;

    /// <summary>An IPv6 packet: the header, then each (type, bytes) extension header in turn, then a TCP/UDP header.</summary>
    private static byte[] Packet(int upper, params (int Type, byte[] Body)[] exts)
    {
        var p = new List<byte>();
        var payload = new List<byte>();
        for (var i = 0; i < exts.Length; i++)
        {
            var next = i + 1 < exts.Length ? exts[i + 1].Type : upper;
            var body = exts[i].Body.ToArray();
            body[0] = (byte)next;
            payload.AddRange(body);
        }
        // TCP (20 bytes, SYN) or UDP (8 bytes) from port 40000 to 443
        var l4 = new byte[upper == Udp ? 8 : 20];
        l4[0] = 40000 >> 8; l4[1] = 40000 & 0xFF; l4[2] = 443 >> 8; l4[3] = 443 & 0xFF;
        if (upper == Tcp) { l4[12] = 0x50; l4[13] = 0x02; }
        payload.AddRange(l4);

        p.AddRange([0x60, 0, 0, 0, (byte)(payload.Count >> 8), (byte)payload.Count, (byte)(exts.Length > 0 ? exts[0].Type : upper), 64]);
        p.AddRange(Src.GetAddressBytes());
        p.AddRange(Dst.GetAddressBytes());
        p.AddRange(payload);
        return [.. p];
    }

    /// <summary>Hop-by-Hop / Destination Options / Routing: next header, length in 8-octet units after the first 8.</summary>
    private static (int, byte[]) Options(int type, int units = 0)
    {
        var b = new byte[8 * (units + 1)];
        b[1] = (byte)units;
        if (type != Routing) { b[2] = 1; b[3] = (byte)(b.Length - 4); }   // one PadN option filling the header
        return (type, b);
    }

    private static (int, byte[]) Frag(int offset8, bool more = false)
    {
        var b = new byte[8];
        var v = (ushort)((offset8 << 3) | (more ? 1 : 0));
        b[2] = (byte)(v >> 8); b[3] = (byte)v;
        return (Fragment, b);
    }

    private static (int, byte[]) AuthHeader()
    {
        var b = new byte[24];            // 12 fixed bytes + 12-byte ICV: length field = 24/4 - 2 = 4
        b[1] = 4;
        return (Ah, b);
    }

    private static void AssertFlow(byte[] packet, int protocol)
    {
        Assert.True(PacketInfo.TryParse(packet, out var f));
        Assert.Equal(protocol, f.Protocol);
        Assert.Equal(new IPEndPoint(Src, 40000), f.Source);
        Assert.Equal(new IPEndPoint(Dst, 443), f.Destination);
    }

    [Fact]
    public void Plain_ipv6_tcp_and_udp_still_parse()
    {
        AssertFlow(Packet(Tcp), Tcp);
        AssertFlow(Packet(Udp), Udp);
    }

    [Theory]
    [InlineData(HopByHop)]
    [InlineData(DestOpts)]
    [InlineData(Routing)]
    public void One_extension_header_is_skipped(int type)
    {
        AssertFlow(Packet(Tcp, Options(type)), Tcp);
        AssertFlow(Packet(Udp, Options(type, units: 2)), Udp);       // a longer one: 24 bytes
    }

    [Fact]
    public void A_chain_of_headers_is_followed()
    {
        AssertFlow(Packet(Tcp, Options(HopByHop), Options(DestOpts, 1), Options(Routing), Frag(0, more: true), AuthHeader(), Options(DestOpts)), Tcp);
    }

    [Fact]
    public void The_first_fragment_is_read_and_later_ones_are_not()
    {
        AssertFlow(Packet(Udp, Frag(0, more: true)), Udp);
        Assert.False(PacketInfo.TryParse(Packet(Udp, Frag(185)), out _));    // offset 1480: no UDP header in it
    }

    [Fact]
    public void A_routing_header_with_segments_left_is_not_a_flow()
    {
        // The IPv6 destination is only the next hop; the real one is in the Routing header. Capturing would send the
        // connection to the wrong address, so it passes uncaptured as before. With no segments left, the header's
        // destination is the final one, so it is read (One_extension_header_is_skipped).
        var (type, body) = Options(Routing, units: 2);
        body[2] = 4;                                                         // Segment Routing header (RFC 8754)
        body[3] = 1;                                                         // one segment left
        Assert.False(PacketInfo.TryParse(Packet(Tcp, (type, body)), out _));
        Assert.False(PacketInfo.TryParse(Packet(Tcp, Options(HopByHop), (type, body)), out _));
    }

    [Fact]
    public void Encrypted_or_empty_payloads_are_not_flows()
    {
        Assert.False(PacketInfo.TryParse(Packet(Tcp, (Esp, new byte[8])), out _));
        var none = Packet(Tcp, Options(DestOpts));
        none[40] = NoNext;                                                   // the options header says nothing follows
        Assert.False(PacketInfo.TryParse(none, out _));
    }

    [Fact]
    public void Truncated_or_endless_chains_are_rejected()
    {
        var p = Packet(Tcp, Options(HopByHop, units: 3));
        Assert.False(PacketInfo.TryParse(p.AsSpan(0, 40 + 16), out _));    // the header says 32 bytes, 16 are there
        Assert.False(PacketInfo.TryParse(p.AsSpan(0, 44), out _));         // not even the first 8 bytes of it

        var loop = Packet(Tcp, Enumerable.Range(0, 20).Select(_ => Options(DestOpts)).ToArray());
        Assert.False(PacketInfo.TryParse(loop, out _));                     // longer than any real chain
    }
}
