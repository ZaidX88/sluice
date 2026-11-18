using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Profiles;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>
/// Linux capture's relay listens on loopback only: nftables' redirect sends local IPv4 connections to 127.0.0.1 and IPv6
/// ones to ::1 (same port), so nothing needs to reach it from the network.
/// </summary>
public class LinuxRelayTests
{
    private sealed class NoRedirects : IRedirectTable
    {
        public bool TryGetOriginal(IPEndPoint clientEndPoint, out OriginalDestination original)
        {
            original = null!;
            return false;
        }
    }

    private static LoopbackRelay NewRelay() => LoopbackRelay.Bind(new RoutingEngine(Profile.CreateDefault()), new NoRedirects());

    [Fact]
    public async Task Relay_listens_on_both_loopback_addresses_with_one_port()
    {
        await using var relay = NewRelay();

        Assert.Equal(IPAddress.Loopback, relay.V4.LocalEndPoint.Address);
        Assert.True(relay.Port > 0);
        Assert.Equal(relay.Port, relay.V4.LocalEndPoint.Port);
        if (relay.V6 is { } v6)        // null only on a host without IPv6
        {
            Assert.Equal(IPAddress.IPv6Loopback, v6.LocalEndPoint.Address);
            Assert.Equal(relay.Port, v6.LocalEndPoint.Port);
        }
    }

    [Fact]
    public async Task Relay_accepts_connections_on_both_loopback_addresses()
    {
        await using var relay = NewRelay();
        relay.Start();

        using (var c4 = new TcpClient(AddressFamily.InterNetwork))
            await c4.ConnectAsync(IPAddress.Loopback, relay.Port).WaitAsync(TimeSpan.FromSeconds(5));
        if (relay.V6 is not null)
        {
            using var c6 = new TcpClient(AddressFamily.InterNetworkV6);
            await c6.ConnectAsync(IPAddress.IPv6Loopback, relay.Port).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Relay_is_not_reachable_on_the_machines_network_addresses()
    {
        await using var relay = NewRelay();
        relay.Start();

        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
            .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal)
            .ToList();
        // A machine with no network address can't reach the relay from outside anyway.
        foreach (var address in addresses)
        {
            using var client = new TcpClient(address.AddressFamily);
            var ex = await Assert.ThrowsAsync<SocketException>(
                () => client.ConnectAsync(address, relay.Port).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(SocketError.ConnectionRefused, ex.SocketErrorCode);
        }
    }
}
