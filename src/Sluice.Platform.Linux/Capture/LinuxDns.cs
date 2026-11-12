using System.Net;
using System.Net.Sockets;
using Sluice.Core.Dns;
using Sluice.Core.Interception;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// The DNS side of Linux capture. Every UDP/53 query from other processes is redirected (nftables) to this
/// server: names that go through tunnels get a fake address at once; the rest are forwarded to the real
/// upstream servers and the answer relayed back. Its own sockets carry Sluice's group, so they are not redirected.
/// </summary>
internal sealed class LinuxDnsServer : IAsyncDisposable
{
    // nftables' redirect sends a local query to 127.0.0.1 / ::1. Replies must leave from exactly that address
    // (and port) for conntrack to translate them back to the server the app asked; a wildcard socket would
    // answer from the interface address and the app's resolver would drop the reply.
    private readonly Socket _v4, _v6;
    private readonly DnsRedirector _redirector;
    private readonly Func<IReadOnlyList<IPAddress>> _upstreams;
    private readonly Action<string> _error;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private long _answered, _forwarded;

    public int Port { get; }
    public long Answered => Interlocked.Read(ref _answered);
    public long Forwarded => Interlocked.Read(ref _forwarded);

    public LinuxDnsServer(DnsRedirector redirector, Func<IReadOnlyList<IPAddress>> upstreams, Action<string> error)
    {
        _redirector = redirector;
        _upstreams = upstreams;
        _error = error;
        (_v4, _v6, Port) = BindLoopbackPair();
    }

    /// <summary>127.0.0.1:P and [::1]:P on the same port P (nftables redirects both families to one port).</summary>
    private static (Socket V4, Socket V6, int Port) BindLoopbackPair()
    {
        for (var attempt = 0; ; attempt++)
        {
            var v4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            v4.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)v4.LocalEndPoint!).Port;
            var v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = false };
            try
            {
                v6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                return (v4, v6, port);
            }
            catch (SocketException ex) when (attempt < 20 || ex.SocketErrorCode == SocketError.AddressFamilyNotSupported)
            {
                v6.Dispose();
                if (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported) return (v4, v6, port);   // no IPv6 on this host
                v4.Dispose();
            }
        }
    }

    public void Start()
    {
        _loop = Task.WhenAll(Task.Run(() => LoopAsync(_v4)), _v6.IsBound ? Task.Run(() => LoopAsync(_v6)) : Task.CompletedTask);
    }

    private async Task LoopAsync(Socket socket)
    {
        var buf = new byte[4096];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await socket.ReceiveFromAsync(buf, SocketFlags.None, any, _cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }   // ICMP errors from earlier replies
            var query = buf.AsSpan(0, r.ReceivedBytes).ToArray();
            var client = r.RemoteEndPoint;
            _ = Task.Run(() => HandleAsync(socket, query, client));
        }
    }

    private async Task HandleAsync(Socket socket, byte[] query, EndPoint client)
    {
        try
        {
            var reply = _redirector.TryAnswerQuery(query);
            if (reply is not null) Interlocked.Increment(ref _answered);
            else
            {
                reply = await ForwardAsync(query, _cts.Token);
                if (reply is null) return;   // the app retries / times out as it would without Sluice
                Interlocked.Increment(ref _forwarded);
            }
            await socket.SendToAsync(reply, SocketFlags.None, client, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _error($"DNS query handling failed: {ex.Message}");
        }
    }

    /// <summary>Sends the query as-is to each upstream in turn; returns the first answer with the same ID.</summary>
    private async Task<byte[]?> ForwardAsync(byte[] query, CancellationToken ct)
    {
        if (query.Length < 12) return null;
        foreach (var server in _upstreams())
        {
            using var s = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var ep = new IPEndPoint(server, 53);
                await s.SendToAsync(query, SocketFlags.None, ep, timeout.Token);
                var buf = new byte[4096];
                while (true)
                {
                    var r = await s.ReceiveFromAsync(buf, SocketFlags.None, ep, timeout.Token);
                    if (r.ReceivedBytes >= 12 && buf[0] == query[0] && buf[1] == query[1]) return buf.AsSpan(0, r.ReceivedBytes).ToArray();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* next server */ }
            catch (SocketException) { /* next server */ }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _v4.Dispose();
        _v6.Dispose();
        if (_loop is not null) await _loop;
    }

    // ---------------------------------------------------------------- upstream servers

    /// <summary>
    /// The real upstream servers. /etc/resolv.conf usually names a local stub (systemd-resolved's 127.0.0.53,
    /// dnsmasq…) whose own upstream queries are captured too, so asking it would loop back to Sluice. systemd-resolved
    /// writes the actual upstreams to /run/systemd/resolve/resolv.conf; loopback servers are skipped.
    /// </summary>
    public static IReadOnlyList<IPAddress> UpstreamServers(string root = "/")
    {
        foreach (var file in new[] { "run/systemd/resolve/resolv.conf", "etc/resolv.conf" })
        {
            var servers = ParseResolvConf(Path.Combine(root, file));
            if (servers.Count > 0) return servers;
        }
        return [];
    }

    public static List<IPAddress> ParseResolvConf(string path)
    {
        var list = new List<IPAddress>();
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return list; }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("nameserver", StringComparison.Ordinal)) continue;
            var value = line["nameserver".Length..].Trim();
            var pct = value.IndexOf('%');                              // fe80::1%eth0
            if (pct >= 0) value = value[..pct];
            if (IPAddress.TryParse(value, out var ip) && !IPAddress.IsLoopback(ip) && !ip.IsIPv6LinkLocal && !FakeIpPool.IsFake(ip) && !list.Contains(ip))
                list.Add(ip);
        }
        return list.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
    }
}
