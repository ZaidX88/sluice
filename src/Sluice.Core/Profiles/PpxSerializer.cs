using System.Globalization;
using System.Xml.Linq;

namespace Sluice.Core.Profiles;

/// <summary>
/// Reads/writes Proxifier v4 ".ppx" profiles (ProxifierProfile version="102").
/// Elements observed in real profiles: Options/Resolve, ProxyList/Proxy, RuleList/Rule.
/// Chain and Authentication layout are best-effort (inferred from the UI) and
/// may need adjusting once a profile using them is available.
/// </summary>
public static class PpxSerializer
{
    public static Profile Load(string path) => Parse(XDocument.Load(path));

    public static Profile Parse(string xml) => Parse(XDocument.Parse(xml));

    public static Profile Parse(XDocument doc)
    {
        var root = doc.Root ?? throw new FormatException("Empty profile");
        if (root.Name.LocalName != "ProxifierProfile")
            throw new FormatException($"Not a Proxifier profile (root <{root.Name.LocalName}>)");

        var profile = new Profile();
        ReadOptions(root.Element("Options"), profile.Options);

        foreach (var x in root.Element("ProxyList")?.Elements("Proxy") ?? [])
            profile.Proxies.Add(ReadProxy(x));

        foreach (var x in root.Element("ChainList")?.Elements("Chain") ?? [])
            profile.Chains.Add(ReadChain(x));

        foreach (var x in root.Element("RuleList")?.Elements("Rule") ?? [])
            profile.Rules.Add(ReadRule(x));

        if (!profile.Rules.Any(r => r.IsDefault))
            profile.Rules.Add(new Rule { Name = "Default", Action = RuleAction.Direct });

        return profile;
    }

    public static void Save(Profile profile, string path)
    {
        var doc = ToXml(profile);
        doc.Save(path);
    }

    public static XDocument ToXml(Profile p)
    {
        var o = p.Options;
        var root = new XElement("ProxifierProfile",
            new XAttribute("version", "102"),
            new XAttribute("platform", "Windows"),
            new XAttribute("product_id", "0"),
            new XAttribute("product_minver", "400"),
            new XElement("Options",
                new XElement("Resolve",
                    Flag("AutoModeDetection", o.Resolve.AutoModeDetection),
                    Flag("ViaProxy", o.Resolve.ViaProxy, o.Resolve.ViaProxy ? new XElement("TryLocalDnsFirst", new XAttribute("enabled", B(o.Resolve.TryLocalFirst))) : null),
                    Flag("BlockNonATypes", o.Resolve.BlockNonATypes),
                    new XElement("ExclusionList", new XAttribute("OnlyFromListMode", B(o.Resolve.OnlyFromListMode)), o.Resolve.ExclusionList),
                    new XElement("DnsUdpMode", o.Resolve.DnsUdpMode)),
                new XElement("Encryption", new XAttribute("mode", o.EncryptionMode)),
                new XElement("ConnectionLoopDetection", new XAttribute("enabled", B(o.ConnectionLoopDetection)), new XAttribute("resolve", B(o.ResolveLoopDetection))),
                new XElement("Udp", new XAttribute("mode", UdpToString(o.Udp))),
                Flag("LeakPreventionMode", o.LeakPreventionMode),
                Flag("ProcessOtherUsers", o.ProcessOtherUsers),
                Flag("ProcessServices", o.ProcessServices),
                Flag("HandleDirectConnections", o.HandleDirectConnections),
                Flag("HttpProxiesSupport", o.HttpProxiesSupport)),
            new XElement("ProxyList", p.Proxies.Select(WriteProxy)),
            new XElement("ChainList", p.Chains.Select(WriteChain)),
            new XElement("RuleList", p.Rules.Select(WriteRule)));

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }

    // ---------- reading ----------

    private static void ReadOptions(XElement? x, ProfileOptions o)
    {
        if (x is null) return;
        var r = x.Element("Resolve");
        if (r is not null)
        {
            o.Resolve.AutoModeDetection = Enabled(r.Element("AutoModeDetection"), o.Resolve.AutoModeDetection);
            o.Resolve.ViaProxy = Enabled(r.Element("ViaProxy"), false);
            o.Resolve.TryLocalFirst = Enabled(r.Element("ViaProxy")?.Element("TryLocalDnsFirst"), false);
            o.Resolve.BlockNonATypes = Enabled(r.Element("BlockNonATypes"), false);
            var ex = r.Element("ExclusionList");
            if (ex is not null)
            {
                o.Resolve.ExclusionList = ex.Value;
                o.Resolve.OnlyFromListMode = Bool(ex.Attribute("OnlyFromListMode")?.Value, false);
            }
            o.Resolve.DnsUdpMode = Int(r.Element("DnsUdpMode")?.Value, 0);
        }
        o.EncryptionMode = x.Element("Encryption")?.Attribute("mode")?.Value ?? "basic";
        var loop = x.Element("ConnectionLoopDetection");
        o.ConnectionLoopDetection = Enabled(loop, true);
        o.ResolveLoopDetection = Bool(loop?.Attribute("resolve")?.Value, true);
        o.Udp = UdpFromString(x.Element("Udp")?.Attribute("mode")?.Value);
        o.LeakPreventionMode = Enabled(x.Element("LeakPreventionMode"), false);
        o.ProcessOtherUsers = Enabled(x.Element("ProcessOtherUsers"), false);
        o.ProcessServices = Enabled(x.Element("ProcessServices"), false);
        o.HandleDirectConnections = Enabled(x.Element("HandleDirectConnections"), false);
        o.HttpProxiesSupport = Enabled(x.Element("HttpProxiesSupport"), false);
    }

    private static ProxyServer ReadProxy(XElement x)
    {
        var p = new ProxyServer
        {
            Id = Int(x.Attribute("id")?.Value, 0),
            Type = ProxyTypeFromString(x.Attribute("type")?.Value),
            Address = x.Element("Address")?.Value ?? "",
            Port = Int(x.Element("Port")?.Value, 0),
            Options = Int(x.Element("Options")?.Value, 0),
            Label = x.Element("Label")?.Value,
        };
        var auth = x.Element("Authentication");
        if (auth is not null && Enabled(auth, true))
        {
            p.Username = auth.Element("Username")?.Value;
            p.Password = auth.Element("Password")?.Value;
        }
        return p;
    }

    private static ProxyChain ReadChain(XElement x)
    {
        var c = new ProxyChain
        {
            Id = Int(x.Attribute("id")?.Value, 0),
            Type = (x.Attribute("type")?.Value ?? "simple").ToLowerInvariant() switch
            {
                "redundancy" => ChainType.Redundancy,
                "load_balancing" or "loadbalancing" => ChainType.LoadBalancing,
                _ => ChainType.Simple,
            },
            Name = x.Element("Name")?.Value ?? "",
        };
        foreach (var hop in x.Elements("Proxy"))
            c.Hops.Add(new ChainHop { ProxyId = Int(hop.Value, 0), Enabled = Enabled(hop, true) });
        return c;
    }

    private static Rule ReadRule(XElement x)
    {
        var action = x.Element("Action");
        var type = (action?.Attribute("type")?.Value ?? "Direct") switch
        {
            "Block" => RuleActionType.Block,
            "Proxy" => RuleActionType.Proxy,
            "Chain" => RuleActionType.Chain,
            _ => RuleActionType.Direct,
        };
        return new Rule
        {
            Name = x.Element("Name")?.Value ?? "",
            Enabled = Enabled(x, true),
            Applications = NullIfEmpty(x.Element("Applications")?.Value),
            Targets = NullIfEmpty(x.Element("Targets")?.Value),
            Ports = NullIfEmpty(x.Element("Ports")?.Value),
            Action = new RuleAction
            {
                Type = type,
                TargetId = type is RuleActionType.Proxy or RuleActionType.Chain ? Int(action?.Value, 0) : null,
            },
        };
    }

    // ---------- writing ----------

    private static XElement WriteProxy(ProxyServer p)
    {
        var x = new XElement("Proxy",
            new XAttribute("id", p.Id),
            new XAttribute("type", ProxyTypeToString(p.Type)));
        if (p.Label is not null) x.Add(new XElement("Label", p.Label));
        if (p.Username is not null || p.Password is not null)
            x.Add(new XElement("Authentication", new XAttribute("enabled", "true"),
                new XElement("Username", p.Username ?? ""),
                new XElement("Password", p.Password ?? "")));
        x.Add(new XElement("Options", p.Options),
              new XElement("Port", p.Port),
              new XElement("Address", p.Address));
        return x;
    }

    private static XElement WriteChain(ProxyChain c) =>
        new("Chain",
            new XAttribute("id", c.Id),
            new XAttribute("type", c.Type switch
            {
                ChainType.Redundancy => "redundancy",
                ChainType.LoadBalancing => "load_balancing",
                _ => "simple",
            }),
            c.Hops.Select(h => new XElement("Proxy", new XAttribute("enabled", B(h.Enabled)), h.ProxyId)),
            new XElement("Name", c.Name));

    private static XElement WriteRule(Rule r)
    {
        var x = new XElement("Rule", new XAttribute("enabled", B(r.Enabled)));
        var action = new XElement("Action", new XAttribute("type", r.Action.Type.ToString()));
        if (r.Action.TargetId is int id) action.Value = id.ToString(CultureInfo.InvariantCulture);
        x.Add(action);
        if (!string.IsNullOrWhiteSpace(r.Applications)) x.Add(new XElement("Applications", r.Applications));
        if (!string.IsNullOrWhiteSpace(r.Targets)) x.Add(new XElement("Targets", r.Targets));
        if (!string.IsNullOrWhiteSpace(r.Ports)) x.Add(new XElement("Ports", r.Ports));
        x.Add(new XElement("Name", r.Name));
        return x;
    }

    // ---------- helpers ----------

    private static XElement Flag(string name, bool enabled, XElement? child = null) =>
        new(name, new XAttribute("enabled", B(enabled)), child);

    private static string B(bool v) => v ? "true" : "false";

    private static bool Enabled(XElement? x, bool fallback) => Bool(x?.Attribute("enabled")?.Value, fallback);

    private static bool Bool(string? s, bool fallback) =>
        s is null ? fallback : s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";

    private static int Int(string? s, int fallback) =>
        int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static ProxyType ProxyTypeFromString(string? s) => (s ?? "").ToUpperInvariant() switch
    {
        "SOCKS4" => ProxyType.Socks4,
        "HTTPS" => ProxyType.Https,
        "HTTP" => ProxyType.Http,
        _ => ProxyType.Socks5,
    };

    private static string ProxyTypeToString(ProxyType t) => t switch
    {
        ProxyType.Socks4 => "SOCKS4",
        ProxyType.Https => "HTTPS",
        ProxyType.Http => "HTTP",
        _ => "SOCKS5",
    };

    private static UdpMode UdpFromString(string? s) => s switch
    {
        "mode_block_quic" => UdpMode.BlockQuic,
        "mode_block_all" => UdpMode.BlockAll,
        _ => UdpMode.Bypass,
    };

    private static string UdpToString(UdpMode m) => m switch
    {
        UdpMode.BlockQuic => "mode_block_quic",
        UdpMode.BlockAll => "mode_block_all",
        _ => "mode_bypass",
    };
}
