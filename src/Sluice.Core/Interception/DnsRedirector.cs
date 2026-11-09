using System.Buffers.Binary;
using Sluice.Core.Dns;

namespace Sluice.Core.Interception;

/// <summary>
/// Answers intercepted outbound DNS queries (UDP/53) locally with fake IPs, so the hostname
/// is preserved for the proxy. Pure packet logic: input is the captured outbound IP packet,
/// output is a complete inbound reply packet to inject (checksums are left to WinDivert).
/// </summary>
public sealed class DnsRedirector(FakeIpPool pool, Func<DnsPolicy> policy, int ownResolverPort, uint ttlSeconds = 5)
{
    public event Action<string, ushort>? Answered;

    /// <summary>Reply packet to inject inbound, or null to let the query through unchanged.</summary>
    public byte[]? TryAnswer(ReadOnlySpan<byte> packet)
    {
        if (!TryParseUdp(packet, out var ipLen, out var srcPort, out var dstPort, out var v6)) return null;
        if (dstPort != 53 || srcPort == ownResolverPort) return null;

        var dns = packet[(ipLen + 8)..];
        var q = DnsMessage.TryParseQuery(dns);
        if (q is null || q.Class != DnsMessage.ClassIN) return null;

        var p = policy();
        if (!p.ShouldFake(q.Name)) return null;

        byte[] answer;
        switch ((DnsType)q.Type)
        {
            case DnsType.A:
                answer = DnsMessage.BuildResponse(dns, q, [pool.GetOrAdd(q.Name)], ttlSeconds);
                break;
            case DnsType.AAAA:
            case DnsType.HTTPS:
            case DnsType.SVCB:
                // No IPv6 fakes, and HTTPS/SVCB records can carry real address hints: answer "no data".
                answer = DnsMessage.BuildResponse(dns, q, [], ttlSeconds);
                break;
            default:
                if (!p.BlockNonATypes) return null;
                answer = DnsMessage.BuildResponse(dns, q, [], ttlSeconds);
                break;
        }

        Answered?.Invoke(q.Name, q.Type);
        return BuildReply(packet, ipLen, v6, answer);
    }

    private static bool TryParseUdp(ReadOnlySpan<byte> p, out int ipLen, out ushort src, out ushort dst, out bool v6)
    {
        ipLen = 0; src = dst = 0; v6 = false;
        if (p.Length < 28) return false;
        switch (p[0] >> 4)
        {
            case 4:
                ipLen = (p[0] & 0xF) * 4;
                if (ipLen < 20 || p[9] != 17) return false;
                break;
            case 6:
                ipLen = 40;
                v6 = true;
                if (p[6] != 17) return false;
                break;
            default:
                return false;
        }
        if (p.Length < ipLen + 8) return false;
        src = BinaryPrimitives.ReadUInt16BigEndian(p[ipLen..]);
        dst = BinaryPrimitives.ReadUInt16BigEndian(p[(ipLen + 2)..]);
        return true;
    }

    /// <summary>Same IP header with addresses and ports swapped, new UDP payload, lengths fixed.</summary>
    private static byte[] BuildReply(ReadOnlySpan<byte> query, int ipLen, bool v6, byte[] dns)
    {
        var r = new byte[ipLen + 8 + dns.Length];
        query[..ipLen].CopyTo(r);

        int addrLen = v6 ? 16 : 4, srcOff = v6 ? 8 : 12, dstOff = srcOff + addrLen;
        query.Slice(srcOff, addrLen).CopyTo(r.AsSpan(dstOff));
        query.Slice(dstOff, addrLen).CopyTo(r.AsSpan(srcOff));

        if (v6)
        {
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), (ushort)(8 + dns.Length));
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), (ushort)r.Length);
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), 0x4000); // DF, no fragments
            r[10] = r[11] = 0;                                         // header checksum (recomputed)
        }

        // UDP: ports swapped, length, checksum zeroed for recomputation.
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(ipLen), BinaryPrimitives.ReadUInt16BigEndian(query[(ipLen + 2)..]));
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(ipLen + 2), BinaryPrimitives.ReadUInt16BigEndian(query[ipLen..]));
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(ipLen + 4), (ushort)(8 + dns.Length));
        r[ipLen + 6] = r[ipLen + 7] = 0;
        dns.CopyTo(r, ipLen + 8);
        return r;
    }
}
