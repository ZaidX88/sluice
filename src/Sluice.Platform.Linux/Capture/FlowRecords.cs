using System.Collections.Concurrent;
using System.Net;
using Sluice.Core.Inbound;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// Where each redirected TCP connection was going, recorded when its first packet is queued and taken when the relay
/// accepts it. Keyed by the app's source endpoint <b>and</b> the original destination: Linux lets two connections share
/// a source port when their destinations differ, so the source alone is not unique (issue #38). Two such connections
/// also clash after <c>redirect</c> (same relay destination) and conntrack renumbers one's source port, so the relay
/// can't use the endpoint it sees as the key: <see cref="TryTake"/> asks for the connection's original tuple first.
/// </summary>
internal sealed class FlowRecords
{
    private readonly ConcurrentDictionary<(IPEndPoint Source, IPEndPoint Destination), (OriginalDestination Dest, DateTime At)> _records = new();

    /// <summary>Gives the relay endpoint the connection reached and the client endpoint it sees; returns the original tuple.</summary>
    public delegate bool OriginalLookup(IPEndPoint client, out IPEndPoint source, out IPEndPoint destination);

    public int Count => _records.Count;

    public void Add(IPEndPoint source, IPEndPoint destination, OriginalDestination dest, DateTime at) =>
        _records[(Normalize(source), Normalize(destination))] = (dest, at);

    /// <summary>
    /// Removes and returns the record of the connection the relay accepted from <paramref name="client"/>.
    /// With <paramref name="lookup"/> (conntrack) the original tuple decides, so neither a renumbered source port nor
    /// a shared one picks the wrong record. Without it, or when it finds nothing, the only record for that source is
    /// used; when there are several, none is (handing over another connection's destination would be worse than a
    /// reset). <paramref name="how"/> says which way it was found, for the log.
    /// </summary>
    public bool TryTake(IPEndPoint client, OriginalLookup? lookup, out OriginalDestination original, out string how)
    {
        client = Normalize(client);
        if (lookup is not null && lookup(client, out var source, out var destination))
        {
            if (_records.TryRemove((Normalize(source), Normalize(destination)), out var r))
            {
                original = r.Dest;
                how = source.Port == client.Port ? "conntrack" : $"conntrack, source port renumbered from {source.Port}";
                return true;
            }
            original = null!;
            how = $"conntrack: {source} -> {destination}, no record";
            return false;
        }

        (IPEndPoint, IPEndPoint)? only = null;
        var several = false;
        foreach (var key in _records.Keys)
        {
            if (!key.Source.Equals(client)) continue;
            if (only is not null) { several = true; break; }
            only = key;
        }
        if (only is { } k && !several && _records.TryRemove(k, out var single))
        {
            original = single.Dest;
            how = "by source";
            return true;
        }
        original = null!;
        how = several ? "several records for this source" : "no record";
        return false;
    }

    /// <summary>Drops records older than <paramref name="maxAge"/> (connections whose relay connection never came).</summary>
    public void Sweep(DateTime now, TimeSpan maxAge)
    {
        foreach (var (k, v) in _records)
            if (now - v.At > maxAge) _records.TryRemove(k, out _);
    }

    private static IPEndPoint Normalize(IPEndPoint ep) => IRedirectTable.Normalize(ep);
}
