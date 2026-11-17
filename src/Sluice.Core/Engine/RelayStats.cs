using System.Diagnostics;

namespace Sluice.Core.Engine;

/// <summary>
/// How the relay moves data: reads and bytes per direction, and how many reads were small. The relay's CPU cost
/// follows the number of reads more than the number of bytes, so the same download can cost very different CPU when
/// a tunnel delivers it in many small pieces. Cheap enough for every read (a few interlocked operations).
/// </summary>
public sealed class RelayStats
{
    /// <summary>A read smaller than this counts as small.</summary>
    public const int SmallRead = 4096;

    private sealed class Direction
    {
        public long Reads, Bytes, Small, IReads, IBytes, ISmall;

        public void Add(int n)
        {
            Interlocked.Increment(ref Reads);
            Interlocked.Add(ref Bytes, n);
            Interlocked.Increment(ref IReads);
            Interlocked.Add(ref IBytes, n);
            if (n < SmallRead)
            {
                Interlocked.Increment(ref Small);
                Interlocked.Increment(ref ISmall);
            }
        }

        public Summary Total => new(Interlocked.Read(ref Reads), Interlocked.Read(ref Bytes), Interlocked.Read(ref Small));

        public Summary Take() =>
            new(Interlocked.Exchange(ref IReads, 0), Interlocked.Exchange(ref IBytes, 0), Interlocked.Exchange(ref ISmall, 0));
    }

    public readonly record struct Summary(long Reads, long Bytes, long SmallReads)
    {
        public double AvgBytes => Reads == 0 ? 0 : (double)Bytes / Reads;

        public override string ToString() => Reads == 0
            ? "none"
            : $"{Reads:N0} reads, {Bytes / 1048576.0:0.0} MB, avg {AvgBytes / 1024:0.0} KB/read, {SmallReads:N0} under {SmallRead / 1024} KB";
    }

    private readonly Direction _down = new(), _up = new();
    private TimeSpan _lastCpu = CurrentCpu();

    /// <summary>A read from the upstream (data going to the app).</summary>
    public void Down(int bytes) => _down.Add(bytes);

    /// <summary>A read from the app (data going upstream).</summary>
    public void Up(int bytes) => _up.Add(bytes);

    public Summary TotalDown => _down.Total;
    public Summary TotalUp => _up.Total;

    /// <summary>
    /// The figures since the previous call and Sluice's CPU time over the same interval; null when nothing was relayed.
    /// </summary>
    public string? TakeInterval()
    {
        var down = _down.Take();
        var up = _up.Take();
        var cpu = CurrentCpu();
        var cpuMs = (cpu - _lastCpu).TotalMilliseconds;
        _lastCpu = cpu;
        if (down.Reads == 0 && up.Reads == 0) return null;
        var mb = (down.Bytes + up.Bytes) / 1048576.0;
        var perMb = mb >= 0.1 ? $", {cpuMs / mb:N0} ms per MB" : "";
        return $"down {down}; up {up}; Sluice CPU {cpuMs:N0} ms{perMb}";
    }

    private static TimeSpan CurrentCpu()
    {
        try { return Process.GetCurrentProcess().TotalProcessorTime; }
        catch (Exception) { return TimeSpan.Zero; }
    }
}
