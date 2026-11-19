using System.Net;
using System.Net.Sockets;
using Sluice.Core.Proxy;

namespace Sluice.Core.Engine;

/// <summary>
/// Remembers tunnel and chain routes that can't reach IPv6 destinations (a tunnel server with no IPv6), so the
/// captures can refuse new IPv6 connections through them at once and apps fall back to IPv4 (#4).
/// <para>
/// Only <see cref="RoutingEngine"/> marks a route, and only on evidence that the problem is IPv6 through this route,
/// not the site or the tunnel:
/// <list type="bullet">
/// <item>connections to at least <see cref="DistinctFailures"/> different IPv6 addresses failed <b>before they
/// opened</b>, each with a SOCKS5 "general failure", "network unreachable" or "host unreachable" reply (sing-box and
/// SSH.NET report an SSH "connect failed" as a general failure), within <see cref="Window"/>;</item>
/// <item>the same route opened a connection to an IPv4 address or a hostname within <see cref="Window"/> (so the
/// tunnel itself works);</item>
/// <item>no IPv6 connection through the route opened in the meantime (any IPv6 success clears the evidence).</item>
/// </list>
/// "Refused", timeouts, failures after the connection opened and failures to reach the tunnel never count. A mark lasts
/// <see cref="MarkFor"/>; IPv4 and hostname targets are never refused.
/// </para>
/// </summary>
public sealed class Ipv6Fallback(Func<DateTime>? clock = null)
{
    public static readonly TimeSpan MarkFor = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    public const int DistinctFailures = 2;

    private sealed class RouteState
    {
        public DateTime MarkedUntil;
        public DateTime LastOtherOpen = DateTime.MinValue;
        public readonly Dictionary<IPAddress, DateTime> Failures = new();
    }

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, RouteState> _routes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Raised once when a route gets marked, with how long the mark lasts. Core writes the log line itself.</summary>
    public event Action<string, TimeSpan>? Marked;

    /// <summary>
    /// True while <paramref name="route"/> (as <see cref="RoutingEngine.Describe"/> gives it for the rule's action) is
    /// marked: refuse new connections to IPv6 addresses through it, so the app falls back to IPv4.
    /// </summary>
    public bool ShouldRefuse(string route)
    {
        lock (_gate)
            return _routes.TryGetValue(route, out var s) && s.MarkedUntil > _now();
    }

    /// <summary>A connection through <paramref name="route"/> opened.</summary>
    internal void Opened(string route, string host)
    {
        string? marked = null;
        lock (_gate)
        {
            var now = _now();
            var s = Get(route);
            if (IsIpv6(host, out _))
            {
                s.Failures.Clear();
                s.MarkedUntil = DateTime.MinValue;
                return;
            }
            s.LastOtherOpen = now;
            if (TryMark(s, now)) marked = route;
        }
        if (marked is not null) Marked?.Invoke(marked, MarkFor);
    }

    /// <summary>A connection through <paramref name="route"/> failed before it opened.</summary>
    internal void FailedBeforeOpen(string route, string host, Exception error)
    {
        if (!IsIpv6(host, out var address) || !IsCantReach(error)) return;
        string? marked = null;
        lock (_gate)
        {
            var now = _now();
            var s = Get(route);
            s.Failures[address] = now;
            if (TryMark(s, now)) marked = route;
        }
        if (marked is not null) Marked?.Invoke(marked, MarkFor);
    }

    /// <summary>Forget everything (the profile changed, so routes may now lead elsewhere).</summary>
    internal void Clear()
    {
        lock (_gate) _routes.Clear();
    }

    /// <summary>A SOCKS5 reply that means the tunnel could not reach the destination.</summary>
    internal static bool IsCantReach(Exception error) => error is ProxyException { Socks5Reply: 0x01 or 0x03 or 0x04 };

    internal static bool IsIpv6(string host, out IPAddress address)
    {
        if (IPAddress.TryParse(host, out address!) && address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6)
            return true;
        address = IPAddress.None;
        return false;
    }

    private RouteState Get(string route)
    {
        if (!_routes.TryGetValue(route, out var s)) _routes[route] = s = new RouteState();
        return s;
    }

    private static bool TryMark(RouteState s, DateTime now)
    {
        if (s.MarkedUntil > now) return false;   // already marked: not extended, not announced again
        foreach (var (ip, at) in s.Failures.ToList())
            if (now - at > Window) s.Failures.Remove(ip);
        if (s.Failures.Count < DistinctFailures || now - s.LastOtherOpen > Window) return false;
        s.MarkedUntil = now + MarkFor;
        s.Failures.Clear();
        return true;
    }
}
