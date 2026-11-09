using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Sluice.Core.Profiles;

namespace Sluice.Core.Rules;

/// <summary>What we know about an outgoing connection when choosing a rule.</summary>
public sealed record ConnectionInfo(
    string? ProcessPath,
    string? TargetHost,
    IPAddress? TargetIp,
    int TargetPort);

public sealed record RuleMatch(Rule Rule, RuleAction Action);

/// <summary>
/// Proxifier semantics: enabled rules are evaluated top to bottom, first match wins.
/// Empty Applications / Targets / Ports mean "any". The rule named "Default" always
/// matches and is evaluated last regardless of its position.
/// </summary>
public sealed class RuleEngine
{
    private readonly List<CompiledRule> _rules;
    private readonly CompiledRule _default;

    public RuleEngine(IEnumerable<Rule> rules, string? computerName = null)
    {
        var env = new MacroContext(computerName ?? Environment.MachineName);
        var all = rules.ToList();
        _rules = all.Where(r => r.Enabled && !r.IsDefault).Select(r => new CompiledRule(r, env)).ToList();
        var def = all.FirstOrDefault(r => r.IsDefault) ?? new Rule { Name = "Default", Action = RuleAction.Direct };
        _default = new CompiledRule(def, env);
    }

    public RuleMatch Match(ConnectionInfo c)
    {
        foreach (var r in _rules)
            if (r.Matches(c))
                return new RuleMatch(r.Rule, r.Rule.Action);
        return new RuleMatch(_default.Rule, _default.Rule.Action);
    }

    private sealed record MacroContext(string ComputerName);

    private sealed class CompiledRule
    {
        public Rule Rule { get; }
        private readonly List<Func<string?, bool>>? _apps;
        private readonly List<TargetPattern>? _targets;
        private readonly List<(int From, int To)>? _ports;

        public CompiledRule(Rule rule, MacroContext env)
        {
            Rule = rule;
            _apps = ParseApps(rule.Applications);
            _targets = ParseTargets(rule.Targets, env);
            _ports = ParsePorts(rule.Ports);
        }

        public bool Matches(ConnectionInfo c)
        {
            if (_apps is not null && !_apps.Any(f => f(c.ProcessPath))) return false;
            if (_ports is not null && !_ports.Any(p => c.TargetPort >= p.From && c.TargetPort <= p.To)) return false;
            if (_targets is not null && !_targets.Any(t => t.Matches(c.TargetHost, c.TargetIp))) return false;
            return true;
        }
    }

    // ---------- applications ----------

    internal static List<Func<string?, bool>>? ParseApps(string? spec)
    {
        var tokens = Tokenize(spec);
        if (tokens.Count == 0) return null;
        var list = new List<Func<string?, bool>>();
        foreach (var tok in tokens)
        {
            var expanded = Environment.ExpandEnvironmentVariables(tok);
            var hasPath = expanded.Contains('\\') || expanded.Contains('/');
            var rx = WildcardToRegex(expanded.Replace('/', '\\'));
            list.Add(path =>
            {
                if (string.IsNullOrEmpty(path)) return false;
                var candidate = hasPath ? path : Path.GetFileName(path);
                return rx.IsMatch(candidate);
            });
        }
        return list;
    }

    // ---------- ports ----------

    internal static List<(int, int)>? ParsePorts(string? spec)
    {
        var tokens = Tokenize(spec);
        if (tokens.Count == 0) return null;
        var list = new List<(int, int)>();
        foreach (var tok in tokens)
        {
            var dash = tok.IndexOf('-');
            if (dash > 0 && int.TryParse(tok[..dash], out var a) && int.TryParse(tok[(dash + 1)..], out var b))
                list.Add((Math.Min(a, b), Math.Max(a, b)));
            else if (int.TryParse(tok, out var p))
                list.Add((p, p));
        }
        return list.Count == 0 ? null : list;
    }

    // ---------- targets ----------

    internal abstract class TargetPattern
    {
        public abstract bool Matches(string? host, IPAddress? ip);
    }

    private sealed class HostWildcard(Regex rx) : TargetPattern
    {
        public override bool Matches(string? host, IPAddress? ip) =>
            (host is not null && rx.IsMatch(host.TrimEnd('.'))) ||
            (ip is not null && rx.IsMatch(ip.ToString()));
    }

    private sealed class SimpleHostnames : TargetPattern
    {
        public override bool Matches(string? host, IPAddress? ip) =>
            host is not null && !host.Contains('.') && !host.Contains(':') && !IPAddress.TryParse(host, out _);
    }

    private sealed class IpRange(byte[] from, byte[] to) : TargetPattern
    {
        public override bool Matches(string? host, IPAddress? ip)
        {
            ip ??= host is not null && IPAddress.TryParse(host, out var parsed) ? parsed : null;
            if (ip is null) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var b = ip.GetAddressBytes();
            return b.Length == from.Length && Compare(b, from) >= 0 && Compare(b, to) <= 0;
        }

        private static int Compare(byte[] x, byte[] y)
        {
            for (var i = 0; i < x.Length; i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return 0;
        }
    }

    internal static List<TargetPattern>? ParseTargets(string? spec, string computerName) =>
        ParseTargets(spec, new MacroContext(computerName));

    private static List<TargetPattern>? ParseTargets(string? spec, MacroContext env)
    {
        var tokens = Tokenize(spec);
        if (tokens.Count == 0) return null;
        var list = new List<TargetPattern>();
        foreach (var raw in tokens)
        {
            if (raw.Equals("%SimpleHostnames%", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new SimpleHostnames());
                continue;
            }
            var tok = raw.Replace("%ComputerName%", env.ComputerName, StringComparison.OrdinalIgnoreCase);

            if (TryParseRange(tok, out var range) || TryParseCidr(tok, out range) || TryParseIpv4Wildcard(tok, out range))
                list.Add(range!);
            else if (IPAddress.TryParse(tok, out var single))
            {
                var b = Normalize(single).GetAddressBytes();
                list.Add(new IpRange(b, b));
            }
            else
                list.Add(new HostWildcard(WildcardToRegex(tok)));
        }
        return list;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static bool TryParseRange(string tok, out TargetPattern? p)
    {
        p = null;
        var dash = tok.IndexOf('-');
        if (dash <= 0) return false;
        if (!IPAddress.TryParse(tok[..dash].Trim(), out var a) || !IPAddress.TryParse(tok[(dash + 1)..].Trim(), out var b))
            return false;
        var ab = Normalize(a).GetAddressBytes();
        var bb = Normalize(b).GetAddressBytes();
        if (ab.Length != bb.Length) return false;
        p = new IpRange(ab, bb);
        return true;
    }

    private static bool TryParseCidr(string tok, out TargetPattern? p)
    {
        p = null;
        var slash = tok.IndexOf('/');
        if (slash <= 0 || !IPAddress.TryParse(tok[..slash], out var ip) || !int.TryParse(tok[(slash + 1)..], out var bits))
            return false;
        var bytes = Normalize(ip).GetAddressBytes();
        if (bits < 0 || bits > bytes.Length * 8) return false;
        var lo = (byte[])bytes.Clone();
        var hi = (byte[])bytes.Clone();
        for (var i = 0; i < bytes.Length * 8; i++)
        {
            if (i < bits) continue;
            var mask = (byte)(0x80 >> (i % 8));
            lo[i / 8] &= (byte)~mask;
            hi[i / 8] |= mask;
        }
        p = new IpRange(lo, hi);
        return true;
    }

    /// <summary>192.168.1.* or 10.*.*.* — trailing wildcard octets become a range.</summary>
    private static bool TryParseIpv4Wildcard(string tok, out TargetPattern? p)
    {
        p = null;
        var parts = tok.Split('.');
        if (parts.Length != 4 || !tok.Contains('*')) return false;
        var lo = new byte[4];
        var hi = new byte[4];
        var seenStar = false;
        for (var i = 0; i < 4; i++)
        {
            if (parts[i] == "*") { seenStar = true; lo[i] = 0; hi[i] = 255; }
            else if (!seenStar && byte.TryParse(parts[i], out var v)) { lo[i] = hi[i] = v; }
            else return false;
        }
        p = new IpRange(lo, hi);
        return true;
    }

    // ---------- shared ----------

    /// <summary>Splits on ';' (and ','), honouring double quotes.</summary>
    internal static List<string> Tokenize(string? spec)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(spec)) return result;
        var cur = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in spec)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && (ch == ';' || ch == ',' || ch == '\n' || ch == '\r'))
            {
                Flush();
                continue;
            }
            cur.Append(ch);
        }
        Flush();
        return result;

        void Flush()
        {
            var t = cur.ToString().Trim();
            if (t.Length > 0) result.Add(t);
            cur.Clear();
        }
    }

    internal static Regex WildcardToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
