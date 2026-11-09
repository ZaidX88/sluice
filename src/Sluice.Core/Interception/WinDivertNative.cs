using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Sluice.Core.Interception;

/// <summary>P/Invoke surface for WinDivert 2.2 (WinDivert.dll + WinDivert64.sys next to the exe).</summary>
internal static class WinDivertNative
{
    private const string Dll = "WinDivert.dll";

    public enum Layer : int { Network = 0, NetworkForward = 1, Flow = 2, Socket = 3, Reflect = 4 }

    public enum Event : byte
    {
        NetworkPacket = 0, FlowEstablished = 1, FlowDeleted = 2,
        SocketBind = 3, SocketConnect = 4, SocketListen = 5, SocketAccept = 6, SocketClose = 7,
    }

    [Flags]
    public enum OpenFlags : ulong { None = 0, Sniff = 0x1, Drop = 0x2, RecvOnly = 0x4, SendOnly = 0x8, NoInstall = 0x10, Fragments = 0x20 }

    public enum Shutdown : int { Recv = 1, Send = 2, Both = 3 }

    /// <summary>WINDIVERT_ADDRESS (80 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct Address
    {
        [FieldOffset(0)] public long Timestamp;
        [FieldOffset(8)] public uint Bits;      // Layer:8 Event:8 Sniffed:1 Outbound:1 Loopback:1 Impostor:1 IPv6:1 IPChk:1 TCPChk:1 UDPChk:1
        [FieldOffset(12)] public uint Reserved2;

        // Network layer
        [FieldOffset(16)] public uint IfIdx;
        [FieldOffset(20)] public uint SubIfIdx;

        // Socket layer
        [FieldOffset(16)] public ulong EndpointId;
        [FieldOffset(24)] public ulong ParentEndpointId;
        [FieldOffset(32)] public uint ProcessId;
        [FieldOffset(36)] public Ipv6Words LocalAddr;
        [FieldOffset(52)] public Ipv6Words RemoteAddr;
        [FieldOffset(68)] public ushort LocalPort;
        [FieldOffset(70)] public ushort RemotePort;
        [FieldOffset(72)] public byte Protocol;

        public readonly Event Event => (Event)((Bits >> 8) & 0xFF);
        public readonly bool Outbound => (Bits & (1u << 17)) != 0;
        public readonly bool Loopback => (Bits & (1u << 18)) != 0;
        public readonly bool IPv6 => (Bits & (1u << 20)) != 0;

        public void SetOutbound(bool value)
        {
            if (value) Bits |= 1u << 17; else Bits &= ~(1u << 17);
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    public struct Ipv6Words
    {
        public uint W0, W1, W2, W3;
    }

    [DllImport(Dll, EntryPoint = "WinDivertOpen", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr Open(string filter, Layer layer, short priority, OpenFlags flags);

    [DllImport(Dll, EntryPoint = "WinDivertRecv", SetLastError = true)]
    public static extern bool Recv(IntPtr handle, byte[]? packet, uint packetLen, out uint recvLen, ref Address addr);

    [DllImport(Dll, EntryPoint = "WinDivertSend", SetLastError = true)]
    public static extern bool Send(IntPtr handle, byte[] packet, uint packetLen, out uint sendLen, ref Address addr);

    [DllImport(Dll, EntryPoint = "WinDivertShutdown", SetLastError = true)]
    public static extern bool ShutdownHandle(IntPtr handle, Shutdown how);

    [DllImport(Dll, EntryPoint = "WinDivertClose", SetLastError = true)]
    public static extern bool Close(IntPtr handle);

    [DllImport(Dll, EntryPoint = "WinDivertHelperCalcChecksums")]
    public static extern bool CalcChecksums(byte[] packet, uint packetLen, ref Address addr, ulong flags);

    [DllImport(Dll, EntryPoint = "WinDivertHelperFormatIPv6Address", CharSet = CharSet.Ansi)]
    private static extern bool FormatIPv6(ref Ipv6Words addr, StringBuilder buffer, uint bufLen);

    /// <summary>Socket-layer addresses are WinDivert's internal IPv6 form (IPv4 appears as ::ffff:a.b.c.d).</summary>
    public static IPAddress ToIPAddress(Ipv6Words words)
    {
        var sb = new StringBuilder(64);
        if (!FormatIPv6(ref words, sb, (uint)sb.Capacity)) return IPAddress.IPv6None;
        var ip = IPAddress.Parse(sb.ToString());
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    public static IntPtr InvalidHandle => new(-1);
}
