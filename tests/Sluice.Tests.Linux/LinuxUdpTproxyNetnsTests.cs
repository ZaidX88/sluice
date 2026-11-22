using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>
/// The UDP tproxy for real, in a network namespace of its own: the nft rules, a queue verdict with the UDP mark, the
/// policy routing, the transparent socket with the original destination, and the reply from that destination. Opt-in:
/// it changes the namespace's network, so it runs only with <c>SLUICE_TEST_NETNS=1</c>, under
/// <c>unshare -rn dotnet test … --filter LinuxUdpTproxyNetnsTests</c> (a throwaway namespace; the machine's own
/// network is never touched). Otherwise it passes without doing anything.
/// </summary>
public class LinuxUdpTproxyNetnsTests
{
    private const ushort Queue = 1473;
    private const uint UdpMark = 0x5C1D0;

    private static bool Enabled => Environment.GetEnvironmentVariable("SLUICE_TEST_NETNS") == "1";

    [Fact]
    public async Task Marked_udp_reaches_the_transparent_socket_and_the_reply_comes_from_the_original_destination()
    {
        if (!Enabled) return;
        // A dummy interface with a default route, as a real machine has one: the app's first route lookup needs it.
        Ip("link set lo up");
        Ip("link add sluice0 type dummy");
        Ip("addr add 192.0.2.1/24 dev sluice0");
        Ip("-6 addr add 2001:db8::1/64 dev sluice0 nodad");
        Ip("link set sluice0 up");
        Ip("route add default dev sluice0");
        Ip("-6 route add default dev sluice0");

        var seen = new ConcurrentQueue<(IPEndPoint App, IPEndPoint Destination, string Payload)>();
        TransparentUdp? tproxy = null;
        var errors = new ConcurrentQueue<string>();
        tproxy = TransparentUdp.Open((app, dst, payload) =>
        {
            var text = Encoding.ASCII.GetString(payload.Span);
            seen.Enqueue((app, dst, text));
            tproxy!.SendToApp(dst, app, Encoding.ASCII.GetBytes("re:" + text));
        }, errors.Enqueue);
        using var _ = tproxy;
        using var queue = NfQueue.Open(Queue);
        var stop = false;
        var verdicts = 0;
        var loop = new Thread(() =>
        {
            // Every queued packet is a flow to take: what LinuxCapture answers for a tunnel or placeholder flow.
            while (!stop && queue.Receive(p => { Interlocked.Increment(ref verdicts); queue.Verdict(p.Id, NfQueue.Accept, UdpMark); })) { }
        }) { IsBackground = true };
        try
        {
            // A group no socket here has: the test's own sockets are the "apps".
            NftRules.Run(NftRules.Build(new NftRules.Settings(Queue, 0x5C1CE, Gid: 4_242_424, RelayPort: 1, DnsPort: null,
                QueueUdp: true, UdpMark: UdpMark, UdpPort: tproxy.Port)));
            PolicyRoutes.Add(UdpMark, out var ipv6Problem);
            Assert.Null(ipv6Problem);
            tproxy.Start();
            loop.Start();

            foreach (var (local, remote) in new[] { ("192.0.2.1", "198.18.0.42"), ("2001:db8::1", "2001:db8:ffff::42") })
            {
                using var app = new UdpClient(new IPEndPoint(IPAddress.Parse(local), 0));
                var dst = new IPEndPoint(IPAddress.Parse(remote), 443);
                for (var i = 1; i <= 3; i++)   // the first packets are queued; later ones carry the mark from conntrack
                {
                    await app.SendAsync(Encoding.ASCII.GetBytes($"hello {i}"), dst);
                    var reply = await app.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(dst, reply.RemoteEndPoint);                    // from where the app sent it
                    Assert.Equal($"re:hello {i}", Encoding.ASCII.GetString(reply.Buffer));
                }
                var got = seen.Where(s => s.Destination.Equals(dst)).ToList();
                Assert.Equal(3, got.Count);
                Assert.All(got, s => Assert.Equal((IPEndPoint)app.Client.LocalEndPoint!, s.App));
            }
            Assert.True(verdicts >= 2);
            Assert.Equal(2, tproxy.ReplySockets);
            Assert.Empty(errors);
        }
        finally
        {
            stop = true;
            queue.Stop();
            loop.Join(2000);
            PolicyRoutes.Remove(UdpMark);
            NftRules.Remove();
        }

        // Nothing left behind.
        Assert.DoesNotContain($"lookup {PolicyRoutes.Table}", Ip("rule"));
        Assert.DoesNotContain($"lookup {PolicyRoutes.Table}", Ip("-6 rule"));
        Assert.Equal("", Ip($"route show table {PolicyRoutes.Table}").Trim());
        Assert.Equal("", Ip($"-6 route show table {PolicyRoutes.Table}").Trim());
        Assert.DoesNotContain("sluice", Run("nft", "list tables"));
    }

    [Fact]
    public void Routes_left_by_a_crash_are_replaced_not_doubled()
    {
        if (!Enabled) return;
        Ip("link set lo up");
        try
        {
            PolicyRoutes.Add(UdpMark, out _);
            PolicyRoutes.Add(UdpMark, out _);    // as at the next start after a crash
            Assert.Single(Ip("rule").Split('\n'), l => l.Contains($"lookup {PolicyRoutes.Table}"));
            Assert.Single(Ip("-6 rule").Split('\n'), l => l.Contains($"lookup {PolicyRoutes.Table}"));
            Assert.Single(Ip($"route show table {PolicyRoutes.Table}").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            PolicyRoutes.Remove(UdpMark);
        }
        Assert.DoesNotContain($"lookup {PolicyRoutes.Table}", Ip("rule"));
    }

    private static string Ip(string args) => Run("ip", args);

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
