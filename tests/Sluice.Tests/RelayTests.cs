using System.Net;
using System.Net.Sockets;
using Sluice.Core.Engine;

namespace Sluice.Tests;

public class RelayTests
{
    /// <summary>Two connected loopback sockets.</summary>
    private static async Task<(Socket A, Socket B)> PairAsync()
    {
        using var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        l.Listen(1);
        var a = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accept = l.AcceptAsync();
        await a.ConnectAsync(l.LocalEndPoint!);
        var b = await accept;
        // Small kernel buffers so "the reader is not reading" shows up quickly.
        foreach (var s in new[] { a, b }) { s.ReceiveBufferSize = 16 * 1024; s.SendBufferSize = 16 * 1024; }
        return (a, b);
    }

    /// <summary>
    /// The upstream writes data while the application reads nothing. With the elastic buffer the upstream
    /// is still drained (it never blocks); without it the upstream's writes stall after a few socket buffers.
    /// </summary>
    [Theory]
    [InlineData(8 * 1024 * 1024, 4, true)]
    [InlineData(0, 64, false)]
    public async Task SlowApp_DoesNotBlockUpstream_OnlyWithElasticBuffer(int buffer, int megabytes, bool expectDrained)
    {
        var (appInner, appOuter) = await PairAsync();       // relay side, application side
        var (upInner, upOuter) = await PairAsync();         // relay side, upstream server side
        using var cts = new CancellationTokenSource();
        var relay = Relay.RunAsync(appInner, upInner, _ => { }, _ => { }, cts.Token, buffer);

        var data = new byte[megabytes * 1024 * 1024];
        Random.Shared.NextBytes(data);
        var send = Task.Run(async () =>
        {
            var off = 0;
            while (off < data.Length) off += await upOuter.SendAsync(data.AsMemory(off, Math.Min(65536, data.Length - off)), SocketFlags.None);
        });
        var drained = await Task.WhenAny(send, Task.Delay(3000)) == send;
        Assert.Equal(expectDrained, drained);

        if (expectDrained)
        {
            // Everything arrives intact once the application starts reading.
            var got = new byte[data.Length];
            await appOuter.ReceiveAsync(got.AsMemory(0, 1), SocketFlags.None);
            var n = 1;
            while (n < got.Length) n += await appOuter.ReceiveAsync(got.AsMemory(n), SocketFlags.None);
            Assert.Equal(data, got);
        }

        cts.Cancel();
        await relay;
        foreach (var s in new[] { appInner, appOuter, upInner, upOuter }) s.Dispose();
    }

    [Fact]
    public async Task ElasticRelay_ForwardsBothWaysAndHalfClose()
    {
        var (appInner, appOuter) = await PairAsync();
        var (upInner, upOuter) = await PairAsync();
        long sent = 0, received = 0;
        var relay = Relay.RunAsync(appInner, upInner, n => sent += n, n => received += n, default, 1024 * 1024);

        await appOuter.SendAsync("ping"u8.ToArray(), SocketFlags.None);
        var buf = new byte[4];
        await upOuter.ReceiveAsync(buf, SocketFlags.None);
        Assert.Equal("ping"u8.ToArray(), buf);
        await upOuter.SendAsync("pong!"u8.ToArray(), SocketFlags.None);
        upOuter.Shutdown(SocketShutdown.Send);   // server finished
        var back = new byte[16];
        var n = 0;
        int r;
        while ((r = await appOuter.ReceiveAsync(back.AsMemory(n), SocketFlags.None)) > 0) n += r;   // EOF forwarded
        Assert.Equal("pong!", System.Text.Encoding.ASCII.GetString(back, 0, n));
        appOuter.Shutdown(SocketShutdown.Send);
        await relay.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((4L, 5L), (sent, received));
        foreach (var s in new[] { appInner, appOuter, upInner, upOuter }) s.Dispose();
    }
}
