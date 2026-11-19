using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

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

    /// <param name="RejectMark">A SYN Sluice gives this mark is refused with a TCP reset, so the app's connect() fails at
    /// once (used for IPv6 through a tunnel without IPv6, see <see cref="Sluice.Core.Engine.Ipv6Fallback"/>). 0: no such rule.</param>
    public sealed record Settings(ushort Queue, uint Mark, uint Gid, int RelayPort, int? DnsPort, bool QueueUdp, uint RejectMark = 0);

    public static string Build(Settings s)
    {
        string N(long v) => v.ToString(CultureInfo.InvariantCulture);
        var udp = s.QueueUdp ? $"\n        meta l4proto udp udp dport != 53 ct state new queue num {N(s.Queue)} bypass" : "";
        var dns = s.DnsPort is { } d ? $"\n        meta l4proto udp udp dport 53 redirect to :{N(d)}" : "";
        // After queue_new (mangle) and to_sluice (dstnat): the mark set with the NFQUEUE verdict is on the packet by then.
        var refuse = s.RejectMark == 0 ? "" : $$"""

                chain refuse {
                    type filter hook output priority filter; policy accept;
                    meta mark {{N(s.RejectMark)}} meta l4proto tcp reject with tcp reset
                }
            """;
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
                }{{refuse}}
            }

            """.ReplaceLineEndings("\n");   // a raw string keeps the source file's line endings (CRLF on Windows checkouts); nft rejects CR
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

    private static int _signalThreadStarted;

    /// <summary>
    /// .NET starts its signal-handling thread (".NET SigHandler") the first time the app starts a process or registers a
    /// signal handler. If that is nft, the thread is created while CAP_NET_ADMIN is raised and keeps it for good, so it
    /// is started here first, by registering (and at once removing) a handler for a signal nobody acts on.
    /// </summary>
    private static void EnsureSignalThread()
    {
        if (Interlocked.Exchange(ref _signalThreadStarted, 1) != 0) return;
        try { PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => { }).Dispose(); }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException) { /* not fatal: nft still starts */ }
    }

    internal static void Run(string script)
    {
        var nft = FindNft() ?? throw new InvalidOperationException("nftables is not installed (no nft command). Install the \"nftables\" package.");
        var psi = new ProcessStartInfo(nft, "-f -")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        // Under setcap (not root) nft would start without CAP_NET_ADMIN: hand it on explicitly, for nft only. Lowered
        // again as soon as nft has started (it keeps its copy): left raised, every thread and program this thread starts
        // later would get it too, and GTK's sandboxed image loader (bwrap) refuses to run with it (#99).
        EnsureSignalThread();
        Native.Libc.RaiseAmbient(Native.Libc.CAP_NET_ADMIN, out var capProblem);
        Process? started;
        try { started = Process.Start(psi); }
        finally { Native.Libc.LowerAmbient(Native.Libc.CAP_NET_ADMIN, out _); }
        using var p = started ?? throw new InvalidOperationException("Could not run nft.");
        p.StandardInput.Write(script.ReplaceLineEndings("\n"));
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } throw new InvalidOperationException("nft did not finish."); }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"nft failed: {err.Trim()}" + (err.Contains("Operation not permitted")
                ? capProblem is null ? " (needs root or CAP_NET_ADMIN)" : $" (could not pass CAP_NET_ADMIN to nft: {capProblem})"
                : ""));
    }
}
