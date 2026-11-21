using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sluice.Platform.Linux.Native;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// The policy routing the UDP tproxy needs, over rtnetlink (no <c>ip</c> command): for IPv4 and IPv6, a rule
/// "fwmark <see cref="Mark"/> lookup <see cref="Table"/>" and, in that table, "local default dev lo". A locally sent
/// packet carrying the mark is routed back in through <c>lo</c>, where nftables' <c>tproxy</c> can hand it to Sluice
/// (<see cref="NftRules"/>). Only marked packets are affected. Like the nft table, both outlive a crash, so
/// <see cref="Add"/> first removes what an earlier run left.
/// </summary>
internal static class PolicyRoutes
{
    public const int Table = 0x5C1;          // 1473, like the queue number
    public const int Priority = 0x5C1;       // before the main table (32766)

    private const int NETLINK_ROUTE = 0;
    private const ushort RTM_NEWROUTE = 24, RTM_DELROUTE = 25, RTM_NEWRULE = 32, RTM_DELRULE = 33;
    private const ushort NLM_F_REQUEST = 1, NLM_F_ACK = 4, NLM_F_EXCL = 0x200, NLM_F_CREATE = 0x400;
    private const ushort NLMSG_ERROR = 2;
    private const byte FR_ACT_TO_TBL = 1, RT_TABLE_UNSPEC = 0, RTPROT_STATIC = 4, RT_SCOPE_HOST = 254, RTN_LOCAL = 2;
    private const ushort FRA_PRIORITY = 6, FRA_FWMARK = 10, FRA_TABLE = 15, FRA_FWMASK = 16;
    private const ushort RTA_OIF = 4, RTA_TABLE = 15;
    public const byte AF_INET = 2, AF_INET6 = 10;
    private const int ENOENT = 2, ESRCH = 3, EEXIST = 17, EAFNOSUPPORT = 97;

    /// <summary>
    /// Adds the rule and route for both families, after removing any left from an earlier run. An IPv6 failure because
    /// the system has no IPv6 is not an error (returned in <paramref name="ipv6Problem"/>); any IPv4 failure throws.
    /// </summary>
    public static void Add(uint mark, out string? ipv6Problem)
    {
        Remove(mark);
        ipv6Problem = null;
        using var nl = Netlink.Open();
        var lo = LoopbackIndex();
        nl.Request(RuleMessage(RTM_NEWRULE, NLM_F_CREATE | NLM_F_EXCL, nl.NextSeq(), AF_INET, mark), "add the IPv4 routing rule");
        nl.Request(RouteMessage(RTM_NEWROUTE, NLM_F_CREATE | NLM_F_EXCL, nl.NextSeq(), AF_INET, lo), "add the IPv4 route");
        try
        {
            nl.Request(RuleMessage(RTM_NEWRULE, NLM_F_CREATE | NLM_F_EXCL, nl.NextSeq(), AF_INET6, mark), "add the IPv6 routing rule");
            nl.Request(RouteMessage(RTM_NEWROUTE, NLM_F_CREATE | NLM_F_EXCL, nl.NextSeq(), AF_INET6, lo), "add the IPv6 route");
        }
        catch (NetlinkException ex) when (ex.Errno is EAFNOSUPPORT)
        {
            ipv6Problem = ex.Message;
        }
    }

    /// <summary>Removes the rules for <paramref name="mark"/> and the routes (no error when they are not there). Never throws.</summary>
    public static void Remove(uint mark)
    {
        try
        {
            using var nl = Netlink.Open();
            var lo = LoopbackIndex();
            foreach (var family in new[] { AF_INET, AF_INET6 })
            {
                // Several copies could exist if earlier runs failed half-way: delete until there is none.
                for (var i = 0; i < 8 && nl.TryRequest(RuleMessage(RTM_DELRULE, 0, nl.NextSeq(), family, mark), out _); i++) { }
                for (var i = 0; i < 8 && nl.TryRequest(RouteMessage(RTM_DELROUTE, 0, nl.NextSeq(), family, lo), out _); i++) { }
            }
        }
        catch (InvalidOperationException) { /* best effort */ }
    }

    /// <summary>RTM_NEWRULE / RTM_DELRULE: fib_rule_hdr + priority, fwmark/mask, table.</summary>
    internal static byte[] RuleMessage(ushort type, ushort extraFlags, uint seq, byte family, uint mark)
    {
        var w = new NetlinkWriter(type, (ushort)(NLM_F_REQUEST | NLM_F_ACK | extraFlags), seq);
        Span<byte> hdr = stackalloc byte[12];            // family, dst_len, src_len, tos, table, res1, res2, action, flags (u32)
        hdr[0] = family;
        hdr[4] = RT_TABLE_UNSPEC;                         // the table goes in FRA_TABLE (it is above 255)
        hdr[7] = FR_ACT_TO_TBL;
        w.Raw(hdr);
        w.AttrU32(FRA_PRIORITY, Priority);
        w.AttrU32(FRA_FWMARK, mark);
        w.AttrU32(FRA_FWMASK, 0xFFFFFFFF);
        w.AttrU32(FRA_TABLE, Table);
        return w.Finish();
    }

    /// <summary>RTM_NEWROUTE / RTM_DELROUTE: "local default dev lo table <see cref="Table"/>".</summary>
    internal static byte[] RouteMessage(ushort type, ushort extraFlags, uint seq, byte family, int loopbackIndex)
    {
        var w = new NetlinkWriter(type, (ushort)(NLM_F_REQUEST | NLM_F_ACK | extraFlags), seq);
        Span<byte> rtm = stackalloc byte[12];            // family, dst_len, src_len, tos, table, protocol, scope, type, flags (u32)
        rtm[0] = family;
        rtm[1] = 0;                                       // default: 0.0.0.0/0, ::/0
        rtm[4] = RT_TABLE_UNSPEC;
        rtm[5] = RTPROT_STATIC;
        rtm[6] = RT_SCOPE_HOST;
        rtm[7] = RTN_LOCAL;
        w.Raw(rtm);
        w.AttrU32(RTA_TABLE, Table);
        w.AttrU32(RTA_OIF, (uint)loopbackIndex);
        return w.Finish();
    }

    private static int LoopbackIndex()
    {
        var i = if_nametoindex("lo");
        return i > 0 ? (int)i : throw new InvalidOperationException("The loopback interface (lo) was not found.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint if_nametoindex([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    internal sealed class NetlinkException(string message, int errno) : InvalidOperationException(message)
    {
        public int Errno { get; } = errno;
    }

    /// <summary>A NETLINK_ROUTE socket that sends one request at a time and waits for its acknowledgement.</summary>
    private sealed class Netlink : IDisposable
    {
        private readonly int _fd;
        private readonly byte[] _buf = new byte[8192];
        private uint _seq = (uint)Environment.TickCount;

        private Netlink(int fd) => _fd = fd;

        public static Netlink Open()
        {
            var fd = Libc.socket(Libc.AF_NETLINK, Libc.SOCK_RAW | Libc.SOCK_CLOEXEC, NETLINK_ROUTE);
            if (fd < 0) throw new InvalidOperationException($"Cannot open a routing netlink socket: {Libc.Error(Marshal.GetLastPInvokeError())}");
            var tv = new byte[16];                         // struct timeval { sec = 2 }: never wait forever for the kernel
            BinaryPrimitives.WriteInt64LittleEndian(tv, 2);
            Libc.setsockopt(fd, Libc.SOL_SOCKET, Libc.SO_RCVTIMEO, tv, tv.Length);
            return new Netlink(fd);
        }

        public uint NextSeq() => ++_seq;

        public void Request(byte[] msg, string what)
        {
            if (!TryRequest(msg, out var errno))
                throw new NetlinkException($"Could not {what}: {Libc.Error(errno)}" + (errno == 1 ? " (needs root or CAP_NET_ADMIN)" : ""), errno);
        }

        /// <summary>Sends <paramref name="msg"/>; true when the kernel acknowledged it without an error.</summary>
        public bool TryRequest(byte[] msg, out int errno)
        {
            var seq = BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(8));
            if (Libc.send(_fd, msg, msg.Length, 0) < 0)
            {
                errno = Marshal.GetLastPInvokeError();
                return false;
            }
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var n = (int)Libc.recv(_fd, _buf, _buf.Length, 0);
                if (n < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                    if (errno == Libc.EINTR) continue;
                    return false;
                }
                for (var off = 0; off + 16 <= n;)
                {
                    var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(off));
                    var t = BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(off + 4));
                    var s = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(off + 8));
                    if (t == NLMSG_ERROR && s == seq && off + 20 <= n)
                    {
                        errno = -BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(off + 16));
                        return errno == 0;
                    }
                    if (len < 16) break;
                    off += (len + 3) & ~3;
                }
            }
            errno = Libc.EAGAIN;
            return false;
        }

        public void Dispose() => Libc.close(_fd);
    }
}

/// <summary>Builds one netlink message: nlmsghdr, a fixed header, then attributes (host byte order).</summary>
internal sealed class NetlinkWriter
{
    private readonly MemoryStream _ms = new();

    public NetlinkWriter(ushort type, ushort flags, uint seq)
    {
        Span<byte> h = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], seq);
        _ms.Write(h);
    }

    public void Raw(ReadOnlySpan<byte> bytes) => _ms.Write(bytes);

    public void AttrU32(ushort type, uint value)
    {
        Span<byte> a = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(a, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(a[2..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(a[4..], value);
        _ms.Write(a);
    }

    public byte[] Finish()
    {
        var b = _ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)b.Length);
        return b;
    }
}
