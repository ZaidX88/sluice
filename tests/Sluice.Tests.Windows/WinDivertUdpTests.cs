using System.Net;
using System.Text;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Profiles;
using Sluice.Platform.Windows.Capture;

namespace Sluice.Tests;

/// <summary>
/// The WinDivert adapter over <see cref="UdpFlows"/> (#53) with synthetic packets: capture → SOCKS5 UDP → echo → injected
/// reply. The flow logic itself is covered by <c>UdpFlowsTests</c>.
/// </summary>
public class WinDivertUdpTests
{
    private static readonly IPEndPoint App = new(IPAddress.Parse("192.168.50.10"), 50123);

    [Fact]
    public async Task A_tunneled_packet_is_taken_and_the_reply_is_injected_with_its_address()
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
        await using var udp = new WinDivertUdp(engine, fakes, (pkt, ctx) => injected.TrySetResult((pkt, ctx)), _ => 4242);

        var dst = new IPEndPoint(fakeIp, echo.Port);
        var packet = UdpPacket.Build(App, dst, "hello"u8);
        var buffer = new byte[packet.Length + 100];   // the capture's buffer is larger than the packet
        packet.CopyTo(buffer, 0);
        Assert.Equal(UdpFlows.Verdict.Drop, udp.ProcessOutbound(buffer, packet.Length, "ctx-1"));
        Array.Clear(buffer);                          // and is reused at once

        var (reply, ctx) = await injected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ctx-1", ctx);
        Assert.True(UdpPacket.TryParse(reply, out var from, out var to, out var off, out var len));
        Assert.Equal(dst, from);                      // from where the app sent it, the placeholder address
        Assert.Equal(App, to);
        Assert.Equal("hello", Encoding.ASCII.GetString(reply, off, len));
        Assert.Equal("game.example", socks.LastTargetHost);
        Assert.Equal(1, udp.FlowCount);
        Assert.Equal(1, udp.DatagramsTunneled);
        udp.Sweep(TimeSpan.Zero);
        Assert.Equal(0, udp.FlowCount);
    }

    [Fact]
    public async Task Direct_traffic_and_packets_that_are_not_UDP_pass()
    {
        await using var engine = new RoutingEngine(Profile.CreateDefault());
        await using var udp = new WinDivertUdp(engine, new FakeIpPool(), (_, _) => throw new InvalidOperationException("no injection expected"), _ => 4242);
        var direct = UdpPacket.Build(App, new IPEndPoint(IPAddress.Parse("203.0.113.1"), 3478), "x"u8);
        Assert.Equal(UdpFlows.Verdict.Pass, udp.ProcessOutbound(direct, direct.Length, null));   // Default rule: Direct
        Assert.Equal(UdpFlows.Verdict.Pass, udp.ProcessOutbound(new byte[30], 30, null));        // not an IP/UDP packet
        Assert.Equal(UdpFlows.Verdict.Pass, udp.ProcessOutbound(direct, 10, null));              // truncated
    }
}
