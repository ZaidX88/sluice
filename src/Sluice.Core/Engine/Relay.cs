using System.Net.Sockets;

namespace Sluice.Core.Engine;

/// <summary>Bidirectional byte pump between the application socket and the upstream socket.</summary>
public static class Relay
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Copies both directions until both reach EOF (half-close is forwarded),
    /// or until either side errors / <paramref name="ct"/> fires, which tears down both.
    /// </summary>
    public static async Task RunAsync(Socket app, Socket upstream, Action<long> onSent, Action<long> onReceived, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = PumpAsync(app, upstream, onSent, cts.Token);
        var down = PumpAsync(upstream, app, onReceived, cts.Token);

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
}
