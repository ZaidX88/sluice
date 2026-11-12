using System.Runtime.InteropServices;

namespace Sluice.Platform.Linux.Native;

/// <summary>The few libc calls .NET has no managed API for (netlink sockets, group IDs).</summary>
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

    public const uint Unchanged = uint.MaxValue;   // (gid_t)-1

    public static string Error(int errno) => $"{Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})";
}
