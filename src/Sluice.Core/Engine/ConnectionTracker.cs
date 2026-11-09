using System.Collections.Concurrent;

namespace Sluice.Core.Engine;

public enum LogLevel { Error, Normal, Verbose, Debug }

public enum ConnectionState { Connecting, Open, Closed, Failed, Blocked }

/// <summary>One proxied (or direct) connection, as shown in the Connections pane.</summary>
public sealed class ConnectionEntry
{
    private long _sent, _received;
    internal CancellationTokenSource Cts { get; } = new();

    public long Id { get; init; }
    public int ProcessId { get; init; }
    public string? ProcessPath { get; init; }
    public string ProcessName => ProcessPath is null ? "?" : Path.GetFileName(ProcessPath);
    public required string Target { get; init; }
    public required string RuleName { get; init; }
    public required string Via { get; init; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public DateTime? EndedAt { get; internal set; }
    public ConnectionState State { get; internal set; } = ConnectionState.Connecting;
    public string? Error { get; internal set; }

    public long BytesSent => Interlocked.Read(ref _sent);
    public long BytesReceived => Interlocked.Read(ref _received);

    internal void AddSent(long n) => Interlocked.Add(ref _sent, n);
    internal void AddReceived(long n) => Interlocked.Add(ref _received, n);

    public void Disconnect() => Cts.Cancel();
}

/// <summary>Live connection list + totals + log stream; UI-agnostic.</summary>
public sealed class ConnectionTracker
{
    private long _nextId;
    private long _totalSent, _totalReceived;
    private readonly ConcurrentDictionary<long, ConnectionEntry> _active = new();

    public LogLevel Level { get; set; } = LogLevel.Normal;

    public event Action<ConnectionEntry>? Opened;
    public event Action<ConnectionEntry>? Closed;
    public event Action<LogLevel, string>? Log;

    public IReadOnlyCollection<ConnectionEntry> Active => _active.Values.ToList();
    public long TotalSent => Interlocked.Read(ref _totalSent);
    public long TotalReceived => Interlocked.Read(ref _totalReceived);

    internal ConnectionEntry Begin(int pid, string? path, string target, string rule, string via)
    {
        var e = new ConnectionEntry
        {
            Id = Interlocked.Increment(ref _nextId),
            ProcessId = pid,
            ProcessPath = path,
            Target = target,
            RuleName = rule,
            Via = via,
        };
        _active[e.Id] = e;
        Opened?.Invoke(e);
        return e;
    }

    internal void CountSent(ConnectionEntry e, long n) { e.AddSent(n); Interlocked.Add(ref _totalSent, n); }
    internal void CountReceived(ConnectionEntry e, long n) { e.AddReceived(n); Interlocked.Add(ref _totalReceived, n); }

    internal void End(ConnectionEntry e, ConnectionState state, string? error = null)
    {
        e.State = state;
        e.Error = error;
        e.EndedAt = DateTime.Now;
        if (_active.TryRemove(e.Id, out _)) Closed?.Invoke(e);
    }

    public void Write(LogLevel level, string message)
    {
        if (level <= Level) Log?.Invoke(level, message);
    }
}
