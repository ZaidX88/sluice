using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sluice.Platform.Windows;

/// <summary>
/// A kill-on-close job object: the processes in it end when its last handle closes. Sluice keeps one (<see cref="Shared"/>)
/// open for its whole life and never closes it, so Windows closes it when Sluice ends, however it ends (a normal exit,
/// a crash, Task Manager), and the sing-box helpers in it end too.
/// </summary>
internal sealed class HelperJob : IDisposable
{
    private const uint KillOnJobClose = 0x2000;              // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    private const int ExtendedLimitInformation = 9;          // JobObjectExtendedLimitInformation

    private static readonly Lazy<HelperJob?> SharedJob = new(Create);

    /// <summary>The job the helpers go in, created on first use. Null when Windows wouldn't create it.</summary>
    public static HelperJob? Shared => SharedJob.Value;

    /// <summary>The job's handle.</summary>
    public IntPtr Handle { get; private set; }

    private HelperJob(IntPtr handle) => Handle = handle;

    /// <summary>A new kill-on-close job, or null when it can't be created or set up.</summary>
    public static HelperJob? Create()
    {
        var h = CreateJobObject(IntPtr.Zero, null);
        if (h == IntPtr.Zero) return null;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (SetInformationJobObject(h, ExtendedLimitInformation, ptr, (uint)size)) return new HelperJob(h);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        CloseHandle(h);   // a job without kill-on-close would not end the helpers
        return null;
    }

    /// <summary>Puts <paramref name="process"/> in this job. False when it has exited, wasn't started, or Windows refused.
    /// Never throws.</summary>
    public bool Assign(Process process)
    {
        if (Handle == IntPtr.Zero) return false;
        try
        {
            if (process.HasExited) return false;
            return AssignProcessToJobObject(Handle, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;   // not started, or no access to it
        }
    }

    /// <summary>Closes the job, which ends every process in it. Only tests do this: the shared job is never closed.</summary>
    public void Dispose()
    {
        var h = Handle;
        Handle = IntPtr.Zero;
        if (h != IntPtr.Zero) CloseHandle(h);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
