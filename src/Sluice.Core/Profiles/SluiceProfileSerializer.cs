using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sluice.Core.Profiles;

/// <summary>
/// Sluice's own profile format (".sluice"): readable JSON that can hold every tunnel type,
/// including protocol settings Proxifier's .ppx cannot store. Passwords and other secret settings
/// are encrypted with the same modes as .ppx (disabled / basic / account / master).
/// </summary>
public static class SluiceProfileSerializer
{
    public const string FormatName = "sluice-profile";
    public const int CurrentVersion = 1;

    /// <summary>Setting keys whose values are secrets (encrypted like passwords).</summary>
    public static readonly IReadOnlySet<string> SecretSettings =
        new HashSet<string>(["password", "passphrase", "privatekey", "uuid", "auth", "psk", "token", "obfspassword"], StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        Converters = { new JsonStringEnumConverter(new LowercasePolicy()) },
    };

    /// <summary>Enum values as protocol names are usually written: "socks5", "vless", "hysteria2".</summary>
    private sealed class LowercasePolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name) => name.ToLowerInvariant();
    }

    // ---------------- document shape ----------------

    private sealed class Doc
    {
        public string Format { get; set; } = FormatName;
        public int Version { get; set; } = CurrentVersion;
        public string? PasswordHash { get; set; }
        public ProfileOptions Options { get; set; } = new();
        public List<TunnelDoc> Tunnels { get; set; } = [];
        public List<TunnelChain> Chains { get; set; } = [];
        public List<Rule> Rules { get; set; } = [];
        public List<Subscription>? Subscriptions { get; set; }
    }

    private sealed class TunnelDoc
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public TunnelType Type { get; set; }
        public string Address { get; set; } = "";
        public int Port { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool? Socks4aRemoteResolve { get; set; }
        public int? ProxifierOptions { get; set; }
        public Dictionary<string, string>? Settings { get; set; }
        public int? Subscription { get; set; }
    }

    // ---------------- reading ----------------

    public static Profile Load(string path, MasterPasswordProvider? masterPassword = null) =>
        Parse(File.ReadAllText(path), masterPassword, Path.GetFileNameWithoutExtension(path));

    public static Profile Parse(string json, MasterPasswordProvider? masterPassword = null, string profileName = "profile")
    {
        Doc doc;
        try
        {
            doc = JsonSerializer.Deserialize<Doc>(json, Json) ?? throw new FormatException("Empty profile");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not a valid Sluice profile: {ex.Message}");
        }
        if (doc.Format != FormatName) throw new FormatException($"Not a Sluice profile (format \"{doc.Format}\")");
        if (doc.Version > CurrentVersion)
            throw new FormatException($"This profile was saved by a newer Sluice (format version {doc.Version}); please update Sluice.");

        var profile = new Profile { Options = doc.Options };
        var codec = PpxSerializer.OpenCodec(profile, doc.PasswordHash, masterPassword, profileName);

        foreach (var d in doc.Tunnels)
        {
            var t = new Tunnel
            {
                Id = d.Id,
                Label = d.Name,
                Type = d.Type,
                Address = d.Address,
                Port = d.Port,
                Username = d.Username,
                Socks4aRemoteResolve = d.Socks4aRemoteResolve ?? true,
                Options = d.ProxifierOptions ?? 0,
                SubscriptionId = d.Subscription,
            };
            t.Password = Decode(codec, d.Password, t, "password");
            foreach (var (k, v) in d.Settings ?? [])
                t.Settings[k] = SecretSettings.Contains(k) ? Decode(codec, v, t, k)! : v;
            profile.Tunnels.Add(t);
        }
        profile.Chains.AddRange(doc.Chains);
        profile.Rules.AddRange(doc.Rules);
        profile.Subscriptions.AddRange(doc.Subscriptions ?? []);
        if (!profile.Rules.Any(r => r.IsDefault))
            profile.Rules.Add(new Rule { Name = "Default", Action = RuleAction.Direct });
        return profile;
    }

    private static string? Decode(IPasswordCodec codec, string? stored, Tunnel t, string what)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        try { return codec.Decode(stored); }
        catch (CryptographicFailure ex) { throw new CryptographicFailure($"Cannot decrypt the {what} of tunnel {t}: {ex.Message}"); }
    }

    // ---------------- writing ----------------

    public static void Save(Profile profile, string path)
    {
        var json = Serialize(profile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);   // never leave a half-written profile behind
    }

    public static string Serialize(Profile p)
    {
        var codec = new ProxifierPasswordCodec(p.Options.PasswordEncryption, p.MasterPassword);
        var doc = new Doc
        {
            PasswordHash = codec.Hash,
            Options = p.Options,
            Chains = p.Chains,
            Rules = p.Rules,
            Subscriptions = p.Subscriptions.Count == 0 ? null : p.Subscriptions,
            Tunnels = p.Tunnels.Select(t => new TunnelDoc
            {
                Id = t.Id,
                Name = t.Label,
                Type = t.Type,
                Address = t.Address,
                Port = t.Port,
                Username = t.Username,
                Password = string.IsNullOrEmpty(t.Password) ? null : codec.Encode(t.Password),
                Socks4aRemoteResolve = t.Type == TunnelType.Socks4 ? t.Socks4aRemoteResolve : null,
                ProxifierOptions = t.Options == 0 ? null : t.Options,
                Subscription = t.SubscriptionId,
                Settings = t.Settings.Count == 0
                    ? null
                    : t.Settings.ToDictionary(kv => kv.Key, kv => SecretSettings.Contains(kv.Key) && kv.Value.Length > 0 ? codec.Encode(kv.Value) : kv.Value),
            }).ToList(),
        };
        return JsonSerializer.Serialize(doc, Json);
    }
}

/// <summary>Picks the right format for a profile file: .sluice (JSON) or Proxifier's .ppx (XML).</summary>
public static class ProfileFile
{
    public const string Extension = ".sluice";

    public static bool IsProxifier(string path) =>
        path.EndsWith(".ppx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".prx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads either format, deciding by the file's content rather than its name.</summary>
    public static Profile Load(string path, MasterPasswordProvider? masterPassword = null)
    {
        var text = File.ReadAllText(path).TrimStart('﻿', ' ', '\t', '\r', '\n');
        var name = Path.GetFileNameWithoutExtension(path);
        return text.StartsWith('<')
            ? PpxSerializer.Parse(text, masterPassword, name)
            : SluiceProfileSerializer.Parse(text, masterPassword, name);
    }

    /// <summary>Saves by extension. A .ppx export may lose tunnels Proxifier cannot express; see <paramref name="warnings"/>.</summary>
    public static void Save(Profile profile, string path, List<string>? warnings = null)
    {
        if (IsProxifier(path)) PpxSerializer.ToXml(profile, warnings).Save(path);
        else SluiceProfileSerializer.Save(profile, path);
    }
}
