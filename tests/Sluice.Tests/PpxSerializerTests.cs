using Sluice.Core.Profiles;

namespace Sluice.Tests;

public class PpxSerializerTests
{
    // Verbatim structure of a profile saved by Proxifier 4.14.
    private const string RealProfile = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <ProxifierProfile version="102" platform="Windows" product_id="0" product_minver="400">
        	<Options>
        		<Resolve>
        			<AutoModeDetection enabled="true" />
        			<ViaProxy enabled="false" />
        			<BlockNonATypes enabled="false" />
        			<ExclusionList OnlyFromListMode="false">%ComputerName%; localhost; *.local</ExclusionList>
        			<DnsUdpMode>0</DnsUdpMode>
        		</Resolve>
        		<Encryption mode="basic" />
        		<ConnectionLoopDetection enabled="true" resolve="true" />
        		<Udp mode="mode_bypass" />
        		<LeakPreventionMode enabled="false" />
        		<ProcessOtherUsers enabled="false" />
        		<ProcessServices enabled="false" />
        		<HandleDirectConnections enabled="false" />
        		<HttpProxiesSupport enabled="false" />
        	</Options>
        	<ProxyList>
        		<Proxy id="100" type="SOCKS5">
        			<Options>48</Options>
        			<Port>6002</Port>
        			<Address>127.0.0.1</Address>
        		</Proxy>
        	</ProxyList>
        	<ChainList />
        	<RuleList>
        		<Rule enabled="true">
        			<Action type="Direct" />
        			<Targets>localhost; 127.0.0.1; %ComputerName%; ::1</Targets>
        			<Name>Localhost</Name>
        		</Rule>
        		<Rule enabled="true">
        			<Action type="Proxy">100</Action>
        			<Name>Default</Name>
        		</Rule>
        	</RuleList>
        </ProxifierProfile>
        """;

    [Fact]
    public void ParsesRealProxifierProfile()
    {
        var p = PpxSerializer.Parse(RealProfile);

        Assert.True(p.Options.Resolve.AutoModeDetection);
        Assert.False(p.Options.Resolve.ViaProxy);
        Assert.Equal("%ComputerName%; localhost; *.local", p.Options.Resolve.ExclusionList);
        Assert.Equal(UdpMode.Bypass, p.Options.Udp);
        Assert.True(p.Options.ConnectionLoopDetection);

        var proxy = Assert.Single(p.Proxies);
        Assert.Equal((100, ProxyType.Socks5, "127.0.0.1", 6002, 48), (proxy.Id, proxy.Type, proxy.Address, proxy.Port, proxy.Options));

        Assert.Equal(["Localhost", "Default"], p.Rules.Select(r => r.Name));
        Assert.Equal(RuleActionType.Direct, p.Rules[0].Action.Type);
        Assert.Equal("localhost; 127.0.0.1; %ComputerName%; ::1", p.Rules[0].Targets);
        Assert.Equal(RuleActionType.Proxy, p.Rules[1].Action.Type);
        Assert.Equal(100, p.Rules[1].Action.TargetId);
    }

    [Fact]
    public void RoundTripsEverything()
    {
        var p = Profile.CreateDefault();
        p.Options.Resolve.ViaProxy = true;
        p.Options.Resolve.TryLocalFirst = true;
        p.Options.Udp = UdpMode.BlockQuic;
        p.Options.LeakPreventionMode = true;
        p.Proxies.Add(new ProxyServer { Id = 100, Type = ProxyType.Https, Address = "proxy.corp", Port = 3128, Username = "u", Password = "p", Label = "Corp" });
        p.Proxies.Add(new ProxyServer { Id = 101, Type = ProxyType.Socks4, Address = "10.0.0.1", Port = 1080 });
        var chain = new ProxyChain { Id = 200, Name = "Fail", Type = ChainType.Redundancy };
        chain.Hops.Add(new ChainHop { ProxyId = 100 });
        chain.Hops.Add(new ChainHop { ProxyId = 101, Enabled = false });
        p.Chains.Add(chain);
        p.Rules.Insert(0, new Rule { Name = "Browsers", Applications = "firefox.exe; \"C:\\a b\\c.exe\"", Ports = "80;443", Action = RuleAction.ViaChain(200) });
        p.Rules.Insert(1, new Rule { Name = "Ads", Targets = "*.ads.test", Enabled = false, Action = RuleAction.Block });

        var back = PpxSerializer.Parse(PpxSerializer.ToXml(p).ToString());

        Assert.True(back.Options.Resolve.ViaProxy);
        Assert.True(back.Options.Resolve.TryLocalFirst);
        Assert.Equal(UdpMode.BlockQuic, back.Options.Udp);
        Assert.True(back.Options.LeakPreventionMode);

        var https = back.FindProxy(100)!;
        Assert.Equal((ProxyType.Https, "proxy.corp", 3128, "u", "p", "Corp"), (https.Type, https.Address, https.Port, https.Username, https.Password, https.Label));
        Assert.Equal(ProxyType.Socks4, back.FindProxy(101)!.Type);

        var c = back.FindChain(200)!;
        Assert.Equal(ChainType.Redundancy, c.Type);
        Assert.Equal("Fail", c.Name);
        Assert.Equal([(100, true), (101, false)], c.Hops.Select(h => (h.ProxyId, h.Enabled)));

        Assert.Equal(["Browsers", "Ads", "Localhost", "Default"], back.Rules.Select(r => r.Name));
        Assert.Equal("firefox.exe; \"C:\\a b\\c.exe\"", back.Rules[0].Applications);
        Assert.Equal(RuleActionType.Chain, back.Rules[0].Action.Type);
        Assert.Equal(200, back.Rules[0].Action.TargetId);
        Assert.False(back.Rules[1].Enabled);
        Assert.Equal(RuleActionType.Block, back.Rules[1].Action.Type);
    }

    [Fact]
    public void AddsDefaultRuleIfMissing()
    {
        var p = PpxSerializer.Parse("<ProxifierProfile version=\"102\"><RuleList/></ProxifierProfile>");
        Assert.Contains(p.Rules, r => r.IsDefault);
    }

    [Fact]
    public void RejectsForeignXml() =>
        Assert.Throws<FormatException>(() => PpxSerializer.Parse("<foo/>"));
}
