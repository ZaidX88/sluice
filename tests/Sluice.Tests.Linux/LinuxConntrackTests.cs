using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>
/// Issue #38: the relay finds a redirected connection's original addresses in conntrack (ctnetlink IPCTNL_MSG_CT_GET by
/// the reply tuple). Message layout tests run everywhere on Linux; the kernel test needs CAP_NET_ADMIN in a network
/// namespace of its own and runs only there (see <see cref="Kernel_finds_both_connections_of_a_source_port_clash"/>).
/// </summary>
public class LinuxConntrackTests
{
    private static readonly Conntrack.Tuple Reply4 = new(IPEndPoint.Parse("127.0.0.1:40425"), IPEndPoint.Parse("192.0.2.10:1130"));

    private static ushort U16(byte[] b, int off) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(off));

    [Fact]
    public void Request_asks_for_one_tcp_connection_by_its_reply_tuple()
    {
        var m = Conntrack.GetRequest(7, Reply4);

        Assert.Equal((uint)m.Length, BinaryPrimitives.ReadUInt32LittleEndian(m));
        Assert.Equal(0x0101, U16(m, 4));                            // NFNL_SUBSYS_CTNETLINK << 8 | IPCTNL_MSG_CT_GET
        Assert.Equal(1, U16(m, 6));                                 // NLM_F_REQUEST, no dump
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(8)));
        Assert.Equal(2, m[16]);                                     // AF_INET
        Assert.Equal(0x8000 | 2, U16(m, 22));                       // nested CTA_TUPLE_REPLY
        Assert.Equal(0x8000 | 1, U16(m, 26));                       // nested CTA_TUPLE_IP
        Assert.Equal((8, 1), (U16(m, 28), U16(m, 30)));             // CTA_IP_V4_SRC, 4 bytes
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, m[32..36]);
        Assert.Equal((8, 2), (U16(m, 36), U16(m, 38)));             // CTA_IP_V4_DST
        Assert.Equal(new byte[] { 192, 0, 2, 10 }, m[40..44]);
        Assert.Equal(0x8000 | 2, U16(m, 46));                       // nested CTA_TUPLE_PROTO
        Assert.Equal((5, 1, 6), (U16(m, 48), U16(m, 50), m[52]));   // CTA_PROTO_NUM = TCP
        Assert.Equal((6, 2), (U16(m, 56), U16(m, 58)));             // CTA_PROTO_SRC_PORT, big-endian
        Assert.Equal(40425, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(60)));
        Assert.Equal((6, 3), (U16(m, 64), U16(m, 66)));             // CTA_PROTO_DST_PORT
        Assert.Equal(1130, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(68)));
        Assert.Equal(72, m.Length);
    }

    [Fact]
    public void Ipv6_request_uses_the_ipv6_family_and_attributes()
    {
        var m = Conntrack.GetRequest(1, new Conntrack.Tuple(IPEndPoint.Parse("[::1]:40425"), IPEndPoint.Parse("[2001:db8::10]:1130")));
        Assert.Equal(10, m[16]);                                    // AF_INET6
        Assert.Equal((20, 3), (U16(m, 28), U16(m, 30)));            // CTA_IP_V6_SRC, 16 bytes
        Assert.Equal(IPAddress.IPv6Loopback.GetAddressBytes(), m[32..48]);
        Assert.Equal((20, 4), (U16(m, 48), U16(m, 50)));            // CTA_IP_V6_DST
    }

    // ------------------------------------------------------------ answers

    private static byte[] Attr(ushort type, params byte[][] parts)
    {
        var payload = parts.SelectMany(p => p).ToArray();
        var b = new byte[(4 + payload.Length + 3) & ~3];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(4 + payload.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), type);
        payload.CopyTo(b, 4);
        return b;
    }

    private static byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];

    private static byte[] TupleAttr(ushort type, IPEndPoint src, IPEndPoint dst, bool v6 = false) =>
        Attr((ushort)(0x8000 | type),
            Attr(0x8000 | 1, Attr((ushort)(v6 ? 3 : 1), src.Address.GetAddressBytes()), Attr((ushort)(v6 ? 4 : 2), dst.Address.GetAddressBytes())),
            Attr(0x8000 | 2, Attr(1, [6]), Attr(2, Be16(src.Port)), Attr(3, Be16(dst.Port))));

    private static byte[] Message(ushort type, uint seq, byte family, params byte[][] attrs)
    {
        var body = attrs.SelectMany(a => a).ToArray();
        var m = new byte[20 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(m, (uint)m.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), seq);
        m[16] = family;
        body.CopyTo(m, 20);
        return m;
    }

    private static byte[] Error(uint seq, int errno)
    {
        var m = new byte[36];                                       // nlmsghdr + int error + the request's nlmsghdr
        BinaryPrimitives.WriteUInt32LittleEndian(m, 36);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(4), 2);   // NLMSG_ERROR
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), seq);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(16), -errno);
        return m;
    }

    [Fact]
    public void Answer_gives_the_original_tuple()
    {
        var app = IPEndPoint.Parse("192.0.2.10:40000");
        var server = IPEndPoint.Parse("198.51.100.2:80");
        var answer = Message(0x0100, 7, 2,
            TupleAttr(1, app, server),                              // CTA_TUPLE_ORIG
            TupleAttr(2, Reply4.Source, Reply4.Destination),        // CTA_TUPLE_REPLY
            Attr(3, [0, 0, 0, 8]));                                 // CTA_STATUS, ignored

        Assert.True(Conntrack.ParseAnswer(answer, 7, out var orig, out _));
        Assert.Equal(app, orig.Source);
        Assert.Equal(server, orig.Destination);
    }

    [Fact]
    public void Ipv6_answer_gives_the_original_tuple()
    {
        var app = IPEndPoint.Parse("[2001:db8::10]:40000");
        var server = IPEndPoint.Parse("[2001:db8::80]:443");
        Assert.True(Conntrack.ParseAnswer(Message(0x0100, 3, 10, TupleAttr(1, app, server, v6: true)), 3, out var orig, out _));
        Assert.Equal((app, server), (orig.Source, orig.Destination));
    }

    [Fact]
    public void Errors_and_other_requests_answers_are_told_apart()
    {
        Assert.False(Conntrack.ParseAnswer(Error(7, Conntrack.ENOENT), 7, out _, out var errno));
        Assert.Equal(Conntrack.ENOENT, errno);

        Assert.Null(Conntrack.ParseAnswer(Error(6, Conntrack.ENOENT), 7, out _, out _));          // a late answer to request 6
        Assert.Null(Conntrack.ParseAnswer(Error(7, 0), 7, out _, out _));                         // an acknowledgement
        Assert.False(Conntrack.ParseAnswer(Message(0x0100, 7, 2, Attr(3, [0, 0, 0, 8])), 7, out _, out _));   // no tuple
    }

    // ------------------------------------------------------------ the kernel

    /// <summary>
    /// The real thing, in a network namespace of its own (never the machine's firewall):
    /// <c>unshare -rn sh -c 'ip link set lo up &amp;&amp; SLUICE_NETNS_TEST=1 dotnet test tests/Sluice.Tests.Linux --no-build --filter LinuxConntrackTests'</c>.
    /// Two sockets bound to one port connect to 192.0.2.1:80 and 192.0.2.2:80; nftables redirects both to one listener,
    /// so they clash and conntrack renumbers one's source port. For each accepted connection the reply tuple must give
    /// back the app's source port and the right destination.
    /// </summary>
    [LinuxFact]
    public async Task Kernel_finds_both_connections_of_a_source_port_clash()
    {
        if (Environment.GetEnvironmentVariable("SLUICE_NETNS_TEST") != "1") return;   // only in a namespace of its own

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(8);
        var relay = (IPEndPoint)listener.LocalEndPoint!;
        Run("ip", "route add 192.0.2.0/24 dev lo");
        Run("nft", "-f -", $$"""
            table ip t {
                chain out {
                    type nat hook output priority dstnat; policy accept;
                    ip daddr 192.0.2.0/24 tcp dport 80 redirect to :{{relay.Port}}
                }
            }

            """);
        using var conntrack = Conntrack.TryOpen(out var problem) ?? throw new InvalidOperationException(problem);

        var port = Random.Shared.Next(40000, 60000);
        var clients = new List<Socket>();
        foreach (var (dst, tag) in new[] { ("192.0.2.1", (byte)'A'), ("192.0.2.2", (byte)'B') })
        {
            var c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            c.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            c.Bind(new IPEndPoint(IPAddress.Loopback, port));
            await c.ConnectAsync(IPAddress.Parse(dst), 80);
            await c.SendAsync(new[] { tag }, SocketFlags.None);
            clients.Add(c);
        }

        var seenPorts = new List<int>();
        for (var i = 0; i < 2; i++)
        {
            using var accepted = await listener.AcceptAsync();
            var tag = new byte[1];
            await accepted.ReceiveAsync(tag, SocketFlags.None);
            var seen = (IPEndPoint)accepted.RemoteEndPoint!;
            seenPorts.Add(seen.Port);
            Assert.True(conntrack.TryGetOriginal(new Conntrack.Tuple(relay, seen), out var orig, out var errno), $"errno {errno}");
            Assert.Equal(port, orig.Source.Port);                                      // the app's own source port
            Assert.Equal(tag[0] == 'A' ? "192.0.2.1:80" : "192.0.2.2:80", orig.Destination.ToString());
        }
        Assert.Contains(seenPorts, p => p != port);                                     // the clash really happened
        foreach (var c in clients) c.Dispose();
    }

    private static void Run(string file, string args, string? input = null)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardInput = input is not null, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        if (input is not null) { p.StandardInput.Write(input); p.StandardInput.Close(); }
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} {args}: {err}");
    }
}
