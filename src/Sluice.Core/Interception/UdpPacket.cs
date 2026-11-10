using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Sluice.Core.Interception;

/// <summary>Minimal IPv4 / IPv6 + UDP parsing and building (no IP options or extension headers on output).</summary>
public static class UdpPacket
{
    public static bool TryParse(ReadOnlySpan<byte> p, out IPEndPoint src, out IPEndPoint dst, out int payloadOffset, out int payloadLength)
    {
        src = dst = null!;
        payloadOffset = payloadLength = 0;
        if (p.Length < 28) return false;
        int ipLen, total;
        IPAddress s, d;
        switch (p[0] >> 4)
        {
            case 4:
                ipLen = (p[0] & 0xF) * 4;
                if (ipLen < 20 || p[9] != 17) return false;
                // Fragments (other than an unfragmented packet) cannot be handled: the UDP header is only in the first.
                var frag = BinaryPrimitives.ReadUInt16BigEndian(p[6..]);
                if ((frag & 0x3FFF) != 0) return false;
                total = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(p[2..]), p.Length);
                s = new IPAddress(p[12..16]);
                d = new IPAddress(p[16..20]);
                break;
            case 6:
                ipLen = 40;
                if (p[6] != 17 || p.Length < 48) return false;   // extension headers not supported
                total = Math.Min(40 + BinaryPrimitives.ReadUInt16BigEndian(p[4..]), p.Length);
                s = new IPAddress(p[8..24]);
                d = new IPAddress(p[24..40]);
                break;
            default:
                return false;
        }
        if (total < ipLen + 8) return false;
        var udpLen = BinaryPrimitives.ReadUInt16BigEndian(p[(ipLen + 4)..]);
        if (udpLen < 8 || ipLen + udpLen > total) return false;
        src = new IPEndPoint(s, BinaryPrimitives.ReadUInt16BigEndian(p[ipLen..]));
        dst = new IPEndPoint(d, BinaryPrimitives.ReadUInt16BigEndian(p[(ipLen + 2)..]));
        payloadOffset = ipLen + 8;
        payloadLength = udpLen - 8;
        return true;
    }

    /// <summary>A complete IP + UDP packet with valid checksums.</summary>
    public static byte[] Build(IPEndPoint src, IPEndPoint dst, ReadOnlySpan<byte> payload)
    {
        if (src.AddressFamily != dst.AddressFamily) throw new ArgumentException("address families differ");
        var v6 = src.AddressFamily == AddressFamily.InterNetworkV6;
        var ipLen = v6 ? 40 : 20;
        var udpLen = 8 + payload.Length;
        var r = new byte[ipLen + udpLen];
        var sa = src.Address.GetAddressBytes();
        var da = dst.Address.GetAddressBytes();
        if (v6)
        {
            r[0] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), (ushort)udpLen);
            r[6] = 17;
            r[7] = 64;
            sa.CopyTo(r, 8);
            da.CopyTo(r, 24);
        }
        else
        {
            r[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), (ushort)r.Length);
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), 0x4000);   // DF
            r[8] = 64;
            r[9] = 17;
            sa.CopyTo(r, 12);
            da.CopyTo(r, 16);
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(10), Fold(Sum(r.AsSpan(0, 20), 0)));
        }
        var u = r.AsSpan(ipLen);
        BinaryPrimitives.WriteUInt16BigEndian(u, (ushort)src.Port);
        BinaryPrimitives.WriteUInt16BigEndian(u[2..], (ushort)dst.Port);
        BinaryPrimitives.WriteUInt16BigEndian(u[4..], (ushort)udpLen);
        payload.CopyTo(u[8..]);

        // Pseudo-header: src, dst, protocol, UDP length.
        uint sum = Sum(sa, 0);
        sum = Sum(da, sum);
        sum += 17u + (uint)udpLen;
        sum = Sum(u, sum);
        var check = Fold(sum);
        BinaryPrimitives.WriteUInt16BigEndian(u[6..], check == 0 ? (ushort)0xFFFF : check);
        return r;
    }

    private static uint Sum(ReadOnlySpan<byte> b, uint sum)
    {
        var i = 0;
        for (; i + 1 < b.Length; i += 2) sum += (uint)((b[i] << 8) | b[i + 1]);
        if (i < b.Length) sum += (uint)(b[i] << 8);
        return sum;
    }

    private static ushort Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}
