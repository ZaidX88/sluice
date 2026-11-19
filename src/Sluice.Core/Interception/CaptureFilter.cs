using System.Net;
using System.Net.Sockets;

namespace Sluice.Core.Interception;

/// <summary>
/// WinDivert network-layer filters for <see cref="Interceptor"/>. Only packets Sluice may have to rewrite are captured,
/// so every other packet on the PC (direct apps, the tunnel helpers' own connections to their servers) never makes the
/// trip through Sluice's user-mode thread:
/// <list type="bullet">
/// <item><b>Main</b>: every new connection's first SYN (to decide it), the relay's packets back to apps, everything to
/// the fake-address range (those flows are always redirected), and UDP as the UDP mode needs.</item>
/// <item><b>Followers</b>: the later packets of redirected connections to real addresses (a rule matched an app or an
/// IP rather than a faked name). The set of addresses changes, so this filter is rebuilt and its handle reopened.</item>
/// </list>
/// The two filters never match the same packet (followers exclude SYNs and the relay port; fake addresses are never
/// followed), so a packet is handled once.
/// </summary>
public static class CaptureFilter
{
    /// <summary>198.18.0.0/15, the fake-address range (<see cref="Dns.FakeIpPool"/>).</summary>
    public const string FakeRange = "(ip.DstAddr >= 198.18.0.0 and ip.DstAddr <= 198.19.255.255)";

    /// <summary>The opposite of <see cref="FakeRange"/> (WinDivert can't negate a bracketed expression).</summary>
    public const string NotFakeRange = "(ipv6 or ip.DstAddr < 198.18.0.0 or ip.DstAddr > 198.19.255.255)";

    /// <summary>More followed addresses than this: use <see cref="FollowersBroad"/> (WinDivert filters have a size limit).</summary>
    public const int MaxFollowed = 150;

    /// <summary>
    /// UDP from port 53 is a DNS server's answer (for example Windows' DNS proxy answering WSL and virtual machines), never
    /// an app's query or datagram: it is left alone, or the UDP mode would block it (docs/windows/FIXES.md §1).
    /// </summary>
    public static string Main(ushort relayPort, bool captureUdp) =>
        "outbound and !loopback and (" +
        $"(tcp and ((tcp.Syn and !tcp.Ack) or tcp.SrcPort == {relayPort} or {FakeRange}))" +
        (captureUdp ? " or (udp and udp.SrcPort != 53))" : " or (udp and udp.DstPort == 53 and udp.SrcPort != 53))");

    /// <summary>The filter for later packets to <paramref name="addresses"/>; null when there are none.</summary>
    public static string? Followers(ushort relayPort, IEnumerable<IPAddress> addresses)
    {
        var tests = addresses.Select(Test).Distinct().Order(StringComparer.Ordinal).ToList();
        if (tests.Count == 0) return null;
        if (tests.Count > MaxFollowed) return FollowersBroad(relayPort);
        return FollowersPrefix(relayPort) + "(" + string.Join(" or ", tests) + ")";
    }

    /// <summary>Every later TCP packet that the main filter doesn't take (the behaviour before narrowing).</summary>
    public static string FollowersBroad(ushort relayPort) => FollowersPrefix(relayPort) + NotFakeRange;

    private static string FollowersPrefix(ushort relayPort) =>
        $"outbound and !loopback and tcp and !tcp.Syn and tcp.SrcPort != {relayPort} and ";

    private static string Test(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        return a.AddressFamily == AddressFamily.InterNetwork
            ? $"ip.DstAddr == {a}"
            : $"ipv6.DstAddr == {new IPAddress(a.GetAddressBytes())}";   // no scope id: WinDivert doesn't parse "%n"
    }
}
