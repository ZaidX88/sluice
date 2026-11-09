using System.Collections.Concurrent;
using System.Net.Sockets;
using Sluice.Core.Profiles;

namespace Sluice.Core.Proxy;

public sealed class ConnectionBlockedException(string rule) : IOException($"Blocked by rule '{rule}'");

/// <summary>
/// Opens an outgoing TCP connection to host:port according to a rule action:
/// direct, a single proxy, or a chain (simple / redundancy / load balancing).
/// </summary>
public sealed class ProxyConnector(Profile profile)
{
    private readonly ConcurrentDictionary<int, DateTime> _failedUntil = new();
    private readonly ConcurrentDictionary<int, int> _pidAffinity = new();

    public TimeSpan DirectConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public async Task<TcpClient> ConnectAsync(RuleAction action, string ruleName, string host, int port,
        int processId = 0, CancellationToken ct = default)
    {
        switch (action.Type)
        {
            case RuleActionType.Block:
                throw new ConnectionBlockedException(ruleName);

            case RuleActionType.Direct:
                return await OpenTcpAsync(host, port, DirectConnectTimeout, ct);

            case RuleActionType.Proxy:
                var proxy = profile.FindProxy(action.TargetId ?? -1)
                            ?? throw new InvalidOperationException($"Rule '{ruleName}' references missing proxy {action.TargetId}");
                return await ThroughHopsAsync([proxy], host, port, DirectConnectTimeout, ct);

            case RuleActionType.Chain:
                var chain = profile.FindChain(action.TargetId ?? -1)
                            ?? throw new InvalidOperationException($"Rule '{ruleName}' references missing chain {action.TargetId}");
                return await ThroughChainAsync(chain, host, port, processId, ct);

            default:
                throw new NotSupportedException(action.Type.ToString());
        }
    }

    private async Task<TcpClient> ThroughChainAsync(ProxyChain chain, string host, int port, int pid, CancellationToken ct)
    {
        var hops = chain.Hops.Where(h => h.Enabled)
            .Select(h => profile.FindProxy(h.ProxyId))
            .OfType<ProxyServer>()
            .ToList();
        if (hops.Count == 0) throw new InvalidOperationException($"Chain '{chain.Name}' has no enabled proxies");

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
                throw new ProxyException($"All proxies in chain '{chain.Name}' failed: {string.Join("; ", errors)}");
            }

            case ChainType.LoadBalancing:
            {
                ProxyServer pick;
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

    /// <summary>Connect to hops[0], then tunnel hop-by-hop, finally to host:port.</summary>
    private static async Task<TcpClient> ThroughHopsAsync(IReadOnlyList<ProxyServer> hops, string host, int port,
        TimeSpan timeout, CancellationToken ct)
    {
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
            throw new TimeoutException($"Proxy handshake timed out via {hops[0]}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> OpenTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Connection to {host}:{port} timed out");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
