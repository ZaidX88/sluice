using System.Net;
using System.Net.Sockets;

namespace Sluice.Core.Proxy;

/// <summary>
/// Connects to the first address that answers (RFC 8305 "Happy Eyeballs", simplified). A name often
/// resolves to several addresses, and some never answer (e.g. a NAT64 IPv6 address on a network with
/// no working IPv6 route). Trying them one by one would wait out each one's full TCP timeout. Here the
/// next address is tried after 250 ms, or at once when an attempt fails, and the first success wins.
/// </summary>
public static class HappyEyeballs
{
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250);

    public static async Task<TcpClient> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct)
    {
        if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
        var ordered = Interleave(addresses);
        if (ordered.Count == 1) return await AttemptAsync(ordered[0], port, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new List<Task<TcpClient>>();
        Exception? last = null;
        var next = 0;
        var startNext = true;
        try
        {
            while (true)
            {
                if (startNext && next < ordered.Count) running.Add(AttemptAsync(ordered[next++], port, cts.Token));
                if (running.Count == 0) throw last ?? new SocketException((int)SocketError.HostUnreachable);

                var timer = next < ordered.Count ? Task.Delay(Stagger, cts.Token) : Task.Delay(Timeout.Infinite, cts.Token);
                var done = await Task.WhenAny(running.Cast<Task>().Append(timer));
                ct.ThrowIfCancellationRequested();
                if (done == timer)
                {
                    startNext = true;   // slow answer: start the next address too
                    continue;
                }
                var attempt = (Task<TcpClient>)done;
                running.Remove(attempt);
                if (attempt.IsCompletedSuccessfully) return attempt.Result;
                last = attempt.Exception?.InnerException ?? last;
                startNext = true;       // failed: try the next one at once
            }
        }
        finally
        {
            cts.Cancel();
            // Late winners are closed; nobody is going to use them.
            foreach (var t in running)
                _ = t.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result.Dispose(); }, TaskScheduler.Default);
        }
    }

    private static async Task<TcpClient> AttemptAsync(IPAddress address, int port, CancellationToken ct)
    {
        var tcp = new TcpClient(address.AddressFamily) { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(address, port, ct);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Alternate families, starting with whichever the resolver listed first; drop duplicates.</summary>
    internal static List<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        var unique = addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Distinct().ToList();
        var first = unique.Where(a => a.AddressFamily == unique[0].AddressFamily).ToList();
        var other = unique.Where(a => a.AddressFamily != unique[0].AddressFamily).ToList();
        var result = new List<IPAddress>(unique.Count);
        for (var i = 0; i < Math.Max(first.Count, other.Count); i++)
        {
            if (i < first.Count) result.Add(first[i]);
            if (i < other.Count) result.Add(other[i]);
        }
        return result;
    }
}
