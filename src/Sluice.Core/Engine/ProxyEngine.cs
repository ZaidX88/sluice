using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;
using Sluice.Core.Rules;

namespace Sluice.Core.Engine;

/// <summary>A connection an application wants to make, as captured by an inbound listener.</summary>
public sealed record InboundRequest(string Host, int Port, int ProcessId, string? ProcessPath);

/// <summary>Lets the inbound protocol answer the application once the upstream outcome is known.</summary>
public interface IInboundReply
{
    Task SuccessAsync(CancellationToken ct);
    Task FailAsync(Exception error, CancellationToken ct);
}

/// <summary>
/// The heart of the app: rule matching → upstream connect (direct / proxy / chain) → relay.
/// Inbound listeners (local SOCKS5 now, WinDivert transparent redirection later) feed it.
/// </summary>
public sealed class ProxyEngine
{
    private readonly ConcurrentDictionary<IPEndPoint, byte> _ownEndpoints = new();
    private volatile Snapshot _state;

    private sealed record Snapshot(Profile Profile, RuleEngine Rules, ProxyConnector Connector);

    public ConnectionTracker Tracker { get; }

    public ProxyEngine(Profile profile, ConnectionTracker? tracker = null)
    {
        Tracker = tracker ?? new ConnectionTracker();
        _state = Build(profile);
    }

    public Profile Profile => _state.Profile;

    private Func<string, CancellationToken, Task<IPAddress[]>>? _resolver;

    /// <summary>Resolver for hostnames Sluice connects to itself; null = OS resolver.</summary>
    public Func<string, CancellationToken, Task<IPAddress[]>>? HostResolver
    {
        get => _resolver;
        set
        {
            _resolver = value;
            _state = Build(_state.Profile);
        }
    }

    /// <summary>Swap in a new profile; existing connections keep running.</summary>
    public void Reload(Profile profile)
    {
        _state = Build(profile);
        Tracker.Write(LogLevel.Normal, $"Profile loaded: {profile.Proxies.Count} proxies, {profile.Chains.Count} chains, {profile.Rules.Count} rules");
    }

    private Snapshot Build(Profile p) => new(p, new RuleEngine(p.Rules), new ProxyConnector(p) { Resolver = _resolver });

    /// <summary>Endpoints this app listens on; connections to them are refused (loop guard).</summary>
    public void RegisterOwnEndpoint(IPEndPoint ep) => _ownEndpoints[ep] = 0;
    public void UnregisterOwnEndpoint(IPEndPoint ep) => _ownEndpoints.TryRemove(ep, out _);

    public RuleMatch Classify(InboundRequest req)
    {
        if (req.ProcessId != 0 && _state.Profile.Options.ConnectionLoopDetection && IsLocalProxyHost(req.ProcessId))
            return new RuleMatch(LoopPreventionRule, RuleAction.Direct);
        IPAddress.TryParse(req.Host, out var ip);
        return _state.Rules.Match(new ConnectionInfo(req.ProcessPath, req.Host, ip, req.Port));
    }

    // ---------- infinite connection loop detection ----------

    private static readonly Rule LoopPreventionRule = new() { Name = "Loop prevention", Action = RuleAction.Direct };
    private (Profile Profile, DateTime At, HashSet<int> Pids)? _proxyHosts;
    private readonly HashSet<int> _loopReported = new();

    /// <summary>
    /// True when <paramref name="pid"/> hosts one of the profile's proxies on this machine (it listens on a
    /// local proxy's port). Its own outgoing connections must not be sent to itself, or they would loop.
    /// </summary>
    public bool IsLocalProxyHost(int pid)
    {
        var state = _state;
        var cache = _proxyHosts;
        if (cache is not { } c || !ReferenceEquals(c.Profile, state.Profile) || DateTime.UtcNow - c.At > TimeSpan.FromSeconds(5))
        {
            var pids = new HashSet<int>();
            foreach (var p in state.Profile.Proxies.Where(p => IsLocalAddress(p.Address)))
                pids.UnionWith(Platform.ProcessLookup.FindListeners(p.Port));
            pids.Remove(Environment.ProcessId);
            c = (state.Profile, DateTime.UtcNow, pids);
            _proxyHosts = c;
        }

        if (!c.Pids.Contains(pid)) return false;
        lock (_loopReported)
        {
            if (_loopReported.Add(pid))
                Tracker.Write(LogLevel.Normal,
                    $"Loop prevention: {Path.GetFileName(Platform.ProcessLookup.GetProcessPath(pid)) ?? "?"} ({pid}) runs a proxy from this profile; its connections go direct");
        }
        return true;
    }

    private static bool IsLocalAddress(string address)
    {
        if (address.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(u => u.Address.Equals(ip));
        }
        catch { return false; }
    }

    public string Describe(RuleAction a) => a.Type switch
    {
        RuleActionType.Direct => "Direct",
        RuleActionType.Block => "Block",
        RuleActionType.Proxy => _state.Profile.FindProxy(a.TargetId ?? -1) is { } p ? $"{p.Type.ToString().ToUpperInvariant()} {p}" : $"proxy #{a.TargetId}",
        RuleActionType.Chain => _state.Profile.FindChain(a.TargetId ?? -1) is { } c ? $"chain {c.Name}" : $"chain #{a.TargetId}",
        _ => a.Type.ToString(),
    };

    /// <summary>Serve one application connection end to end. Never throws; owns <paramref name="app"/>.</summary>
    public async Task HandleAsync(Socket app, InboundRequest req, IInboundReply reply, CancellationToken ct)
    {
        var state = _state;
        var target = FormatTarget(req.Host, req.Port);
        var match = Classify(req);
        var via = Describe(match.Action);
        var entry = Tracker.Begin(req.ProcessId, req.ProcessPath, target, match.Rule.Name, via);
        var who = $"{entry.ProcessName} ({req.ProcessId})";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Cts.Token);

        try
        {
            if (IsLoop(req))
                throw new IOException("Connection loop detected: target is this proxy's own listener");

            using var upstream = await state.Connector.ConnectAsync(match.Action, match.Rule.Name, req.Host, req.Port, req.ProcessId, linked.Token);

            await reply.SuccessAsync(linked.Token);
            entry.State = ConnectionState.Open;
            Tracker.Write(LogLevel.Normal, $"{who} - {target} open through {via} (rule {match.Rule.Name})");

            await Relay.RunAsync(app, upstream.Client,
                n => Tracker.CountSent(entry, n),
                n => Tracker.CountReceived(entry, n),
                linked.Token);

            Tracker.Write(LogLevel.Verbose, $"{who} - {target} closed, {entry.BytesSent} bytes sent, {entry.BytesReceived} bytes received");
            Tracker.End(entry, ConnectionState.Closed);
        }
        catch (ConnectionBlockedException ex)
        {
            Tracker.Write(LogLevel.Normal, $"{who} - {target} blocked by rule {match.Rule.Name}");
            await SafeFail(reply, ex, ct);
            Tracker.End(entry, ConnectionState.Blocked, ex.Message);
        }
        catch (Exception ex)
        {
            Tracker.Write(LogLevel.Error, $"{who} - {target} error via {via}: {ex.Message}");
            await SafeFail(reply, ex, ct);
            Tracker.End(entry, ConnectionState.Failed, ex.Message);
        }
        finally
        {
            app.Dispose();
        }
    }

    private bool IsLoop(InboundRequest req)
    {
        if (!IPAddress.TryParse(req.Host, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var ep in _ownEndpoints.Keys)
        {
            if (ep.Port != req.Port) continue;
            if (ep.Address.Equals(ip) || ((ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any)) && IPAddress.IsLoopback(ip)))
                return true;
        }
        return false;
    }

    private static async Task SafeFail(IInboundReply reply, Exception ex, CancellationToken ct)
    {
        try { await reply.FailAsync(ex, ct); } catch { /* app already gone */ }
    }

    private static string FormatTarget(string host, int port) =>
        host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
}
