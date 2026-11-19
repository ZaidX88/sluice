using System.Collections.Concurrent;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// Decides queued packets on a few worker threads, so one slow decision (a program Sluice hasn't seen yet) doesn't hold
/// up the new connections of every other program behind it. The NFQUEUE thread only receives and hands packets over
/// (<see cref="Post"/>); verdicts go out by packet id, in any order.
/// <para>
/// The hand-off is bounded. When it is full, the packet is accepted at once without a mark (fail-open, like the
/// kernel queue's own <c>FAIL_OPEN</c>): the receive loop never blocks, and every packet always gets a verdict.
/// </para>
/// </summary>
internal sealed class VerdictWorkers
{
    public const int DefaultWorkers = 4;       // as on Windows (docs/windows/FIXES.md §3)
    public const int DefaultCapacity = 1024;

    /// <summary>Decides one packet: its verdict and, for an accepted packet, an optional new mark.</summary>
    public delegate (uint Verdict, uint? Mark) Decide(NfQueue.Packet packet);

    /// <summary>Gives packet <paramref name="id"/> its verdict (<see cref="NfQueue.Verdict"/>).</summary>
    public delegate void Give(uint id, uint verdict, uint? mark);

    private readonly BlockingCollection<NfQueue.Packet> _pending;
    private readonly Decide _decide;
    private readonly Give _give;
    private readonly Action<Exception> _onError;
    private readonly Thread[] _threads;
    private long _overflowed;

    public VerdictWorkers(Decide decide, Give give, Action<Exception> onError,
        int workers = DefaultWorkers, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _decide = decide;
        _give = give;
        _onError = onError;
        _pending = new BlockingCollection<NfQueue.Packet>(new ConcurrentQueue<NfQueue.Packet>(), capacity);
        _threads = new Thread[workers];
        for (var i = 0; i < workers; i++)
        {
            _threads[i] = new Thread(Work) { IsBackground = true, Name = $"Sluice verdicts {i + 1}" };
            _threads[i].Start();
        }
    }

    /// <summary>Packets accepted unmarked because the workers were behind (or already stopped).</summary>
    public long Overflowed => Interlocked.Read(ref _overflowed);

    /// <summary>Raised once each time the hand-off fills up after being free (worth one log line, not one per packet).</summary>
    public event Action? Overflow;
    private int _overflowing;

    /// <summary>Hands a packet to the workers; never blocks. A packet that doesn't fit is accepted at once.</summary>
    public void Post(NfQueue.Packet packet)
    {
        bool added;
        try { added = _pending.TryAdd(packet); }
        catch (InvalidOperationException) { added = false; }   // stopped: no more decisions
        if (added)
        {
            Volatile.Write(ref _overflowing, 0);
            return;
        }
        Interlocked.Increment(ref _overflowed);
        if (Interlocked.Exchange(ref _overflowing, 1) == 0) Overflow?.Invoke();
        _give(packet.Id, NfQueue.Accept, null);
    }

    private void Work()
    {
        foreach (var p in _pending.GetConsumingEnumerable())
        {
            uint verdict = NfQueue.Accept;
            uint? mark = null;
            try { (verdict, mark) = _decide(p); }
            catch (Exception ex) { _onError(ex); verdict = NfQueue.Accept; mark = null; }
            try { _give(p.Id, verdict, mark); }
            catch (Exception ex) { _onError(ex); }
        }
    }

    /// <summary>
    /// Takes no more packets, lets the workers finish what is queued (each packet gets its verdict) and waits up to
    /// <paramref name="wait"/> for them. Packets still queued after that are accepted unmarked. Call before closing the
    /// queue's socket.
    /// </summary>
    public void Stop(TimeSpan wait)
    {
        _pending.CompleteAdding();
        var until = DateTime.UtcNow + wait;
        foreach (var t in _threads)
        {
            var left = until - DateTime.UtcNow;
            t.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }
        while (_pending.TryTake(out var p))
        {
            try { _give(p.Id, NfQueue.Accept, null); }
            catch (Exception ex) { _onError(ex); }
        }
    }
}
