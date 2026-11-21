using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Platform;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Core.Interception;

/// <summary>One UDP datagram an app sent: from App to Destination. Context is the platform's own (e.g. the WinDivert
/// address it arrived with), handed back unchanged in SendToApp.</summary>
public readonly record struct AppDatagram(IPEndPoint App, IPEndPoint Destination, ReadOnlyMemory<byte> Payload, object? Context);

/// <summary>Implemented by each capture: delivers a reply to the app as if it came from <paramref name="from"/>.</summary>
public interface IDatagramSender
{
    void SendToApp(IPEndPoint from, IPEndPoint app, ReadOnlyMemory<byte> payload, object? context);
}

/// <summary>
/// Carries applications' UDP, with no packet format in it (docs/architecture/platforms.md §6.4). Each datagram a capture
/// hands in is classified per flow (application socket → destination): passed unchanged, dropped, or taken and sent on
/// by Sluice. Taken datagrams go through a SOCKS5 UDP association to the rule's tunnel, or, for a Direct rule to a
/// placeholder address, through a plain UDP socket to the name's real address. Replies reach the app through
/// <see cref="IDatagramSender"/> from the address it sent to (the placeholder included), so it sees ordinary UDP.
/// </summary>
public sealed class UdpFlows : IAsyncDisposable
{
    public enum Verdict { Pass, Drop }   // Drop: taken (tunneled or blocked); the platform must not send it on

    /// <summary>How a taken flow leaves: through a tunnel, or straight to the real address behind a placeholder.</summary>
    private enum Way { Pass, Block, Tunnel, DirectByName }

    private sealed class Flow
    {
        public required IPEndPoint Src { get; init; }
        public required IPEndPoint Dst { get; init; }
        public required Way Way { get; init; }
        public string SendHost { get; init; } = "";
        public Client? Client { get; set; }
        public Direct? Direct { get; set; }
        public DateTime DirectFailedAt;
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

    /// <summary>A Direct flow's own socket, sending to the real address of the name behind the placeholder.</summary>
    private sealed class Direct
    {
        public required Task<(Socket Socket, IPEndPoint Target)> Open { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    private readonly RoutingEngine _engine;
    private readonly FakeIpPool _fakes;
    private readonly IDatagramSender _sender;
    private readonly Func<IPEndPoint, int> _findOwner;
    private readonly ConcurrentDictionary<(IPEndPoint, IPEndPoint), Flow> _flows = new();
    private readonly ConcurrentDictionary<(IPEndPoint, int), Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, byte> _reported = new();
    private Profile? _profileSeen;
    private long _tunneled, _dropped;

    public UdpFlows(RoutingEngine engine, FakeIpPool fakes, IDatagramSender sender, Func<IPEndPoint, int> findOwner)
    {
        _engine = engine;
        _fakes = fakes;
        _sender = sender;
        _findOwner = findOwner;
    }

    public int FlowCount => _flows.Count;
    /// <summary>Datagrams Sluice sent on itself: through a tunnel, or Direct to the real address behind a placeholder.</summary>
    public long DatagramsTunneled => Interlocked.Read(ref _tunneled);
    public long DatagramsDropped => Interlocked.Read(ref _dropped);

    /// <summary>Decide one datagram an app sent. Drop = Sluice took it (sent it on itself, or blocked it).</summary>
    public Verdict Handle(in AppDatagram datagram)
    {
        var src = datagram.App;
        var dst = datagram.Destination;
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

        switch (flow.Way)
        {
            case Way.Pass:
                return Verdict.Pass;
            case Way.Block:
                Interlocked.Increment(ref _dropped);
                return Verdict.Drop;
        }

        flow.Context = datagram.Context;
        // The capture may reuse its buffer once this returns.
        var data = datagram.Payload.ToArray();
        return flow.Way == Way.DirectByName ? SendDirect(flow, data) : SendTunneled(flow, data);
    }

    private Verdict SendTunneled(Flow flow, byte[] data)
    {
        var client = flow.Client!;
        if (client.Dead)
        {
            // Retry a failed tunnel at most every 5 s; reopen at once when the server merely closed the association.
            if (DateTime.UtcNow - client.FailedAt < TimeSpan.FromSeconds(5))
            {
                Interlocked.Increment(ref _dropped);
                return Verdict.Drop;
            }
            client = flow.Client = GetClient(flow.Src, client.Tunnel);
        }
        client.Replies[(flow.SendHost, flow.Dst.Port)] = flow;
        client.LastByPort[flow.Dst.Port] = flow;
        Interlocked.Increment(ref _tunneled);
        if (flow.Entry is { } e) _engine.Tracker.CountSent(e, data.Length);
        _ = SendAsync(client, flow, data);
        return Verdict.Drop;
    }

    private Flow NewFlow(IPEndPoint src, IPEndPoint dst)
    {
        var pid = _findOwner(src);
        var fake = _fakes.TryGetHost(dst.Address, out var hostname);
        // A placeholder Sluice no longer knows (from an app's DNS cache before a restart) leads nowhere: drop it, so the
        // app gives up on it and looks the name up again.
        if (!fake && FakeIpPool.IsFake(dst.Address))
            return new Flow { Src = src, Dst = dst, Way = Way.Block };
        if (pid == Environment.ProcessId || (pid == 0 && !fake && !_engine.Profile.Options.ProcessServices))
            return new Flow { Src = src, Dst = dst, Way = Way.Pass };

        var path = pid == 0 ? null : ProcessLookup.GetProcessPath(pid);
        var host = fake ? hostname! : Unmap(dst.Address).ToString();
        var req = new InboundRequest(host, dst.Port, pid, path);
        var route = _engine.ClassifyUdp(req);
        var app = $"{Path.GetFileName(path) ?? "?"} ({pid})";

        // A placeholder only exists inside Sluice: a Direct datagram to it can't go out as it is, so Sluice sends it to
        // the name's real address itself, as it does for a Direct TCP connection to a placeholder.
        var way = route.Verdict switch
        {
            UdpVerdict.Tunnel => Way.Tunnel,
            UdpVerdict.Block => Way.Block,
            _ => fake ? Way.DirectByName : Way.Pass,
        };

        _engine.Tracker.Write(LogLevel.Debug, $"UDP {src} -> {host}:{dst.Port} pid {pid} rule {route.Match.Rule.Name} => {way} {route.Via}");
        var flow = new Flow { Src = src, Dst = dst, Way = way, SendHost = host };
        if (way == Way.Block && route.Match.Action.Type is RuleActionType.Tunnel or RuleActionType.Chain)
            Report($"block:{pid}:{route.Via}", LogLevel.Normal, $"{app} - UDP {route.Via.ToLowerInvariant()} (rule {route.Match.Rule.Name})");
        if (way is Way.Pass or Way.Block) return flow;

        if (way == Way.Tunnel) flow.Client = GetClient(src, route.Tunnel!);
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
        Deliver(flow, data);
    }

    /// <summary>Hands a reply to the app as if it came from where the app sent its datagram (a placeholder included).</summary>
    private void Deliver(Flow flow, ReadOnlyMemory<byte> data)
    {
        if (!_flows.ContainsKey((flow.Src, flow.Dst))) return;   // flow ended
        Interlocked.Exchange(ref flow.LastSeenTicks, DateTime.UtcNow.Ticks);
        if (flow.Entry is { } e) _engine.Tracker.CountReceived(e, data.Length);
        if (flow.Dst.AddressFamily != flow.Src.AddressFamily) return;
        _sender.SendToApp(flow.Dst, flow.Src, data, flow.Context);
    }

    private Verdict SendDirect(Flow flow, byte[] data)
    {
        Direct d;
        lock (flow)
        {
            if (flow.Direct is null)
            {
                // Look the name up again at most every 5 s after a failure.
                if (DateTime.UtcNow - flow.DirectFailedAt < TimeSpan.FromSeconds(5))
                {
                    Interlocked.Increment(ref _dropped);
                    return Verdict.Drop;
                }
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                flow.Direct = new Direct { Open = OpenDirectAsync(flow, cts.Token), Cts = cts };
            }
            d = flow.Direct;
        }
        Interlocked.Increment(ref _tunneled);
        if (flow.Entry is { } e) _engine.Tracker.CountSent(e, data.Length);
        _ = SendDirectAsync(flow, d, data);
        return Verdict.Drop;
    }

    private async Task<(Socket, IPEndPoint)> OpenDirectAsync(Flow flow, CancellationToken ct)
    {
        await Task.Yield();   // resolve and receive off the capture's thread
        var target = await ResolveAsync(flow.SendHost, flow.Dst.Port, flow.Src.AddressFamily, ct);
        var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(target.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _ = ReceiveDirectAsync(flow, socket, target, ct);
        return (socket, target);
    }

    /// <summary>The name's real address, resolved like a Direct TCP connection; the app's address family first.</summary>
    private async Task<IPEndPoint> ResolveAsync(string host, int port, AddressFamily prefer, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var addrs = _engine.HostResolver is { } resolve
            ? await resolve(host, cts.Token)
            : await System.Net.Dns.GetHostAddressesAsync(host, cts.Token);
        var usable = addrs.Select(Unmap).Where(a => !FakeIpPool.IsFake(a)).ToList();
        var pick = usable.FirstOrDefault(a => a.AddressFamily == prefer) ?? usable.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
        return new IPEndPoint(pick, port);
    }

    private async Task SendDirectAsync(Flow flow, Direct d, byte[] data)
    {
        try
        {
            var (socket, target) = await d.Open;
            await socket.SendToAsync(data, SocketFlags.None, target, d.Cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            lock (flow)
            {
                if (flow.Direct != d) return;
                flow.Direct = null;
                flow.DirectFailedAt = DateTime.UtcNow;
            }
            CloseDirect(d);
            Report($"direct-fail:{flow.SendHost}:{ex.Message}", LogLevel.Error, $"UDP to {flow.SendHost}:{flow.Dst.Port} failed: {ex.Message}");
        }
    }

    private async Task ReceiveDirectAsync(Flow flow, Socket socket, IPEndPoint target, CancellationToken ct)
    {
        var buf = new byte[65536];
        EndPoint any = new IPEndPoint(target.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult r;
                try { r = await socket.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
                // Windows reports an ICMP "port unreachable" for an earlier datagram on the next receive: keep listening.
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                // Only the host the app talks to may answer it.
                if (r.RemoteEndPoint is not IPEndPoint from || from.Port != target.Port || !Unmap(from.Address).Equals(target.Address)) continue;
                Deliver(flow, buf.AsMemory(0, r.ReceivedBytes));
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { /* flow ended */ }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { /* socket closed */ }
    }

    private void CloseDirect(Direct d)
    {
        d.Cts.Cancel();
        _ = d.Open.ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            t.Result.Socket.Dispose();
        }, TaskScheduler.Default);
        d.Cts.Dispose();
    }

    /// <summary>End flows idle for longer than <paramref name="idle"/>; close associations nobody uses.</summary>
    public void Sweep(TimeSpan idle)
    {
        var cutoff = DateTime.UtcNow.Ticks - idle.Ticks;
        foreach (var (k, f) in _flows)
            if (Interlocked.Read(ref f.LastSeenTicks) < cutoff) EndFlow(k, ConnectionState.Closed, null);
        var used = _flows.Values.Select(f => f.Client).OfType<Client>().ToHashSet();
        foreach (var (k, c) in _clients)
            if (!used.Contains(c) && _clients.TryRemove(k, out _)) _ = CloseAsync(c);
    }

    private bool EndFlow((IPEndPoint, IPEndPoint) key, ConnectionState state, string? error)
    {
        if (!_flows.TryRemove(key, out var f)) return false;
        Direct? d;
        lock (f)
        {
            d = f.Direct;
            f.Direct = null;
        }
        if (d is not null) CloseDirect(d);
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
