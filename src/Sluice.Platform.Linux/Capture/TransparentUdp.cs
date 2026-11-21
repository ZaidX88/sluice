using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Sluice.Platform.Linux.Native;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// Sluice's end of the UDP tproxy (<see cref="NftRules"/>, <see cref="PolicyRoutes"/>): one transparent UDP socket per
/// address family on the same port, receiving the datagrams of the flows Sluice takes with their original destination
/// (<c>IP_RECVORIGDSTADDR</c>), since tproxy leaves the packet unchanged. Replies go out from a transparent socket bound
/// to the address the app sent to, so the app sees them come from there (<see cref="SendToApp"/>). The sockets are
/// created by Sluice, so they carry its network group and nftables leaves their packets alone.
/// </summary>
internal sealed partial class TransparentUdp : IDisposable
{
    private const int AF_INET = 2, AF_INET6 = 10, SOCK_DGRAM = 2;
    private const int SOL_IP = 0, SOL_IPV6 = 41;
    private const int SO_REUSEADDR = 2;
    private const int IP_TRANSPARENT = 19, IP_RECVORIGDSTADDR = 20, IP_ORIGDSTADDR = 20;
    private const int IPV6_V6ONLY = 26, IPV6_RECVORIGDSTADDR = 74, IPV6_ORIGDSTADDR = 74, IPV6_TRANSPARENT = 75;
    private const int EADDRINUSE = 98, EAFNOSUPPORT = 97, EBADF = 9;

    /// <summary>A datagram an app sent: from its socket to where it sent it, and the payload (valid during the call only).</summary>
    public delegate void DatagramHandler(IPEndPoint app, IPEndPoint destination, ReadOnlyMemory<byte> payload);

    private sealed class ReplySocket(int fd)
    {
        public readonly int Fd = fd;
        public long LastUsedTicks = DateTime.UtcNow.Ticks;
        public bool Closed;
    }

    private readonly int _fd4, _fd6;
    private readonly DatagramHandler _onDatagram;
    private readonly Action<string> _onError;
    private readonly ConcurrentDictionary<IPEndPoint, ReplySocket> _replies = new();
    private readonly List<Thread> _threads = [];
    private volatile bool _stopping;
    private int _sendErrorReported;

    public int Port { get; }
    /// <summary>Why there is no IPv6 socket (the system has no IPv6), or null.</summary>
    public string? Ipv6Problem { get; }

    private TransparentUdp(int fd4, int fd6, int port, string? ipv6Problem, DatagramHandler onDatagram, Action<string> onError)
    {
        _fd4 = fd4;
        _fd6 = fd6;
        Port = port;
        Ipv6Problem = ipv6Problem;
        _onDatagram = onDatagram;
        _onError = onError;
    }

    /// <summary>Opens the receiving sockets on a free port (the same for IPv4 and IPv6). Needs CAP_NET_ADMIN.</summary>
    public static TransparentUdp Open(DatagramHandler onDatagram, Action<string> onError)
    {
        for (var attempt = 0; ; attempt++)
        {
            var fd4 = OpenReceiver(AF_INET, 0);
            int port;
            try { port = LocalPort(fd4); }
            catch { Libc.close(fd4); throw; }
            try
            {
                var fd6 = OpenReceiver(AF_INET6, port);
                return new TransparentUdp(fd4, fd6, port, null, onDatagram, onError);
            }
            catch (SocketError6 ex) when (ex.Errno == EAFNOSUPPORT)
            {
                return new TransparentUdp(fd4, -1, port, "no IPv6 on this system", onDatagram, onError);
            }
            catch (SocketError6 ex) when (ex.Errno == EADDRINUSE && attempt < 5)
            {
                Libc.close(fd4);    // the port is taken for IPv6: try another one
            }
            catch
            {
                Libc.close(fd4);
                throw;
            }
        }
    }

    private sealed class SocketError6(string message, int errno) : InvalidOperationException(message)
    {
        public int Errno { get; } = errno;
    }

    private static int OpenReceiver(int family, int port)
    {
        var fd = socket(family, SOCK_DGRAM | Libc.SOCK_CLOEXEC, 0);
        if (fd < 0) throw Fail(family, "open", Marshal.GetLastPInvokeError());
        try
        {
            SetInt(fd, Libc.SOL_SOCKET, SO_REUSEADDR, 1);
            SetInt(fd, Libc.SOL_SOCKET, Libc.SO_RCVBUF, 4 * 1024 * 1024);
            if (family == AF_INET6) SetInt(fd, SOL_IPV6, IPV6_V6ONLY, 1);
            if (!TrySetInt(fd, family == AF_INET ? SOL_IP : SOL_IPV6, family == AF_INET ? IP_TRANSPARENT : IPV6_TRANSPARENT, 1, out var err))
                throw Fail(family, "make transparent", err, " (needs CAP_NET_ADMIN)");
            SetInt(fd, family == AF_INET ? SOL_IP : SOL_IPV6, family == AF_INET ? IP_RECVORIGDSTADDR : IPV6_RECVORIGDSTADDR, 1);
            var tv = new byte[16];                        // struct timeval { sec = 0, usec = 500 ms }: lets Dispose stop the loops
            BinaryPrimitives.WriteInt64LittleEndian(tv.AsSpan(8), 500_000);
            Libc.setsockopt(fd, Libc.SOL_SOCKET, Libc.SO_RCVTIMEO, tv, tv.Length);
            var addr = Sockaddr(new IPEndPoint(family == AF_INET ? IPAddress.Any : IPAddress.IPv6Any, port));
            if (Libc.bind(fd, addr, addr.Length) != 0) throw Fail(family, "bind", Marshal.GetLastPInvokeError());
            return fd;
        }
        catch
        {
            Libc.close(fd);
            throw;
        }
    }

    private static SocketError6 Fail(int family, string what, int errno, string hint = "") =>
        new($"Could not {what} the transparent {(family == AF_INET ? "IPv4" : "IPv6")} UDP socket: {Libc.Error(errno)}{(errno == 1 ? hint : "")}", errno);

    private static int LocalPort(int fd)
    {
        var addr = new byte[128];
        var len = addr.Length;
        if (getsockname(fd, addr, ref len) != 0) throw new InvalidOperationException($"getsockname: {Libc.Error(Marshal.GetLastPInvokeError())}");
        return BinaryPrimitives.ReadUInt16BigEndian(addr.AsSpan(2));
    }

    /// <summary>Starts one receiving thread per socket.</summary>
    public void Start()
    {
        foreach (var fd in new[] { _fd4, _fd6 })
        {
            if (fd < 0) continue;
            var t = new Thread(() => ReceiveLoop(fd)) { IsBackground = true, Name = "Sluice UDP tproxy" };
            _threads.Add(t);
            t.Start();
        }
    }

    private unsafe void ReceiveLoop(int fd)
    {
        var data = new byte[65536];
        var name = new byte[128];
        var control = new byte[256];
        while (!_stopping)
        {
            nint n;
            int nameLen, controlLen;
            fixed (byte* pData = data, pName = name, pControl = control)
            {
                var iov = new IoVec { Base = (nint)pData, Len = (nuint)data.Length };
                var msg = new MsgHdr
                {
                    Name = (nint)pName, NameLen = (uint)name.Length, Iov = (nint)(&iov), IovLen = 1,
                    Control = (nint)pControl, ControlLen = (nuint)control.Length,
                };
                n = recvmsg(fd, &msg, 0);
                nameLen = (int)msg.NameLen;
                controlLen = (int)msg.ControlLen;
            }
            if (n < 0)
            {
                var err = Marshal.GetLastPInvokeError();
                if (_stopping || err == EBADF) return;
                if (err is Libc.EAGAIN or Libc.EINTR or Libc.ENOBUFS) continue;   // timeout: check _stopping
                _onError($"Receiving tunneled UDP failed: {Libc.Error(err)}");
                Thread.Sleep(100);
                continue;
            }
            if (ParseSockaddr(name.AsSpan(0, Math.Min(nameLen, name.Length))) is not { } app) continue;
            if (!TryParseOriginalDestination(control.AsSpan(0, Math.Min(controlLen, control.Length)), out var destination)) continue;
            try { _onDatagram(app, destination, data.AsMemory(0, (int)n)); }
            catch (Exception ex) { _onError($"Handling a tunneled UDP datagram failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Sends <paramref name="payload"/> to <paramref name="app"/> from <paramref name="from"/> (any address, through a
    /// transparent socket bound to it, opened on first use and kept while it is used).
    /// </summary>
    public unsafe void SendToApp(IPEndPoint from, IPEndPoint app, ReadOnlySpan<byte> payload)
    {
        if (_stopping || from.AddressFamily != app.AddressFamily) return;
        var r = ReplyFor(from);
        if (r is null) return;
        var to = Sockaddr(app);
        lock (r)
        {
            if (r.Closed) return;
            Interlocked.Exchange(ref r.LastUsedTicks, DateTime.UtcNow.Ticks);
            fixed (byte* p = payload)
            {
                if (sendto(r.Fd, p, payload.Length, 0, to, to.Length) < 0) ReportSendError(from, Marshal.GetLastPInvokeError());
            }
        }
    }

    private ReplySocket? ReplyFor(IPEndPoint from)
    {
        if (_replies.TryGetValue(from, out var r) && !r.Closed) return r;
        lock (_replies)
        {
            if (_replies.TryGetValue(from, out r) && !r.Closed) return r;
            var family = from.AddressFamily == AddressFamily.InterNetworkV6 ? AF_INET6 : AF_INET;
            var fd = socket(family, SOCK_DGRAM | Libc.SOCK_CLOEXEC, 0);
            if (fd < 0)
            {
                ReportSendError(from, Marshal.GetLastPInvokeError());
                return null;
            }
            SetInt(fd, Libc.SOL_SOCKET, SO_REUSEADDR, 1);
            if (family == AF_INET6) SetInt(fd, SOL_IPV6, IPV6_V6ONLY, 1);
            SetInt(fd, family == AF_INET ? SOL_IP : SOL_IPV6, family == AF_INET ? IP_TRANSPARENT : IPV6_TRANSPARENT, 1);
            var addr = Sockaddr(from);
            if (Libc.bind(fd, addr, addr.Length) != 0)
            {
                ReportSendError(from, Marshal.GetLastPInvokeError());
                Libc.close(fd);
                return null;
            }
            r = new ReplySocket(fd);
            _replies[from] = r;
            return r;
        }
    }

    private void ReportSendError(IPEndPoint from, int errno)
    {
        if (Interlocked.Exchange(ref _sendErrorReported, 1) == 0)
            _onError($"Could not deliver a tunneled UDP reply from {from}: {Libc.Error(errno)}");
    }

    /// <summary>Closes reply sockets unused for longer than <paramref name="idle"/>; returns how many.</summary>
    public int SweepReplies(TimeSpan idle)
    {
        var cutoff = DateTime.UtcNow.Ticks - idle.Ticks;
        var closed = 0;
        foreach (var (from, r) in _replies)
        {
            if (Interlocked.Read(ref r.LastUsedTicks) >= cutoff) continue;
            lock (_replies)
            {
                if (!_replies.TryRemove(new KeyValuePair<IPEndPoint, ReplySocket>(from, r))) continue;
            }
            Close(r);
            closed++;
        }
        return closed;
    }

    public int ReplySockets => _replies.Count;

    private static void Close(ReplySocket r)
    {
        lock (r)
        {
            if (r.Closed) return;
            r.Closed = true;
            Libc.close(r.Fd);
        }
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        foreach (var t in _threads) t.Join(2000);     // recvmsg returns within 0.5 s
        if (_fd4 >= 0) Libc.close(_fd4);
        if (_fd6 >= 0) Libc.close(_fd6);
        lock (_replies)
        {
            foreach (var r in _replies.Values) Close(r);
            _replies.Clear();
        }
    }

    /// <summary>sockaddr_in / sockaddr_in6 for <paramref name="ep"/>.</summary>
    internal static byte[] Sockaddr(IPEndPoint ep)
    {
        if (ep.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = new byte[28];                     // family, port, flowinfo, addr[16], scope_id
            BinaryPrimitives.WriteUInt16LittleEndian(b, AF_INET6);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)ep.Port);
            ep.Address.GetAddressBytes().CopyTo(b, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), (uint)ep.Address.ScopeId);
            return b;
        }
        var a = new byte[16];                         // family, port, addr[4], zero[8]
        BinaryPrimitives.WriteUInt16LittleEndian(a, AF_INET);
        BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(2), (ushort)ep.Port);
        ep.Address.GetAddressBytes().CopyTo(a, 4);
        return a;
    }

    /// <summary>The endpoint in a sockaddr_in / sockaddr_in6, or null for another family or a short buffer.</summary>
    internal static IPEndPoint? ParseSockaddr(ReadOnlySpan<byte> b)
    {
        if (b.Length < 2) return null;
        var family = BinaryPrimitives.ReadUInt16LittleEndian(b);
        if (family == AF_INET && b.Length >= 8)
            return new IPEndPoint(new IPAddress(b.Slice(4, 4)), BinaryPrimitives.ReadUInt16BigEndian(b[2..]));
        if (family == AF_INET6 && b.Length >= 24)
        {
            var ip = new IPAddress(b.Slice(8, 16));
            if (b.Length >= 28 && BinaryPrimitives.ReadUInt32LittleEndian(b[24..]) is var scope and > 0) ip.ScopeId = scope;
            return new IPEndPoint(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, BinaryPrimitives.ReadUInt16BigEndian(b[2..]));
        }
        return null;
    }

    /// <summary>
    /// Finds IP_ORIGDSTADDR / IPV6_ORIGDSTADDR in recvmsg's control data (64-bit cmsghdr: size_t len, int level,
    /// int type, then the data, each message aligned to 8 bytes).
    /// </summary>
    internal static bool TryParseOriginalDestination(ReadOnlySpan<byte> control, out IPEndPoint destination)
    {
        destination = null!;
        var off = 0;
        while (off + 16 <= control.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt64LittleEndian(control[off..]);
            var level = BinaryPrimitives.ReadInt32LittleEndian(control[(off + 8)..]);
            var type = BinaryPrimitives.ReadInt32LittleEndian(control[(off + 12)..]);
            if (len < 16 || off + len > control.Length) return false;
            if ((level == SOL_IP && type == IP_ORIGDSTADDR) || (level == SOL_IPV6 && type == IPV6_ORIGDSTADDR))
            {
                if (ParseSockaddr(control.Slice(off + 16, len - 16)) is { } ep)
                {
                    destination = ep;
                    return true;
                }
            }
            off += (len + 7) & ~7;
        }
        return false;
    }

    private static void SetInt(int fd, int level, int name, int value) => TrySetInt(fd, level, name, value, out _);

    private static bool TrySetInt(int fd, int level, int name, int value, out int errno)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        var ok = Libc.setsockopt(fd, level, name, b, 4) == 0;
        errno = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public nint Base;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public nint Name;
        public uint NameLen;
        public nint Iov;
        public nuint IovLen;
        public nint Control;
        public nuint ControlLen;
        public int Flags;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getsockname(int fd, byte[] addr, ref int len);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint recvmsg(int fd, MsgHdr* msg, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint sendto(int fd, byte* buf, nint len, int flags, byte[] addr, int addrLen);
}
