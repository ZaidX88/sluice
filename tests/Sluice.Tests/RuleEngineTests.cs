using System.Net;
using Sluice.Core.Profiles;
using Sluice.Core.Rules;

namespace Sluice.Tests;

public class RuleEngineTests
{
    private static RuleEngine Engine(params Rule[] rules) =>
        new(rules.Append(new Rule { Name = "Default", Action = RuleAction.ViaTunnel(100) }), computerName: "MYPC");

    private static string Match(RuleEngine e, string? app, string? host, int port, string? ip = null) =>
        e.Match(new ConnectionInfo(app, host, ip is null ? null : IPAddress.Parse(ip), port)).Rule.Name;

    [Fact]
    public void FirstMatchWins_DefaultLast()
    {
        // Default listed first must still be evaluated last.
        var e = new RuleEngine([
            new Rule { Name = "Default", Action = RuleAction.Direct },
            new Rule { Name = "Web", Ports = "80;443", Action = RuleAction.ViaTunnel(1) },
            new Rule { Name = "AlsoWeb", Ports = "443", Action = RuleAction.Block },
        ]);
        Assert.Equal("Web", e.Match(new ConnectionInfo(null, "a.com", null, 443)).Rule.Name);
        Assert.Equal("Default", e.Match(new ConnectionInfo(null, "a.com", null, 22)).Rule.Name);
    }

    [Fact]
    public void DisabledRulesAreSkipped()
    {
        var e = Engine(new Rule { Name = "Off", Enabled = false, Ports = "80" });
        Assert.Equal("Default", Match(e, null, "a.com", 80));
    }


    [Theory]
    [InlineData("www.example.com", null, "Example")]
    [InlineData("EXAMPLE.com", null, "Default")]          // *.example.com needs a subdomain
    [InlineData("a.b.example.com.", null, "Example")]     // trailing dot ignored
    [InlineData(null, "192.168.1.77", "Lan")]
    [InlineData("192.168.1.5", null, "Lan")]              // IP given as host string
    [InlineData(null, "10.3.4.5", "Range")]
    [InlineData(null, "10.6.0.0", "Default")]
    [InlineData(null, "172.16.9.9", "Cidr")]
    [InlineData(null, "172.32.0.1", "Default")]
    [InlineData("intranet", null, "Simple")]
    [InlineData("mypc", null, "Me")]
    [InlineData(null, "::1", "V6")]
    [InlineData(null, "::ffff:192.168.1.9", "Lan")]       // v4-mapped v6
    public void Targets(string? host, string? ip, string expected)
    {
        var e = Engine(
            new Rule { Name = "Example", Targets = "*.example.com" },
            new Rule { Name = "Lan", Targets = "192.168.1.*" },
            new Rule { Name = "Range", Targets = "10.1.0.0-10.5.255.255" },
            new Rule { Name = "Cidr", Targets = "172.16.0.0/12" },
            new Rule { Name = "Me", Targets = "%ComputerName%" },
            new Rule { Name = "Simple", Targets = "%SimpleHostnames%" },
            new Rule { Name = "V6", Targets = "::1" });
        Assert.Equal(expected, Match(e, null, host, 80, ip));
    }

    [Theory]
    [InlineData(80, "Ports")]
    [InlineData(8500, "Ports")]
    [InlineData(9001, "Default")]
    public void PortRanges(int port, string expected)
    {
        var e = Engine(new Rule { Name = "Ports", Ports = "80; 8000-9000" });
        Assert.Equal(expected, Match(e, null, "a.com", port));
    }

    // #230: a Ports value that doesn't parse used to mean "any port", so a Block rule blocked everything.
    [Theory]
    [InlineData("https")]
    [InlineData("70000")]
    [InlineData("-80")]
    [InlineData("+443")]
    [InlineData("80-")]
    [InlineData("http; https")]
    public void UnparsablePortsMatchNothing(string ports)
    {
        var e = Engine(new Rule { Name = "Bad", Ports = ports, Action = RuleAction.Block });
        foreach (var port in new[] { 1, 80, 443, 8080, 65535 })
            Assert.Equal("Default", Match(e, null, "a.com", port));
        Assert.Contains("the rule matches no connection", Assert.Single(e.Problems));
    }

    [Fact]
    public void PartlyUnparsablePortsKeepWhatParsed()
    {
        var e = Engine(new Rule { Name = "Web", Ports = "80; https; 8000-9000" });
        Assert.Equal("Web", Match(e, null, "a.com", 80));
        Assert.Equal("Web", Match(e, null, "a.com", 8500));
        Assert.Equal("Default", Match(e, null, "a.com", 443));
        var problem = Assert.Single(e.Problems);
        Assert.Contains("Rule \"Web\"", problem);
        Assert.Contains("\"https\"", problem);
        Assert.Contains("ignored", problem);
    }

    [Theory]
    [InlineData("80 443")]
    [InlineData("80, 443")]
    [InlineData("80;443")]
    [InlineData("  80\n443 ")]
    [InlineData("80 440 - 450")]
    public void PortsSplitOnSpacesToo(string ports)
    {
        var e = Engine(new Rule { Name = "Web", Ports = ports });
        Assert.Equal("Web", Match(e, null, "a.com", 80));
        Assert.Equal("Web", Match(e, null, "a.com", 443));
        Assert.Equal("Default", Match(e, null, "a.com", 8080));
        Assert.Empty(e.Problems);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ; ")]
    public void EmptyPortsMatchAny(string? ports)
    {
        Assert.True(RuleEngine.TryParsePorts(ports, out var ranges, out var invalid));
        Assert.Null(ranges);
        Assert.Empty(invalid);
        Assert.Equal("Any", Match(Engine(new Rule { Name = "Any", Ports = ports }), null, "a.com", 12345));
    }

    [Fact]
    public void TryParsePortsReportsTheInvalidParts()
    {
        Assert.False(RuleEngine.TryParsePorts("443 https 9000-8000 99999", out var ranges, out var invalid));
        Assert.Equal([(443, 443), (8000, 9000)], ranges!);
        Assert.Equal(["https", "99999"], invalid);

        Assert.True(RuleEngine.TryParsePorts("0; 65535", out ranges, out invalid));
        Assert.Equal([(0, 0), (65535, 65535)], ranges!);
        Assert.Empty(invalid);
    }

    [Fact]
    public void DisabledRulesReportNoProblems()
    {
        var e = Engine(new Rule { Name = "Off", Enabled = false, Ports = "https" });
        Assert.Empty(e.Problems);
    }

    [Fact]
    public async Task UnparsablePortsAreLoggedOncePerLoad()
    {
        var tracker = new Sluice.Core.Engine.ConnectionTracker();
        var log = new List<(Sluice.Core.Engine.LogLevel, string)>();
        tracker.Log += (l, m) => { lock (log) log.Add((l, m)); };
        var profile = Profile.CreateDefault();
        profile.Rules.Insert(0, new Rule { Name = "Bad", Ports = "https", Action = RuleAction.Block });

        await using var engine = new Sluice.Core.Engine.RoutingEngine(profile, tracker);
        engine.HostResolver = null;   // rebuilds the rules without a new profile: no second report
        engine.Reload(profile);

        lock (log)
        {
            var reports = log.Where(e => e.Item2.Contains("Rule \"Bad\"")).ToList();
            Assert.Equal(2, reports.Count);   // the first load and the reload, nothing per connection
            Assert.All(reports, r => Assert.Equal(Sluice.Core.Engine.LogLevel.Error, r.Item1));
        }
    }


    [Fact]
    public void MissingDefaultFallsBackToDirect()
    {
        var e = new RuleEngine([new Rule { Name = "X", Ports = "1" }]);
        var m = e.Match(new ConnectionInfo(null, "a", null, 2));
        Assert.Equal(RuleActionType.Direct, m.Action.Type);
    }
}
