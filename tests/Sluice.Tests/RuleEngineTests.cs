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
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", "Browsers")]
    [InlineData(@"C:\x\FIREFOX.EXE", "Browsers")]
    [InlineData(@"C:\tools\chrome.exe", "Browsers")]
    [InlineData(@"C:\Program Files\Some App\app.exe", "QuotedPath")]
    [InlineData(@"D:\Program Files\Some App\app.exe", "Default")]
    [InlineData(@"C:\x\curl.exe", "Default")]
    [InlineData(null, "Default")]
    public void Applications(string? path, string expected)
    {
        var e = Engine(
            new Rule { Name = "Browsers", Applications = "fire*.exe; chrome.exe" },
            new Rule { Name = "QuotedPath", Applications = "\"C:\\Program Files\\Some App\\app.exe\"" });
        Assert.Equal(expected, Match(e, path, "a.com", 80));
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

    [Fact]
    public void AllConditionsMustMatch()
    {
        var e = Engine(new Rule { Name = "Combo", Applications = "app.exe", Targets = "*.corp", Ports = "443" });
        Assert.Equal("Combo", Match(e, @"C:\app.exe", "git.corp", 443));
        Assert.Equal("Default", Match(e, @"C:\app.exe", "git.corp", 80));
        Assert.Equal("Default", Match(e, @"C:\other.exe", "git.corp", 443));
        Assert.Equal("Default", Match(e, @"C:\app.exe", "git.com", 443));
    }

    [Fact]
    public void MissingDefaultFallsBackToDirect()
    {
        var e = new RuleEngine([new Rule { Name = "X", Ports = "1" }]);
        var m = e.Match(new ConnectionInfo(null, "a", null, 2));
        Assert.Equal(RuleActionType.Direct, m.Action.Type);
    }
}
