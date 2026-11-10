namespace Sluice.Core.Profiles;

/// <summary>
/// How a tunnel reaches its server. The first four are plain proxy protocols Sluice speaks itself
/// (and that Proxifier's .ppx format can store); the rest are encrypted tunnels added in later steps.
/// </summary>
public enum TunnelType
{
    Socks4, Socks5, Https, Http,
    Ssh, Shadowsocks, VMess, VLess, Trojan, Hysteria2, Tuic,
}

public static class TunnelTypes
{
    /// <summary>Plain proxy protocols: storable in .ppx and handled by Sluice's own handshakes.</summary>
    public static bool IsSimple(this TunnelType t) => t is TunnelType.Socks4 or TunnelType.Socks5 or TunnelType.Https or TunnelType.Http;

    public static string DisplayName(this TunnelType t) => t switch
    {
        TunnelType.Socks4 => "SOCKS4",
        TunnelType.Socks5 => "SOCKS5",
        TunnelType.Https => "HTTPS",
        TunnelType.Http => "HTTP",
        TunnelType.Ssh => "SSH",
        TunnelType.Shadowsocks => "Shadowsocks",
        TunnelType.VMess => "VMess",
        TunnelType.VLess => "VLESS",
        TunnelType.Trojan => "Trojan",
        TunnelType.Hysteria2 => "Hysteria2",
        TunnelType.Tuic => "TUIC",
        _ => t.ToString(),
    };
}

public enum RuleActionType { Direct, Block, Tunnel, Chain }

public enum ChainType { Simple, Redundancy, LoadBalancing }

/// <summary>
/// What happens to UDP from applications whose rule sends them through a tunnel (Direct rules always pass,
/// Block rules always drop). Only applies while traffic is intercepted.
///   Bypass     UDP is not captured: it goes direct (Proxifier's default)
///   BlockQuic  UDP to port 443 (QUIC / HTTP3) is dropped so browsers fall back to TCP; other UDP goes direct
///   BlockAll   all their UDP is dropped
///   Tunnel     UDP goes through the tunnel (SOCKS5 and sing-box tunnels); dropped if the tunnel cannot carry UDP
/// </summary>
public enum UdpMode { Bypass, BlockQuic, BlockAll, Tunnel }

/// <summary>An outbound route that rules can send traffic through.</summary>
public sealed class Tunnel
{
    public int Id { get; set; }
    public TunnelType Type { get; set; } = TunnelType.Socks5;
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Display name; falls back to address:port.</summary>
    public string? Label { get; set; }

    /// <summary>Raw Proxifier option bitmask (kept for round-tripping .ppx).</summary>
    public int Options { get; set; }

    /// <summary>SOCKS4a: let the tunnel server resolve hostnames.</summary>
    public bool Socks4aRemoteResolve { get; set; } = true;

    /// <summary>
    /// Protocol-specific settings (e.g. SSH payload, VLESS uuid / transport / REALITY keys), keyed by
    /// lowercase names. Only stored in .sluice profiles.
    /// </summary>
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The subscription this tunnel came from (refreshing it may update or remove the tunnel).</summary>
    public int? SubscriptionId { get; set; }

    public override string ToString() => Label ?? $"{Address}:{Port}";
}

public sealed class TunnelChain
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
    public int TunnelId { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class RuleAction
{
    public RuleActionType Type { get; set; } = RuleActionType.Direct;

    /// <summary>Tunnel id or chain id when <see cref="Type"/> is Tunnel/Chain.</summary>
    public int? TargetId { get; set; }

    public static RuleAction Direct => new() { Type = RuleActionType.Direct };
    public static RuleAction Block => new() { Type = RuleActionType.Block };
    public static RuleAction ViaTunnel(int id) => new() { Type = RuleActionType.Tunnel, TargetId = id };
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

    [System.Text.Json.Serialization.JsonIgnore]
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
    public UdpMode Udp { get; set; } = UdpMode.Tunnel;
    public bool LeakPreventionMode { get; set; }
    public bool ProcessOtherUsers { get; set; }
    public bool ProcessServices { get; set; }
    public bool HandleDirectConnections { get; set; }
    public bool HttpProxiesSupport { get; set; }
}

/// <summary>A URL that serves a list of share links (v2rayN-style subscription).</summary>
public sealed class Subscription
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime? LastUpdated { get; set; }
    public string? LastError { get; set; }
}

public sealed class Profile
{
    public ProfileOptions Options { get; set; } = new();
    public List<Tunnel> Tunnels { get; } = new();
    public List<TunnelChain> Chains { get; } = new();
    public List<Rule> Rules { get; } = new();
    public List<Subscription> Subscriptions { get; } = new();

    /// <summary>Master password for <see cref="PasswordEncryptionMode.MasterPassword"/>; kept in memory only, never saved.</summary>
    public string? MasterPassword { get; set; }

    public Tunnel? FindTunnel(int id) => Tunnels.FirstOrDefault(p => p.Id == id);
    public TunnelChain? FindChain(int id) => Chains.FirstOrDefault(c => c.Id == id);

    /// <summary>Same defaults Proxifier ships with: Localhost → Direct, Default → Direct.</summary>
    public static Profile CreateDefault()
    {
        var p = new Profile();
        p.Rules.Add(new Rule { Name = "Localhost", Targets = "localhost; 127.0.0.1; %ComputerName%; ::1", Action = RuleAction.Direct });
        p.Rules.Add(new Rule { Name = "Default", Action = RuleAction.Direct });
        return p;
    }
}
