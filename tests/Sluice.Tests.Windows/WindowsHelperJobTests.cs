using System.Diagnostics;
using System.Runtime.InteropServices;
using Sluice.Platform.Windows;

namespace Sluice.Tests;

/// <summary>
/// sing-box helpers end with Sluice (#56): <see cref="WindowsPlatform.TieChildProcess"/> puts them in a kill-on-close job
/// object, which Windows closes when Sluice ends however it ends; and <see cref="WindowsPlatform.FlushDnsCache"/>.
/// </summary>
public class WindowsHelperJobTests
{
    /// <summary>A harmless process that runs for about a minute unless something ends it.</summary>
    private static Process StartLongRunning() => Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    })!;

    private static void End(Process p)
    {
        try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
        p.Dispose();
    }

    [Fact]
    public void Closing_the_job_ends_the_processes_in_it()
    {
        // What happens to the shared job when Sluice ends, crashes or is killed: Windows closes its last handle.
        using var job = HelperJob.Create();
        Assert.NotNull(job);
        var p = StartLongRunning();
        try
        {
            Assert.True(job.Assign(p));
            Assert.False(p.HasExited);
            job.Dispose();
            Assert.True(p.WaitForExit(5000), "the process outlived its job");
        }
        finally { End(p); }
    }

    [Fact]
    public void TieChildProcess_puts_the_helper_in_Sluice_s_job()
    {
        var p = StartLongRunning();
        try
        {
            new WindowsPlatform().TieChildProcess(p);
            Assert.NotNull(HelperJob.Shared);
            Assert.True(IsProcessInJob(p.Handle, HelperJob.Shared.Handle, out var inJob));
            Assert.True(inJob);
        }
        finally { End(p); }
    }

    [Fact]
    public void A_process_that_has_ended_or_never_started_is_left_alone()
    {
        using var job = HelperJob.Create();
        Assert.NotNull(job);
        var ended = StartLongRunning();
        ended.Kill();
        ended.WaitForExit();
        Assert.False(job.Assign(ended));
        ended.Dispose();

        using var notStarted = new Process { StartInfo = new ProcessStartInfo("ping.exe") };
        Assert.False(job.Assign(notStarted));
        new WindowsPlatform().TieChildProcess(notStarted);   // no exception
    }

    [Fact]
    public void Assigning_after_the_job_is_closed_does_nothing()
    {
        var job = HelperJob.Create();
        Assert.NotNull(job);
        job.Dispose();
        var p = StartLongRunning();
        try
        {
            Assert.False(job.Assign(p));
            Assert.False(p.HasExited);
        }
        finally { End(p); }
    }

    [Fact]
    public void FlushDnsCache_never_throws()
    {
        new WindowsPlatform().FlushDnsCache();
        DnsCache.Flush();   // true with the DNS Client service running, false otherwise; never an exception
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
}
