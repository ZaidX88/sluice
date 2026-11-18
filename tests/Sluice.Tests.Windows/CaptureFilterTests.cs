using System.Net;
using Sluice.Core.Interception;

namespace Sluice.Tests;

/// <summary>The narrowed WinDivert filters: what each one takes, and that WinDivert accepts them.</summary>
public class CaptureFilterTests
{
    private const ushort Relay = 50123;

    [Fact]
    public void Main_takes_new_syns_relay_packets_fake_range_and_dns()
    {
        var f = CaptureFilter.Main(Relay, captureUdp: false);
        Assert.StartsWith("outbound and !loopback and (", f);
        Assert.Contains("(tcp.Syn and !tcp.Ack)", f);
        Assert.Contains("tcp.SrcPort == 50123", f);
        Assert.Contains(CaptureFilter.FakeRange, f);
        Assert.Contains("(udp and udp.DstPort == 53)", f);
        Assert.Contains(" or udp)", CaptureFilter.Main(Relay, captureUdp: true));
    }

    [Fact]
    public void Followers_list_each_address_once_and_skip_syns_and_the_relay()
    {
        Assert.Null(CaptureFilter.Followers(Relay, []));
        var f = CaptureFilter.Followers(Relay,
            [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("::ffff:93.184.216.34"), IPAddress.Parse("2606:2800:220:1::1")])!;
        Assert.Contains("!tcp.Syn", f);
        Assert.Contains("tcp.SrcPort != 50123", f);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(f, @"ip\.DstAddr == 93\.184\.216\.34"));
        Assert.Contains("ipv6.DstAddr == 2606:2800:220:1::1", f);
    }

    [Fact]
    public void Too_many_addresses_fall_back_to_all_later_packets()
    {
        var many = Enumerable.Range(0, CaptureFilter.MaxFollowed + 1).Select(i => new IPAddress([10, 0, (byte)(i / 256), (byte)(i % 256)]));
        Assert.Equal(CaptureFilter.FollowersBroad(Relay), CaptureFilter.Followers(Relay, many));
        Assert.EndsWith(CaptureFilter.NotFakeRange, CaptureFilter.FollowersBroad(Relay));
    }

    [Fact]
    public void WinDivert_accepts_the_filters()
    {
        if (!OperatingSystem.IsWindows()) return;   // WinDivert is Windows-only
        var some = Enumerable.Range(1, 40).Select(i => new IPAddress([93, 184, 216, (byte)i]))
            .Append(IPAddress.Parse("2606:2800:220:1::1")).Append(IPAddress.Parse("fe80::1%12"));
        string[] filters =
        [
            CaptureFilter.Main(Relay, false), CaptureFilter.Main(Relay, true),
            CaptureFilter.Followers(Relay, some)!, CaptureFilter.FollowersBroad(Relay),
        ];
        foreach (var f in filters)
            Assert.Null(WinDivertNative.CheckFilter(f, WinDivertNative.Layer.Network));
    }

    [Fact]
    public void The_largest_followed_list_still_compiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var max = Enumerable.Range(0, CaptureFilter.MaxFollowed).Select(i => new IPAddress([10, 1, (byte)(i / 256), (byte)(i % 256)]));
        Assert.Null(WinDivertNative.CheckFilter(CaptureFilter.Followers(Relay, max)!, WinDivertNative.Layer.Network));
    }
}
