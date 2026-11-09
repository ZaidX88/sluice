using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sluice.Core.Dns;

public enum DnsType : ushort { A = 1, CNAME = 5, AAAA = 28, SVCB = 64, HTTPS = 65 }

/// <summary>A single-question DNS query (the only kind stub resolvers send).</summary>
public sealed record DnsQuery(ushort Id, ushort Flags, string Name, ushort Type, ushort Class, int QuestionEnd)
{
    public bool RecursionDesired => (Flags & 0x0100) != 0;
}

/// <summary>Minimal DNS wire format (RFC 1035) helpers: parse queries, build answers, read A/AAAA replies.</summary>
public static class DnsMessage
{
    public const ushort ClassIN = 1;

    /// <summary>Parse a standard query with exactly one question; null for anything else.</summary>
    public static DnsQuery? TryParseQuery(ReadOnlySpan<byte> msg)
    {
        if (msg.Length < 12) return null;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(msg[2..]);
        if ((flags & 0x8000) != 0) return null;           // a response, not a query
        if (((flags >> 11) & 0xF) != 0) return null;      // opcode != QUERY
        if (BinaryPrimitives.ReadUInt16BigEndian(msg[4..]) != 1) return null;

        var pos = 12;
        var name = ReadName(msg, ref pos, allowPointers: false);
        if (name is null || pos + 4 > msg.Length) return null;
        var type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
        var cls = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 2)..]);
        return new DnsQuery(BinaryPrimitives.ReadUInt16BigEndian(msg), flags, name, type, cls, pos + 4);
    }

    /// <summary>Build a response to <paramref name="q"/> with the given addresses (empty = NODATA).</summary>
    public static byte[] BuildResponse(ReadOnlySpan<byte> query, DnsQuery q, IReadOnlyList<IPAddress> answers, uint ttl, byte rcode = 0)
    {
        var question = query[12..q.QuestionEnd];
        var size = 12 + question.Length + answers.Sum(a => 12 + a.GetAddressBytes().Length);
        var r = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(r, q.Id);
        var flags = (ushort)(0x8000 | 0x0080 | (q.Flags & 0x0100) | (rcode & 0xF)); // QR, RA, copy RD
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), (ushort)answers.Count);
        question.CopyTo(r.AsSpan(12));

        var pos = 12 + question.Length;
        foreach (var ip in answers)
        {
            var bytes = ip.GetAddressBytes();
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(pos), 0xC00C);   // pointer to the question name
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(pos + 2), (ushort)(bytes.Length == 4 ? DnsType.A : DnsType.AAAA));
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(pos + 4), ClassIN);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(pos + 6), ttl);
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(pos + 10), (ushort)bytes.Length);
            bytes.CopyTo(r, pos + 12);
            pos += 12 + bytes.Length;
        }
        return r;
    }

    /// <summary>Encode a query for <paramref name="name"/> (used by our own resolver).</summary>
    public static byte[] BuildQuery(ushort id, string name, DnsType type)
    {
        var labels = name.TrimEnd('.').Split('.');
        var size = 12 + labels.Sum(l => 1 + Encoding.ASCII.GetByteCount(l)) + 1 + 4;
        var q = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(q, id);
        BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(2), 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(4), 1);
        var pos = 12;
        foreach (var l in labels)
        {
            var b = Encoding.ASCII.GetBytes(l);
            if (b.Length is 0 or > 63) throw new ArgumentException($"Invalid DNS name '{name}'");
            q[pos++] = (byte)b.Length;
            b.CopyTo(q, pos);
            pos += b.Length;
        }
        q[pos++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(pos), (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(pos + 2), ClassIN);
        return q;
    }

    /// <summary>Extract A/AAAA addresses from a response; rcode via out parameter.</summary>
    public static List<IPAddress> ParseAddresses(ReadOnlySpan<byte> msg, out ushort id, out int rcode)
    {
        var result = new List<IPAddress>();
        id = 0;
        rcode = -1;
        if (msg.Length < 12) return result;
        id = BinaryPrimitives.ReadUInt16BigEndian(msg);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(msg[2..]);
        rcode = flags & 0xF;
        var qd = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            if (ReadName(msg, ref pos, allowPointers: true) is null) return result;
            pos += 4;
        }
        for (var i = 0; i < an && pos < msg.Length; i++)
        {
            if (ReadName(msg, ref pos, allowPointers: true) is null || pos + 10 > msg.Length) break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
            var len = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 8)..]);
            pos += 10;
            if (pos + len > msg.Length) break;
            if (type == (ushort)DnsType.A && len == 4) result.Add(new IPAddress(msg.Slice(pos, 4)));
            else if (type == (ushort)DnsType.AAAA && len == 16) result.Add(new IPAddress(msg.Slice(pos, 16)));
            pos += len;
        }
        return result;
    }

    /// <summary>Read a (possibly compressed) domain name, advancing <paramref name="pos"/> past it.</summary>
    private static string? ReadName(ReadOnlySpan<byte> msg, ref int pos, bool allowPointers)
    {
        var sb = new StringBuilder();
        var p = pos;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            if (p >= msg.Length) return null;
            var len = msg[p];
            if (len == 0)
            {
                if (!jumped) pos = p + 1;
                return sb.Length == 0 ? "." : sb.ToString();
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (!allowPointers || p + 1 >= msg.Length) return null;
                if (!jumped) pos = p + 2;
                jumped = true;
                p = ((len & 0x3F) << 8) | msg[p + 1];
                continue;
            }
            if ((len & 0xC0) != 0 || p + 1 + len > msg.Length) return null;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(msg.Slice(p + 1, len)));
            p += 1 + len;
        }
        return null;
    }

    public static bool IsIPv4(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetwork;
}
