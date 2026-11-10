using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Platform;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Core.Interception;

/// <summary>
/// Carries applications' UDP through tunnels. Each captured outbound datagram is classified per flow
/// (application socket → destination): passed unchanged, dropped, or taken out of the network and sent
/// through a SOCKS5 UDP association to the rule's tunnel. Replies from the association are rebuilt as
/// inbound packets from the original destination and injected, so the application sees ordinary UDP.
/// Pure logic apart from the delegates, so it is testable without WinDivert.
/// </summary>
public sealed class UdpNat : IAsyncDisposable
{
    public enum Verdict { Pass, Drop }

    /// <summary>Injects a complete inbound IP packet; <c>context</c> is what the capture passed with the app's packet.</summary>
    public delegate void Injector(byte[] packet, object? context);

    private sealed class Flow
    {
        public required IPEndPoint Src { get; init; }
        public required IPEndPoint Dst { get; init; }
        public required UdpVerdict Verdict { get; init; }
        public string SendHost { get; init; } = "";
        public Client? Client { get; set; }
        public ConnectionEntry? Entry { get; set; }
        public object? Context;
        public long LastSeenTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>One application socket's association with one tunnel.</summary>
    private sealed class Client
    {
        public required IPEndPoint Src { get; init; }
        public required Tunnel Tunnel { get; init; }
        public Task<Socks5UdpAssociation>? Association;
        public readonly ConcurrentDictionary<(string Host, int Port), Flow> Replies = new();
        public readonly ConcurrentDictionary<int, Flow> LastByPort = new();
        public DateTime FailedAt;
        public volatile bool Dead;
        public IDisposable? Lease;
    }

    private readonly RoutingEngine _engine;
    private readonly FakeIpPool _fakes;
    private readonly Func<IPEndPoint, int> _findOwner;
    private readonly Injector _inject;
    private readonly ConcurrentDictionary<(IPEndPoint, IPEndPoint), Flow> _flows = new();
    private readonly ConcurrentDictionary<(IPEndPoint, int), Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, byte> _reported = new();
    private Profile? _profileSeen;
    private long _tunneled, _dropped;

    public UdpNat(RoutingEngine engine, FakeIpPool fakes, Injector inject, Func<IPEndPoint, int>? findOwner = null)
    {
        _engine = engine;
        _fakes = fakes;
        _inject = inject;
        _findOwner = findOwner ?? ProcessLookup.FindUdpOwner;
    }

    public int FlowCount => _flows.Count;
    public long DatagramsTunneled => Interlocked.Read(ref _tunneled);
    public long DatagramsDropped => Interlocked.Read(ref _dropped);

    /// <summary>Inspect one outbound UDP packet. Drop = the packet was consumed (tunneled or blocked).</summary>
    public Verdict ProcessOutbound(ReadOnlySpan<byte> packet, object? context)
    {
        if (!UdpPacket.TryParse(packet, out var src, out var dst, out var off, out var len)) return Verdict.Pass;
        if (IsNeverCaptured(dst)) return Verdict.Pass;

        // A different profile may route differently: decide every flow again.
        var profile = _engine.Profile;
        if (!ReferenceEquals(profile, _profileSeen))
        {
            _profileSeen = profile;
            ResetFlows("profile changed");
        }

        var key = (src, dst);
        if (!_flows.TryGetValue(key, out var flow)) flow = _flows.GetOrAdd(key, _ => NewFlow(src, dst));
        Interlocked.Exchange(ref flow.LastSeenTicks, DateTime.UtcNow.Ticks);

        switch (flow.Verdict)
        {
            case UdpVerdict.Pass:
                return Verdict.Pass;
            case UdpVerdict.Block:
                Interlocked.Increment(ref _dropped);
                return Verdict.Drop;
        }

        flow.Context = context;
        var client = flow.Client!;
        if (client.Dead)
        {
            // Retry a failed tunnel at most every 5 s; reopen at once when the server merely closed the association.
            if (DateTime.UtcNow - client.FailedAt < TimeSpan.FromSeconds(5))
            {
                Interlocked.Increment(ref _dropped);
                return Verdict.Drop;
            }
            client = flow.Client = GetClient(src, client.Tunnel);
        }
        client.Replies[(flow.SendHost, dst.Port)] = flow;
        client.LastByPort[dst.Port] = flow;
        var data = packet.Slice(off, len).ToArray();
        Interlocked.Increment(ref _tunneled);
        if (flow.Entry is { } e) _engine.Tracker.CountSent(e, data.Length);
        _ = SendAsync(client, flow, data);
        return Verdict.Drop;
    }

    private Flow NewFlow(IPEndPoint src, IPEndPoint dst)
    {
        var pid = _findOwner(src);
        var fake = _fakes.TryGetHost(dst.Address, out var hostname);
        if (pid == Environment.ProcessId || (pid == 0 && !fake && !_engine.Profile.Options.ProcessServices))
            return new Flow { Src = src, Dst = dst, Verdict = UdpVerdict.Pass };

        var path = pid == 0 ? null : ProcessLookup.GetProcessPath(pid);
        var host = fake ? hostname! : Unmap(dst.Address).ToString();
        var req = new InboundRequest(host, dst.Port, pid, path);
        var route = _engine.ClassifyUdp(req);
        var verdict = route.Verdict;
        var app = $"{Path.GetFileName(path) ?? "?"} ({pid})";

        // A fake address only exists inside Sluice: such datagrams can only go through a tunnel.
        if (fake && verdict == UdpVerdict.Pass)
        {
            verdict = UdpVerdict.Block;
            Report($"fake-direct:{host}", LogLevel.Verbose, $"{app} - {host}:{dst.Port} UDP dropped (name resolved by Sluice, rule {route.Match.Rule.Name} is Direct; the app will retry over TCP)");
        }

        _engine.Tracker.Write(LogLevel.Debug, $"UDP {src} -> {host}:{dst.Port} pid {pid} rule {route.Match.Rule.Name} => {verdict} {route.Via}");
        var flow = new Flow { Src = src, Dst = dst, Verdict = verdict, SendHost = host };
        if (verdict == UdpVerdict.Block && route.Match.Action.Type is RuleActionType.Tunnel or RuleActionType.Chain)
            Report($"block:{pid}:{route.Via}", LogLevel.Normal, $"{app} - UDP {route.Via.ToLowerInvariant()} (rule {route.Match.Rule.Name})");
        if (verdict != UdpVerdict.Tunnel) return flow;

        flow.Client = GetClient(src, route.Tunnel!);
        var entry = _engine.BeginUdp(req, route);
        entry.State = ConnectionState.Open;
        flow.Entry = entry;
        entry.Cts.Token.Register(() => EndFlow((src, dst), ConnectionState.Closed, "disconnected"));
        _engine.Tracker.Write(LogLevel.Normal, $"{app} - {host}:{dst.Port} UDP through {route.Via} (rule {route.Match.Rule.Name})");
        return flow;
    }

    private Client GetClient(IPEndPoint src, Tunnel t)
    {
        var key = (src, t.Id);
        while (true)
        {
            var c = _clients.GetOrAdd(key, _ => new Client { Src = src, Tunnel = t });
            if (!c.Dead)
            {
                lock (c) c.Lease ??= _engine.Backends.Lease(t.Id);   // after the add: the factory may run twice
                return c;
            }
            if (_clients.TryRemove(new KeyValuePair<(IPEndPoint, int), Client>(key, c))) c.Lease?.Dispose();
        }
    }

    private async Task SendAsync(Client c, Flow flow, byte[] data)
    {
        try
        {
            Task<Socks5UdpAssociation> assoc;
            lock (c)
            {
                assoc = c.Association ??= OpenAsync(c);
            }
            var a = await assoc;
            await a.SendAsync(flow.SendHost, flow.Dst.Port, data, _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            if (!c.Dead)
            {
                c.Dead = true;
                c.FailedAt = DateTime.UtcNow;
                if (_clients.TryRemove(new KeyValuePair<(IPEndPoint, int), Client>((c.Src, c.Tunnel.Id), c))) c.Lease?.Dispose();
                Report($"fail:{c.Tunnel.Id}:{ex.Message}", LogLevel.Error, $"UDP through {c.Tunnel.Type.DisplayName()} {c.Tunnel} failed: {ex.Message}");
            }
        }
    }

    private async Task<Socks5UdpAssociation> OpenAsync(Client c)
    {
        var a = await _engine.OpenUdpAsync(c.Tunnel, (host, port, data) => OnReply(c, host, port, data), _cts.Token);
        a.Closed += () =>
        {
            c.Dead = true;
            if (_clients.TryRemove(new KeyValuePair<(IPEndPoint, int), Client>((c.Src, c.Tunnel.Id), c))) c.Lease?.Dispose();
        };
        return a;
    }

    private void OnReply(Client c, string host, int port, ReadOnlyMemory<byte> data)
    {
        if (IPAddress.TryParse(host, out var ip)) host = Unmap(ip).ToString();
        if (!c.Replies.TryGetValue((host, port), out var flow) && !c.LastByPort.TryGetValue(port, out flow)) return;
        if (!_flows.ContainsKey((flow.Src, flow.Dst))) return;   // flow ended
        Interlocked.Exchange(ref flow.LastSeenTicks, DateTime.UtcNow.Ticks);
        if (flow.Entry is { } e) _engine.Tracker.CountReceived(e, data.Length);
        // The reply must appear to come from where the app sent its datagram (including a fake address).
        var from = flow.Dst;
        var to = flow.Src;
        if (from.AddressFamily != to.AddressFamily) return;
        _inject(UdpPacket.Build(from, to, data.Span), flow.Context);
    }

    /// <summary>End flows idle for longer than <paramref name="idle"/>; close associations nobody uses.</summary>
    public int Sweep(TimeSpan idle)
    {
        var cutoff = DateTime.UtcNow.Ticks - idle.Ticks;
        var removed = 0;
        foreach (var (k, f) in _flows)
            if (Interlocked.Read(ref f.LastSeenTicks) < cutoff && EndFlow(k, ConnectionState.Closed, null)) removed++;
        var used = _flows.Values.Select(f => f.Client).OfType<Client>().ToHashSet();
        foreach (var (k, c) in _clients)
            if (!used.Contains(c) && _clients.TryRemove(k, out _)) _ = CloseAsync(c);
        return removed;
    }

    private bool EndFlow((IPEndPoint, IPEndPoint) key, ConnectionState state, string? error)
    {
        if (!_flows.TryRemove(key, out var f)) return false;
        if (f.Entry is { } e)
        {
            _engine.Tracker.Write(LogLevel.Verbose, $"{e.ProcessName} ({e.ProcessId}) - {e.Target} closed, {e.BytesSent} bytes sent, {e.BytesReceived} bytes received");
            _engine.Tracker.End(e, state, error);
        }
        return true;
    }

    private void ResetFlows(string why)
    {
        foreach (var k in _flows.Keys) EndFlow(k, ConnectionState.Closed, why);
        foreach (var (k, c) in _clients)
            if (_clients.TryRemove(k, out _)) _ = CloseAsync(c);
    }

    private static async Task CloseAsync(Client c)
    {
        c.Dead = true;
        c.Lease?.Dispose();
        try
        {
            if (c.Association is { } t) await (await t).DisposeAsync();
        }
        catch { /* never opened */ }
    }

    private void Report(string key, LogLevel level, string message)
    {
        if (_reported.Count > 1000) _reported.Clear();
        if (_reported.TryAdd(key, 0)) _engine.Tracker.Write(level, message);
    }

    /// <summary>Multicast, broadcast, link-local and DNS/DHCP are always left alone.</summary>
    internal static bool IsNeverCaptured(IPEndPoint dst)
    {
        if (dst.Port is 53 or 67 or 68 or 5353 or 5355 or 1900) return true;
        var a = Unmap(dst.Address);
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] >= 224 || (b[0] == 169 && b[1] == 254) || b[0] == 127 || b[0] == 0;
        }
        return a.IsIPv6Multicast || a.IsIPv6LinkLocal || IPAddress.IsLoopback(a);
    }

    private static IPAddress Unmap(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        ResetFlows("stopped");
        await Task.CompletedTask;
    }
}
