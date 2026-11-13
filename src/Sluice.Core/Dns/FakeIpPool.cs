using System.Net;
using System.Text.Json;

namespace Sluice.Core.Dns;

/// <summary>
/// Hands out placeholder IPv4 addresses for hostnames so the real name can be sent to the proxy
/// (Proxifier's "fake IP" technique). Uses 198.18.0.0/15 (RFC 2544 benchmarking range) instead of
/// Proxifier's 127.8.x.x, because loopback connections cannot be redirected with WinDivert.
/// Apps (browsers especially) cache the answers beyond Sluice's lifetime, so the mappings are saved
/// (<see cref="Save"/>) and restored on the next start (<see cref="Load"/>): an old cached address then
/// still means the same host.
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
    private bool _dirty;

    /// <summary>Where capture keeps the mappings between runs: the Sluice settings folder.</summary>
    public static string DefaultStatePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sluice", "fake-dns.json");

    /// <summary>True when mappings were added since the last <see cref="Save"/> or <see cref="Load"/>.</summary>
    public bool IsDirty { get { lock (_gate) return _dirty; } }

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
            _dirty = true;
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

    /// <summary>Writes the mappings to <paramref name="path"/> (atomically: a temp file, then a rename).</summary>
    public void Save(string path)
    {
        List<(string Host, uint Offset)> entries;
        uint next;
        lock (_gate)
        {
            entries = _byName.Select(kv => (kv.Key, kv.Value - Base)).OrderBy(e => e.Item2).ToList();
            next = _next;
            _dirty = false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var f = File.Create(tmp))
        using (var w = new Utf8JsonWriter(f))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteNumber("next", next);
            w.WriteStartArray("entries");
            foreach (var (host, offset) in entries)
            {
                w.WriteStartArray();
                w.WriteStringValue(host);
                w.WriteNumberValue(offset);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Restores mappings saved by <see cref="Save"/>, keeping any already in the pool. A missing or damaged file is
    /// ignored (the pool simply starts empty). Returns how many mappings were restored.
    /// </summary>
    public int Load(string path)
    {
        JsonDocument doc;
        try
        {
            if (!File.Exists(path)) return 0;
            doc = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array)
                return 0;
            var restored = 0;
            lock (_gate)
            {
                foreach (var e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2) continue;
                    if (e[0].ValueKind != JsonValueKind.String || !e[1].TryGetUInt32(out var offset)) continue;
                    if (offset < 1 || offset > _capacity) continue;
                    var host = Normalize(e[0].GetString() ?? "");
                    if (host.Length == 0 || _byName.ContainsKey(host) || _byIp.ContainsKey(Base + offset)) continue;
                    _byName[host] = Base + offset;
                    _byIp[Base + offset] = host;
                    restored++;
                }
                if (root.TryGetProperty("next", out var n) && n.TryGetUInt32(out var next) && next >= 1 && next <= _capacity)
                    _next = next;   // continue after the last address handed out, so restored ones are recycled last
                _dirty = false;
            }
            return restored;
        }
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
