using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Profiles;

namespace Sluice.Core.Proxy;

public sealed class ConnectionBlockedException(string rule) : IOException($"Blocked by rule '{rule}'");

/// <summary>
/// Opens an outgoing TCP connection to host:port according to a rule action:
/// direct, a single tunnel, or a chain (simple / redundancy / load balancing).
/// Encrypted tunnels (SSH, …) are run by <see cref="Tunnels.TunnelBackends"/> and reached through
/// their local SOCKS5 endpoint.
/// </summary>
public sealed class TunnelConnector(Profile profile, Tunnels.TunnelBackends? backends = null)
{
    private readonly ConcurrentDictionary<int, DateTime> _failedUntil = new();
    private readonly ConcurrentDictionary<int, int> _pidAffinity = new();

    public TimeSpan DirectConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Resolves hostnames Sluice itself connects to (direct targets, proxy addresses).
    /// Null = the OS resolver. While DNS faking is active this must bypass the OS cache.
    /// </summary>
    public Func<string, CancellationToken, Task<IPAddress[]>>? Resolver { get; set; }

    public async Task<TcpClient> ConnectAsync(RuleAction action, string ruleName, string host, int port,
        int processId = 0, CancellationToken ct = default)
    {
        switch (action.Type)
        {
            case RuleActionType.Block:
                throw new ConnectionBlockedException(ruleName);

            case RuleActionType.Direct:
                return await OpenTcpAsync(host, port, DirectConnectTimeout, ct);

            case RuleActionType.Tunnel:
                var proxy = profile.FindTunnel(action.TargetId ?? -1)
                            ?? throw new InvalidOperationException($"Rule '{ruleName}' references missing tunnel {action.TargetId}");
                // Turned off: fail rather than send the traffic out directly.
                if (!proxy.Enabled) throw new ProxyException($"Tunnel \"{proxy}\" is turned off");
                return await ThroughHopsAsync([proxy], host, port, DirectConnectTimeout, ct);

            case RuleActionType.Chain:
                var chain = profile.FindChain(action.TargetId ?? -1)
                            ?? throw new InvalidOperationException($"Rule '{ruleName}' references missing chain {action.TargetId}");
                return await ThroughChainAsync(chain, host, port, processId, ct);

            default:
                throw new NotSupportedException(action.Type.ToString());
        }
    }

    private async Task<TcpClient> ThroughChainAsync(TunnelChain chain, string host, int port, int pid, CancellationToken ct)
    {
        var hops = chain.Hops.Where(h => h.Enabled)
            .Select(h => profile.FindTunnel(h.TunnelId))
            .OfType<Tunnel>()
            .Where(t => t.Enabled)
            .ToList();
        if (hops.Count == 0) throw new ProxyException($"Chain '{chain.Name}' has no tunnels turned on");

        switch (chain.Type)
        {
            case ChainType.Simple:
                return await ThroughHopsAsync(hops, host, port, DirectConnectTimeout, ct);

            case ChainType.Redundancy:
            {
                var timeout = TimeSpan.FromSeconds(Math.Max(1, chain.RedundancyTimeoutSeconds));
                var recheck = TimeSpan.FromSeconds(Math.Max(1, chain.RedundancyRecheckSeconds));
                var errors = new List<string>();
                foreach (var p in hops)
                {
                    if (_failedUntil.TryGetValue(p.Id, out var until) && until > DateTime.UtcNow) continue;
                    try
                    {
                        var c = await ThroughHopsAsync([p], host, port, timeout, ct);
                        _failedUntil.TryRemove(p.Id, out _);
                        return c;
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
                    {
                        _failedUntil[p.Id] = DateTime.UtcNow + recheck;
                        errors.Add($"{p}: {ex.Message}");
                    }
                }
                if (chain.RedundancyDirectFallback)
                    return await OpenTcpAsync(host, port, DirectConnectTimeout, ct);
                throw new ProxyException($"All tunnels in chain '{chain.Name}' failed: {string.Join("; ", errors)}");
            }

            case ChainType.LoadBalancing:
            {
                Tunnel pick;
                if (chain.LoadBalanceSamePerPid && pid != 0 &&
                    _pidAffinity.TryGetValue(pid, out var id) && hops.FirstOrDefault(h => h.Id == id) is { } sticky)
                    pick = sticky;
                else
                {
                    pick = hops[Random.Shared.Next(hops.Count)];
                    if (chain.LoadBalanceSamePerPid && pid != 0) _pidAffinity[pid] = pick.Id;
                }
                return await ThroughHopsAsync([pick], host, port, DirectConnectTimeout, ct);
            }

            default:
                throw new NotSupportedException(chain.Type.ToString());
        }
    }

    /// <summary>
    /// Replaces an encrypted first hop by the local SOCKS5 endpoint of its running session.
    /// (Later hops would need the session's transport to run through the earlier hops; not supported yet.)
    /// </summary>
    private async Task<IReadOnlyList<Tunnel>> ResolveBackendHopsAsync(IReadOnlyList<Tunnel> hops, CancellationToken ct)
    {
        for (var i = 1; i < hops.Count; i++)
            if (Tunnels.TunnelBackends.Handles(hops[i].Type))
                throw new ProxyException($"{hops[i].Type.DisplayName()} tunnel \"{hops[i]}\" can only be the first hop of a chain");
        if (!Tunnels.TunnelBackends.Handles(hops[0].Type)) return hops;
        if (backends is null) throw new ProxyException($"{hops[0].Type.DisplayName()} tunnels are not available here");
        var local = await backends.AsLocalSocksAsync(hops[0], ct);
        return [local, .. hops.Skip(1)];
    }

    /// <summary>Connect to hops[0], then tunnel hop-by-hop, finally to host:port.</summary>
    private async Task<TcpClient> ThroughHopsAsync(IReadOnlyList<Tunnel> hops, string host, int port,
        TimeSpan timeout, CancellationToken ct)
    {
        hops = await ResolveBackendHopsAsync(hops, ct);
        var client = await OpenTcpAsync(hops[0].Address, hops[0].Port, timeout, ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var stream = client.GetStream();
            for (var i = 0; i < hops.Count; i++)
            {
                var (nextHost, nextPort) = i + 1 < hops.Count ? (hops[i + 1].Address, hops[i + 1].Port) : (host, port);
                await ProxyHandshake.ConnectAsync(stream, hops[i], nextHost, nextPort, cts.Token);
            }
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Tunnel handshake timed out via {hops[0]}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<TcpClient> OpenTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        IPAddress[] addrs;
        if (IPAddress.TryParse(host, out var literal)) addrs = [literal];
        else if (Resolver is not null)
        {
            try { addrs = await Resolver(host, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Resolving {host} timed out");
            }
        }
        else addrs = await System.Net.Dns.GetHostAddressesAsync(host, cts.Token);

        try
        {
            return await HappyEyeballs.ConnectAsync(addrs, port, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Connection to {host}:{port} timed out");
        }
    }
}
