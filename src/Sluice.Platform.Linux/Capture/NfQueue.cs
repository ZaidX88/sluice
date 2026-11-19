using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sluice.Platform.Linux.Native;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// Minimal NFQUEUE client over a raw netlink socket (no libnetfilter_queue needed): packets that an nftables
/// <c>queue</c> rule sends to userspace arrive here and wait until we give a verdict (accept, optionally with a
/// packet mark, or drop). Message layouts are from linux/netfilter/nfnetlink_queue.h.
/// </summary>
internal sealed class NfQueue : IDisposable
{
    // netlink / nfnetlink
    private const ushort NLM_F_REQUEST = 1, NLM_F_ACK = 4;
    private const ushort NLMSG_ERROR = 2, NLMSG_DONE = 3;
    private const int NfnlSubsysQueue = 3;
    private const ushort MsgPacket = (NfnlSubsysQueue << 8) | 0, MsgVerdict = (NfnlSubsysQueue << 8) | 1, MsgConfig = (NfnlSubsysQueue << 8) | 2;

    // attributes
    private const ushort CfgCmd = 1, CfgParams = 2, CfgQueueMaxLen = 3, CfgMask = 4, CfgFlags = 5;
    private const ushort AttrPacketHdr = 1, AttrVerdictHdr = 2, AttrMark = 3, AttrPayload = 10;
    private const byte CmdBind = 1;
    private const byte CopyPacket = 2;
    private const uint FlagFailOpen = 1;

    public const uint Drop = 0, Accept = 1;

    private readonly int _fd;
    private readonly ushort _queue;
    private readonly byte[] _buf = new byte[256 * 1024];
    private uint _seq;
    private volatile bool _closed, _stopping;
    private int _sending;     // verdicts being sent now: Dispose waits for them before closing the socket

    public readonly record struct Packet(uint Id, ArraySegment<byte> Payload);

    private NfQueue(int fd, ushort queue)
    {
        _fd = fd;
        _queue = queue;
    }

    /// <summary>Binds queue <paramref name="queue"/>; packets are copied up to <paramref name="copyRange"/> bytes.</summary>
    public static NfQueue Open(ushort queue, int copyRange = 128)
    {
        var fd = Libc.socket(Libc.AF_NETLINK, Libc.SOCK_RAW | Libc.SOCK_CLOEXEC, Libc.NETLINK_NETFILTER);
        if (fd < 0) throw new InvalidOperationException($"Cannot open a netfilter netlink socket: {Libc.Error(Marshal.GetLastPInvokeError())}");
        var q = new NfQueue(fd, queue);
        try
        {
            var addr = new byte[12];                       // sockaddr_nl { family, pad, pid = 0 (kernel assigns), groups = 0 }
            BinaryPrimitives.WriteUInt16LittleEndian(addr, Libc.AF_NETLINK);
            if (Libc.bind(fd, addr, addr.Length) != 0)
                throw new InvalidOperationException($"Cannot bind the netlink socket: {Libc.Error(Marshal.GetLastPInvokeError())}");

            SetInt(fd, Libc.SOL_SOCKET, Libc.SO_RCVBUF, 4 * 1024 * 1024);
            SetInt(fd, Libc.SOL_NETLINK, Libc.NETLINK_NO_ENOBUFS, 1);
            var tv = new byte[16];                         // struct timeval { sec = 0, usec = 500 ms }: lets Dispose stop the loop
            BinaryPrimitives.WriteInt64LittleEndian(tv.AsSpan(8), 500_000);
            Libc.setsockopt(fd, Libc.SOL_SOCKET, Libc.SO_RCVTIMEO, tv, tv.Length);

            q.Request(MsgConfig, w => w.Attr(CfgCmd, [CmdBind, 0, 0, 0]));   // cmd, pad, pf = AF_UNSPEC
            q.Request(MsgConfig, w =>
            {
                var p = new byte[5];                                       // { __be32 copy_range; __u8 copy_mode } packed
                BinaryPrimitives.WriteUInt32BigEndian(p, (uint)copyRange);
                p[4] = CopyPacket;
                w.Attr(CfgParams, p);
                w.AttrBe32(CfgQueueMaxLen, 4096);
                // Fail open: if we fall behind, packets pass instead of being dropped.
                w.AttrBe32(CfgMask, FlagFailOpen);
                w.AttrBe32(CfgFlags, FlagFailOpen);
            });
            return q;
        }
        catch
        {
            q.Dispose();
            throw;
        }
    }

    private static void SetInt(int fd, int level, int name, int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        Libc.setsockopt(fd, level, name, b, 4);
    }

    /// <summary>
    /// Blocks until packets arrive (or ~0.5 s passes) and hands each to <paramref name="onPacket"/>, which must
    /// call <see cref="Verdict"/>. Returns false once the queue is closed.
    /// </summary>
    public bool Receive(Action<Packet> onPacket)
    {
        if (_stopping || _closed) return false;
        var n = (int)Libc.recv(_fd, _buf, _buf.Length, 0);
        if (n < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (_stopping || _closed) return false;
            if (err is Libc.EAGAIN or Libc.EINTR or Libc.ENOBUFS) return true;   // timeout / overrun: keep going
            throw new InvalidOperationException($"NFQUEUE receive failed: {Libc.Error(err)}");
        }
        foreach (var p in Parse(_buf.AsSpan(0, n).ToArray())) onPacket(p);
        return true;
    }

    /// <summary>Splits a netlink datagram into queued packets (public for tests).</summary>
    public static List<Packet> Parse(byte[] data)
    {
        var list = new List<Packet>();
        var off = 0;
        while (off + 16 <= data.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 4));
            if (len < 16 || off + len > data.Length) break;
            if (type == MsgPacket)
            {
                uint? id = null;
                ArraySegment<byte> payload = default;
                var a = off + 16 + 4;                                     // nlmsghdr + nfgenmsg
                while (a + 4 <= off + len)
                {
                    var alen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(a));
                    var atype = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(a + 2)) & 0x3FFF);
                    if (alen < 4 || a + alen > off + len) break;
                    if (atype == AttrPacketHdr && alen >= 4 + 4) id = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(a + 4));
                    else if (atype == AttrPayload) payload = new ArraySegment<byte>(data, a + 4, alen - 4);
                    a += Align(alen);
                }
                if (id is { } i) list.Add(new Packet(i, payload));
            }
            off += Align(len);
        }
        return list;
    }

    /// <summary>
    /// Lets packet <paramref name="id"/> continue (<see cref="Accept"/>, optionally setting its mark) or drops it. Safe
    /// from several threads at once (one <c>send</c> per verdict). A verdict given after <see cref="Dispose"/> is
    /// dropped quietly: the kernel lets that packet pass anyway once the queue is gone.
    /// </summary>
    public void Verdict(uint id, uint verdict, uint? mark = null)
    {
        var msg = VerdictMessage(NextSeq(), _queue, id, verdict, mark);
        Interlocked.Increment(ref _sending);
        try
        {
            if (!_closed) Libc.send(_fd, msg, msg.Length, 0);
        }
        finally
        {
            Interlocked.Decrement(ref _sending);
        }
    }

    /// <summary>The next netlink sequence number; unique across threads.</summary>
    internal uint NextSeq() => Interlocked.Increment(ref _seq);

    /// <summary>NFQNL_MSG_VERDICT: verdict header (verdict, packet id) and an optional new packet mark.</summary>
    internal static byte[] VerdictMessage(uint seq, ushort queue, uint id, uint verdict, uint? mark)
    {
        var w = new Writer(MsgVerdict, NLM_F_REQUEST, seq, queue);
        var hdr = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, verdict);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), id);
        w.Attr(AttrVerdictHdr, hdr);
        if (mark is { } m) w.AttrBe32(AttrMark, m);
        return w.Finish();
    }

    /// <summary>Sends a config request and waits for the kernel's acknowledgement.</summary>
    private void Request(ushort type, Action<Writer> build)
    {
        var seq = NextSeq();
        var w = new Writer(type, NLM_F_REQUEST | NLM_F_ACK, seq, _queue);
        build(w);
        var msg = w.Finish();
        if (Libc.send(_fd, msg, msg.Length, 0) < 0)
            throw new InvalidOperationException($"NFQUEUE config failed: {Libc.Error(Marshal.GetLastPInvokeError())}");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var n = (int)Libc.recv(_fd, _buf, _buf.Length, 0);
            if (n < 0) continue;
            for (var off = 0; off + 16 <= n;)
            {
                var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(off));
                var t = BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(off + 4));
                var s = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(off + 8));
                if (t == NLMSG_ERROR && s == seq)
                {
                    var errno = -BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(off + 16));
                    if (errno == 0) return;
                    throw new InvalidOperationException(errno == 1
                        ? "NFQUEUE needs root or the CAP_NET_ADMIN capability"
                        : $"NFQUEUE config failed: {Libc.Error(errno)}" + (errno == 16 ? " — another program already uses this queue" : ""));
                }
                if (len < 16) break;
                off += Align(len);
            }
        }
        throw new InvalidOperationException("NFQUEUE config: no answer from the kernel (is the nfnetlink_queue module available?)");
    }

    private static int Align(int n) => (n + 3) & ~3;

    /// <summary>Builds one netlink message: nlmsghdr + nfgenmsg + attributes.</summary>
    private sealed class Writer
    {
        private readonly MemoryStream _ms = new();

        public Writer(ushort type, ushort flags, uint seq, ushort queue)
        {
            Span<byte> h = stackalloc byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(h[4..], type);
            BinaryPrimitives.WriteUInt16LittleEndian(h[6..], flags);
            BinaryPrimitives.WriteUInt32LittleEndian(h[8..], seq);
            h[16] = 0;                                                    // nfgen_family AF_UNSPEC
            h[17] = 0;                                                    // NFNETLINK_V0
            BinaryPrimitives.WriteUInt16BigEndian(h[18..], queue);        // res_id = queue number
            _ms.Write(h);
        }

        public void Attr(ushort type, ReadOnlySpan<byte> payload)
        {
            Span<byte> h = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(h, (ushort)(4 + payload.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(h[2..], type);
            _ms.Write(h);
            _ms.Write(payload);
            for (var pad = Align(payload.Length) - payload.Length; pad > 0; pad--) _ms.WriteByte(0);
        }

        public void AttrBe32(ushort type, uint value)
        {
            Span<byte> v = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(v, value);
            Attr(type, v);
        }

        public byte[] Finish()
        {
            var b = _ms.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)b.Length);
            return b;
        }
    }

    /// <summary>Makes <see cref="Receive"/> return false within ~0.5 s; close with <see cref="Dispose"/> after the loop ends.</summary>
    public void Stop() => _stopping = true;

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Interlocked.MemoryBarrier();
        // A verdict that saw the socket open may still be in send(): closing now could let the number be reused for
        // another file and the verdict land there. Sends take microseconds.
        var spin = new SpinWait();
        while (Volatile.Read(ref _sending) > 0) spin.SpinOnce();
        Libc.close(_fd);
    }

    /// <summary>For tests: an instance with no socket (fd -1), so <see cref="Verdict"/> and <see cref="NextSeq"/> can run.</summary>
    internal static NfQueue ForTests(ushort queue = 1) => new(-1, queue);
}
