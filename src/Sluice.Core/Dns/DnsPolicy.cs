using Sluice.Core.Profiles;
using Sluice.Core.Rules;

namespace Sluice.Core.Dns;

/// <summary>
/// Decides which hostnames are resolved "through the proxy" (answered with a fake IP and later
/// sent to the proxy by name) versus left to the system's DNS, per the profile's Name Resolution
/// settings and exclusion list.
/// </summary>
public sealed class DnsPolicy
{
    private readonly bool _enabled;
    private readonly bool _onlyListed;
    private readonly List<RuleEngine.TargetPattern>? _list;

    public bool Enabled => _enabled;
    public bool BlockNonATypes { get; }

    public DnsPolicy(ResolveOptions o, bool leakPrevention, string? computerName = null)
    {
        // Auto-detect mode (Proxifier's default) is treated as "resolve locally" until proxy
        // reachability probing exists; explicit "via proxy" or leak prevention turns faking on.
        _enabled = o.ViaProxy || leakPrevention;
        _onlyListed = o.OnlyFromListMode;
        BlockNonATypes = o.BlockNonATypes || leakPrevention;
        _list = RuleEngine.ParseTargets(o.ExclusionList, computerName ?? Environment.MachineName);
    }

    /// <summary>
    /// Names that are local by definition and must never be resolved remotely, whatever the
    /// profile says: RFC 6761 "localhost" (Windows uses e.g. wsl.localhost), mDNS ".local",
    /// RFC 8375 ".home.arpa", and reverse-lookup ".arpa" zones.
    /// </summary>
    private static readonly string[] AlwaysLocalSuffixes = [".localhost", ".local", ".arpa"];

    public bool ShouldFake(string name)
    {
        if (!_enabled) return false;
        name = name.TrimEnd('.');
        if (name.Length == 0 || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (AlwaysLocalSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return false;
        if (System.Net.IPAddress.TryParse(name, out _)) return false;
        var listed = _list?.Any(p => p.Matches(name, null)) ?? false;
        return _onlyListed ? listed : !listed;
    }
}
