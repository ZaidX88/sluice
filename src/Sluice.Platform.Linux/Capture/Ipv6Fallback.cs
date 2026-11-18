using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Engine;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// Remembers the routes (tunnels, chains) through which IPv6 destinations fail, so new IPv6 connections meant for them
/// are refused at the first packet and the app falls back to IPv4 at once.
/// <para>
/// Why: the relay completes the app's TCP handshake locally before the tunnel opens the destination. When the tunnel
/// server has no IPv6, the app has already seen its IPv6 connect succeed, so Happy Eyeballs settles on IPv6 and never
/// tries the IPv4 address; the app then fails over and over ("SOCKS5: general SOCKS server failure"). Electron apps
/// (Claude desktop, ChatGPT/Codex) and many CLIs prefer IPv6 whenever the machine has it.
/// </para>
/// A route is marked by one failed IPv6 connection and stays marked for <see cref="Hold"/>; an IPv6 connection that
/// worked through it clears the mark. Refusing is harmless for dual-stack sites (the app uses IPv4 instead).
/// </summary>
internal sealed class Ipv6Fallback
{
    public static readonly TimeSpan Hold = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, DateTime> _failing = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _now;

    public Ipv6Fallback(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>
    /// Learns from a finished connection (<see cref="ConnectionTracker.Closed"/>). True when this marks
    /// <paramref name="via"/> as failing IPv6 for the first time (worth one log line).
    /// </summary>
    public bool Observe(string target, string via, ConnectionState state, string? error, long bytesReceived)
    {
        if (!IsTunnelRoute(via) || !IsIpv6Target(target)) return false;
        if (state == ConnectionState.Failed)
        {
            // One step, so that failures arriving together (an app opening several connections at once) report "new"
            // only once. When the add loses a race, AddOrUpdate retries with the update, which resets isNew.
            var now = _now();
            var isNew = false;
            _failing.AddOrUpdate(via,
                _ => { isNew = true; return now; },
                (_, since) => { isNew = now - since >= Hold; return now; });   // an expired mark counts as new
            return isNew;
        }
        if (state == ConnectionState.Closed && error is null && bytesReceived > 0)
            _failing.TryRemove(via, out _);      // IPv6 works through this route after all
        return false;
    }

    /// <summary>True to refuse a new connection to <paramref name="remote"/> that the rules send through <paramref name="via"/>.</summary>
    public bool ShouldRefuse(IPAddress remote, string via) =>
        remote.AddressFamily == AddressFamily.InterNetworkV6 && !remote.IsIPv4MappedToIPv6 && IsMarked(via);

    private bool IsMarked(string via)
    {
        if (!_failing.TryGetValue(via, out var at)) return false;
        if (_now() - at < Hold) return true;
        _failing.TryRemove(via, out _);          // try IPv6 again
        return false;
    }

    /// <summary>"Direct" and "Block" are decided without a tunnel; everything else names a tunnel or a chain.</summary>
    private static bool IsTunnelRoute(string via) => via is not ("Direct" or "Block");

    /// <summary>The tracker's target for an address is "[2001:db8::1]:443"; for a name it is "example.com:443".</summary>
    private static bool IsIpv6Target(string target)
    {
        if (!target.StartsWith('[')) return false;
        var end = target.IndexOf(']');
        return end > 1 && IPAddress.TryParse(target.AsSpan(1, end - 1), out var a) && !a.IsIPv4MappedToIPv6;
    }
}
