using System.Buffers.Binary;
using System.Net;

namespace Sluice.Core.Interception;

/// <summary>
/// Minimal in-place view over an IPv4/IPv6 + TCP packet: read endpoints, swap addresses,
/// set ports. Checksums are recalculated by WinDivert afterwards.
/// </summary>
public readonly ref struct TcpPacket
{
    private readonly Span<byte> _p;
    private readonly bool _v6;
    private readonly int _tcp;

    public bool IsValid { get; }

    public TcpPacket(Span<byte> packet)
    {
        _p = packet;
        IsValid = false;
        _v6 = false;
        _tcp = 0;
        if (packet.Length < 20) return;
        var version = packet[0] >> 4;
        if (version == 4)
        {
            var ihl = (packet[0] & 0x0F) * 4;
            if (ihl < 20 || packet[9] != 6 || packet.Length < ihl + 20) return;
            _tcp = ihl;
        }
        else if (version == 6)
        {
            // Extension headers are not followed; such packets are passed through untouched.
            if (packet.Length < 60 || packet[6] != 6) return;
            _v6 = true;
            _tcp = 40;
        }
        else return;
        IsValid = true;
    }

    public bool IsIPv6 => _v6;

    public IPAddress SrcAddr => new(_v6 ? _p.Slice(8, 16) : _p.Slice(12, 4));
    public IPAddress DstAddr => new(_v6 ? _p.Slice(24, 16) : _p.Slice(16, 4));

    public ushort SrcPort
    {
        get => BinaryPrimitives.ReadUInt16BigEndian(_p.Slice(_tcp));
        set => BinaryPrimitives.WriteUInt16BigEndian(_p.Slice(_tcp), value);
    }

    public ushort DstPort
    {
        get => BinaryPrimitives.ReadUInt16BigEndian(_p.Slice(_tcp + 2));
        set => BinaryPrimitives.WriteUInt16BigEndian(_p.Slice(_tcp + 2), value);
    }

    private byte Flags => _p[_tcp + 13];
    public bool Syn => (Flags & 0x02) != 0;
    public bool Ack => (Flags & 0x10) != 0;
    public bool Fin => (Flags & 0x01) != 0;
    public bool Rst => (Flags & 0x04) != 0;

    /// <summary>Exchange source and destination IP addresses.</summary>
    public void SwapAddresses()
    {
        int len = _v6 ? 16 : 4, src = _v6 ? 8 : 12, dst = src + len;
        Span<byte> tmp = stackalloc byte[16];
        _p.Slice(src, len).CopyTo(tmp);
        _p.Slice(dst, len).CopyTo(_p.Slice(src, len));
        tmp[..len].CopyTo(_p.Slice(dst, len));
    }
}
