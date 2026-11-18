using System.Net;
using Sluice.Core.Profiles;
using Sluice.Core.Rules;

namespace Sluice.Tests;

/// <summary>Application rules against Windows paths (<c>C:\…\app.exe</c>); Linux paths are tested in Sluice.Tests.Linux.</summary>
public class WindowsRuleEngineTests
{
    private static RuleEngine Engine(params Rule[] rules) =>
        new(rules.Append(new Rule { Name = "Default", Action = RuleAction.ViaTunnel(100) }), computerName: "MYPC");

    private static string Match(RuleEngine e, string? app, string? host, int port, string? ip = null) =>
        e.Match(new ConnectionInfo(app, host, ip is null ? null : IPAddress.Parse(ip), port)).Rule.Name;

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

    [Fact]
    public void AllConditionsMustMatch()
    {
        var e = Engine(new Rule { Name = "Combo", Applications = "app.exe", Targets = "*.corp", Ports = "443" });
        Assert.Equal("Combo", Match(e, @"C:\app.exe", "git.corp", 443));
        Assert.Equal("Default", Match(e, @"C:\app.exe", "git.corp", 80));
        Assert.Equal("Default", Match(e, @"C:\other.exe", "git.corp", 443));
        Assert.Equal("Default", Match(e, @"C:\app.exe", "git.com", 443));
    }
}
