using System.Runtime.InteropServices;

namespace Sluice.Platform.Linux.Native;

/// <summary>The few libc calls .NET has no managed API for (netlink sockets, group IDs, capabilities).</summary>
internal static partial class Libc
{
    public const int AF_NETLINK = 16;
    public const int SOCK_RAW = 3;
    public const int SOCK_CLOEXEC = 0x80000;
    public const int NETLINK_NETFILTER = 12;
    public const int SOL_SOCKET = 1;
    public const int SO_RCVBUF = 8;
    public const int SO_RCVTIMEO = 20;
    public const int SOL_NETLINK = 270;
    public const int NETLINK_NO_ENOBUFS = 5;
    public const int EINTR = 4, EAGAIN = 11, ENOBUFS = 105;

    [LibraryImport("libc", SetLastError = true)]
    public static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int bind(int fd, byte[] addr, int len);

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint send(int fd, byte[] buf, nint len, int flags);

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint recv(int fd, byte[] buf, nint len, int flags);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int setsockopt(int fd, int level, int name, byte[] value, int len);

    public const int NETLINK_SOCK_DIAG = 4;

    /// <summary>recv that also returns the sender's address (sockaddr_nl for netlink: family, pad, nl_pid, groups).</summary>
    [LibraryImport("libc", SetLastError = true)]
    public static partial nint recvfrom(int fd, byte[] buf, nint len, int flags, byte[] addr, ref int addrLen);

    public const int O_RDONLY = 0, O_DIRECTORY = 0x10000, O_CLOEXEC = 0x80000;

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    /// <summary>Directory entries as struct linux_dirent64 { u64 ino; s64 off; u16 reclen; u8 type; char name[]; }.</summary>
    [LibraryImport("libc", SetLastError = true)]
    public static partial nint getdents64(int fd, byte[] buffer, nint size);

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint readlinkat(int dirFd, byte[] path, byte[] buffer, nint size);

    public const int AT_FDCWD = -100;
    public const uint STATX_UID = 0x8;

    /// <summary>statx(2): <c>struct statx</c> has the same layout on every architecture; stx_uid is at offset 20.</summary>
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int statx(int dirFd, string path, int flags, uint mask, byte[] buffer);

    public const int AT_SYMLINK_NOFOLLOW = 0x100;

    /// <summary>realpath(3) with a buffer it allocates (free it with <see cref="free"/>); 0 when the path can't be resolved.</summary>
    [LibraryImport("libc", EntryPoint = "realpath", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint realpath(string path, nint resolved);

    [LibraryImport("libc")]
    public static partial void free(nint pointer);

    [LibraryImport("libc")]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int setresgid(uint rgid, uint egid, uint sgid);

    [LibraryImport("libc")]
    public static partial uint getegid();

    [LibraryImport("libc")]
    public static partial uint getgid();

    public const uint Unchanged = uint.MaxValue;   // (gid_t)-1

    public const int SIGTERM = 15;

    [LibraryImport("libc", SetLastError = true)]
    public static partial int kill(int pid, int sig);

    // ---------- capabilities ----------

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int capget(int* header, int* data);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int capset(int* header, int* data);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    private const int LinuxCapabilityVersion3 = 0x20080522;
    private const int PR_CAP_AMBIENT = 47, PR_CAP_AMBIENT_IS_SET = 1, PR_CAP_AMBIENT_RAISE = 2, PR_CAP_AMBIENT_LOWER = 3,
        PR_CAP_AMBIENT_CLEAR_ALL = 4;
    public const int CAP_NET_ADMIN = 12, CAP_SETGID = 6, CAP_SYS_PTRACE = 19, CAP_DAC_READ_SEARCH = 2;

    /// <summary>
    /// Clears the calling thread's ambient and inheritable sets, so the programs it starts next get no capabilities
    /// from Sluice. The launcher passes the capture capabilities to Sluice as ambient capabilities, and every thread
    /// created from a thread that has them has them too. Call it on the starting thread right before every program
    /// start; nft then gets <see cref="CAP_NET_ADMIN"/> alone through <see cref="RaiseAmbient"/>. Sluice's own
    /// (permitted, effective) capabilities stay. Harmless without capabilities and for root.
    /// </summary>
    public static unsafe bool ClearPassedOn(out string? problem)
    {
        problem = null;
        if (prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_CLEAR_ALL, 0, 0, 0) != 0) { problem = $"ambient clear: {Error(Marshal.GetLastPInvokeError())}"; return false; }
        var header = stackalloc int[2] { LinuxCapabilityVersion3, 0 };
        var data = stackalloc int[6];                              // 2 x { effective, permitted, inheritable }
        if (capget(header, data) != 0) { problem = $"capget: {Error(Marshal.GetLastPInvokeError())}"; return false; }
        if (data[2] == 0 && data[5] == 0) return true;            // nothing inheritable
        data[2] = data[5] = 0;
        if (capset(header, data) != 0) { problem = $"capset: {Error(Marshal.GetLastPInvokeError())}"; return false; }
        return true;
    }

    /// <summary>
    /// Lets programs this thread starts next keep <paramref name="cap"/>. Capabilities given to Sluice with setcap are
    /// not inherited by the commands it runs (nft); an "ambient" capability is. Needs the capability in the permitted
    /// set (it is, from setcap). No-op for root, whose children are privileged anyway. Affects only the calling thread,
    /// so call it right before starting the process on the same thread.
    /// </summary>
    public static unsafe bool RaiseAmbient(int cap, out string? problem)
    {
        problem = null;
        if (Environment.IsPrivilegedProcess) return true;
        var header = stackalloc int[2] { LinuxCapabilityVersion3, 0 };
        var data = stackalloc int[6];                              // 2 x { effective, permitted, inheritable }
        if (capget(header, data) != 0) { problem = $"capget: {Error(Marshal.GetLastPInvokeError())}"; return false; }
        int word = cap / 32, bit = 1 << (cap % 32);
        if ((data[word * 3 + 1] & bit) == 0) { problem = $"capability {cap} is not permitted for this process (setcap missing?)"; return false; }
        data[word * 3 + 2] |= bit;                                 // inheritable (required for ambient)
        if (capset(header, data) != 0) { problem = $"capset: {Error(Marshal.GetLastPInvokeError())}"; return false; }
        if (prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_RAISE, (nuint)cap, 0, 0) != 0) { problem = $"ambient raise: {Error(Marshal.GetLastPInvokeError())}"; return false; }
        return true;
    }

    /// <summary>
    /// Undoes <see cref="RaiseAmbient"/> on the calling thread: lowers <paramref name="cap"/> from the ambient set and
    /// clears it from the inheritable set, so the threads and programs this thread starts later don't get it (#99).
    /// Call it once the program that needed it has started. No-op for root, like <see cref="RaiseAmbient"/>.
    /// </summary>
    public static unsafe bool LowerAmbient(int cap, out string? problem)
    {
        problem = null;
        if (Environment.IsPrivilegedProcess) return true;
        if (prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_LOWER, (nuint)cap, 0, 0) != 0) { problem = $"ambient lower: {Error(Marshal.GetLastPInvokeError())}"; return false; }
        var header = stackalloc int[2] { LinuxCapabilityVersion3, 0 };
        var data = stackalloc int[6];
        if (capget(header, data) != 0) { problem = $"capget: {Error(Marshal.GetLastPInvokeError())}"; return false; }
        int word = cap / 32, bit = 1 << (cap % 32);
        if ((data[word * 3 + 2] & bit) == 0) return true;          // not inheritable: nothing more to clear
        data[word * 3 + 2] &= ~bit;
        if (capset(header, data) != 0) { problem = $"capset: {Error(Marshal.GetLastPInvokeError())}"; return false; }
        return true;
    }

    /// <summary>Whether <paramref name="cap"/> is in the calling thread's ambient set.</summary>
    public static bool IsAmbient(int cap) => prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_IS_SET, (nuint)cap, 0, 0) == 1;

    /// <summary>Whether <paramref name="cap"/> is in the calling thread's inheritable set (false if it can't be read).</summary>
    public static unsafe bool IsInheritable(int cap)
    {
        var header = stackalloc int[2] { LinuxCapabilityVersion3, 0 };
        var data = stackalloc int[6];
        return capget(header, data) == 0 && (data[cap / 32 * 3 + 2] & (1 << (cap % 32))) != 0;
    }

    /// <summary>Whether <paramref name="cap"/> is in the calling thread's permitted set (false if it can't be read).</summary>
    public static unsafe bool IsPermitted(int cap)
    {
        var header = stackalloc int[2] { LinuxCapabilityVersion3, 0 };
        var data = stackalloc int[6];
        return capget(header, data) == 0 && (data[cap / 32 * 3 + 1] & (1 << (cap % 32))) != 0;
    }

    public static string Error(int errno) => $"{Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})";
}
