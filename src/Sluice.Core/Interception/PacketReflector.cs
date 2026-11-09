using System.Collections.Concurrent;
using System.Net;
using Sluice.Core.Inbound;

namespace Sluice.Core.Interception;

/// <summary>
/// Redirects chosen outbound TCP flows to a local relay port by "reflection":
///
///   app  L:a → R:r            becomes   R:a → L:relay   (reinjected inbound)
///   relay L:relay → R:a       becomes   R:r → L:a       (reinjected inbound)
///
/// The relay therefore sees a client at R:a, and the app believes it is talking to R:r.
/// The flow is keyed by (R, a); its original destination port r is remembered here.
/// Pure packet logic: no WinDivert calls, so it is unit-testable.
/// </summary>
public sealed class PacketReflector : IRedirectTable
{
    public enum Verdict { Pass, Reflected }

    private sealed class Flow
    {
        public required IPEndPoint Original { get; init; }
        public required int ProcessId { get; init; }
        public required bool Redirect { get; init; }
        public long LastSeenTicks;
    }

    private readonly ConcurrentDictionary<IPEndPoint, Flow> _flows = new();
    private readonly Func<IPEndPoint, IPEndPoint, (bool Redirect, int Pid)> _decide;

    public ushort RelayPort { get; }

    /// <param name="relayPort">Port of the transparent listener (bound to the wildcard address).</param>
    /// <param name="decide">Called on the SYN of each new flow with (local, remote); returns whether to redirect, and the owning PID.</param>
    public PacketReflector(ushort relayPort, Func<IPEndPoint, IPEndPoint, (bool Redirect, int Pid)> decide)
    {
        RelayPort = relayPort;
        _decide = decide;
    }

    public int FlowCount => _flows.Count;

    /// <summary>Inspect one outbound packet, rewriting it in place when it belongs to a redirected flow.</summary>
    public Verdict ProcessOutbound(Span<byte> packet)
    {
        var tcp = new TcpPacket(packet);
        if (!tcp.IsValid) return Verdict.Pass;

        // Relay → app: source port is the relay; destination is (R, a).
        if (tcp.SrcPort == RelayPort)
        {
            var key = new IPEndPoint(tcp.DstAddr, tcp.DstPort);
            if (!_flows.TryGetValue(key, out var f) || !f.Redirect) return Verdict.Pass;
            Touch(f);
            tcp.SrcPort = (ushort)f.Original.Port;
            tcp.SwapAddresses();
            return Verdict.Reflected;
        }

        // App → remote.
        var remote = new IPEndPoint(tcp.DstAddr, tcp.DstPort);
        var flowKey = new IPEndPoint(tcp.DstAddr, tcp.SrcPort);
        _flows.TryGetValue(flowKey, out var flow);

        if (tcp.Syn && !tcp.Ack && (flow is null || flow.Original.Port != remote.Port || IsStale(flow)))
        {
            var (redirect, pid) = _decide(new IPEndPoint(tcp.SrcAddr, tcp.SrcPort), remote);
            flow = new Flow { Original = remote, ProcessId = pid, Redirect = redirect, LastSeenTicks = DateTime.UtcNow.Ticks };
            _flows[flowKey] = flow;
        }

        if (flow is null || !flow.Redirect || flow.Original.Port != remote.Port) return Verdict.Pass;

        Touch(flow);
        tcp.DstPort = RelayPort;
        tcp.SwapAddresses();
        return Verdict.Reflected;
    }

    /// <summary>Lookup used by the transparent listener: client endpoint is (R, a).</summary>
    public bool TryGetOriginal(IPEndPoint clientEndPoint, out OriginalDestination original)
    {
        if (_flows.TryGetValue(IRedirectTable.Normalize(clientEndPoint), out var f) && f.Redirect)
        {
            original = new OriginalDestination(f.Original, f.ProcessId);
            return true;
        }
        original = null!;
        return false;
    }

    /// <summary>Drop flows idle for longer than <paramref name="idle"/>.</summary>
    public int Sweep(TimeSpan idle)
    {
        var cutoff = DateTime.UtcNow.Ticks - idle.Ticks;
        var removed = 0;
        foreach (var (k, f) in _flows)
            if (Interlocked.Read(ref f.LastSeenTicks) < cutoff && _flows.TryRemove(k, out _))
                removed++;
        return removed;
    }

    private static void Touch(Flow f) => Interlocked.Exchange(ref f.LastSeenTicks, DateTime.UtcNow.Ticks);

    // A fresh SYN on a key whose flow saw no traffic for a while is a new connection (port reuse).
    private static bool IsStale(Flow f) =>
        DateTime.UtcNow.Ticks - Interlocked.Read(ref f.LastSeenTicks) > TimeSpan.FromSeconds(30).Ticks;
}
