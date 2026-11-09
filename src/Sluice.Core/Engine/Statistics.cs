namespace Sluice.Core.Engine;

public enum StatisticsGrouping { Application, Target, Rule }

/// <summary>One line of the Statistics pane.</summary>
public sealed record StatisticsRow(
    string Key,
    int Connections,
    int Active,
    int Failed,
    long BytesSent,
    long BytesReceived,
    DateTime LastSeen)
{
    public long BytesTotal => BytesSent + BytesReceived;
}

/// <summary>
/// Aggregates traffic by application, target host or rule. Finished connections are folded in
/// when they close; live ones are added at snapshot time, so totals are always current.
/// </summary>
public sealed class Statistics
{
    private sealed class Acc
    {
        public int Connections, Failed;
        public long Sent, Received;
        public DateTime LastSeen;
    }

    private readonly ConnectionTracker _tracker;
    private readonly object _gate = new();
    private readonly Dictionary<StatisticsGrouping, Dictionary<string, Acc>> _closed = new()
    {
        [StatisticsGrouping.Application] = new(StringComparer.OrdinalIgnoreCase),
        [StatisticsGrouping.Target] = new(StringComparer.OrdinalIgnoreCase),
        [StatisticsGrouping.Rule] = new(StringComparer.OrdinalIgnoreCase),
    };

    public Statistics(ConnectionTracker tracker)
    {
        _tracker = tracker;
        tracker.Closed += OnClosed;
    }

    public static string KeyFor(ConnectionEntry e, StatisticsGrouping g) => g switch
    {
        StatisticsGrouping.Application => e.ProcessName,
        StatisticsGrouping.Target => HostOf(e.Target),
        _ => e.RuleName,
    };

    private void OnClosed(ConnectionEntry e)
    {
        lock (_gate)
        {
            foreach (var (g, map) in _closed)
            {
                var key = KeyFor(e, g);
                if (!map.TryGetValue(key, out var a)) map[key] = a = new Acc();
                a.Connections++;
                if (e.State is ConnectionState.Failed or ConnectionState.Blocked) a.Failed++;
                a.Sent += e.BytesSent;
                a.Received += e.BytesReceived;
                a.LastSeen = e.EndedAt ?? DateTime.Now;
            }
        }
    }

    /// <summary>Current totals, busiest first.</summary>
    public IReadOnlyList<StatisticsRow> Snapshot(StatisticsGrouping grouping)
    {
        var rows = new Dictionary<string, (Acc Acc, int Active)>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var (k, a) in _closed[grouping])
                rows[k] = (new Acc { Connections = a.Connections, Failed = a.Failed, Sent = a.Sent, Received = a.Received, LastSeen = a.LastSeen }, 0);
        }
        foreach (var e in _tracker.Active)
        {
            var key = KeyFor(e, grouping);
            if (!rows.TryGetValue(key, out var r)) r = (new Acc(), 0);
            r.Acc.Connections++;
            r.Acc.Sent += e.BytesSent;
            r.Acc.Received += e.BytesReceived;
            r.Acc.LastSeen = DateTime.Now;
            rows[key] = (r.Acc, r.Active + 1);
        }
        return rows
            .Select(kv => new StatisticsRow(kv.Key, kv.Value.Acc.Connections, kv.Value.Active, kv.Value.Acc.Failed,
                kv.Value.Acc.Sent, kv.Value.Acc.Received, kv.Value.Acc.LastSeen))
            .OrderByDescending(r => r.BytesTotal)
            .ThenByDescending(r => r.Connections)
            .ToList();
    }

    public void Reset()
    {
        lock (_gate)
            foreach (var map in _closed.Values) map.Clear();
    }

    private static string HostOf(string target)
    {
        if (target.StartsWith('['))
        {
            var end = target.IndexOf(']');
            return end > 0 ? target[1..end] : target;
        }
        var colon = target.LastIndexOf(':');
        return colon > 0 ? target[..colon] : target;
    }
}
