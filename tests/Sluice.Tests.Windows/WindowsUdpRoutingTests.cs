using Sluice.Core.Engine;
using Sluice.Core.Profiles;

namespace Sluice.Tests;

/// <summary>UDP routing by application, with a Windows path. The rest of UDP routing is in the shared project.</summary>
public class WindowsUdpRoutingTests
{
    [Fact]
    public async Task AppRoutedThroughSsh_GetsItsUdpBlocked()
    {
        var p = Profile.CreateDefault();
        p.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = 1 });
        p.Tunnels.Add(new Tunnel { Id = 101, Type = TunnelType.Ssh, Address = "ssh.example", Port = 22 });
        p.Rules.Insert(0, new Rule { Name = "Games", Applications = "game.exe", Action = RuleAction.ViaTunnel(101) });
        p.Rules.Single(r => r.IsDefault).Action = RuleAction.ViaTunnel(100);
        p.Options.Udp = UdpMode.Tunnel;
        await using var engine = new RoutingEngine(p);

        // SSH cannot carry UDP, so the game's UDP is blocked rather than sent elsewhere.
        Assert.Equal(UdpVerdict.Block, engine.ClassifyUdp(new InboundRequest("x.example", 443, 1234, @"C:\g\game.exe")).Verdict);
    }
}
