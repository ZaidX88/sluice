using System.Net;

namespace Sluice.Core.Dns;

/// <summary>
/// Hands out placeholder IPv4 addresses for hostnames so the real name can be sent to the proxy
/// (Proxifier's "fake IP" technique). Uses 198.18.0.0/15 (RFC 2544 benchmarking range) instead of
/// Proxifier's 127.8.x.x, because loopback connections cannot be redirected with WinDivert.
/// Mappings are only meaningful while Sluice runs.
/// </summary>
public sealed class FakeIpPool
{
    private const uint Base = (198u << 24) | (18u << 16);   // 198.18.0.0
    private const uint Size = 1u << 17;                       // /15 = 131072 addresses

    private readonly object _gate = new();
    private readonly Dictionary<string, uint> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _byIp = new();
    private readonly uint _capacity;
    private uint _next = 1;

    /// <param name="capacity">Max addresses before the oldest are recycled (tests use small values).</param>
    public FakeIpPool(uint capacity = Size - 2)
    {
        _capacity = Math.Clamp(capacity, 1, Size - 2);
    }

    public int Count { get { lock (_gate) return _byName.Count; } }

    public static bool IsFake(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var v = ToUInt(ip);
        return v >= Base && v < Base + Size;
    }

    /// <summary>The fake address for <paramref name="host"/>, allocating one if needed.</summary>
    public IPAddress GetOrAdd(string host)
    {
        host = Normalize(host);
        lock (_gate)
        {
            if (_byName.TryGetValue(host, out var existing)) return FromUInt(existing);

            var offset = _next;
            _next = _next % _capacity + 1;   // 1.._capacity, never .0 of the range
            var ip = Base + offset;
            if (_byIp.Remove(ip, out var evicted)) _byName.Remove(evicted);
            _byIp[ip] = host;
            _byName[host] = ip;
            return FromUInt(ip);
        }
    }

    public bool TryGetHost(IPAddress ip, out string host)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        lock (_gate)
        {
            if (IsFake(ip) && _byIp.TryGetValue(ToUInt(ip), out var h)) { host = h; return true; }
        }
        host = "";
        return false;
    }

    private static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt(uint v) =>
        new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
}
