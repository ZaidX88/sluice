using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Profiles;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>
/// #216: capture's pieces with IPv6 disabled by sysctl (<c>net.ipv6.conf.*.disable_ipv6=1</c>), in a network namespace
/// of its own. IPv6 sockets can still be made there, but <c>::1</c> can't be bound and IPv6 routes can't be added, so
/// each piece must carry on with IPv4 only. Opt-in like <see cref="LinuxUdpTproxyNetnsTests"/>: it runs only with
/// <c>SLUICE_TEST_NETNS=1</c>, under <c>unshare -rn</c> (the machine's own network is never touched). Each test turns
/// IPv6 back on when it ends, so the other namespace tests can run in the same namespace.
/// </summary>
[Collection(NetnsTests.Collection)]   // one at a time: they change the same namespace
public class LinuxIpv6DisabledNetnsTests
{
    private const uint UdpMark = 0x5C1D0;

    private static bool Enabled => Environment.GetEnvironmentVariable("SLUICE_TEST_NETNS") == "1";

    /// <summary>Disables IPv6 in the namespace until the result is disposed.</summary>
    private static Ipv6Off DisableIpv6()
    {
        Run("ip", "link set lo up");
        var off = new Ipv6Off();
        // As on a real machine with IPv6 disabled: ::1 is gone from lo.
        Assert.DoesNotContain("::1", Run("ip", "-6 addr show dev lo"));
        return off;
    }

    private sealed class Ipv6Off : IDisposable
    {
        private static readonly string[] Devices = ["all", "default", "lo"];
        public Ipv6Off() => Set("1");
        public void Dispose() => Set("0");
        private static void Set(string value)
        {
            foreach (var dev in Devices) File.WriteAllText($"/proc/sys/net/ipv6/conf/{dev}/disable_ipv6", value);
        }
    }

    [Fact]
    public void Dns_server_binds_ipv4_only()
    {
        if (!Enabled) return;
        using var ipv6Off = DisableIpv6();

        var (v4, v6, port) = LinuxDnsServer.BindLoopbackPair();
        try
        {
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, port), v4.LocalEndPoint);
            Assert.False(v6.IsBound);
        }
        finally
        {
            v4.Dispose();
            v6.Dispose();
        }
    }

    [Fact]
    public async Task Relay_binds_ipv4_only()
    {
        if (!Enabled) return;
        using var ipv6Off = DisableIpv6();

        await using var relay = LoopbackRelay.Bind(new RoutingEngine(Profile.CreateDefault()), new NoRedirects());
        Assert.Null(relay.V6);
        Assert.True(relay.Port > 0);
    }

    [Fact]
    public void Transparent_udp_socket_opens()
    {
        if (!Enabled) return;
        using var ipv6Off = DisableIpv6();

        using var tproxy = TransparentUdp.Open((_, _, _) => { }, _ => { });
        Assert.True(tproxy.Port > 0);
    }

    [Fact]
    public void Policy_routes_are_added_for_ipv4_and_nothing_is_left_after_remove()
    {
        if (!Enabled) return;
        using var ipv6Off = DisableIpv6();

        try
        {
            PolicyRoutes.Add(UdpMark, out var ipv6Problem);
            Assert.NotNull(ipv6Problem);                                   // IPv6 left out, said why
            Assert.Contains($"lookup {PolicyRoutes.Table}", Run("ip", "rule"));
            Assert.Contains("local default dev lo", Run("ip", $"route show table {PolicyRoutes.Table}"));
            Assert.DoesNotContain($"lookup {PolicyRoutes.Table}", Run("ip", "-6 rule"));   // no half-added IPv6 rule
        }
        finally
        {
            PolicyRoutes.Remove(UdpMark);
        }
        Assert.DoesNotContain($"lookup {PolicyRoutes.Table}", Run("ip", "rule"));
        Assert.Equal("", Run("ip", $"route show table {PolicyRoutes.Table}").Trim());
    }

    private sealed class NoRedirects : IRedirectTable
    {
        public bool TryGetOriginal(IPEndPoint clientEndPoint, out OriginalDestination original)
        {
            original = null!;
            return false;
        }
    }

    private static string Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} {args}: {error.Trim()}");
        return output;
    }
}

/// <summary>The xUnit collection of the tests that change their network namespace, so they never run at once.</summary>
public static class NetnsTests
{
    public const string Collection = "Network namespace";
}
