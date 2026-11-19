using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Sluice.Platform.Linux.Native;

/// <summary>
/// Finds the socket behind a connection through the kernel's socket diagnostics (<c>NETLINK_SOCK_DIAG</c>,
/// <c>inet_diag</c>), instead of reading and parsing all of <c>/proc/net/tcp</c> and <c>tcp6</c> for every new
/// connection (issue #82). A TCP connection is asked for by its four addresses (one answer); an unconnected UDP socket
/// has no remote end, so UDP sockets are listed and matched by local port. Each answer carries the socket's inode and
/// its owner's uid. Message layouts are from linux/sock_diag.h and linux/inet_diag.h.
/// <para>
/// Safe for several callers at once: each lookup borrows its own netlink socket from a small pool, so no caller waits
/// for another's answer. Answers are accepted only from the kernel (<c>nl_pid</c> 0) and only for the request's
/// sequence number. A socket that timed out is closed rather than reused (a late answer would confuse the next
/// lookup), and after a few timeouts in a row lookups are skipped for a while (callers fall back to /proc).
/// </para>
/// </summary>
internal sealed class SockDiag : IDisposable
{
    private const ushort NLM_F_REQUEST = 1, NLM_F_DUMP = 0x300;
    private const ushort NLMSG_ERROR = 2, NLMSG_DONE = 3, SOCK_DIAG_BY_FAMILY = 20;
    private const byte AfInet = 2, AfInet6 = 10, IpProtoTcp = 6, IpProtoUdp = 17;
    private const int ReqLength = 56, MsgLength = 72;           // inet_diag_req_v2, inet_diag_msg
    private const int TimeoutMs = 100, TimeoutsBeforePause = 3;
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(30);
    public const int ENOENT = 2;

    private readonly ConcurrentBag<int> _idle = new();
    private int _seq, _timeouts;
    private long _pausedUntilTicks;
    private volatile bool _closed;

    /// <summary>The socket's inode (0 for a socket without one, e.g. a closing connection) and its owner's uid.</summary>
    public readonly record struct Socket(ulong Inode, uint Uid);

    /// <summary>Told once when lookups are paused after timeouts (the caller logs it).</summary>
    public Action<string>? Problem { get; set; }

    private SockDiag() { }

    /// <summary>Opens and checks the netlink socket; null with <paramref name="problem"/> when sock_diag can't be used.</summary>
    public static SockDiag? TryOpen(out string? problem)
    {
        var d = new SockDiag();
        var fd = OpenSocket(out problem);
        if (fd < 0) return null;
        d._idle.Add(fd);
        // Look up a connection that certainly exists: without the inet_diag / tcp_diag handler (some containers) the
        // kernel answers "no such entry" for every socket, which a lookup of a made-up one couldn't tell apart.
        try
        {
            using var listener = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            using var client = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(listener.LocalEndPoint!);
            if (!d.TryFindTcp((IPEndPoint)client.LocalEndPoint!, (IPEndPoint)client.RemoteEndPoint!, out var probe) || probe.Inode == 0)
            {
                problem = "sock_diag doesn't find TCP sockets (no inet_diag / tcp_diag support?)";
                d.Dispose();
                return null;
            }
        }
        catch (SocketException ex)
        {
            problem = $"cannot check sock_diag: {ex.Message}";
            d.Dispose();
            return null;
        }
        return d;
    }

    /// <summary>The TCP socket with these addresses (as the app's socket sees them: local → remote).</summary>
    public bool TryFindTcp(IPEndPoint local, IPEndPoint remote, out Socket socket)
    {
        socket = default;
        var v6 = local.AddressFamily == AddressFamily.InterNetworkV6 && !local.Address.IsIPv4MappedToIPv6;
        var seq = (uint)Interlocked.Increment(ref _seq);
        return TryLookUp(Request(SOCK_DIAG_BY_FAMILY, NLM_F_REQUEST, seq, v6 ? AfInet6 : AfInet, IpProtoTcp, local, remote), null,
            out socket, out _);
    }

    /// <summary>
    /// The UDP socket bound to <paramref name="local"/>'s port: one bound to that exact address if there is one,
    /// otherwise one bound to the wildcard address (0.0.0.0 / ::), as /proc/net/udp matching did.
    /// </summary>
    public bool TryFindUdp(IPEndPoint local, out Socket socket)
    {
        socket = default;
        Socket? wildcard = null;
        foreach (var family in new[] { AfInet, AfInet6 })
        {
            var seq = (uint)Interlocked.Increment(ref _seq);
            Socket? exact = null;
            var any = new IPEndPoint(family == AfInet ? IPAddress.Any : IPAddress.IPv6Any, 0);
            var ok = TryLookUp(Request(SOCK_DIAG_BY_FAMILY, NLM_F_REQUEST | NLM_F_DUMP, seq, family, IpProtoUdp, any, any),
                m =>
                {
                    if (m.Local.Port != local.Port || m.Socket.Inode == 0) return;
                    if (SameAddress(m.Local.Address, local.Address)) exact ??= m.Socket;
                    else if (IsAny(m.Local.Address)) wildcard ??= m.Socket;
                }, out _, out _);
            if (!ok) return false;
            if (exact is { } e) { socket = e; return true; }
        }
        if (wildcard is { } w) { socket = w; return true; }
        return false;
    }

    // ------------------------------------------------------------ one request

    /// <summary>
    /// Sends <paramref name="request"/> on a pooled socket and reads the answer: a single socket (<paramref name="onDump"/>
    /// null) or every socket of a dump. False with <paramref name="errno"/> when there is none or no answer.
    /// </summary>
    private bool TryLookUp(byte[] request, Action<DumpEntry>? onDump, out Socket socket, out int errno)
    {
        socket = default;
        errno = 0;
        if (_closed || Stopwatch.GetTimestamp() < Interlocked.Read(ref _pausedUntilTicks)) { errno = Libc.EAGAIN; return false; }
        if (!_idle.TryTake(out var fd))
        {
            fd = OpenSocket(out _);
            if (fd < 0) { errno = Marshal.GetLastPInvokeError(); return false; }
        }
        var keep = false;
        try
        {
            if (Libc.send(fd, request, request.Length, 0) < 0) { errno = Marshal.GetLastPInvokeError(); return false; }
            var seq = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8));
            var buf = new byte[onDump is null ? 4096 : 32 * 1024];
            var addr = new byte[12];
            var found = false;
            while (true)
            {
                var addrLen = addr.Length;
                var n = (int)Libc.recvfrom(fd, buf, buf.Length, 0, addr, ref addrLen);
                if (n < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                    if (errno == Libc.EINTR) continue;
                    if (errno == Libc.EAGAIN) NoteTimeout();
                    return false;                                            // the socket is closed below, not reused
                }
                if (addrLen < 8 || BinaryPrimitives.ReadUInt32LittleEndian(addr.AsSpan(4)) != 0) continue;   // not from the kernel
                switch (Parse(buf.AsSpan(0, n), seq, onDump, ref socket, ref found, out errno))
                {
                    case ParseResult.More: continue;
                    case ParseResult.Done:
                        Interlocked.Exchange(ref _timeouts, 0);
                        keep = true;                                         // the answer is complete: the socket is clean
                        return found || onDump is not null;
                    case ParseResult.Error:
                        Interlocked.Exchange(ref _timeouts, 0);
                        keep = true;
                        return false;
                }
            }
        }
        finally
        {
            if (keep && !_closed) _idle.Add(fd);
            else Libc.close(fd);
        }
    }

    internal void NoteTimeout()
    {
        if (Interlocked.Increment(ref _timeouts) < TimeoutsBeforePause) return;
        Interlocked.Exchange(ref _timeouts, 0);
        Interlocked.Exchange(ref _pausedUntilTicks, Stopwatch.GetTimestamp() + (long)(Pause.TotalSeconds * Stopwatch.Frequency));
        Problem?.Invoke($"sock_diag didn't answer {TimeoutsBeforePause} times in a row: owners are found through /proc for {Pause.TotalSeconds:0} s");
    }

    internal enum ParseResult { More, Done, Error }

    /// <summary>A socket of a dump: its local endpoint and inode/uid.</summary>
    internal readonly record struct DumpEntry(IPEndPoint Local, IPEndPoint Remote, Socket Socket);

    /// <summary>
    /// Reads one netlink datagram for request <paramref name="seq"/>: a single answer (Done, <paramref name="found"/>),
    /// part of a dump (More, entries to <paramref name="onDump"/>), its end (Done) or an error (Error, errno; ENOENT when
    /// there is no such socket). Messages for other sequence numbers are skipped.
    /// </summary>
    internal static ParseResult Parse(ReadOnlySpan<byte> data, uint seq, Action<DumpEntry>? onDump, ref Socket socket, ref bool found, out int errno)
    {
        errno = 0;
        for (var off = 0; off + 16 <= data.Length;)
        {
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[off..]);
            if (len < 16 || off + len > data.Length) break;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 4)..]);
            var s = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 8)..]);
            var msg = data.Slice(off, len);
            off += (len + 3) & ~3;
            if (s != seq) continue;
            if (type == NLMSG_DONE) return ParseResult.Done;
            if (type == NLMSG_ERROR)
            {
                errno = len >= 20 ? -BinaryPrimitives.ReadInt32LittleEndian(msg[16..]) : Libc.EAGAIN;
                if (errno == 0) continue;
                return ParseResult.Error;
            }
            if (type != SOCK_DIAG_BY_FAMILY || len < 16 + MsgLength) continue;
            var entry = ReadMessage(msg[16..]);
            if (onDump is null)
            {
                socket = entry.Socket;
                found = true;
                return ParseResult.Done;
            }
            onDump(entry);
        }
        return ParseResult.More;
    }

    /// <summary>struct inet_diag_msg: family, state, timer, retrans, inet_diag_sockid (48), expires, rqueue, wqueue, uid, inode.</summary>
    private static DumpEntry ReadMessage(ReadOnlySpan<byte> m)
    {
        var v6 = m[0] == AfInet6;
        var size = v6 ? 16 : 4;
        var local = new IPEndPoint(new IPAddress(m.Slice(8, size)), BinaryPrimitives.ReadUInt16BigEndian(m[4..]));
        var remote = new IPEndPoint(new IPAddress(m.Slice(24, size)), BinaryPrimitives.ReadUInt16BigEndian(m[6..]));
        return new DumpEntry(local, remote, new Socket(BinaryPrimitives.ReadUInt32LittleEndian(m[68..]), BinaryPrimitives.ReadUInt32LittleEndian(m[64..])));
    }

    /// <summary>nlmsghdr + struct inet_diag_req_v2 { family, protocol, ext, pad, states, inet_diag_sockid }.</summary>
    internal static byte[] Request(ushort type, ushort flags, uint seq, byte family, byte protocol, IPEndPoint local, IPEndPoint remote)
    {
        var b = new byte[16 + ReqLength];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)b.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), type);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), seq);
        var r = b.AsSpan(16);
        r[0] = family;
        r[1] = protocol;
        BinaryPrimitives.WriteUInt32LittleEndian(r[4..], 0xFFFFFFFF);         // every TCP state (a dump), ignored by one lookup
        BinaryPrimitives.WriteUInt16BigEndian(r[8..], (ushort)local.Port);    // idiag_sport
        BinaryPrimitives.WriteUInt16BigEndian(r[10..], (ushort)remote.Port);  // idiag_dport
        AddressBytes(local.Address, family).CopyTo(r[12..]);                 // idiag_src[4]
        AddressBytes(remote.Address, family).CopyTo(r[28..]);                // idiag_dst[4]
        BinaryPrimitives.WriteUInt32LittleEndian(r[48..], 0xFFFFFFFF);        // idiag_cookie: INET_DIAG_NOCOOKIE
        BinaryPrimitives.WriteUInt32LittleEndian(r[52..], 0xFFFFFFFF);
        return b;
    }

    private static byte[] AddressBytes(IPAddress a, byte family)
    {
        if (family == AfInet) return (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).GetAddressBytes();
        return (a.AddressFamily == AddressFamily.InterNetwork ? a.MapToIPv6() : a).GetAddressBytes();
    }

    private static bool IsAny(IPAddress a) => a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);

    private static bool SameAddress(IPAddress a, IPAddress b)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
        return a.Equals(b);
    }

    private static int OpenSocket(out string? problem)
    {
        problem = null;
        var fd = Libc.socket(Libc.AF_NETLINK, Libc.SOCK_RAW | Libc.SOCK_CLOEXEC, Libc.NETLINK_SOCK_DIAG);
        if (fd < 0)
        {
            problem = $"cannot open a sock_diag netlink socket: {Libc.Error(Marshal.GetLastPInvokeError())}";
            return -1;
        }
        var addr = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(addr, Libc.AF_NETLINK);
        if (Libc.bind(fd, addr, addr.Length) != 0)
        {
            problem = $"cannot bind the sock_diag socket: {Libc.Error(Marshal.GetLastPInvokeError())}";
            Libc.close(fd);
            return -1;
        }
        var tv = new byte[16];                                                // struct timeval { 0 s, 100 ms }
        BinaryPrimitives.WriteInt64LittleEndian(tv.AsSpan(8), TimeoutMs * 1000);
        Libc.setsockopt(fd, Libc.SOL_SOCKET, Libc.SO_RCVTIMEO, tv, tv.Length);
        return fd;
    }

    public void Dispose()
    {
        _closed = true;
        while (_idle.TryTake(out var fd)) Libc.close(fd);
    }
}
