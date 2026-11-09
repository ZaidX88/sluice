namespace Sluice.Core.Profiles;

public enum ProxyType { Socks4, Socks5, Https, Http }

public enum RuleActionType { Direct, Block, Proxy, Chain }

public enum ChainType { Simple, Redundancy, LoadBalancing }

public enum UdpMode { Bypass, BlockQuic, BlockAll }

public sealed class ProxyServer
{
    public int Id { get; set; }
    public ProxyType Type { get; set; } = ProxyType.Socks5;
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Label { get; set; }

    /// <summary>Raw Proxifier option bitmask (kept for round-tripping).</summary>
    public int Options { get; set; }

    /// <summary>SOCKS4a: let the proxy resolve hostnames.</summary>
    public bool Socks4aRemoteResolve { get; set; } = true;

    public override string ToString() => Label ?? $"{Address}:{Port}";
}

public sealed class ProxyChain
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ChainType Type { get; set; } = ChainType.Simple;
    public List<ChainHop> Hops { get; } = new();
    public int RedundancyTimeoutSeconds { get; set; } = 5;
    public int RedundancyRecheckSeconds { get; set; } = 60;
    public bool RedundancyDirectFallback { get; set; }
    public bool LoadBalanceSamePerPid { get; set; }
}

public sealed class ChainHop
{
    public int ProxyId { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class RuleAction
{
    public RuleActionType Type { get; set; } = RuleActionType.Direct;

    /// <summary>Proxy id or chain id when <see cref="Type"/> is Proxy/Chain.</summary>
    public int? TargetId { get; set; }

    public static RuleAction Direct => new() { Type = RuleActionType.Direct };
    public static RuleAction Block => new() { Type = RuleActionType.Block };
    public static RuleAction ViaProxy(int id) => new() { Type = RuleActionType.Proxy, TargetId = id };
    public static RuleAction ViaChain(int id) => new() { Type = RuleActionType.Chain, TargetId = id };
}

public sealed class Rule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>"app.exe; "C:\path\x.exe"; fire*.exe" — null/empty = any.</summary>
    public string? Applications { get; set; }

    /// <summary>"127.0.0.1; *.example.com; 10.1.0.0-10.5.255.255; 192.168.1.*" — null/empty = any.</summary>
    public string? Targets { get; set; }

    /// <summary>"80; 8000-9000" — null/empty = any.</summary>
    public string? Ports { get; set; }

    public RuleAction Action { get; set; } = RuleAction.Direct;

    public bool IsDefault => string.Equals(Name, "Default", StringComparison.OrdinalIgnoreCase);
}

public sealed class ResolveOptions
{
    public bool AutoModeDetection { get; set; } = true;
    public bool ViaProxy { get; set; }
    public bool TryLocalFirst { get; set; }
    public bool BlockNonATypes { get; set; }
    public string ExclusionList { get; set; } = "%ComputerName%; localhost; *.local";
    public bool OnlyFromListMode { get; set; }
    public int DnsUdpMode { get; set; }
}

public sealed class ProfileOptions
{
    public ResolveOptions Resolve { get; } = new();
    /// <summary>How proxy passwords are stored in the profile file (Proxifier default: basic).</summary>
    public PasswordEncryptionMode PasswordEncryption { get; set; } = PasswordEncryptionMode.Basic;
    public bool ConnectionLoopDetection { get; set; } = true;
    public bool ResolveLoopDetection { get; set; } = true;
    public UdpMode Udp { get; set; } = UdpMode.Bypass;
    public bool LeakPreventionMode { get; set; }
    public bool ProcessOtherUsers { get; set; }
    public bool ProcessServices { get; set; }
    public bool HandleDirectConnections { get; set; }
    public bool HttpProxiesSupport { get; set; }
}

public sealed class Profile
{
    public ProfileOptions Options { get; } = new();
    public List<ProxyServer> Proxies { get; } = new();
    public List<ProxyChain> Chains { get; } = new();
    public List<Rule> Rules { get; } = new();

    /// <summary>Master password for <see cref="PasswordEncryptionMode.MasterPassword"/>; kept in memory only, never saved.</summary>
    public string? MasterPassword { get; set; }

    public ProxyServer? FindProxy(int id) => Proxies.FirstOrDefault(p => p.Id == id);
    public ProxyChain? FindChain(int id) => Chains.FirstOrDefault(c => c.Id == id);

    /// <summary>Same defaults Proxifier ships with: Localhost → Direct, Default → Direct.</summary>
    public static Profile CreateDefault()
    {
        var p = new Profile();
        p.Rules.Add(new Rule { Name = "Localhost", Targets = "localhost; 127.0.0.1; %ComputerName%; ::1", Action = RuleAction.Direct });
        p.Rules.Add(new Rule { Name = "Default", Action = RuleAction.Direct });
        return p;
    }
}
