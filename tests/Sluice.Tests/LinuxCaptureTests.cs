using System.Buffers.Binary;
using System.Net;
using Sluice.Core.Dns;
using Sluice.Core.Interception;
using Sluice.Core.Profiles;
using Sluice.Platform.Linux;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>The OS-independent parts of Linux capture: message formats, /proc parsing, the ruleset.</summary>
public class LinuxCaptureTests
{
    [Fact]
    public void Verdict_message_has_header_verdict_and_mark()
    {
        var m = NfQueue.VerdictMessage(seq: 7, queue: 0x5C1, id: 42, verdict: NfQueue.Accept, mark: 0x5C1CE);

        Assert.Equal(m.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(m));
        Assert.Equal((3 << 8) | 1, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(4)));   // NFQNL_MSG_VERDICT
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(8)));
        Assert.Equal(0x5C1, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(18)));            // res_id = queue
        // NFQA_VERDICT_HDR { be32 verdict, be32 id }
        Assert.Equal(12, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(20)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(22)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(24)));
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(28)));
        // NFQA_MARK
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(34)));
        Assert.Equal(0x5C1CEu, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(36)));
        Assert.Equal(40, m.Length);
    }

    [Fact]
    public void Queued_packets_are_parsed_from_a_netlink_datagram()
    {
        var ip = Ipv4Syn(IPAddress.Parse("192.168.1.5"), 40000, IPAddress.Parse("93.184.216.34"), 443);
        var data = Concat(PacketMessage(11, ip), PacketMessage(12, ip));

        var packets = NfQueue.Parse(data);

        Assert.Equal([11u, 12u], packets.Select(p => p.Id));
        Assert.True(PacketInfo.TryParse(packets[0].Payload, out var f));
        Assert.Equal(PacketInfo.Tcp, f.Protocol);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.5"), 40000), f.Source);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 443), f.Destination);
    }

    [Fact]
    public void Ipv6_udp_packet_is_parsed()
    {
        var p = new byte[48];
        p[0] = 0x60;
        p[6] = 17;
        IPAddress.Parse("2001:db8::5").GetAddressBytes().CopyTo(p, 8);
        IPAddress.Parse("2001:db8::1").GetAddressBytes().CopyTo(p, 24);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(40), 5555);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(42), 443);

        Assert.True(PacketInfo.TryParse(p, out var f));
        Assert.Equal(PacketInfo.Udp, f.Protocol);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("2001:db8::5"), 5555), f.Source);
        Assert.Equal(443, f.Destination.Port);
    }

    [Fact]
    public void Proc_net_lines_are_parsed()
    {
        Assert.True(LinuxProcessLookup.TryParseLine(
            "   1: 0501A8C0:9C40 22D8B85D:01BB 02 00000000:00000000 01:00000064 00000000  1000        0 98765 1 0000000000000000 100 0 0 10 0",
            out var v4));
        Assert.Equal(IPAddress.Parse("192.168.1.5"), v4.Local);
        Assert.Equal(40000, v4.LocalPort);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), v4.Remote);
        Assert.Equal(443, v4.RemotePort);
        Assert.Equal(2, v4.State);     // SYN_SENT
        Assert.Equal(98765ul, v4.Inode);

        // tcp6: ::ffff:127.0.0.1 listening on 8080
        Assert.True(LinuxProcessLookup.TryParseLine(
            "   0: 0000000000000000FFFF00000100007F:1F90 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 4242 1 0000000000000000 100 0 0 10 0",
            out var v6));
        Assert.Equal(IPAddress.Parse("::ffff:127.0.0.1"), v6.Local);
        Assert.Equal(8080, v6.LocalPort);
        Assert.Equal(0x0A, v6.State);
    }

    [Fact]
    public void Socket_owner_is_found_through_proc()
    {
        if (!OperatingSystem.IsLinux()) return;   // needs Linux-style symlinks ("socket:[inode]")
        var root = Directory.CreateTempSubdirectory("sluice-proc").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "net"));
            File.WriteAllText(Path.Combine(root, "net", "tcp"),
                "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n" +
                "   1: 0501A8C0:9C40 22D8B85D:01BB 02 00000000:00000000 01:00000064 00000000  1000        0 98765 1\n");
            File.WriteAllText(Path.Combine(root, "net", "tcp6"), "header\n");
            var fd = Path.Combine(root, "1234", "fd");
            Directory.CreateDirectory(fd);
            File.CreateSymbolicLink(Path.Combine(fd, "7"), "socket:[98765]");
            File.CreateSymbolicLink(Path.Combine(root, "1234", "exe"), "/usr/bin/curl");

            var lookup = new LinuxProcessLookup(root);
            var pid = lookup.FindTcpOwner(new IPEndPoint(IPAddress.Parse("192.168.1.5"), 40000), new IPEndPoint(IPAddress.Parse("93.184.216.34"), 443));

            Assert.Equal(1234, pid);
            Assert.Equal("/usr/bin/curl", lookup.GetProcessPath(pid));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Ruleset_queues_syns_redirects_marked_ones_and_skips_sluice()
    {
        var r = NftRules.Build(new NftRules.Settings(Queue: 1473, Mark: 0x5C1CE, Gid: 378318, RelayPort: 41000, DnsPort: 42000, QueueUdp: true));

        Assert.Contains("table inet sluice {", r);
        Assert.Contains("delete table inet sluice", r);
        Assert.Contains("meta skgid 378318 return", r);
        Assert.Contains("tcp flags & (fin | syn | rst | ack) == syn ct state new queue num 1473 bypass", r);
        Assert.Contains("udp dport != 53 ct state new queue num 1473 bypass", r);
        Assert.Contains("meta mark 377294 meta l4proto tcp redirect to :41000", r);
        Assert.Contains("udp dport 53 redirect to :42000", r);

        var noUdp = NftRules.Build(new NftRules.Settings(1473, 1, 2, 41000, null, QueueUdp: false));
        Assert.DoesNotContain("udp", noUdp);

        // nft rejects CR ("unexpected CRLF line terminators"); the source file may be checked out with CRLF.
        Assert.DoesNotContain('\r', r);
        Assert.DoesNotContain('\r', noUdp);
    }

    [Fact]
    public void Upstream_dns_skips_local_stubs()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "# generated\nnameserver 127.0.0.53\nnameserver 2001:4860:4860::8888\nnameserver fe80::1%eth0\nnameserver 10.204.8.3\noptions edns0\n");
            Assert.Equal([IPAddress.Parse("10.204.8.3"), IPAddress.Parse("2001:4860:4860::8888")], LinuxDnsServer.ParseResolvConf(f));
        }
        finally
        {
            File.Delete(f);
        }
    }

    [Fact]
    public void Network_group_avoids_existing_groups()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "root:x:0:\nodd:x:377294:\nodd2:x:377295:\n");
            Assert.Equal(377296u, SluiceGroup.Pick(f));
            Assert.Equal(377294u, SluiceGroup.Pick(Path.Combine(Path.GetTempPath(), "no-such-group-file")));
        }
        finally
        {
            File.Delete(f);
        }
    }

    [Fact]
    public void Dns_query_payload_gets_a_fake_answer()
    {
        var pool = new FakeIpPool();
        var policy = new DnsPolicy(new ResolveOptions { ViaProxy = true }, leakPrevention: false, computerName: "box");
        var r = new DnsRedirector(pool, () => policy, ownResolverPort: 1);

        var reply = r.TryAnswerQuery(DnsQuery("example.com", 1));

        Assert.NotNull(reply);
        Assert.Equal(0x12, reply![0]);                                         // same ID
        Assert.True((reply[2] & 0x80) != 0);                                   // QR: response
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6))); // one answer
        Assert.Equal(pool.GetOrAdd("example.com").GetAddressBytes(), reply[^4..]);
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Ipv4Syn(IPAddress src, int sport, IPAddress dst, int dport)
    {
        var p = new byte[40];
        p[0] = 0x45;
        p[9] = 6;
        src.GetAddressBytes().CopyTo(p, 12);
        dst.GetAddressBytes().CopyTo(p, 16);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), (ushort)sport);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), (ushort)dport);
        p[33] = 0x02;   // SYN
        return p;
    }

    /// <summary>NFQNL_MSG_PACKET with NFQA_PACKET_HDR and NFQA_PAYLOAD, as the kernel sends it.</summary>
    private static byte[] PacketMessage(uint id, byte[] payload)
    {
        var hdrAttr = new byte[4 + 8];                      // { be32 id, be16 hw_protocol, u8 hook } padded
        BinaryPrimitives.WriteUInt16LittleEndian(hdrAttr, 4 + 7);
        BinaryPrimitives.WriteUInt16LittleEndian(hdrAttr.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(hdrAttr.AsSpan(4), id);
        BinaryPrimitives.WriteUInt16BigEndian(hdrAttr.AsSpan(8), 0x0800);
        hdrAttr[10] = 3;                                     // NF_INET_LOCAL_OUT
        var payAttr = new byte[(4 + payload.Length + 3) & ~3];
        BinaryPrimitives.WriteUInt16LittleEndian(payAttr, (ushort)(4 + payload.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(payAttr.AsSpan(2), 10);
        payload.CopyTo(payAttr, 4);
        var msg = new byte[16 + 4 + hdrAttr.Length + payAttr.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(msg, (uint)msg.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(4), 3 << 8);
        hdrAttr.CopyTo(msg, 20);
        payAttr.CopyTo(msg, 20 + hdrAttr.Length);
        return msg;
    }

    private static byte[] DnsQuery(string name, ushort type)
    {
        var q = new List<byte> { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            q.Add((byte)label.Length);
            q.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        q.AddRange(new byte[] { 0, (byte)(type >> 8), (byte)type, 0, 1 });
        return q.ToArray();
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
