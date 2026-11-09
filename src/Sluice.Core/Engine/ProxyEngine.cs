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

    /// <summary>Swap in a new profile; existing connections keep running.</summary>
    public void Reload(Profile profile)
    {
        _state = Build(profile);
        Tracker.Write(LogLevel.Normal, $"Profile loaded: {profile.Proxies.Count} proxies, {profile.Chains.Count} chains, {profile.Rules.Count} rules");
    }

    private static Snapshot Build(Profile p) => new(p, new RuleEngine(p.Rules), new ProxyConnector(p));

    /// <summary>Endpoints this app listens on; connections to them are refused (loop guard).</summary>
    public void RegisterOwnEndpoint(IPEndPoint ep) => _ownEndpoints[ep] = 0;
    public void UnregisterOwnEndpoint(IPEndPoint ep) => _ownEndpoints.TryRemove(ep, out _);

    public RuleMatch Classify(InboundRequest req)
    {
        IPAddress.TryParse(req.Host, out var ip);
        return _state.Rules.Match(new ConnectionInfo(req.ProcessPath, req.Host, ip, req.Port));
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
