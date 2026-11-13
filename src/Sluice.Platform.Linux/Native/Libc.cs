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

    [LibraryImport("libc")]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int setresgid(uint rgid, uint egid, uint sgid);

    [LibraryImport("libc")]
    public static partial uint getegid();

    [LibraryImport("libc")]
    public static partial uint getgid();

    public const uint Unchanged = uint.MaxValue;   // (gid_t)-1

    // ---------- capabilities ----------

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int capget(int* header, int* data);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int capset(int* header, int* data);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    private const int LinuxCapabilityVersion3 = 0x20080522;
    private const int PR_CAP_AMBIENT = 47, PR_CAP_AMBIENT_RAISE = 2;
    public const int CAP_NET_ADMIN = 12;

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

    public static string Error(int errno) => $"{Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})";
}
