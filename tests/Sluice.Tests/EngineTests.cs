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

public class EngineTests
{
    private static readonly IPEndPoint AnyLoopback = new(IPAddress.Loopback, 0);

    private static async Task<TcpClient> ViaLocalSocks(Socks5Inbound inbound, string host, int port)
    {
        var c = new TcpClient();
        await c.ConnectAsync(inbound.LocalEndPoint);
        await ProxyHandshake.Socks5Async(c.GetStream(), host, port, null, null, CancellationToken.None);
        return c;
    }

    private static async Task Echo(TcpClient c, string text = "ping")
    {
        var s = c.GetStream();
        var msg = Encoding.ASCII.GetBytes(text);
        await s.WriteAsync(msg);
        var back = new byte[msg.Length];
        await s.ReadExactlyAsync(back).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(text, Encoding.ASCII.GetString(back));
    }

    [Fact]
    public async Task LocalSocks_Direct_RelaysAndCounts()
    {
        await using var echo = new EchoServer();
        var engine = new RoutingEngine(Profile.CreateDefault());
        await using var inbound = new Socks5Inbound(engine, AnyLoopback);
        inbound.Start();

        var closed = new TaskCompletionSource<ConnectionEntry>();
        engine.Tracker.Closed += e => closed.TrySetResult(e);

        using (var c = await ViaLocalSocks(inbound, "127.0.0.1", echo.Port))
        {
            Assert.Single(engine.Tracker.Active);
            await Echo(c, "hello-relay");
        }

        var entry = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectionState.Closed, entry.State);
        Assert.Equal("Localhost", entry.RuleName);
        Assert.Equal("Direct", entry.Via);
        Assert.Equal(11, entry.BytesSent);
        Assert.Equal(11, entry.BytesReceived);
        Assert.Empty(engine.Tracker.Active);
    }

    [Fact]
    public async Task LocalSocks_RuleSendsThroughUpstreamProxy_WithHostname()
    {
        await using var echo = new EchoServer();
        await using var upstream = new Socks5TestServer();
        var profile = Profile.CreateDefault();
        profile.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = upstream.Port });
        profile.Rules.Insert(0, new Rule { Name = "Test", Targets = "*.test", Action = RuleAction.ViaTunnel(100) });

        var engine = new RoutingEngine(profile);
        await using var inbound = new Socks5Inbound(engine, AnyLoopback);
        inbound.Start();

        using var c = await ViaLocalSocks(inbound, "echo.test", echo.Port);
        await Echo(c);
        // Hostname reached the upstream proxy unresolved.
        Assert.Equal($"echo.test:{echo.Port}", upstream.LastTarget);
        Assert.Equal("Test", Assert.Single(engine.Tracker.Active).RuleName);
    }

    [Fact]
    public async Task BlockRule_ReturnsSocksNotAllowed()
    {
        var profile = Profile.CreateDefault();
        profile.Rules.Insert(0, new Rule { Name = "NoAds", Targets = "*.ads.test", Action = RuleAction.Block });
        var engine = new RoutingEngine(profile);
        await using var inbound = new Socks5Inbound(engine, AnyLoopback);
        inbound.Start();

        var ex = await Assert.ThrowsAsync<ProxyException>(() => ViaLocalSocks(inbound, "x.ads.test", 80));
        Assert.Contains("not allowed by ruleset", ex.Message);
    }

    [Fact]
    public async Task UpstreamRefused_ReturnsSocksError()
    {
        int deadPort;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); deadPort = ((IPEndPoint)l.LocalEndpoint).Port; }
        var engine = new RoutingEngine(Profile.CreateDefault());
        await using var inbound = new Socks5Inbound(engine, AnyLoopback);
        inbound.Start();

        var ex = await Assert.ThrowsAsync<ProxyException>(() => ViaLocalSocks(inbound, "127.0.0.1", deadPort));
        Assert.Contains("connection refused", ex.Message);
    }

    [Fact]
    public async Task LoopGuard_RefusesConnectionToOwnListener()
    {
        var engine = new RoutingEngine(Profile.CreateDefault());
        await using var inbound = new Socks5Inbound(engine, AnyLoopback);
        inbound.Start();

        await Assert.ThrowsAsync<ProxyException>(() => ViaLocalSocks(inbound, "127.0.0.1", inbound.LocalEndPoint.Port));
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
    public async Task Disconnect_TearsDownLiveConnection()
    {
        await using var echo = new EchoServer();
        var engine = new RoutingEngine(Profile.CreateDefault());
        await using var inbound = new Socks5Inbound(engine, AnyLoopback);
        inbound.Start();

        using var c = await ViaLocalSocks(inbound, "127.0.0.1", echo.Port);
        await Echo(c);
        Assert.Single(engine.Tracker.Active).Disconnect();

        var buf = new byte[1];
        var n = await c.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task Transparent_UsesRedirectTable()
    {
        await using var echo = new EchoServer();
        var table = new FakeRedirectTable();
        var engine = new RoutingEngine(Profile.CreateDefault());
        engine.Tracker.Level = LogLevel.Debug;
        var log = new ConcurrentQueue<string>();
        engine.Tracker.Log += (_, m) => log.Enqueue(m);
        await using var inbound = new TransparentInbound(engine, AnyLoopback, table);
        inbound.Start();

        // Pretend the app wanted "127.0.0.1:<echo>" and WinDivert redirected it to us.
        using var c = new TcpClient();
        c.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        table.Map[IRedirectTable.Normalize((IPEndPoint)c.Client.LocalEndPoint!)] =new OriginalDestination(new IPEndPoint(IPAddress.Loopback, echo.Port), Environment.ProcessId);
        await c.ConnectAsync(inbound.LocalEndPoint);

        try { await Echo(c); }
        catch (Exception ex) { Assert.Fail(ex.Message + "\nLOG:\n" + string.Join("\n", log)); }
        var entry = Assert.Single(engine.Tracker.Active);
        Assert.Equal($"127.0.0.1:{echo.Port}", entry.Target);
        Assert.Equal(Environment.ProcessId, entry.ProcessId);
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

    [Fact]
    public void ProcessLookup_ResolvesOwnPath() =>
        Assert.Equal(Environment.ProcessPath, ProcessLookup.GetProcessPath(Environment.ProcessId), ignoreCase: true);

    private sealed class FakeRedirectTable : IRedirectTable
    {
        public ConcurrentDictionary<IPEndPoint, OriginalDestination> Map { get; } = new();
        public bool TryGetOriginal(IPEndPoint clientEndPoint, out OriginalDestination original) =>
            Map.TryGetValue(clientEndPoint, out original!);
    }
}
