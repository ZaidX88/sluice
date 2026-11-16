using System.Diagnostics;

namespace Sluice.Core.Interception;

/// <summary>
/// Count, average and maximum of a duration measured in <see cref="Stopwatch"/> ticks, both since start and since the
/// last <see cref="TakeInterval"/>. Cheap enough for the packet thread (a few interlocked operations).
/// </summary>
public sealed class LatencyStats
{
    private long _count, _sum, _max, _iCount, _iSum, _iMax, _slow;

    /// <summary>Samples above this many milliseconds are counted as slow.</summary>
    public double SlowMs { get; init; } = 1;

    public readonly record struct Summary(long Count, double AvgMs, double MaxMs)
    {
        public override string ToString() => Count == 0 ? "none" : $"avg {AvgMs:0.###} ms, max {MaxMs:0.###} ms ({Count:N0})";
    }

    public void Add(long ticks)
    {
        if (ticks < 0) return;
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sum, ticks);
        Interlocked.Increment(ref _iCount);
        Interlocked.Add(ref _iSum, ticks);
        Raise(ref _max, ticks);
        Raise(ref _iMax, ticks);
        if (ToMs(ticks) > SlowMs) Interlocked.Increment(ref _slow);
    }

    public long Slow => Interlocked.Read(ref _slow);

    public Summary Total => Make(Interlocked.Read(ref _count), Interlocked.Read(ref _sum), Interlocked.Read(ref _max));

    /// <summary>The figures since the previous call, then starts a new interval.</summary>
    public Summary TakeInterval() =>
        Make(Interlocked.Exchange(ref _iCount, 0), Interlocked.Exchange(ref _iSum, 0), Interlocked.Exchange(ref _iMax, 0));

    private static Summary Make(long count, long sum, long max) =>
        new(count, count == 0 ? 0 : ToMs(sum) / count, ToMs(max));

    public static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static void Raise(ref long target, long value)
    {
        var cur = Interlocked.Read(ref target);
        while (value > cur)
        {
            var seen = Interlocked.CompareExchange(ref target, value, cur);
            if (seen == cur) return;
            cur = seen;
        }
    }
}
