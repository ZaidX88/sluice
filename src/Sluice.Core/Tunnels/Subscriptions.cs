using System.Net;
using System.Net.Sockets;
using Sluice.Core.Profiles;

namespace Sluice.Core.Tunnels;

/// <summary>What importing links or refreshing a subscription changed.</summary>
public sealed class ImportResult
{
    public List<Tunnel> Added { get; } = [];
    public List<Tunnel> Updated { get; } = [];
    public List<Tunnel> Unchanged { get; } = [];
    public List<Tunnel> Removed { get; } = [];
    public List<string> Warnings { get; } = [];

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Added.Count > 0) parts.Add($"{Added.Count} added");
            if (Updated.Count > 0) parts.Add($"{Updated.Count} updated");
            if (Unchanged.Count > 0) parts.Add($"{Unchanged.Count} unchanged");
            if (Removed.Count > 0) parts.Add($"{Removed.Count} removed");
            if (Warnings.Count > 0) parts.Add($"{Warnings.Count} skipped or warned");
            return parts.Count == 0 ? "nothing to import" : string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Imports share links into a profile and keeps subscriptions in sync. Tunnels keep their ids across
/// refreshes (matched by server identity, then by name), so rules and chains that use them keep working.
/// </summary>
public static class Subscriptions
{
    public const string UserAgent = "Sluice/1.0 (v2rayN-compatible subscription client)";

    /// <summary>Adds every usable link in <paramref name="text"/> as a new tunnel (duplicates of existing tunnels are skipped).</summary>
    public static ImportResult ImportLinks(Profile profile, string text)
    {
        var result = new ImportResult();
        foreach (var r in ShareLinks.ParseMany(text))
        {
            if (!r.Ok)
            {
                result.Warnings.Add($"{Shorten(r.Link)}: {r.Error}");
                continue;
            }
            var t = r.Tunnel!;
            if (profile.Tunnels.FirstOrDefault(x => x.SubscriptionId is null && Identity(x) == Identity(t)) is { } existing)
            {
                result.Unchanged.Add(existing);
                continue;
            }
            t.Id = NextTunnelId(profile);
            profile.Tunnels.Add(t);
            result.Added.Add(t);
        }
        if (result.Added.Count + result.Unchanged.Count == 0 && result.Warnings.Count == 0)
            result.Warnings.Add(LooksLikeUnsupportedFormat(text) ?? "No share links found");
        return result;
    }

    public static Subscription AddSubscription(Profile profile, string url, string? name = null)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("A subscription must be an http:// or https:// URL");
        if (profile.Subscriptions.FirstOrDefault(s => s.Url.Equals(uri.ToString(), StringComparison.Ordinal)) is { } same) return same;
        var sub = new Subscription
        {
            Id = profile.Subscriptions.Count == 0 ? 1 : profile.Subscriptions.Max(s => s.Id) + 1,
            Url = uri.ToString(),
            Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim(),
        };
        profile.Subscriptions.Add(sub);
        return sub;
    }

    /// <summary>Removes a subscription and its tunnels (rules using them fall back to Direct).</summary>
    public static ImportResult RemoveSubscription(Profile profile, Subscription sub)
    {
        var result = new ImportResult();
        foreach (var t in profile.Tunnels.Where(t => t.SubscriptionId == sub.Id).ToList()) RemoveTunnel(profile, t, result);
        profile.Subscriptions.Remove(sub);
        return result;
    }

    /// <summary>Downloads a subscription body (sent direct; Sluice's own traffic is never captured).</summary>
    public static async Task<string> FetchAsync(string url, TunnelTransport.Resolver? resolve = null, CancellationToken ct = default)
    {
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            UseProxy = false,
        };
        if (resolve is not null)
        {
            // Resolve with Sluice's resolver: the OS cache may hold Sluice's own fake answers.
            handler.ConnectCallback = async (ctx, token) =>
            {
                var host = ctx.DnsEndPoint.Host;
                var addrs = IPAddress.TryParse(host, out var ip) ? [ip] : await resolve(host, token);
                Exception? last = null;
                foreach (var a in addrs)
                {
                    var s = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await s.ConnectAsync(a, ctx.DnsEndPoint.Port, token);
                        return new NetworkStream(s, ownsSocket: true);
                    }
                    catch (SocketException ex)
                    {
                        s.Dispose();
                        last = ex;
                    }
                }
                throw last ?? new SocketException((int)SocketError.HostNotFound);
            };
        }
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"The server answered {(int)resp.StatusCode} {resp.ReasonPhrase}");
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (body.Length > 8 * 1024 * 1024) throw new InvalidDataException("The subscription is larger than 8 MB");
        return body;
    }

    /// <summary>Fetches and applies a subscription. Network or format failures leave the existing tunnels as they are.</summary>
    public static async Task<ImportResult> UpdateAsync(Profile profile, Subscription sub, TunnelTransport.Resolver? resolve = null, CancellationToken ct = default)
    {
        string body;
        try
        {
            body = await FetchAsync(sub.Url, resolve, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            sub.LastError = ex is TaskCanceledException ? "timed out" : ex.Message;
            throw new HttpRequestException($"Cannot update \"{sub.Name}\": {sub.LastError}", ex);
        }
        var result = Apply(profile, sub, body);
        if (result.Added.Count + result.Updated.Count + result.Unchanged.Count == 0)
        {
            sub.LastError = result.Warnings.FirstOrDefault() ?? "no usable links";
            throw new InvalidDataException($"Cannot update \"{sub.Name}\": {sub.LastError}");
        }
        sub.LastError = null;
        sub.LastUpdated = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    /// Replaces the tunnels of <paramref name="sub"/> with those in <paramref name="body"/>. If the body has
    /// no usable link at all, nothing is touched (a broken download must not wipe the tunnels).
    /// </summary>
    public static ImportResult Apply(Profile profile, Subscription sub, string body)
    {
        var result = new ImportResult();
        var incoming = new List<Tunnel>();
        foreach (var r in ShareLinks.ParseMany(body))
        {
            if (r.Ok) incoming.Add(r.Tunnel!);
            else result.Warnings.Add($"{Shorten(r.Link)}: {r.Error}");
        }
        if (incoming.Count == 0)
        {
            if (result.Warnings.Count == 0) result.Warnings.Add(LooksLikeUnsupportedFormat(body) ?? "No share links found");
            return result;
        }

        var existing = profile.Tunnels.Where(t => t.SubscriptionId == sub.Id).ToList();
        var claimed = new HashSet<Tunnel>();

        Tunnel? Claim(Func<Tunnel, bool> match)
        {
            var hit = existing.FirstOrDefault(e => !claimed.Contains(e) && match(e));
            if (hit is not null) claimed.Add(hit);
            return hit;
        }

        var pending = new List<Tunnel>();
        // Pass 1: same server identity (type, host, port, user id) → same tunnel.
        foreach (var t in incoming)
        {
            if (Claim(e => Identity(e) == Identity(t)) is { } hit) Merge(hit, t, result);
            else pending.Add(t);
        }
        // Pass 2: same name (the provider moved the server or rotated credentials).
        var stillNew = new List<Tunnel>();
        foreach (var t in pending)
        {
            if (t.Label is not null && Claim(e => e.Type == t.Type && e.Label == t.Label) is { } hit) Merge(hit, t, result);
            else stillNew.Add(t);
        }
        foreach (var t in stillNew)
        {
            t.Id = NextTunnelId(profile);
            t.SubscriptionId = sub.Id;
            profile.Tunnels.Add(t);
            result.Added.Add(t);
        }
        foreach (var gone in existing.Where(e => !claimed.Contains(e))) RemoveTunnel(profile, gone, result);
        return result;
    }

    private static void Merge(Tunnel target, Tunnel from, ImportResult result)
    {
        var before = TunnelBackends.Fingerprint(target) + "|" + target.Label;
        var sameServer = target.Address.Equals(from.Address, StringComparison.OrdinalIgnoreCase) && target.Port == from.Port;
        target.Type = from.Type;
        target.Address = from.Address;
        target.Port = from.Port;
        target.Username = from.Username;
        target.Password = from.Password;
        target.Label = from.Label;
        target.Socks4aRemoteResolve = from.Socks4aRemoteResolve;
        // Keep what Sluice learned locally about this server (a trusted SSH host key) unless the server moved.
        var keep = sameServer ? target.Settings.Where(kv => kv.Key.Equals("hostkey", StringComparison.OrdinalIgnoreCase)).ToList() : [];
        target.Settings.Clear();
        foreach (var (k, v) in from.Settings) target.Settings[k] = v;
        foreach (var (k, v) in keep) target.Settings.TryAdd(k, v);
        (TunnelBackends.Fingerprint(target) + "|" + target.Label == before ? result.Unchanged : result.Updated).Add(target);
    }

    /// <summary>Removes a tunnel; rules that used it go Direct and chains lose the hop.</summary>
    public static void RemoveTunnel(Profile profile, Tunnel t, ImportResult result)
    {
        profile.Tunnels.Remove(t);
        result.Removed.Add(t);
        foreach (var c in profile.Chains)
        {
            if (c.Hops.RemoveAll(h => h.TunnelId == t.Id) > 0)
                result.Warnings.Add($"Chain \"{c.Name}\" lost the removed tunnel \"{t}\"");
        }
        foreach (var r in profile.Rules.Where(r => r.Action.Type == RuleActionType.Tunnel && r.Action.TargetId == t.Id))
        {
            r.Action = RuleAction.Direct;
            result.Warnings.Add($"Rule \"{r.Name}\" used the removed tunnel \"{t}\" and now goes Direct");
        }
    }

    /// <summary>Who the server is and who we are on it; display names and transport tweaks do not count.</summary>
    internal static string Identity(Tunnel t)
    {
        var user = t.Settings.TryGetValue("uuid", out var u) && u.Length > 0 ? u : t.Username + ":" + t.Password;
        return $"{t.Type}|{t.Address.ToLowerInvariant()}|{t.Port}|{user}";
    }

    public static int NextTunnelId(Profile p) => p.Tunnels.Count == 0 ? 100 : Math.Max(100, p.Tunnels.Max(x => x.Id) + 1);

    private static string Shorten(string link) => link.Length <= 48 ? link : link[..45] + "...";

    private static string? LooksLikeUnsupportedFormat(string body)
    {
        var b = body.TrimStart();
        if (b.StartsWith('<')) return "The URL returned a web page, not a subscription";
        if (b.Contains("proxies:", StringComparison.Ordinal)) return "Clash YAML subscriptions are not supported; ask the provider for a v2rayN / base64 link list";
        if (b.StartsWith('{') && b.Contains("\"outbounds\"", StringComparison.Ordinal)) return "sing-box JSON subscriptions are not supported; ask the provider for a v2rayN / base64 link list";
        return null;
    }
}
