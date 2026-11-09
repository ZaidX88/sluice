using System.Net;
using System.Net.Sockets;
using System.Text;
using Sluice.Core.Checker;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Tests;

/// <summary>Minimal web server: answers any request with a 200.</summary>
public sealed class TinyHttpServer : LoopbackServer
{
    protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[1024];
        var got = 0;
        while (!Encoding.ASCII.GetString(buf, 0, got).Contains("\r\n\r\n"))
        {
            var n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) return;
            got += n;
        }
        await s.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nhi"u8.ToArray(), ct);
    }
}

public class CheckerTests
{
    private static ProxyServer Socks(int port, string? user = null, string? pass = null) =>
        new() { Type = ProxyType.Socks5, Address = "127.0.0.1", Port = port, Username = user, Password = pass };

    private static int DeadPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task WorkingProxy_PassesAllThree_AndLoadsPage()
    {
        await using var web = new TinyHttpServer();
        await using var socks = new Socks5TestServer("u", "p");
        var steps = new List<CheckStep>();

        var report = await ProxyChecker.CheckAsync(Socks(socks.Port, "u", "p"),
            new CheckSettings { TargetHost = "127.0.0.1", TargetPort = web.Port, LatencySamples = 4 },
            new SyncProgress(steps.Add));

        Assert.True(report.Passed);
        Assert.Equal([1, 2, 3], report.Steps.Select(s => s.Number));
        Assert.All(report.Steps, s => Assert.Equal(CheckStatus.Passed, s.Status));
        Assert.Contains("HTTP/1.1 200 OK", report.Steps[1].Detail);
        Assert.Contains("authentication was successful", report.Steps[1].Detail);
        Assert.Contains("over 4 connections", report.Steps[2].Detail);
        Assert.NotNull(report.Latency);
        Assert.Equal(3, steps.Count);                    // progress reported each step
        Assert.Equal(1 + 1 + 4, socks.Accepted);         // test 1 connects, test 2 tunnels once, test 3 four times
    }

    [Fact]
    public async Task DeadProxy_FailsTest1_SkipsTheRest()
    {
        var report = await ProxyChecker.CheckAsync(Socks(DeadPort()));
        Assert.False(report.Passed);
        Assert.Equal(CheckStatus.Failed, report.Steps[0].Status);
        Assert.Contains("actively refused", report.Steps[0].Detail);
        Assert.All(report.Steps.Skip(1), s => Assert.Equal(CheckStatus.Skipped, s.Status));
    }

    [Fact]
    public async Task WrongCredentials_FailTest2_WithAuthMessage()
    {
        await using var socks = new Socks5TestServer("u", "p");
        var report = await ProxyChecker.CheckAsync(Socks(socks.Port, "u", "WRONG"), new CheckSettings { LoadWebPage = false });
        Assert.Equal(CheckStatus.Passed, report.Steps[0].Status);
        Assert.Equal(CheckStatus.Failed, report.Steps[1].Status);
        Assert.Contains("Authentication on the proxy server failed", report.Steps[1].Detail);
        Assert.Equal(CheckStatus.Skipped, report.Steps[2].Status);
    }

    [Fact]
    public async Task NonWebTarget_FailsPageLoad_ButPassesWithoutIt()
    {
        await using var echo = new EchoServer();     // echoes our GET back: not an HTTP reply
        await using var socks = new Socks5TestServer();
        var target = new CheckSettings { TargetHost = "127.0.0.1", TargetPort = echo.Port };

        var withPage = await ProxyChecker.CheckAsync(Socks(socks.Port), target);
        Assert.Equal(CheckStatus.Failed, withPage.Steps[1].Status);
        Assert.Contains("does not look like a usual Web Server reply", withPage.Steps[1].Detail);

        var noPage = await ProxyChecker.CheckAsync(Socks(socks.Port), target with { LoadWebPage = false });
        Assert.True(noPage.Passed);
    }

    [Fact]
    public async Task HungProxy_TimesOut()
    {
        // Accepts TCP but never answers the SOCKS greeting.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        var report = await ProxyChecker.CheckAsync(Socks(port), new CheckSettings { Timeout = TimeSpan.FromMilliseconds(500) });
        Assert.Equal(CheckStatus.Passed, report.Steps[0].Status);
        Assert.Equal(CheckStatus.Failed, report.Steps[1].Status);
        Assert.Contains("Timed out", report.Steps[1].Detail);
    }

    private sealed class SyncProgress(Action<CheckStep> a) : IProgress<CheckStep>
    {
        public void Report(CheckStep value) => a(value);
    }
}

public class StatisticsAndLogTests
{
    [Fact]
    public async Task Statistics_GroupsClosedAndLiveConnections()
    {
        await using var echo = new EchoServer();
        var profile = Profile.CreateDefault();
        profile.Rules.Insert(0, new Rule { Name = "NoAds", Targets = "*.ads.test", Action = RuleAction.Block });
        var engine = new ProxyEngine(profile);
        var stats = new Statistics(engine.Tracker);
        await using var inbound = new Socks5Inbound(engine, new IPEndPoint(IPAddress.Loopback, 0));
        inbound.Start();

        async Task<TcpClient> Open(string host, int port)
        {
            var c = new TcpClient();
            await c.ConnectAsync(inbound.LocalEndPoint);
            await ProxyHandshake.Socks5Async(c.GetStream(), host, port, null, null, CancellationToken.None);
            return c;
        }

        var closed = new TaskCompletionSource();
        var n = 0;
        engine.Tracker.Closed += _ => { if (Interlocked.Increment(ref n) == 2) closed.TrySetResult(); };

        using (var c1 = await Open("127.0.0.1", echo.Port))
        {
            await c1.GetStream().WriteAsync("12345"u8.ToArray());
            await c1.GetStream().ReadExactlyAsync(new byte[5]);
        }
        await Assert.ThrowsAsync<ProxyException>(() => Open("x.ads.test", 80));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var live = await Open("127.0.0.1", echo.Port);   // still open during the snapshot
        await live.GetStream().WriteAsync("ab"u8.ToArray());
        await live.GetStream().ReadExactlyAsync(new byte[2]);

        var byTarget = stats.Snapshot(StatisticsGrouping.Target);
        var local = byTarget.Single(r => r.Key == "127.0.0.1");
        Assert.Equal((2, 1, 0, 7L, 7L), (local.Connections, local.Active, local.Failed, local.BytesSent, local.BytesReceived));
        var ads = byTarget.Single(r => r.Key == "x.ads.test");
        Assert.Equal((1, 1), (ads.Connections, ads.Failed));

        var byRule = stats.Snapshot(StatisticsGrouping.Rule);
        Assert.Contains(byRule, r => r.Key == "NoAds");
        var byApp = stats.Snapshot(StatisticsGrouping.Application);
        Assert.Equal(3, byApp.Sum(r => r.Connections));

        stats.Reset();
        Assert.Equal(1, stats.Snapshot(StatisticsGrouping.Target).Single().Connections); // only the live one remains
    }

    [Fact]
    public async Task DisconnectAll_DuringConnect_IsClosedNotFailed()
    {
        // Upstream proxy that accepts but never answers: the connection hangs in the handshake.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var profile = Profile.CreateDefault();
        profile.Proxies.Add(new ProxyServer { Id = 1, Type = ProxyType.Socks5, Address = "127.0.0.1", Port = ((IPEndPoint)silent.LocalEndpoint).Port });
        profile.Rules.Single(r => r.IsDefault).Action = RuleAction.ViaProxy(1);
        var engine = new ProxyEngine(profile);
        var errors = new List<string>();
        engine.Tracker.Log += (l, m) => { if (l == LogLevel.Error) lock (errors) errors.Add(m); };
        var closed = new TaskCompletionSource<ConnectionEntry>();
        engine.Tracker.Closed += e => closed.TrySetResult(e);
        await using var inbound = new Socks5Inbound(engine, new IPEndPoint(IPAddress.Loopback, 0));
        inbound.Start();

        using var c = new TcpClient();
        await c.ConnectAsync(inbound.LocalEndPoint);
        var handshake = ProxyHandshake.Socks5Async(c.GetStream(), "example.com", 80, null, null, CancellationToken.None);
        while (engine.Tracker.Active.Count == 0) await Task.Delay(10);

        engine.Tracker.DisconnectAll();
        Assert.True(await engine.Tracker.WaitIdleAsync(TimeSpan.FromSeconds(5)));
        var entry = await closed.Task;
        Assert.Equal(ConnectionState.Closed, entry.State);
        Assert.Equal("disconnected", entry.Error);
        Assert.Empty(errors);
        await Assert.ThrowsAnyAsync<Exception>(() => handshake);   // the app's side was closed
    }

    [Fact]
    public void FileLog_WritesDailyFile_RespectingItsLevel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sluice-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tracker = new ConnectionTracker { Level = LogLevel.Normal };
            using (var log = new FileLog(tracker, dir, LogLevel.Verbose))
            {
                Assert.Equal(LogLevel.Verbose, tracker.Level);   // raised so verbose lines are emitted
                tracker.Write(LogLevel.Error, "boom");
                tracker.Write(LogLevel.Verbose, "details");
                tracker.Write(LogLevel.Debug, "noise");
                Assert.EndsWith($"Sluice-{DateTime.Now:yyyy-MM-dd}.log", log.CurrentFile);
            }
            var lines = File.ReadAllLines(Directory.GetFiles(dir).Single());
            Assert.Equal(2, lines.Length);
            Assert.Contains("ERROR", lines[0]);
            Assert.EndsWith("boom", lines[0]);
            Assert.Contains("VERBOSE", lines[1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
