using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Platform;

namespace Sluice.Tests;

/// <summary>
/// The lookups that find Windows' DNS proxy for WSL and virtual machines (the SharedAccess service) and its UDP ports,
/// so capture leaves them alone (docs/windows/FIXES.md §1).
/// </summary>
public class WindowsServiceLookupTests
{
    [Fact]
    public void A_running_service_has_its_process_id()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pid = ProcessLookup.FindServicePid("Dnscache");   // the DNS client service runs on every Windows
        Assert.True(pid > 0);
        using var p = Process.GetProcessById(pid);
        Assert.Equal("svchost", p.ProcessName, ignoreCase: true);
    }

    [Fact]
    public void An_unknown_service_has_none()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(0, ProcessLookup.FindServicePid("SluiceNoSuchService"));
    }

    [Fact]
    public void The_udp_ports_of_a_process_include_its_open_sockets()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var v4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var v6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        var ports = ProcessLookup.FindUdpPorts(Environment.ProcessId);
        Assert.Contains(((IPEndPoint)v4.Client.LocalEndPoint!).Port, ports);
        Assert.Contains(((IPEndPoint)v6.Client.LocalEndPoint!).Port, ports);
        Assert.Empty(ProcessLookup.FindUdpPorts(0));
    }
}
