using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sluice.Core.Dns;

/// <summary>
/// Sluice's own stub resolver. It talks to the system's DNS servers directly over UDP from one
/// known local port, so the interceptor can tell its queries apart and never fakes them, and
/// it never sees the fake addresses cached by the Windows DNS client.
/// </summary>
public sealed class DnsResolver : IDisposable
{
    private readonly Socket _socket;
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<byte[]>> _pending = new();
    private readonly ConcurrentDictionary<string, (IPAddress[] Addrs, DateTime Until)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<IReadOnlyList<IPAddress>> _servers;
    private int _nextId = Random.Shared.Next(ushort.MaxValue);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan CacheTime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Local UDP port all queries are sent from.</summary>
    public int LocalPort { get; }

    /// <summary>Server port; only tests change it.</summary>
    internal int PortOverride { get; init; } = 53;

    public DnsResolver(Func<IReadOnlyList<IPAddress>>? servers = null)
    {
        _servers = servers ?? SystemDnsServers;
        _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        LocalPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _ = Task.Run(ReceiveLoop);
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        host = host.TrimEnd('.');
        if (_cache.TryGetValue(host, out var c) && c.Until > DateTime.UtcNow) return c.Addrs;

        var servers = _servers();
        if (servers.Count == 0)
        {
            // No usable server found: fall back to the OS, filtering out our own fake answers.
            var os = (await System.Net.Dns.GetHostAddressesAsync(host, ct)).Where(a => !FakeIpPool.IsFake(a)).ToArray();
            return os.Length > 0 ? os : throw new SocketException((int)SocketError.HostNotFound);
        }

        Exception? last = null;
        foreach (var server in servers)
        {
            try
            {
                var v4 = await QueryAsync(server, host, DnsType.A, ct);
                var addrs = v4.Count > 0 ? v4 : await QueryAsync(server, host, DnsType.AAAA, ct);
                if (addrs.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
                var arr = addrs.ToArray();
                _cache[host] = (arr, DateTime.UtcNow + CacheTime);
                return arr;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
            {
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException)
            {
                last = ex;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private async Task<List<IPAddress>> QueryAsync(IPAddress server, string host, DnsType type, CancellationToken ct)
    {
        var id = (ushort)Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var query = DnsMessage.BuildQuery(id, host, type);
            var ep = new IPEndPoint(server.AddressFamily == AddressFamily.InterNetwork ? server.MapToIPv6() : server, PortOverride);
            await _socket.SendToAsync(query, SocketFlags.None, ep, ct);
            byte[] reply;
            try { reply = await tcs.Task.WaitAsync(Timeout, ct); }
            catch (TimeoutException) { throw new TimeoutException($"DNS server {server} did not answer for {host}"); }
            var addrs = DnsMessage.ParseAddresses(reply, out _, out var rcode);
            if (rcode == 3) throw new SocketException((int)SocketError.HostNotFound); // NXDOMAIN
            return addrs;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoop()
    {
        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _socket.ReceiveFromAsync(buf, SocketFlags.None, any, _cts.Token);
                if (r.ReceivedBytes < 12) continue;
                var id = (ushort)((buf[0] << 8) | buf[1]);
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(buf.AsSpan(0, r.ReceivedBytes).ToArray());
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { /* ICMP port unreachable etc. — keep listening */ }
        }
    }

    /// <summary>DNS servers configured on active, non-loopback interfaces (IPv4 first).</summary>
    public static IReadOnlyList<IPAddress> SystemDnsServers()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().DnsAddresses)
                .Where(a => !IPAddress.IsLoopback(a) && !FakeIpPool.IsFake(a) &&
                            !(a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6SiteLocal)) // fec0:0:0:ffff::1-3 placeholders
                .Distinct()
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
    }
}
