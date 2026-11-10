using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Proxy;

namespace Sluice.Tests;

public class HappyEyeballsTests
{
    [Fact]
    public async Task DeadFirstAddress_DoesNotDelayTheWorkingOne()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            // 192.0.2.1 (TEST-NET-1) never answers: like a NAT64 address on a network without IPv6.
            var sw = Stopwatch.StartNew();
            using var c = await HappyEyeballs.ConnectAsync([IPAddress.Parse("192.0.2.1"), IPAddress.Loopback], port, default)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(c.Connected);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
            Assert.Equal(IPAddress.Loopback, ((IPEndPoint)c.Client.RemoteEndPoint!).Address);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AllFail_ThrowsTheLastError()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();   // nothing listens now: refused
        await Assert.ThrowsAsync<SocketException>(() => HappyEyeballs.ConnectAsync([IPAddress.Loopback, IPAddress.IPv6Loopback], port, default));
        await Assert.ThrowsAsync<SocketException>(() => HappyEyeballs.ConnectAsync([], port, default));
    }

    [Fact]
    public void Interleave_AlternatesFamilies_AndDropsDuplicates()
    {
        var v6a = IPAddress.Parse("64:ff9b::cb00:711c");
        var v6b = IPAddress.Parse("2001:db8::2");
        var v4a = IPAddress.Parse("203.0.113.28");
        var v4b = IPAddress.Parse("192.0.2.9");
        Assert.Equal([v6a, v4a, v6b, v4b], HappyEyeballs.Interleave([v6a, v6b, v4a, v4b, v4a.MapToIPv6()]));
    }
}
