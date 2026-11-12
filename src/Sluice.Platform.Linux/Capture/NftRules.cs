using System.Diagnostics;
using System.Globalization;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// The nftables table Sluice installs while capturing ("inet sluice"):
/// <list type="bullet">
/// <item>The first SYN of every new outbound TCP connection (and, when UDP is handled, every new UDP flow) goes to
/// NFQUEUE, where Sluice decides with the owning process in hand. <c>bypass</c>: if Sluice is gone, packets pass.</item>
/// <item>A SYN Sluice marked is redirected to the relay; UDP/53 goes to Sluice's DNS server.</item>
/// <item>Sockets of Sluice's own group (Sluice and the helpers it starts) are never touched, nor is loopback.</item>
/// </list>
/// </summary>
internal static class NftRules
{
    public const string Table = "sluice";

    public sealed record Settings(ushort Queue, uint Mark, uint Gid, int RelayPort, int? DnsPort, bool QueueUdp);

    public static string Build(Settings s)
    {
        string N(long v) => v.ToString(CultureInfo.InvariantCulture);
        var udp = s.QueueUdp ? $"\n        meta l4proto udp udp dport != 53 ct state new queue num {N(s.Queue)} bypass" : "";
        var dns = s.DnsPort is { } d ? $"\n        meta l4proto udp udp dport 53 redirect to :{N(d)}" : "";
        // "table …" + "delete table …" first removes a table left behind by a crash (and does not fail when there is none).
        return $$"""
            table inet {{Table}}
            delete table inet {{Table}}
            table inet {{Table}} {
                chain queue_new {
                    type filter hook output priority mangle; policy accept;
                    meta skgid {{N(s.Gid)}} return
                    oifname "lo" return
                    meta l4proto tcp tcp flags & (fin | syn | rst | ack) == syn ct state new queue num {{N(s.Queue)}} bypass{{udp}}
                }
                chain to_sluice {
                    type nat hook output priority dstnat; policy accept;
                    meta skgid {{N(s.Gid)}} return
                    meta mark {{N(s.Mark)}} meta l4proto tcp redirect to :{{N(s.RelayPort)}}{{dns}}
                }
            }

            """;
    }

    public static void Apply(Settings s) => Run(Build(s));

    /// <summary>Removes the table (no error when it is not there).</summary>
    public static void Remove()
    {
        try { Run($"table inet {Table}\ndelete table inet {Table}\n"); }
        catch (InvalidOperationException) { /* best effort */ }
    }

    public static string? FindNft() =>
        new[] { "/usr/sbin/nft", "/sbin/nft", "/usr/bin/nft", "/bin/nft" }.FirstOrDefault(File.Exists);

    private static void Run(string script)
    {
        var nft = FindNft() ?? throw new InvalidOperationException("nftables is not installed (no nft command). Install the \"nftables\" package.");
        var psi = new ProcessStartInfo(nft, "-f -")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not run nft.");
        p.StandardInput.Write(script);
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } throw new InvalidOperationException("nft did not finish."); }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"nft failed: {err.Trim()}" + (err.Contains("Operation not permitted") ? " (needs root or CAP_NET_ADMIN)" : ""));
    }
}
