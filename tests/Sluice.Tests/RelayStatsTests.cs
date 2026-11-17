using System.Net;
using System.Net.Sockets;
using Sluice.Core.Engine;

namespace Sluice.Tests;

/// <summary>Relay read counters: what they count, and that the relay feeds them.</summary>
public class RelayStatsTests
{
    [Fact]
    public void Counts_reads_bytes_and_small_reads_per_direction()
    {
        var s = new RelayStats();
        s.Down(65536); s.Down(100); s.Up(5000);
        Assert.Equal(new RelayStats.Summary(2, 65636, 1), s.TotalDown);
        Assert.Equal(new RelayStats.Summary(1, 5000, 0), s.TotalUp);

        var line = s.TakeInterval();
        Assert.NotNull(line);
        Assert.Contains("down 2 reads", line);
        Assert.Contains("1 under 4 KB", line);
        Assert.Contains("Sluice CPU", line);
        Assert.Null(s.TakeInterval());              // the interval restarted; totals stay
        Assert.Equal(2, s.TotalDown.Reads);
    }

    [Fact]
    public async Task Relay_counts_what_it_moves()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var ep = (IPEndPoint)listener.LocalEndpoint;

        // app <-> [appSide | relay | upSide] <-> upstream
        using var app = new TcpClient(); await app.ConnectAsync(ep);
        using var appSide = await listener.AcceptSocketAsync();
        using var upstream = new TcpClient(); await upstream.ConnectAsync(ep);
        using var upSide = await listener.AcceptSocketAsync();

        var stats = new RelayStats();
        var relay = Relay.RunAsync(appSide, upSide, _ => { }, _ => { }, CancellationToken.None, stats: stats);

        var payload = new byte[200_000];
        var appStream = app.GetStream();
        await upstream.GetStream().WriteAsync(payload);
        upstream.Client.Shutdown(SocketShutdown.Send);
        await appStream.WriteAsync(new byte[10]);
        app.Client.Shutdown(SocketShutdown.Send);

        var got = 0;
        var buf = new byte[65536];
        int n;
        while ((n = await appStream.ReadAsync(buf)) > 0) got += n;
        await relay.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(payload.Length, got);
        Assert.Equal(payload.Length, stats.TotalDown.Bytes);
        Assert.Equal(10, stats.TotalUp.Bytes);
        Assert.True(stats.TotalDown.Reads >= 1);
    }
}
