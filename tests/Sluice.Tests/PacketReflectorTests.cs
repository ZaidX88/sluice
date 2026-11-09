using System.Buffers.Binary;
using System.Net;
using Sluice.Core.Interception;

namespace Sluice.Tests;

public class PacketReflectorTests
{
    private const ushort Relay = 40000;
    private static readonly IPAddress L4 = IPAddress.Parse("192.168.1.10");
    private static readonly IPAddress R4 = IPAddress.Parse("93.184.216.34");
    private static readonly IPAddress L6 = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress R6 = IPAddress.Parse("2606:2800:220:1::1");

    private const byte SYN = 0x02, ACK = 0x10;

    /// <summary>Build a minimal IPv4/IPv6 + 20-byte TCP header packet.</summary>
    private static byte[] Packet(IPAddress src, ushort sport, IPAddress dst, ushort dport, byte flags)
    {
        var v6 = src.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var ipLen = v6 ? 40 : 20;
        var p = new byte[ipLen + 20];
        if (v6)
        {
            p[0] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), 20);
            p[6] = 6;
            p[7] = 64;
            src.GetAddressBytes().CopyTo(p, 8);
            dst.GetAddressBytes().CopyTo(p, 24);
        }
        else
        {
            p[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
            p[8] = 64;
            p[9] = 6;
            src.GetAddressBytes().CopyTo(p, 12);
            dst.GetAddressBytes().CopyTo(p, 16);
        }
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(ipLen), sport);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(ipLen + 2), dport);
        p[ipLen + 12] = 0x50;
        p[ipLen + 13] = flags;
        return p;
    }

    private static (IPAddress Src, ushort SPort, IPAddress Dst, ushort DPort) Read(byte[] p)
    {
        var t = new TcpPacket(p);
        return (t.SrcAddr, t.SrcPort, t.DstAddr, t.DstPort);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedirectedFlow_RoundTrip(bool v6)
    {
        var (l, r) = v6 ? (L6, R6) : (L4, R4);
        var calls = 0;
        var refl = new PacketReflector(Relay, (local, remote) =>
        {
            calls++;
            Assert.Equal(new IPEndPoint(l, 50000), local);
            Assert.Equal(new IPEndPoint(r, 443), remote);
            return (true, 4242);
        });

        // App SYN L:50000 → R:443 is reflected to R:50000 → L:relay.
        var syn = Packet(l, 50000, r, 443, SYN);
        Assert.Equal(PacketReflector.Verdict.Reflected, refl.ProcessOutbound(syn));
        Assert.Equal((r, (ushort)50000, l, Relay), Read(syn));

        // The relay can recover the original destination and PID.
        Assert.True(refl.TryGetOriginal(new IPEndPoint(r, 50000), out var orig));
        Assert.Equal(new IPEndPoint(r, 443), orig.Destination);
        Assert.Equal(4242, orig.ProcessId);

        // Relay SYN-ACK L:relay → R:50000 is reflected to R:443 → L:50000.
        var synAck = Packet(l, Relay, r, 50000, SYN | ACK);
        Assert.Equal(PacketReflector.Verdict.Reflected, refl.ProcessOutbound(synAck));
        Assert.Equal((r, (ushort)443, l, (ushort)50000), Read(synAck));

        // Later app data on the same flow keeps being reflected without asking again.
        var data = Packet(l, 50000, r, 443, ACK);
        Assert.Equal(PacketReflector.Verdict.Reflected, refl.ProcessOutbound(data));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DirectFlow_PassesUntouched()
    {
        var refl = new PacketReflector(Relay, (_, _) => (false, 7));
        var syn = Packet(L4, 50001, R4, 80, SYN);
        var copy = (byte[])syn.Clone();

        Assert.Equal(PacketReflector.Verdict.Pass, refl.ProcessOutbound(syn));
        Assert.Equal(copy, syn);
        Assert.Equal(PacketReflector.Verdict.Pass, refl.ProcessOutbound(Packet(L4, 50001, R4, 80, ACK)));
        Assert.False(refl.TryGetOriginal(new IPEndPoint(R4, 50001), out _));
    }

    [Fact]
    public void UnknownNonSynFlow_Passes_WithoutDeciding()
    {
        // Connections that existed before interception started are left alone.
        var refl = new PacketReflector(Relay, (_, _) => throw new Xunit.Sdk.XunitException("must not decide"));
        Assert.Equal(PacketReflector.Verdict.Pass, refl.ProcessOutbound(Packet(L4, 50002, R4, 443, ACK)));
        Assert.Equal(PacketReflector.Verdict.Pass, refl.ProcessOutbound(Packet(L4, Relay, R4, 50002, ACK)));
    }

    [Fact]
    public void SameSourcePortToDifferentRemotePort_IsANewFlow()
    {
        var decisions = new Queue<bool>([true, false]);
        var refl = new PacketReflector(Relay, (_, _) => (decisions.Dequeue(), 1));

        Assert.Equal(PacketReflector.Verdict.Reflected, refl.ProcessOutbound(Packet(L4, 50003, R4, 443, SYN)));
        Assert.Equal(PacketReflector.Verdict.Pass, refl.ProcessOutbound(Packet(L4, 50003, R4, 8443, SYN)));
    }

    [Fact]
    public void Sweep_RemovesIdleFlows()
    {
        var refl = new PacketReflector(Relay, (_, _) => (true, 1));
        refl.ProcessOutbound(Packet(L4, 50004, R4, 443, SYN));
        Assert.Equal(1, refl.FlowCount);
        Assert.Equal(0, refl.Sweep(TimeSpan.FromMinutes(1)));
        Assert.Equal(1, refl.Sweep(TimeSpan.Zero - TimeSpan.FromSeconds(1)));
        Assert.Equal(0, refl.FlowCount);
    }

    [Fact]
    public void NonTcpOrGarbage_Passes()
    {
        var refl = new PacketReflector(Relay, (_, _) => (true, 1));
        var udp = Packet(L4, 1, R4, 53, SYN);
        udp[9] = 17;
        Assert.Equal(PacketReflector.Verdict.Pass, refl.ProcessOutbound(udp));
        Assert.Equal(PacketReflector.Verdict.Pass, refl.ProcessOutbound(new byte[10]));
    }
}
