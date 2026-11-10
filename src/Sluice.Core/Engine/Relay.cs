using System.Buffers;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Sluice.Core.Engine;

/// <summary>Bidirectional byte pump between the application socket and the upstream socket.</summary>
public static class Relay
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Copies both directions until both reach EOF (half-close is forwarded),
    /// or until either side errors / <paramref name="ct"/> fires, which tears down both.
    /// </summary>
    /// <param name="downstreamBuffer">
    /// When &gt; 0, data from upstream is read as soon as it arrives and held (up to this many bytes) until the
    /// application takes it. Needed for upstreams without per-connection flow control (SSH.NET opens channels
    /// with an unlimited window and writes them from one thread): one slow reader would otherwise stall every
    /// connection sharing the SSH session.
    /// </param>
    public static async Task RunAsync(Socket app, Socket upstream, Action<long> onSent, Action<long> onReceived, CancellationToken ct,
        int downstreamBuffer = 0)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = PumpAsync(app, upstream, onSent, cts.Token);
        var down = downstreamBuffer > 0
            ? ElasticPumpAsync(upstream, app, onReceived, downstreamBuffer, cts.Token)
            : PumpAsync(upstream, app, onReceived, cts.Token);

        var first = await Task.WhenAny(up, down);
        if (!first.Result) cts.Cancel();
        await Task.WhenAll(up, down);
    }

    /// <returns>true on clean EOF, false on error or cancellation.</returns>
    private static async Task<bool> PumpAsync(Socket from, Socket to, Action<long> count, CancellationToken ct)
    {
        var buf = new byte[BufferSize];
        try
        {
            while (true)
            {
                var n = await from.ReceiveAsync(buf, SocketFlags.None, ct);
                if (n == 0) break;
                var off = 0;
                while (off < n) off += await to.SendAsync(buf.AsMemory(off, n - off), SocketFlags.None, ct);
                count(n);
            }
            to.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Reader drains <paramref name="from"/> into a bounded queue; writer feeds <paramref name="to"/> at its own pace.</summary>
    private static async Task<bool> ElasticPumpAsync(Socket from, Socket to, Action<long> count, int capacity, CancellationToken ct)
    {
        var queue = Channel.CreateUnbounded<(byte[] Buffer, int Length)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var queued = 0L;
        var space = new SemaphoreSlim(0);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task<bool> ReadAsync()
        {
            try
            {
                while (true)
                {
                    // Over capacity: wait for the writer (only now does a slow app slow the upstream).
                    while (Interlocked.Read(ref queued) >= capacity) await space.WaitAsync(stop.Token);
                    var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
                    int n;
                    try { n = await from.ReceiveAsync(buf.AsMemory(0, BufferSize), SocketFlags.None, stop.Token); }
                    catch { ArrayPool<byte>.Shared.Return(buf); throw; }
                    if (n == 0)
                    {
                        ArrayPool<byte>.Shared.Return(buf);
                        queue.Writer.TryComplete();
                        return true;
                    }
                    Interlocked.Add(ref queued, n);
                    queue.Writer.TryWrite((buf, n));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                queue.Writer.TryComplete(ex);
                return false;
            }
        }

        async Task<bool> WriteAsync()
        {
            try
            {
                await foreach (var (buf, n) in queue.Reader.ReadAllAsync(stop.Token))
                {
                    try
                    {
                        var off = 0;
                        while (off < n) off += await to.SendAsync(buf.AsMemory(off, n - off), SocketFlags.None, stop.Token);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buf);
                    }
                    Interlocked.Add(ref queued, -n);
                    if (space.CurrentCount == 0) space.Release();
                    count(n);
                }
                to.Shutdown(SocketShutdown.Send);
                return true;
            }
            catch (Exception)
            {
                return false;   // app gone, cancelled, or the reader failed
            }
        }

        var reader = ReadAsync();
        var writer = WriteAsync();
        var done = await Task.WhenAny(reader, writer);
        if (!done.Result) stop.Cancel();   // either side failed: stop both
        var ok = await reader & await writer;
        // Return any buffers still queued after a failure.
        while (queue.Reader.TryRead(out var left)) ArrayPool<byte>.Shared.Return(left.Buffer);
        return ok;
    }
}
