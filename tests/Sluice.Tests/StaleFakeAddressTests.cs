using System.Diagnostics;
using System.Net;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Tests;

/// <summary>
/// Apps cache Sluice's fake DNS answers beyond Sluice's lifetime (fixed 2026-11-14, docs/HISTORY.md): the pool is
/// kept between runs, and a fake address Sluice doesn't know fails at once instead of being sent into a tunnel.
/// </summary>
public class StaleFakeAddressTests
{
    [Fact]
    public void Pool_SavedAndLoaded_KeepsMappings_AndNewHostsDoNotReuseThem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sluice-fake-{Guid.NewGuid():N}.json");
        try
        {
            var before = new FakeIpPool();
            var a = before.GetOrAdd("a.example");
            var b = before.GetOrAdd("B.Example.");          // normalised
            Assert.True(before.IsDirty);
            before.Save(path);
            Assert.False(before.IsDirty);

            var after = new FakeIpPool();                   // a new Sluice process
            Assert.Equal(2, after.Load(path));
            Assert.False(after.IsDirty);
            Assert.True(after.TryGetHost(a, out var hostA));
            Assert.Equal("a.example", hostA);
            Assert.True(after.TryGetHost(b, out var hostB));
            Assert.Equal("b.example", hostB);
            Assert.Equal(a, after.GetOrAdd("a.example"));   // same answer as before the restart

            var c = after.GetOrAdd("c.example");
            Assert.NotEqual(a, c);
            Assert.NotEqual(b, c);
            Assert.True(after.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pool_Load_IgnoresMissingOrDamagedFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sluice-fake-{Guid.NewGuid():N}.json");
        var pool = new FakeIpPool();
        Assert.Equal(0, pool.Load(path));                   // missing
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Equal(0, pool.Load(path));
            File.WriteAllText(path, """{"version":1,"next":5,"entries":[["ok.example",3],["bad",0],["also-bad",999999999],[1,2],["ok.example",4]]}""");
            Assert.Equal(1, pool.Load(path));               // only the valid, non-duplicate entry
            Assert.True(pool.TryGetHost(IPAddress.Parse("198.18.0.3"), out var host));
            Assert.Equal("ok.example", host);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnknownFakeAddress_FailsAtOnce_AndNeverReachesTheTunnel()
    {
        await using var upstream = new Socks5TestServer();
        var profile = Profile.CreateDefault();
        profile.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = upstream.Port });
        profile.Rules.First(r => r.IsDefault).Action = RuleAction.ViaTunnel(100);
        var engine = new RoutingEngine(profile);
        var log = new List<string>();
        engine.Tracker.Log += (_, m) => { lock (log) log.Add(m); };
        await using var inbound = new Socks5Inbound(engine, new IPEndPoint(IPAddress.Loopback, 0));
        inbound.Start();

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<ProxyException>(async () =>
        {
            using var c = new System.Net.Sockets.TcpClient();
            await c.ConnectAsync(inbound.LocalEndPoint);
            await ProxyHandshake.Socks5Async(c.GetStream(), "198.18.3.4", 443, null, null, CancellationToken.None);
        });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.Null(upstream.LastTarget);                   // the tunnel was never asked to reach 198.18.x.x
        lock (log) Assert.Contains(log, m => m.Contains("198.18.3.4:443 reset") && m.Contains("placeholder"));
    }
}
