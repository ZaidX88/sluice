using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Sluice.Platform.Linux.Native;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// Asks the kernel's connection tracking (ctnetlink, over a raw netlink socket like <see cref="NfQueue"/>) for a TCP
/// connection's original addresses. nftables' <c>redirect</c> sends every captured connection to the relay, and when
/// two connections share a source port (allowed for different destinations) conntrack renumbers one of them; the relay
/// then sees a port the app never used. The reply tuple the relay sees (relay → client) still finds the conntrack
/// entry, whose original tuple gives the app's real source port and the real destination. Needs CAP_NET_ADMIN, which
/// capture has. Message layouts are from linux/netfilter/nfnetlink_conntrack.h.
/// </summary>
internal sealed class Conntrack : IDisposable
{
    private const ushort NLM_F_REQUEST = 1;
    private const ushort NLMSG_ERROR = 2;
    private const int NfnlSubsysCtnetlink = 1;
    private const ushort MsgNew = (NfnlSubsysCtnetlink << 8) | 0, MsgGet = (NfnlSubsysCtnetlink << 8) | 1;
    private const ushort Nested = 0x8000;

    // enum ctattr_type / ctattr_tuple / ctattr_ip / ctattr_l4proto
    private const ushort CtaTupleOrig = 1, CtaTupleReply = 2;
    private const ushort CtaTupleIp = 1, CtaTupleProto = 2;
    private const ushort CtaIpV4Src = 1, CtaIpV4Dst = 2, CtaIpV6Src = 3, CtaIpV6Dst = 4;
    private const ushort CtaProtoNum = 1, CtaProtoSrcPort = 2, CtaProtoDstPort = 3;

    private const byte AfInet = 2, AfInet6 = 10, IpProtoTcp = 6;
    public const int ENOENT = 2, EPERM = 1;

    private readonly Lock _lock = new();
    private readonly int _fd;
    private readonly byte[] _buf = new byte[8192];
    private uint _seq;
    private bool _closed;

    private Conntrack(int fd) => _fd = fd;

    /// <summary>A connection's tuple in one direction.</summary>
    public readonly record struct Tuple(IPEndPoint Source, IPEndPoint Destination);

    /// <summary>Opens the netlink socket; null with <paramref name="problem"/> set when that isn't possible.</summary>
    public static Conntrack? TryOpen(out string? problem)
    {
        problem = null;
        var fd = Libc.socket(Libc.AF_NETLINK, Libc.SOCK_RAW | Libc.SOCK_CLOEXEC, Libc.NETLINK_NETFILTER);
        if (fd < 0)
        {
            problem = $"cannot open a netfilter netlink socket: {Libc.Error(Marshal.GetLastPInvokeError())}";
            return null;
        }
        var addr = new byte[12];                       // sockaddr_nl { family, pad, pid = 0 (kernel assigns), groups = 0 }
        BinaryPrimitives.WriteUInt16LittleEndian(addr, Libc.AF_NETLINK);
        if (Libc.bind(fd, addr, addr.Length) != 0)
        {
            problem = $"cannot bind the netlink socket: {Libc.Error(Marshal.GetLastPInvokeError())}";
            Libc.close(fd);
            return null;
        }
        var tv = new byte[16];                         // struct timeval { sec = 1, usec = 0 }: never wait long for an answer
        BinaryPrimitives.WriteInt64LittleEndian(tv, 1);
        Libc.setsockopt(fd, Libc.SOL_SOCKET, Libc.SO_RCVTIMEO, tv, tv.Length);
        return new Conntrack(fd);
    }

    /// <summary>
    /// The original tuple of the TCP connection whose reply direction is <paramref name="reply"/> (for a redirected
    /// connection: relay → the client endpoint the relay sees). False with <paramref name="errno"/> set when there is
    /// no such entry (<see cref="ENOENT"/>), the request is not allowed (<see cref="EPERM"/>) or no answer came.
    /// </summary>
    public bool TryGetOriginal(Tuple reply, out Tuple original, out int errno)
    {
        original = default;
        lock (_lock)
        {
            errno = 0;
            if (_closed) return false;
            var seq = ++_seq;
            var msg = GetRequest(seq, reply);
            if (Libc.send(_fd, msg, msg.Length, 0) < 0)
            {
                errno = Marshal.GetLastPInvokeError();
                return false;
            }
            for (var attempt = 0; attempt < 8; attempt++)       // skip stale answers to earlier, timed-out requests
            {
                var n = (int)Libc.recv(_fd, _buf, _buf.Length, 0);
                if (n < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                    if (errno == Libc.EINTR) continue;
                    return false;                                // EAGAIN: no answer within a second
                }
                switch (ParseAnswer(_buf.AsSpan(0, n), seq, out original, out errno))
                {
                    case true: return true;
                    case false: return false;
                    case null: continue;
                }
            }
            errno = Libc.EAGAIN;
            return false;
        }
    }

    /// <summary>IPCTNL_MSG_CT_GET for one TCP connection, found by its reply tuple.</summary>
    internal static byte[] GetRequest(uint seq, Tuple reply)
    {
        var v6 = reply.Source.AddressFamily == AddressFamily.InterNetworkV6;
        var ms = new MemoryStream();
        Span<byte> h = stackalloc byte[20];                      // nlmsghdr + nfgenmsg
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], MsgGet);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], NLM_F_REQUEST);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], seq);
        h[16] = v6 ? AfInet6 : AfInet;                           // nfgen_family: the tuple's address family
        ms.Write(h);
        WriteNested(ms, CtaTupleReply, t =>
        {
            WriteNested(t, CtaTupleIp, ip =>
            {
                WriteAttr(ip, v6 ? CtaIpV6Src : CtaIpV4Src, reply.Source.Address.GetAddressBytes());
                WriteAttr(ip, v6 ? CtaIpV6Dst : CtaIpV4Dst, reply.Destination.Address.GetAddressBytes());
            });
            WriteNested(t, CtaTupleProto, p =>
            {
                WriteAttr(p, CtaProtoNum, [IpProtoTcp]);
                WriteAttr(p, CtaProtoSrcPort, Be16(reply.Source.Port));
                WriteAttr(p, CtaProtoDstPort, Be16(reply.Destination.Port));
            });
        });
        var b = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)b.Length);
        return b;
    }

    /// <summary>
    /// Reads one netlink datagram: true with the original tuple for an IPCTNL_MSG_CT_NEW answer to
    /// <paramref name="seq"/>, false with <paramref name="errno"/> for an error answer, null when nothing in it
    /// answers <paramref name="seq"/>.
    /// </summary>
    internal static bool? ParseAnswer(ReadOnlySpan<byte> data, uint seq, out Tuple original, out int errno)
    {
        original = default;
        errno = 0;
        for (var off = 0; off + 16 <= data.Length;)
        {
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[off..]);
            if (len < 16 || off + len > data.Length) break;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 4)..]);
            var s = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 8)..]);
            var msg = data.Slice(off, len);
            off += Align(len);
            if (s != seq) continue;
            if (type == NLMSG_ERROR)
            {
                errno = len >= 20 ? -BinaryPrimitives.ReadInt32LittleEndian(msg[16..]) : Libc.EAGAIN;
                if (errno == 0) continue;                        // an acknowledgement, not an answer
                return false;
            }
            if (type != MsgNew || len < 20) continue;
            var v6 = msg[16] == AfInet6;
            if (Find(msg[20..], CtaTupleOrig) is not { } tuple || !TryReadTuple(tuple, v6, out original))
            {
                errno = Libc.EAGAIN;
                return false;
            }
            return true;
        }
        return null;
    }

    private static bool TryReadTuple(ReadOnlySpan<byte> tuple, bool v6, out Tuple result)
    {
        result = default;
        if (Find(tuple, CtaTupleIp) is not { } ip || Find(tuple, CtaTupleProto) is not { } proto) return false;
        if (Find(ip, v6 ? CtaIpV6Src : CtaIpV4Src) is not { } src || Find(ip, v6 ? CtaIpV6Dst : CtaIpV4Dst) is not { } dst) return false;
        if (Find(proto, CtaProtoSrcPort) is not { Length: >= 2 } sp || Find(proto, CtaProtoDstPort) is not { Length: >= 2 } dp) return false;
        var size = v6 ? 16 : 4;
        if (src.Length < size || dst.Length < size) return false;
        result = new Tuple(
            new IPEndPoint(new IPAddress(src[..size]), BinaryPrimitives.ReadUInt16BigEndian(sp)),
            new IPEndPoint(new IPAddress(dst[..size]), BinaryPrimitives.ReadUInt16BigEndian(dp)));
        return true;
    }

    /// <summary>The payload of the first attribute of <paramref name="type"/> (nested or not) in an attribute list.</summary>
    private static ReadOnlySpan<byte> FindSpan(ReadOnlySpan<byte> attrs, ushort type, out bool found)
    {
        found = false;
        for (var a = 0; a + 4 <= attrs.Length;)
        {
            var alen = BinaryPrimitives.ReadUInt16LittleEndian(attrs[a..]);
            var atype = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(attrs[(a + 2)..]) & 0x3FFF);
            if (alen < 4 || a + alen > attrs.Length) break;
            if (atype == type)
            {
                found = true;
                return attrs.Slice(a + 4, alen - 4);
            }
            a += Align(alen);
        }
        return default;
    }

    private static byte[]? Find(ReadOnlySpan<byte> attrs, ushort type)
    {
        var s = FindSpan(attrs, type, out var found);
        return found ? s.ToArray() : null;
    }

    private static void WriteAttr(Stream s, ushort type, ReadOnlySpan<byte> payload)
    {
        Span<byte> h = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(h, (ushort)(4 + payload.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], type);
        s.Write(h);
        s.Write(payload);
        for (var pad = Align(payload.Length) - payload.Length; pad > 0; pad--) s.WriteByte(0);
    }

    private static void WriteNested(Stream s, ushort type, Action<Stream> body)
    {
        var inner = new MemoryStream();
        body(inner);
        WriteAttr(s, (ushort)(type | Nested), inner.ToArray());
    }

    private static byte[] Be16(int port)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)port);
        return b;
    }

    private static int Align(int n) => (n + 3) & ~3;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            Libc.close(_fd);
        }
    }
}
