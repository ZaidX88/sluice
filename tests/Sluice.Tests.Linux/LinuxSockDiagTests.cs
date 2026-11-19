using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sluice.Platform.Linux;
using Sluice.Platform.Linux.Native;

namespace Sluice.Tests;

/// <summary>
/// Issue #82: socket owners through the kernel's socket diagnostics (sock_diag) instead of parsing all of
/// /proc/net/tcp for every new connection. The results must be the same as the /proc path's.
/// </summary>
public class LinuxSockDiagTests
{
    private static LinuxProcessLookup Diag() => new();
    private static LinuxProcessLookup Proc() => new(useSockDiag: false);

    // ------------------------------------------------------------ same owners as /proc

    [LinuxFact]
    public async Task Tcp_owners_are_the_same_as_through_proc()
    {
        using var diag = Diag();
        if (!diag.UsesSockDiag) return;                                   // no sock_diag here: the /proc path is used anyway
        var proc = Proc();
        var me = Environment.ProcessId;

        foreach (var (family, loopback) in new[] { (AddressFamily.InterNetwork, IPAddress.Loopback), (AddressFamily.InterNetworkV6, IPAddress.IPv6Loopback) })
        {
            using var l = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            l.Bind(new IPEndPoint(loopback, 0));
            l.Listen(8);
            for (var i = 0; i < 5; i++)
            {
                using var c = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
                await c.ConnectAsync(l.LocalEndPoint!);
                using var a = await l.AcceptAsync();
                var local = (IPEndPoint)c.LocalEndPoint!;
                var remote = (IPEndPoint)c.RemoteEndPoint!;
                Assert.Equal(me, diag.FindTcpOwner(local, remote));
                Assert.Equal(me, proc.FindTcpOwner(local, remote));
                Assert.Equal(me, diag.FindTcpOwner((IPEndPoint)a.LocalEndPoint!, (IPEndPoint)a.RemoteEndPoint!));   // the accepted side
            }
        }

        // An IPv4 connection from a dual-stack IPv6 socket (IPv4-mapped addresses), as many apps open them.
        using var l4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l4.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        l4.Listen(1);
        using var dual = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        await dual.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)l4.LocalEndPoint!).Port));
        var dl = (IPEndPoint)dual.LocalEndPoint!;
        var dr = (IPEndPoint)dual.RemoteEndPoint!;
        Assert.Equal(me, diag.FindTcpOwner(new IPEndPoint(dl.Address.MapToIPv4(), dl.Port), new IPEndPoint(dr.Address.MapToIPv4(), dr.Port)));
    }

    [LinuxFact]
    public void Every_connection_on_this_machine_has_the_same_owner_both_ways()
    {
        using var diag = Diag();
        if (!diag.UsesSockDiag) return;
        var proc = Proc();
        int compared = 0, differ = 0;
        var notes = new List<string>();
        foreach (var file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
            foreach (var e in LinuxProcessLookup.ReadTable(file))
            {
                if (e.State is not (0x01 or 0x02) || e.Inode == 0) continue;    // established or SYN_SENT: a connection, not a listener
                var local = new IPEndPoint(e.Local, e.LocalPort);
                var remote = new IPEndPoint(e.Remote, e.RemotePort);
                var a = diag.FindTcpOwner(local, remote);
                var b = proc.FindTcpOwner(local, remote);
                if (a == 0 && b == 0) continue;                               // another user's process (not readable), or gone
                compared++;
                if (a != b && a != 0 && b != 0) { differ++; notes.Add($"{local} -> {remote}: sock_diag {a}, /proc {b}"); }
            }
        Assert.True(differ == 0, string.Join("; ", notes));
        Assert.True(compared > 0, "no readable connection to compare");
    }

    [LinuxFact]
    public void Udp_owners_are_the_same_as_through_proc_including_wildcard_sockets()
    {
        using var diag = Diag();
        if (!diag.UsesSockDiag) return;
        var proc = Proc();
        var me = Environment.ProcessId;

        using var exact4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        exact4.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var any4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        any4.Bind(new IPEndPoint(IPAddress.Any, 0));
        using var any6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        any6.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

        foreach (var (asked, name) in new[]
        {
            ((IPEndPoint)exact4.LocalEndPoint!, "bound to 127.0.0.1"),
            (new IPEndPoint(IPAddress.Parse("192.0.2.10"), ((IPEndPoint)any4.LocalEndPoint!).Port), "bound to 0.0.0.0, asked by a LAN address"),
            (new IPEndPoint(IPAddress.Parse("192.0.2.10"), ((IPEndPoint)any6.LocalEndPoint!).Port), "dual-stack, bound to ::"),
        })
        {
            Assert.True(me == diag.FindUdpOwner(asked), $"sock_diag, {name}");
            Assert.True(me == proc.FindUdpOwner(asked), $"/proc, {name}");
        }
    }

    // ------------------------------------------------------------ concurrency

    [LinuxFact]
    public async Task A_cache_hit_does_not_wait_for_another_callers_scan()
    {
        using var lookup = Diag();
        using var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        l.Listen(1);
        using var c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await c.ConnectAsync(l.LocalEndPoint!);
        var local = (IPEndPoint)c.LocalEndPoint!;
        var remote = (IPEndPoint)c.RemoteEndPoint!;
        Assert.Equal(Environment.ProcessId, lookup.FindTcpOwner(local, remote));   // now cached

        // Another caller misses: an inode nobody has makes it scan every process (tens of ms).
        var scanning = Task.Run(() =>
        {
            var s = Stopwatch.StartNew();
            for (var i = 0; i < 5; i++) lookup.PidForInode(ulong.MaxValue - (ulong)i);
            return s.Elapsed.TotalMilliseconds;
        });
        var hits = new List<double>();
        while (!scanning.IsCompleted)
        {
            var s = Stopwatch.StartNew();
            Assert.Equal(Environment.ProcessId, lookup.FindTcpOwner(local, remote));
            hits.Add(s.Elapsed.TotalMilliseconds);
        }
        var scanMs = await scanning;
        hits.Sort();
        Assert.True(hits.Count > 0);
        Assert.True(hits[hits.Count / 2] < scanMs / 5 && hits[hits.Count / 2] < 5,
            $"median hit {hits[hits.Count / 2]:0.000} ms during {scanMs:0} ms of scans");
    }

    // ------------------------------------------------------------ messages

    [Fact]
    public void Request_asks_for_one_tcp_socket_by_its_addresses()
    {
        var m = SockDiag.Request(20, 1, 9, 2, 6, IPEndPoint.Parse("192.0.2.10:40000"), IPEndPoint.Parse("198.51.100.1:443"));
        Assert.Equal(72, m.Length);
        Assert.Equal(72u, BinaryPrimitives.ReadUInt32LittleEndian(m));
        Assert.Equal(20, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(4)));          // SOCK_DIAG_BY_FAMILY
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(6)));           // NLM_F_REQUEST, no dump
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(8)));
        Assert.Equal((2, 6), (m[16], m[17]));                                           // AF_INET, TCP
        Assert.Equal(40000, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(24)));          // idiag_sport
        Assert.Equal(443, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(26)));            // idiag_dport
        Assert.Equal(new byte[] { 192, 0, 2, 10 }, m[28..32]);                           // idiag_src
        Assert.Equal(new byte[] { 198, 51, 100, 1 }, m[44..48]);                         // idiag_dst
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(64))); // INET_DIAG_NOCOOKIE
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(68)));

        var six = SockDiag.Request(20, 0x301, 1, 10, 17, new IPEndPoint(IPAddress.IPv6Any, 0), new IPEndPoint(IPAddress.IPv6Any, 0));
        Assert.Equal(0x301, BinaryPrimitives.ReadUInt16LittleEndian(six.AsSpan(6)));     // a dump
        Assert.Equal((10, 17), (six[16], six[17]));                                     // AF_INET6, UDP
    }

    /// <summary>One inet_diag_msg answer to request <paramref name="seq"/>.</summary>
    private static byte[] Answer(uint seq, IPEndPoint local, IPEndPoint remote, uint uid, uint inode)
    {
        var m = new byte[16 + 72];
        BinaryPrimitives.WriteUInt32LittleEndian(m, (uint)m.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), seq);
        var b = m.AsSpan(16);
        var v6 = local.AddressFamily == AddressFamily.InterNetworkV6;
        b[0] = (byte)(v6 ? 10 : 2);
        b[1] = 1;                                                                        // TCP_ESTABLISHED
        BinaryPrimitives.WriteUInt16BigEndian(b[4..], (ushort)local.Port);
        BinaryPrimitives.WriteUInt16BigEndian(b[6..], (ushort)remote.Port);
        local.Address.GetAddressBytes().CopyTo(b[8..]);
        remote.Address.GetAddressBytes().CopyTo(b[24..]);
        BinaryPrimitives.WriteUInt32LittleEndian(b[64..], uid);
        BinaryPrimitives.WriteUInt32LittleEndian(b[68..], inode);
        return m;
    }

    private static byte[] Header(ushort type, uint seq, int errno = 0)
    {
        var m = new byte[type == 2 ? 36 : 20];
        BinaryPrimitives.WriteUInt32LittleEndian(m, (uint)m.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), seq);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(16), -errno);
        return m;
    }

    [Fact]
    public void A_single_answer_gives_the_inode_and_uid()
    {
        SockDiag.Socket s = default;
        var found = false;
        var r = SockDiag.Parse(Answer(9, IPEndPoint.Parse("192.0.2.10:40000"), IPEndPoint.Parse("198.51.100.1:443"), 1000, 123456), 9, null, ref s, ref found, out _);
        Assert.Equal(SockDiag.ParseResult.Done, r);
        Assert.True(found);
        Assert.Equal((123456ul, 1000u), (s.Inode, s.Uid));
    }

    [Fact]
    public void A_dump_lists_every_socket_until_done()
    {
        var seen = new List<(IPEndPoint, ulong)>();
        SockDiag.Socket s = default;
        var found = false;
        var part = Answer(4, IPEndPoint.Parse("[2001:db8::1]:5353"), new IPEndPoint(IPAddress.IPv6Any, 0), 0, 11)
            .Concat(Answer(4, IPEndPoint.Parse("[::]:53"), new IPEndPoint(IPAddress.IPv6Any, 0), 0, 12)).ToArray();
        Assert.Equal(SockDiag.ParseResult.More, SockDiag.Parse(part, 4, e => seen.Add((e.Local, e.Socket.Inode)), ref s, ref found, out _));
        Assert.Equal(SockDiag.ParseResult.Done, SockDiag.Parse(Header(3, 4), 4, e => seen.Add((e.Local, e.Socket.Inode)), ref s, ref found, out _));
        Assert.Equal([(IPEndPoint.Parse("[2001:db8::1]:5353"), 11ul), (IPEndPoint.Parse("[::]:53"), 12ul)], seen);
    }

    [Fact]
    public void Errors_and_answers_to_other_requests_are_told_apart()
    {
        SockDiag.Socket s = default;
        var found = false;
        Assert.Equal(SockDiag.ParseResult.Error, SockDiag.Parse(Header(2, 9, SockDiag.ENOENT), 9, null, ref s, ref found, out var errno));
        Assert.Equal(SockDiag.ENOENT, errno);
        var stale = Answer(8, IPEndPoint.Parse("192.0.2.10:40000"), IPEndPoint.Parse("198.51.100.1:443"), 1000, 99);
        Assert.Equal(SockDiag.ParseResult.More, SockDiag.Parse(stale, 9, null, ref s, ref found, out _));   // a late answer to request 8
        Assert.False(found);
    }

    [LinuxFact]
    public void Lookups_pause_after_timeouts_in_a_row_and_say_so_once()
    {
        using var d = SockDiag.TryOpen(out _);
        if (d is null) return;
        var told = new List<string>();
        d.Problem = told.Add;
        d.NoteTimeout();
        d.NoteTimeout();
        Assert.Empty(told);
        d.NoteTimeout();                                                                 // the third in a row
        Assert.Single(told);
        var s = Stopwatch.StartNew();
        Assert.False(d.TryFindTcp(IPEndPoint.Parse("127.0.0.1:1"), IPEndPoint.Parse("127.0.0.1:2"), out _));
        Assert.True(s.ElapsedMilliseconds < 50, "a paused lookup returns at once (the caller uses /proc)");
    }

    // ------------------------------------------------------------ /proc details

    [Theory]
    [InlineData("socket:[123456]", true, 123456ul)]
    [InlineData("socket:[0]", true, 0ul)]
    [InlineData("pipe:[123456]", false, 0ul)]
    [InlineData("socket:[12x]", false, 0ul)]
    [InlineData("socket:[]", false, 0ul)]
    public void Socket_links_are_parsed(string link, bool ok, ulong inode)
    {
        Assert.Equal(ok, LinuxProcessLookup.TryParseSocketLink(System.Text.Encoding.ASCII.GetBytes(link), out var i));
        if (ok) Assert.Equal(inode, i);
    }

    [Fact]
    public void Proc_net_rows_carry_the_owners_uid()
    {
        Assert.True(LinuxProcessLookup.TryParseLine(
            "   1: 0100007F:9C40 0100007F:01BB 01 00000000:00000000 00:00000000 00000000  1000        0 424242 1 0000000000000000 20 4 30 10 -1",
            out var e));
        Assert.Equal((1000u, 424242ul), (e.Uid!.Value, e.Inode));
    }

    [Fact]
    public void A_fake_proc_tree_always_uses_proc_net()
    {
        using var fake = new LinuxProcessLookup("/nonexistent-proc");
        Assert.False(fake.UsesSockDiag);
    }
}
