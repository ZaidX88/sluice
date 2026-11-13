using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Dns;
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

public enum UdpVerdict { Pass, Block, Tunnel }

/// <summary>What to do with an application's UDP flow, and through which tunnel.</summary>
public sealed record UdpRoute(UdpVerdict Verdict, RuleMatch Match, Tunnel? Tunnel, string Via);

/// <summary>
/// The heart of the app: rule matching → upstream connect (direct / tunnel / chain) → relay.
/// Inbound listeners (local SOCKS5, WinDivert transparent redirection) feed it.
/// </summary>
public sealed class RoutingEngine : IAsyncDisposable
{
    private readonly ConcurrentDictionary<IPEndPoint, byte> _ownEndpoints = new();
    private readonly ConcurrentDictionary<string, DateTime> _staleReported = new();
    private volatile Snapshot _state;

    private sealed record Snapshot(Profile Profile, RuleEngine Rules, TunnelConnector Connector);

    public ConnectionTracker Tracker { get; }

    /// <summary>Client sessions of encrypted tunnels (SSH, …), shared by all connections.</summary>
    public Tunnels.TunnelBackends Backends { get; }

    public RoutingEngine(Profile profile, ConnectionTracker? tracker = null)
    {
        Tracker = tracker ?? new ConnectionTracker();
        Backends = new Tunnels.TunnelBackends(line => Tracker.Write(LogLevel.Normal, line));
        _state = Build(profile);
        _ = Backends.PruneAsync(profile);   // learn the tunnels (and which are turned off)
    }

    public async ValueTask DisposeAsync() => await Backends.DisposeAsync();

    public Profile Profile => _state.Profile;

    private Func<string, CancellationToken, Task<IPAddress[]>>? _resolver;

    /// <summary>Resolver for hostnames Sluice connects to itself; null = OS resolver.</summary>
    public Func<string, CancellationToken, Task<IPAddress[]>>? HostResolver
    {
        get => _resolver;
        set
        {
            _resolver = value;
            Backends.Resolver = value is null ? null : new Tunnels.TunnelTransport.Resolver(value);
            _state = Build(_state.Profile);
        }
    }

    /// <summary>Swap in a new profile; existing connections keep running. Edited/removed tunnels' sessions close.</summary>
    public void Reload(Profile profile)
    {
        _state = Build(profile);
        _ = Backends.PruneAsync(profile);
        Tracker.Write(LogLevel.Normal, $"Profile loaded: {profile.Tunnels.Count} tunnels, {profile.Chains.Count} chains, {profile.Rules.Count} rules");
    }

    private Snapshot Build(Profile p) => new(p, new RuleEngine(p.Rules), new TunnelConnector(p, Backends) { Resolver = _resolver });

    /// <summary>Endpoints this app listens on; connections to them are refused (loop guard).</summary>
    public void RegisterOwnEndpoint(IPEndPoint ep) => _ownEndpoints[ep] = 0;
    public void UnregisterOwnEndpoint(IPEndPoint ep) => _ownEndpoints.TryRemove(ep, out _);

    public RuleMatch Classify(InboundRequest req)
    {
        // Sluice's own tunnel helpers (sing-box) talk to tunnel servers: always direct, never into a tunnel.
        if (Backends.IsHelperProcess(req.ProcessId))
            return new RuleMatch(HelperRule, RuleAction.Direct);
        if (req.ProcessId != 0 && _state.Profile.Options.ConnectionLoopDetection && IsLocalProxyHost(req.ProcessId))
            return new RuleMatch(LoopPreventionRule, RuleAction.Direct);
        IPAddress.TryParse(req.Host, out var ip);
        return _state.Rules.Match(new ConnectionInfo(req.ProcessPath, req.Host, ip, req.Port));
    }

    // ---------- infinite connection loop detection ----------

    private static readonly Rule LoopPreventionRule = new() { Name = "Loop prevention", Action = RuleAction.Direct };
    private static readonly Rule HelperRule = new() { Name = "Tunnel helper", Action = RuleAction.Direct };
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
            foreach (var p in state.Profile.Tunnels.Where(p => IsLocalAddress(p.Address)))
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
                    $"Loop prevention: {Path.GetFileName(Platform.ProcessLookup.GetProcessPath(pid)) ?? "?"} ({pid}) runs a tunnel from this profile; its connections go direct");
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

    /// <summary>
    /// Starts the sessions of encrypted tunnels (SSH, sing-box) that enabled rules route to, so they are ready
    /// before the first connection. Runs in the background; failures are logged and retried on first use.
    /// </summary>
    public void WarmUpTunnels()
    {
        var p = _state.Profile;
        var ids = new HashSet<int>();
        foreach (var r in p.Rules.Where(r => r.Enabled))
        {
            if (r.Action.Type == RuleActionType.Tunnel && r.Action.TargetId is { } id) ids.Add(id);
            else if (r.Action.Type == RuleActionType.Chain && p.FindChain(r.Action.TargetId ?? -1) is { } c)
                foreach (var h in c.Hops.Where(h => h.Enabled)) ids.Add(h.TunnelId);
        }
        foreach (var t in ids.Select(p.FindTunnel).OfType<Tunnel>().Where(t => t.Enabled && Tunnels.TunnelBackends.Handles(t.Type)))
        {
            Backends.ResetFailures(t.Id);   // starting capture is a good moment to try a failed tunnel again
            _ = Task.Run(async () =>
            {
                try { await Backends.GetLocalSocksAsync(t, CancellationToken.None); }
                catch { /* already logged by the backends; the next connection retries */ }
            });
        }
    }

    // ---------- UDP ----------

    private void ReportStaleFake(InboundRequest req)
    {
        var name = Path.GetFileName(req.ProcessPath) ?? "?";
        var key = $"{req.ProcessId}/{req.Host}";
        var now = DateTime.UtcNow;
        if (_staleReported.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(30)) return;
        _staleReported[key] = now;
        if (_staleReported.Count > 1000)
            foreach (var (k, v) in _staleReported) if (now - v > TimeSpan.FromMinutes(1)) _staleReported.TryRemove(k, out _);
        Tracker.Write(LogLevel.Normal,
            $"{name} ({req.ProcessId}) - {req.Host}:{req.Port} reset: a placeholder address from before Sluice restarted; the app will look the name up again");
    }

    /// <summary>Closes with RST, so the app sees a failure at once rather than an orderly empty reply.</summary>
    private static void ResetConnection(Socket s)
    {
        try
        {
            s.LingerState = new LingerOption(true, 0);
            s.Close();
        }
        catch (Exception) { /* already closed */ }
    }

    /// <summary>Decides an application's UDP flow per its rule and the profile's UDP mode.</summary>
    public UdpRoute ClassifyUdp(InboundRequest req)
    {
        var match = Classify(req);
        var a = match.Action;
        switch (a.Type)
        {
            case RuleActionType.Direct:
                return new UdpRoute(UdpVerdict.Pass, match, null, "Direct");
            case RuleActionType.Block:
                return new UdpRoute(UdpVerdict.Block, match, null, "Block");
        }
        var mode = _state.Profile.Options.Udp;
        switch (mode)
        {
            case UdpMode.Bypass:
                return new UdpRoute(UdpVerdict.Pass, match, null, "Direct (UDP bypasses tunnels)");
            case UdpMode.BlockAll:
                return new UdpRoute(UdpVerdict.Block, match, null, "Blocked (UDP of tunneled apps is blocked)");
            case UdpMode.BlockQuic:
                return req.Port == 443
                    ? new UdpRoute(UdpVerdict.Block, match, null, "Blocked (QUIC)")
                    : new UdpRoute(UdpVerdict.Pass, match, null, "Direct (UDP bypasses tunnels)");
        }
        if (UdpTunnelFor(a, req.ProcessId) is { } t)
            return new UdpRoute(UdpVerdict.Tunnel, match, t, $"{t.Type.DisplayName()} {t}");
        return new UdpRoute(UdpVerdict.Block, match, null, $"Blocked ({Describe(a)} cannot carry UDP)");
    }

    /// <summary>True for tunnels that can carry UDP: SOCKS5 (UDP ASSOCIATE) and the sing-box protocols.</summary>
    public static bool CarriesUdp(Tunnel t) => t.Type == TunnelType.Socks5 || Tunnels.SingBoxConfig.Handles(t.Type);

    private Tunnel? UdpTunnelFor(RuleAction a, int pid)
    {
        var p = _state.Profile;
        if (a.Type == RuleActionType.Tunnel)
            return p.FindTunnel(a.TargetId ?? -1) is { Enabled: true } t && CarriesUdp(t) ? t : null;
        if (p.FindChain(a.TargetId ?? -1) is not { } chain) return null;
        var hops = chain.Hops.Where(h => h.Enabled).Select(h => p.FindTunnel(h.TunnelId)).OfType<Tunnel>().Where(t => t.Enabled).ToList();
        // A datagram cannot be relayed hop by hop through several tunnels.
        if (chain.Type == ChainType.Simple) return hops.Count == 1 && CarriesUdp(hops[0]) ? hops[0] : null;
        var usable = hops.Where(CarriesUdp).ToList();
        if (usable.Count == 0) return null;
        return chain.Type == ChainType.LoadBalancing ? usable[(pid & 0x7fffffff) % usable.Count] : usable[0];
    }

    /// <summary>Opens a UDP association through <paramref name="t"/> (starting its session if needed).</summary>
    public async Task<Socks5UdpAssociation> OpenUdpAsync(Tunnel t, Action<string, int, ReadOnlyMemory<byte>> onReceive, CancellationToken ct)
    {
        var socks = Tunnels.TunnelBackends.Handles(t.Type) ? await Backends.AsLocalSocksAsync(t, ct) : t;
        return await Socks5UdpAssociation.OpenAsync(socks, onReceive, _resolver, ct: ct);
    }

    internal ConnectionEntry BeginUdp(InboundRequest req, UdpRoute route) =>
        Tracker.Begin(req.ProcessId, req.ProcessPath, FormatTarget(req.Host, req.Port) + " UDP", route.Match.Rule.Name, route.Via);

    public string Describe(RuleAction a) => a.Type switch
    {
        RuleActionType.Direct => "Direct",
        RuleActionType.Block => "Block",
        RuleActionType.Tunnel => _state.Profile.FindTunnel(a.TargetId ?? -1) is { } p ? $"{p.Type.DisplayName()} {p}" : $"tunnel #{a.TargetId}",
        RuleActionType.Chain => _state.Profile.FindChain(a.TargetId ?? -1) is { } c ? $"chain {c.Name}" : $"chain #{a.TargetId}",
        _ => a.Type.ToString(),
    };

    /// <summary>Serve one application connection end to end. Never throws; owns <paramref name="app"/>.</summary>
    public async Task HandleAsync(Socket app, InboundRequest req, IInboundReply reply, CancellationToken ct)
    {
        // A fake address Sluice doesn't know (an app's DNS cache from before Sluice restarted) leads nowhere: reset the
        // connection at once, so the app looks the name up again, instead of sending 198.18.x.x into a tunnel (where it
        // would hang for the connect timeout and make a healthy tunnel look dead).
        if (IPAddress.TryParse(req.Host, out var hostIp) && FakeIpPool.IsFake(hostIp))
        {
            ReportStaleFake(req);
            try { await reply.FailAsync(new IOException("stale placeholder address"), ct); } catch (Exception) { /* app gone */ }
            ResetConnection(app);
            return;
        }

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
                throw new IOException("Connection loop detected: target is Sluice's own listener");

            // Hold the route's tunnels for the life of the connection (they are never stopped as idle meanwhile).
            using var leases = LeaseTunnels(state.Profile, match.Action);
            using var upstream = await state.Connector.ConnectAsync(match.Action, match.Rule.Name, req.Host, req.Port, req.ProcessId, linked.Token);

            await reply.SuccessAsync(linked.Token);
            entry.State = ConnectionState.Open;
            Tracker.Write(LogLevel.Normal, $"{who} - {target} open through {via} (rule {match.Rule.Name})");

            await Relay.RunAsync(app, upstream.Client,
                n => Tracker.CountSent(entry, n),
                n => Tracker.CountReceived(entry, n),
                linked.Token,
                UsesSsh(state.Profile, match.Action) ? SshDownstreamBuffer : 0);

            Tracker.Write(LogLevel.Verbose, $"{who} - {target} closed, {entry.BytesSent} bytes sent, {entry.BytesReceived} bytes received");
            Tracker.End(entry, ConnectionState.Closed);
        }
        catch (ConnectionBlockedException ex)
        {
            Tracker.Write(LogLevel.Normal, $"{who} - {target} blocked by rule {match.Rule.Name}");
            await SafeFail(reply, ex, ct);
            Tracker.End(entry, ConnectionState.Blocked, ex.Message);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Deliberate: the user pressed Disconnect, or Sluice is shutting down.
            Tracker.Write(LogLevel.Verbose, $"{who} - {target} disconnected, {entry.BytesSent} bytes sent, {entry.BytesReceived} bytes received");
            Tracker.End(entry, ConnectionState.Closed, "disconnected");
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

    /// <summary>Encrypted tunnels a rule action may use (a chain's enabled hops), for leases and warm-up.</summary>
    internal static IEnumerable<Tunnel> BackendTunnels(Profile p, RuleAction a) => a.Type switch
    {
        RuleActionType.Tunnel => p.FindTunnel(a.TargetId ?? -1) is { } t && Tunnels.TunnelBackends.Handles(t.Type) ? [t] : [],
        RuleActionType.Chain => p.FindChain(a.TargetId ?? -1) is { } c
            ? c.Hops.Where(h => h.Enabled).Select(h => p.FindTunnel(h.TunnelId)).OfType<Tunnel>().Where(t => Tunnels.TunnelBackends.Handles(t.Type))
            : [],
        _ => [],
    };

    private sealed class LeaseSet(List<IDisposable> leases) : IDisposable
    {
        public void Dispose() { foreach (var l in leases) l.Dispose(); }
    }

    internal IDisposable LeaseTunnels(Profile p, RuleAction a) => new LeaseSet(BackendTunnels(p, a).Select(t => Backends.Lease(t.Id)).ToList());

    /// <summary>Per-connection download buffer for SSH routes (see <see cref="Relay.RunAsync"/>).</summary>
    public const int SshDownstreamBuffer = 16 * 1024 * 1024;

    /// <summary>True when the route's first hop is an SSH tunnel (SSH.NET channels have no flow control).</summary>
    internal static bool UsesSsh(Profile p, RuleAction a) => a.Type switch
    {
        RuleActionType.Tunnel => p.FindTunnel(a.TargetId ?? -1)?.Type == TunnelType.Ssh,
        RuleActionType.Chain => p.FindChain(a.TargetId ?? -1) is { } c &&
                                c.Hops.Where(h => h.Enabled).Select(h => p.FindTunnel(h.TunnelId)).Any(t => t?.Type == TunnelType.Ssh),
        _ => false,
    };

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
