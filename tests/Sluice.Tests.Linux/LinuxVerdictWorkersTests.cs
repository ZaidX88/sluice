using System.Collections.Concurrent;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>
/// Linux capture decides queued packets on worker threads (#83): every packet gets exactly one verdict (also when the
/// hand-off overflows or the workers stop), a slow decision doesn't hold up the others, and verdicts are safe to give
/// from several threads.
/// </summary>
public class LinuxVerdictWorkersTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static NfQueue.Packet Packet(uint id) => new(id, default);

    private sealed class Verdicts
    {
        public readonly ConcurrentDictionary<uint, (uint Verdict, uint? Mark)> Given = new();
        public readonly ConcurrentBag<uint> Duplicates = [];

        public void Give(uint id, uint verdict, uint? mark)
        {
            if (!Given.TryAdd(id, (verdict, mark))) Duplicates.Add(id);
        }

        public bool WaitFor(int count)
        {
            var until = DateTime.UtcNow + Wait;
            while (Given.Count < count && DateTime.UtcNow < until) Thread.Sleep(5);
            return Given.Count >= count;
        }
    }

    [Fact]
    public void Every_packet_gets_one_verdict_when_the_hand_off_overflows()
    {
        var v = new Verdicts();
        using var release = new ManualResetEventSlim();
        using var deciding = new ManualResetEventSlim();
        var errors = new ConcurrentBag<Exception>();
        var workers = new VerdictWorkers(_ => { deciding.Set(); release.Wait(Wait); return (NfQueue.Accept, 7u); }, v.Give, errors.Add,
            workers: 1, capacity: 2);
        var overflows = 0;
        workers.Overflow += () => Interlocked.Increment(ref overflows);

        workers.Post(Packet(1));
        Assert.True(deciding.Wait(Wait));                               // the only worker is busy with packet 1
        for (uint id = 2; id <= 100; id++) workers.Post(Packet(id));   // 2 queued; the other 97 overflow
        Assert.Equal(97, v.Given.Count);                                // overflowed packets are accepted at once
        Assert.All(v.Given.Values, x => Assert.Equal((NfQueue.Accept, (uint?)null), x));
        Assert.Equal(1, overflows);                                     // one log line, not one per packet

        release.Set();
        workers.Stop(Wait);
        Assert.Equal(100, v.Given.Count);
        Assert.Empty(v.Duplicates);
        Assert.Empty(errors);
        Assert.Equal(97, workers.Overflowed);
        Assert.Equal(3, v.Given.Values.Count(x => x.Mark == 7));       // the decided ones
    }

    [Fact]
    public void A_slow_decision_does_not_hold_up_the_others()
    {
        var v = new Verdicts();
        using var release = new ManualResetEventSlim();
        var workers = new VerdictWorkers(p =>
        {
            if (p.Id == 1) release.Wait(Wait);                          // a program Sluice hasn't seen: a long /proc scan
            return (NfQueue.Accept, null);
        }, v.Give, _ => { });

        workers.Post(Packet(1));
        for (uint id = 2; id <= 20; id++) workers.Post(Packet(id));

        Assert.True(v.WaitFor(19));
        Assert.False(v.Given.ContainsKey(1));
        release.Set();
        workers.Stop(Wait);
        Assert.Equal(20, v.Given.Count);
        Assert.Equal(0, workers.Overflowed);
    }

    [Fact]
    public void Decisions_run_on_several_threads_at_once()
    {
        var v = new Verdicts();
        using var together = new Barrier(VerdictWorkers.DefaultWorkers);
        var met = 0;
        var workers = new VerdictWorkers(_ =>
        {
            if (together.SignalAndWait(Wait)) Interlocked.Increment(ref met);
            return (NfQueue.Accept, null);
        }, v.Give, _ => { });

        for (uint id = 1; id <= VerdictWorkers.DefaultWorkers; id++) workers.Post(Packet(id));
        workers.Stop(Wait);

        Assert.Equal(VerdictWorkers.DefaultWorkers, met);
    }

    [Fact]
    public void A_failing_decision_lets_the_packet_through()
    {
        var v = new Verdicts();
        var errors = new ConcurrentBag<Exception>();
        var workers = new VerdictWorkers(_ => throw new InvalidOperationException("boom"), v.Give, errors.Add);

        workers.Post(Packet(1));
        workers.Stop(Wait);

        Assert.Equal((NfQueue.Accept, (uint?)null), v.Given[1]);
        Assert.Single(errors);
    }

    [Fact]
    public void Stop_gives_queued_packets_their_verdicts_and_later_packets_pass()
    {
        var v = new Verdicts();
        var workers = new VerdictWorkers(_ => { Thread.Sleep(2); return (NfQueue.Drop, null); }, v.Give, _ => { },
            workers: 2, capacity: 64);

        for (uint id = 1; id <= 50; id++) workers.Post(Packet(id));
        workers.Stop(Wait);
        Assert.Equal(50, v.Given.Count);
        Assert.All(v.Given.Values, x => Assert.Equal(NfQueue.Drop, x.Verdict));   // all decided, none cut short

        workers.Post(Packet(51));                                       // after Stop: accepted at once, not lost
        Assert.Equal((NfQueue.Accept, (uint?)null), v.Given[51]);
        Assert.Empty(v.Duplicates);
    }

    [LinuxFact]
    public void Sequence_numbers_stay_unique_across_threads_and_late_verdicts_are_quiet()
    {
        var q = NfQueue.ForTests();
        var seen = new ConcurrentDictionary<uint, byte>();
        var duplicates = 0;
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 10_000; i++)
                if (!seen.TryAdd(q.NextSeq(), 0)) Interlocked.Increment(ref duplicates);
        });
        Assert.Equal(0, duplicates);
        Assert.Equal(80_000, seen.Count);

        q.Dispose();
        q.Verdict(1, NfQueue.Accept);                                   // after close: dropped, no exception
    }
}
