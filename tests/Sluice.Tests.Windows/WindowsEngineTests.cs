using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Platform;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Tests;

/// <summary>Engine behaviour that depends on Windows: the owner of a connection from the TCP table, and a local tunnel host
/// started with PowerShell. Linux versions belong in Sluice.Tests.Linux.</summary>
public class WindowsEngineTests
{
    private static readonly IPEndPoint AnyLoopback = new(IPAddress.Loopback, 0);

    private static async Task<TcpClient> ViaLocalSocks(Socks5Inbound inbound, string host, int port)
    {
        var c = new TcpClient();
        await c.ConnectAsync(inbound.LocalEndPoint);
        await ProxyHandshake.Socks5Async(c.GetStream(), host, port, null, null, CancellationToken.None);
        return c;
    }

    [Fact]
    public async Task ApplicationRule_MatchesThisTestProcess()
    {
        // The engine identifies the calling process from the TCP table; block our own exe.
        var exe = Path.GetFileName(Environment.ProcessPath)!;
        var profile = Profile.CreateDefault();
        profile.Rules.Insert(0, new Rule { Name = "Me", Applications = exe, Action = RuleAction.Block });
        var engine = new RoutingEngine(profile);
        await using var inbound = new Socks5Inbound(engine, AnyLoopback);
        inbound.Start();

        var closed = new TaskCompletionSource<ConnectionEntry>();
        engine.Tracker.Closed += e => closed.TrySetResult(e);
        await Assert.ThrowsAsync<ProxyException>(() => ViaLocalSocks(inbound, "anything.example", 80));

        var entry = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Environment.ProcessId, entry.ProcessId);
        Assert.Equal(ConnectionState.Blocked, entry.State);
        Assert.Equal("Me", entry.RuleName);
    }

    [Fact]
    public async Task LoopPrevention_LocalProxyHostGoesDirect()
    {
        // Another process listens on a port; the profile routes everything through a proxy on that port.
        using var host = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$l.Start();" +
                        "[Console]::Out.WriteLine($l.LocalEndpoint.Port);[Console]::Out.Flush();Start-Sleep 30\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var port = int.Parse((await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)))!.Trim());
            var profile = Profile.CreateDefault();
            profile.Tunnels.Add(new Tunnel { Id = 1, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = port });
            profile.Rules.Single(r => r.IsDefault).Action = RuleAction.ViaTunnel(1);
            var engine = new RoutingEngine(profile);

            var fromProxyHost = engine.Classify(new InboundRequest("example.com", 443, host.Id, null));
            Assert.Equal("Loop prevention", fromProxyHost.Rule.Name);
            Assert.Equal(RuleActionType.Direct, fromProxyHost.Action.Type);

            var fromOther = engine.Classify(new InboundRequest("example.com", 443, 999999, null));
            Assert.Equal(RuleActionType.Tunnel, fromOther.Action.Type);

            profile.Options.ConnectionLoopDetection = false;
            engine.Reload(profile);
            Assert.Equal(RuleActionType.Tunnel, engine.Classify(new InboundRequest("example.com", 443, host.Id, null)).Action.Type);
        }
        finally
        {
            host.Kill();
        }
    }
}
