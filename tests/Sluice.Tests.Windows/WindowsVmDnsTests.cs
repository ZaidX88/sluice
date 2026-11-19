using System.Buffers.Binary;
using System.Net;
using Sluice.Core.Dns;
using Sluice.Core.Interception;

namespace Sluice.Tests;

/// <summary>
/// DNS for WSL and virtual machines (docs/windows/FIXES.md §1): the question Sluice takes from a VM on its way to
/// Windows' DNS proxy, and the reply packet it sends back.
/// </summary>
public class WindowsVmDnsTests
{
    private static readonly IPAddress Vm = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress Host = IPAddress.Parse("192.0.2.1");

    /// <summary>An IPv4/UDP packet from the VM's port 44609 to the host's port 53 carrying <paramref name="dns"/>.</summary>
    private static byte[] Question(byte[] dns, ushort dstPort = 53)
    {
        var p = new byte[20 + 8 + dns.Length];
        p[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
        p[8] = 64;
        p[9] = 17;
        Vm.GetAddressBytes().CopyTo(p, 12);
        Host.GetAddressBytes().CopyTo(p, 16);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), 44609);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), (ushort)(8 + dns.Length));
        dns.CopyTo(p, 28);
        return p;
    }

    [Fact]
    public void A_question_to_port_53_is_read()
    {
        var q = Interceptor.VmDnsQuestion(Question(DnsMessage.BuildQuery(0x1234, "example.com", DnsType.A)));
        Assert.NotNull(q);
        Assert.Equal("example.com", q.Value.Query.Name);
        Assert.Equal((ushort)DnsType.A, q.Value.Query.Type);
        Assert.Equal(20, q.Value.UdpOffset);
    }

    [Fact]
    public void Other_packets_are_not_questions()
    {
        var dns = DnsMessage.BuildQuery(1, "example.com", DnsType.A);
        Assert.Null(Interceptor.VmDnsQuestion(Question(dns, dstPort: 5353)));
        var tcp = Question(dns);
        tcp[9] = 6;
        Assert.Null(Interceptor.VmDnsQuestion(tcp));
        Assert.Null(Interceptor.VmDnsQuestion(Question([1, 2, 3])));
    }

    [Fact]
    public void The_reply_goes_from_the_host_back_to_the_vm()
    {
        var question = Question(DnsMessage.BuildQuery(0x1234, "example.com", DnsType.A));
        var q = Interceptor.VmDnsQuestion(question)!.Value;
        var dns = DnsMessage.BuildResponse(question.AsSpan(28), q.Query, [IPAddress.Parse("203.0.113.7")], 30);
        var reply = Interceptor.BuildVmDnsReply(question, q.UdpOffset, dns);

        Assert.Equal(reply.Length, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)));
        Assert.Equal(Host, new IPAddress(reply.AsSpan(12, 4)));
        Assert.Equal(Vm, new IPAddress(reply.AsSpan(16, 4)));
        Assert.Equal(53, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)));
        Assert.Equal(44609, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(22)));
        Assert.Equal(8 + dns.Length, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(24)));
        var addrs = DnsMessage.ParseAddresses(reply.AsSpan(28), out var id, out var rcode);
        Assert.Equal(0x1234, id);
        Assert.Equal(0, rcode);
        Assert.Equal([IPAddress.Parse("203.0.113.7")], addrs);
    }

    [Theory]
    [InlineData("desktop-1.mshome.net", true)]
    [InlineData("MSHOME.NET.", true)]
    [InlineData("host.docker.internal", true)]
    [InlineData("printer.lan", true)]
    [InlineData("nas", true)]
    [InlineData("example.com", false)]
    [InlineData("mshome.net.example.com", false)]
    public void Names_of_the_proxy_and_the_local_network_stay_with_it(string name, bool proxys)
        => Assert.Equal(proxys, Interceptor.IsProxyOwnName(name));

    [Fact]
    public void WinDivert_accepts_the_filter()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(WinDivertNative.CheckFilter(Interceptor.VmDnsFilter, WinDivertNative.Layer.Network));
    }
}
