using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sluice.Core.Profiles;

namespace Sluice.Core.Tunnels;

/// <summary>Outcome of reading one share link.</summary>
public sealed record LinkResult(string Link, Tunnel? Tunnel, string? Error)
{
    public bool Ok => Tunnel is not null;
}

/// <summary>
/// Reads and writes the share-link formats used by v2rayN, NekoRay, Shadowrocket, HTTP Injector and
/// friends: vless:// vmess:// trojan:// ss:// hysteria2:// (hy2://) tuic:// ssh:// socks5:// http(s)://.
/// Also decodes subscription bodies (plain or base64 lists of links).
/// </summary>
public static class ShareLinks
{
    // ======================================================================
    // reading
    // ======================================================================

    /// <summary>
    /// Every link found in <paramref name="text"/>: one per line, or a base64 blob of lines (subscriptions).
    /// Lines that are not links are ignored; links that cannot be used are returned with an error.
    /// </summary>
    public static List<LinkResult> ParseMany(string text)
    {
        text = text.Trim().TrimStart('﻿');
        if (!text.Contains("://") && TryBase64(text) is { } decoded && decoded.Contains("://"))
            text = decoded;
        return text.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains("://") && !l.StartsWith('#'))
            .Select(Parse)
            .ToList();
    }

    public static LinkResult Parse(string link)
    {
        link = link.Trim();
        try
        {
            var scheme = link[..Math.Max(0, link.IndexOf("://", StringComparison.Ordinal))].ToLowerInvariant();
            var t = scheme switch
            {
                "vless" => ParseVlessOrTrojan(link, TunnelType.VLess),
                "trojan" => ParseVlessOrTrojan(link, TunnelType.Trojan),
                "vmess" => ParseVmess(link),
                "ss" => ParseShadowsocks(link),
                "hysteria2" or "hy2" => ParseHysteria2(link),
                "tuic" => ParseTuic(link),
                "ssh" => ParseUserPass(link, TunnelType.Ssh, 22),
                "socks" or "socks5" or "socks5h" => ParseUserPass(link, TunnelType.Socks5, 1080),
                "socks4" or "socks4a" => ParseUserPass(link, TunnelType.Socks4, 1080),
                "http" => ParseUserPass(link, TunnelType.Http, 8080),
                "https" => ParseUserPass(link, TunnelType.Https, 443),
                "" => throw new FormatException("not a link"),
                _ => throw new FormatException($"\"{scheme}://\" links are not supported"),
            };
            if (string.IsNullOrWhiteSpace(t.Address) || t.Port is < 1 or > 65535 ||
                (t.Address.Contains(':') && !System.Net.IPAddress.TryParse(t.Address, out _)))
                throw new FormatException("missing or invalid server or port");
            return new LinkResult(link, t, null);
        }
        catch (Exception ex) when (ex is FormatException or UriFormatException or JsonException or ArgumentException or IndexOutOfRangeException)
        {
            return new LinkResult(link, null, ex.Message);
        }
    }

    private sealed record Parts(string User, string Host, int Port, Dictionary<string, string> Query, string? Name);

    /// <summary>scheme://user@host:port/?query#name, tolerant of IPv6 hosts and odd characters in user info.</summary>
    private static Parts Split(string link, int defaultPort)
    {
        var rest = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        string? name = null;
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            name = Unescape(rest[(hash + 1)..]);
            rest = rest[..hash];
        }
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = rest.IndexOf('?');
        if (q >= 0)
        {
            foreach (var pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) query[Unescape(pair)] = "";
                else query[Unescape(pair[..eq])] = Unescape(pair[(eq + 1)..]);
            }
            rest = rest[..q];
        }
        rest = rest.TrimEnd('/');
        var at = rest.LastIndexOf('@');
        var user = at >= 0 ? rest[..at] : "";
        var (host, port) = TunnelTransport.SplitHostPort(at >= 0 ? rest[(at + 1)..] : rest, defaultPort);
        return new Parts(user, host, port, query, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }

    private static string Unescape(string s) => Uri.UnescapeDataString(s);

    private static Tunnel New(TunnelType type, Parts p) =>
        new() { Type = type, Address = p.Host, Port = p.Port, Label = p.Name };

    private static void Put(Tunnel t, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) t.Settings[key] = value.Trim();
    }

    private static string? Q(Parts p, params string[] keys)
    {
        foreach (var k in keys)
            if (p.Query.TryGetValue(k, out var v) && v.Length > 0) return v;
        return null;
    }

    private static bool Truthy(string? v) => v is "1" or "true" or "True" or "TRUE" or "yes";

    /// <summary>Shared TLS / REALITY / transport query parameters of vless:// and trojan://.</summary>
    private static void ApplyStreamParams(Tunnel t, Parts p, string defaultSecurity)
    {
        var security = (Q(p, "security") ?? defaultSecurity).ToLowerInvariant();
        if (security is "xtls") security = "tls";
        if (t.Type == TunnelType.VLess && security != "none") Put(t, "security", security);
        Put(t, "sni", Q(p, "sni", "peer"));
        Put(t, "fingerprint", Q(p, "fp", "fingerprint"));
        Put(t, "alpn", Q(p, "alpn"));
        Put(t, "publickey", Q(p, "pbk", "publicKey"));
        Put(t, "shortid", Q(p, "sid", "shortId"));
        if (Truthy(Q(p, "allowInsecure", "insecure", "allow_insecure", "skip-cert-verify"))) t.Settings["insecure"] = "true";

        var network = (Q(p, "type", "network") ?? "tcp").ToLowerInvariant();
        switch (network)
        {
            case "tcp" or "raw":
                if (Q(p, "headerType") is "http") throw new FormatException("TCP with HTTP header obfuscation is not supported");
                break;
            case "ws" or "httpupgrade" or "http" or "h2":
                Put(t, "transport", network == "h2" ? "http" : network);
                Put(t, "path", Q(p, "path"));
                Put(t, "host", Q(p, "host"));
                break;
            case "grpc":
                Put(t, "transport", "grpc");
                Put(t, "servicename", Q(p, "serviceName", "servicename", "path"));
                break;
            default:
                throw new FormatException($"the \"{network}\" transport is not supported");
        }
    }

    private static Tunnel ParseVlessOrTrojan(string link, TunnelType type)
    {
        var p = Split(link, 443);
        var t = New(type, p);
        if (type == TunnelType.VLess)
        {
            var uuid = Unescape(p.User);
            if (!Guid.TryParse(uuid, out _)) throw new FormatException("the VLESS id is not a UUID");
            t.Settings["uuid"] = uuid;
            Put(t, "flow", Q(p, "flow"));
            if (Q(p, "encryption") is { } enc && enc != "none") throw new FormatException($"VLESS encryption \"{enc}\" is not supported");
            ApplyStreamParams(t, p, "none");
        }
        else
        {
            t.Password = Unescape(p.User);
            if (string.IsNullOrEmpty(t.Password)) throw new FormatException("missing Trojan password");
            ApplyStreamParams(t, p, "tls");
        }
        return t;
    }

    private static Tunnel ParseVmess(string link)
    {
        var body = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var hash = body.IndexOf('#');
        if (hash >= 0) body = body[..hash];
        var json = TryBase64(body) ?? throw new FormatException("vmess link is not base64 JSON");
        var o = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("empty vmess JSON");
        string? S(string k) => o[k] is { } n ? (n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString()).Trim() : null;

        var t = new Tunnel
        {
            Type = TunnelType.VMess,
            Address = S("add") ?? "",
            Port = int.TryParse(S("port"), out var port) ? port : 0,
            Label = S("ps") is { Length: > 0 } ps ? ps : null,
        };
        var id = S("id") ?? "";
        if (!Guid.TryParse(id, out _)) throw new FormatException("the VMess id is not a UUID");
        t.Settings["uuid"] = id;
        if (int.TryParse(S("aid"), out var aid) && aid != 0) throw new FormatException("legacy VMess alterId > 0 is not supported");
        if (S("scy") is { Length: > 0 } scy && scy != "auto") t.Settings["cipher"] = scy;

        var tls = (S("tls") ?? "").ToLowerInvariant();
        if (tls is "tls" or "reality") t.Settings["security"] = tls;
        Put(t, "sni", S("sni"));
        Put(t, "alpn", S("alpn"));
        Put(t, "fingerprint", S("fp"));
        Put(t, "publickey", S("pbk"));
        Put(t, "shortid", S("sid"));

        var net = (S("net") ?? "tcp").ToLowerInvariant();
        switch (net)
        {
            case "tcp" or "raw":
                if (S("type") is "http") throw new FormatException("TCP with HTTP header obfuscation is not supported");
                break;
            case "ws" or "httpupgrade" or "http" or "h2":
                t.Settings["transport"] = net == "h2" ? "http" : net;
                Put(t, "path", S("path"));
                Put(t, "host", S("host"));
                break;
            case "grpc":
                t.Settings["transport"] = "grpc";
                Put(t, "servicename", S("path"));
                break;
            default:
                throw new FormatException($"the \"{net}\" transport is not supported");
        }
        return t;
    }

    private static Tunnel ParseShadowsocks(string link)
    {
        var body = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        string? name = null;
        var hash = body.IndexOf('#');
        if (hash >= 0)
        {
            name = Unescape(body[(hash + 1)..]);
            body = body[..hash];
        }
        Dictionary<string, string> query = new(StringComparer.OrdinalIgnoreCase);
        var q = body.IndexOf('?');
        if (q >= 0)
        {
            foreach (var pair in body[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) query[Unescape(pair[..eq])] = Unescape(pair[(eq + 1)..]);
            }
            body = body[..q];
        }
        body = body.TrimEnd('/');

        string userInfo, hostPort;
        var at = body.LastIndexOf('@');
        if (at >= 0)
        {
            // SIP002: base64(method:password)@host:port, or plain URL-encoded method:password (2022 ciphers).
            var raw = body[..at];
            userInfo = TryBase64(raw) is { } dec && dec.Contains(':') ? dec : Unescape(raw);
            hostPort = body[(at + 1)..];
        }
        else
        {
            // Legacy: base64(method:password@host:port)
            var dec = TryBase64(body) ?? throw new FormatException("ss link is not valid base64");
            var at2 = dec.LastIndexOf('@');
            if (at2 < 0) throw new FormatException("ss link has no server");
            userInfo = dec[..at2];
            hostPort = dec[(at2 + 1)..];
        }
        var colon = userInfo.IndexOf(':');
        if (colon <= 0) throw new FormatException("ss link has no cipher:password");
        var (host, port) = TunnelTransport.SplitHostPort(hostPort, 8388);
        var t = new Tunnel
        {
            Type = TunnelType.Shadowsocks, Address = host, Port = port,
            Password = userInfo[(colon + 1)..], Label = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
        };
        t.Settings["method"] = userInfo[..colon];
        if (query.TryGetValue("plugin", out var plugin) && plugin.Length > 0)
        {
            var semi = plugin.IndexOf(';');
            t.Settings["plugin"] = semi < 0 ? plugin : plugin[..semi];
            if (semi >= 0) t.Settings["pluginopts"] = plugin[(semi + 1)..];
        }
        return t;
    }

    private static Tunnel ParseHysteria2(string link)
    {
        var p = Split(link, 443);
        var t = New(TunnelType.Hysteria2, p);
        t.Password = Unescape(p.User);
        if (string.IsNullOrEmpty(t.Password)) t.Password = Q(p, "auth", "password");
        if (string.IsNullOrEmpty(t.Password)) throw new FormatException("missing Hysteria2 password");
        Put(t, "sni", Q(p, "sni", "peer"));
        Put(t, "alpn", Q(p, "alpn"));
        if (Truthy(Q(p, "insecure", "allowInsecure", "allow_insecure"))) t.Settings["insecure"] = "true";
        if (Q(p, "obfs") is { } obfs)
        {
            if (!obfs.Equals("salamander", StringComparison.OrdinalIgnoreCase)) throw new FormatException($"Hysteria2 obfs \"{obfs}\" is not supported");
            Put(t, "obfspassword", Q(p, "obfs-password", "obfs_password", "obfsPassword"));
        }
        Put(t, "upmbps", Digits(Q(p, "up", "upmbps")));
        Put(t, "downmbps", Digits(Q(p, "down", "downmbps")));
        return t;
    }

    private static Tunnel ParseTuic(string link)
    {
        var p = Split(link, 443);
        var t = New(TunnelType.Tuic, p);
        var user = Unescape(p.User);
        var colon = user.IndexOf(':');
        var uuid = colon >= 0 ? user[..colon] : user;
        if (!Guid.TryParse(uuid, out _)) throw new FormatException("the TUIC id is not a UUID");
        t.Settings["uuid"] = uuid;
        t.Password = colon >= 0 ? user[(colon + 1)..] : Q(p, "password");
        if (string.IsNullOrEmpty(t.Password)) throw new FormatException("missing TUIC password");
        Put(t, "sni", Q(p, "sni"));
        Put(t, "alpn", Q(p, "alpn"));
        Put(t, "congestion", Q(p, "congestion_control", "congestion"));
        if (Truthy(Q(p, "allow_insecure", "insecure", "allowInsecure"))) t.Settings["insecure"] = "true";
        return t;
    }

    private static Tunnel ParseUserPass(string link, TunnelType type, int defaultPort)
    {
        var p = Split(link, defaultPort);
        var t = New(type, p);
        var user = p.User;
        // Some apps base64 the whole user:pass.
        if (user.Length > 0 && !user.Contains(':') && TryBase64(user) is { } dec && dec.Contains(':')) user = dec;
        var colon = user.IndexOf(':');
        t.Username = (colon >= 0 ? Unescape(user[..colon]) : Unescape(user)) is { Length: > 0 } u ? u : null;
        t.Password = colon >= 0 && Unescape(user[(colon + 1)..]) is { Length: > 0 } pw ? pw : null;
        if (type == TunnelType.Socks4) t.Socks4aRemoteResolve = link.StartsWith("socks4a", StringComparison.OrdinalIgnoreCase);
        return t;
    }

    private static string? Digits(string? s) => s is null ? null : new string(s.TakeWhile(char.IsDigit).ToArray()) is { Length: > 0 } d ? d : null;

    /// <summary>Standard or URL-safe base64, with or without padding; null when it is not valid UTF-8 base64.</summary>
    public static string? TryBase64(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "");
        if (s.Length == 0 || s.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))) return null;
        s = s.TrimEnd('=');
        s += new string('=', (4 - s.Length % 4) % 4);
        try
        {
            var bytes = Convert.FromBase64String(s);
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return text;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    // ======================================================================
    // writing
    // ======================================================================

    /// <summary>A share link for <paramref name="t"/> that other clients (and Sluice) can import.</summary>
    public static string ToLink(Tunnel t)
    {
        string E(string s) => Uri.EscapeDataString(s);
        string? Get(string k) => t.Settings.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        var host = t.Address.Contains(':') ? $"[{t.Address}]" : t.Address;
        var name = t.Label is { Length: > 0 } l ? "#" + E(l) : "";

        string Query(IEnumerable<(string Key, string? Value)> items)
        {
            var parts = items.Where(i => !string.IsNullOrEmpty(i.Value)).Select(i => $"{i.Key}={E(i.Value!)}").ToList();
            return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
        }

        IEnumerable<(string, string?)> Stream(string defaultSecurity)
        {
            var transport = Get("transport") ?? "tcp";
            yield return ("security", Get("security") ?? defaultSecurity);
            yield return ("sni", Get("sni"));
            yield return ("fp", Get("fingerprint"));
            yield return ("alpn", Get("alpn"));
            yield return ("pbk", Get("publickey"));
            yield return ("sid", Get("shortid"));
            if (Get("insecure") is "true") yield return ("allowInsecure", "1");
            yield return ("type", transport);
            if (transport == "grpc") yield return ("serviceName", Get("servicename"));
            else if (transport != "tcp") { yield return ("path", Get("path")); yield return ("host", Get("host")); }
        }

        switch (t.Type)
        {
            case TunnelType.VLess:
                return $"vless://{Get("uuid")}@{host}:{t.Port}" +
                       Query(new (string, string?)[] { ("encryption", "none"), ("flow", Get("flow")) }.Concat(Stream("none"))) + name;
            case TunnelType.Trojan:
                return $"trojan://{E(t.Password ?? "")}@{host}:{t.Port}" + Query(Stream("tls")) + name;
            case TunnelType.VMess:
            {
                var transport = Get("transport") ?? "tcp";
                var o = new JsonObject
                {
                    ["v"] = "2", ["ps"] = t.Label ?? "", ["add"] = t.Address, ["port"] = t.Port.ToString(), ["id"] = Get("uuid") ?? "",
                    ["aid"] = "0", ["scy"] = Get("cipher") ?? "auto", ["net"] = transport == "http" ? "h2" : transport, ["type"] = "none",
                    ["host"] = Get("host") ?? "", ["path"] = transport == "grpc" ? Get("servicename") ?? "" : Get("path") ?? "",
                    ["tls"] = Get("security") ?? "", ["sni"] = Get("sni") ?? "", ["alpn"] = Get("alpn") ?? "", ["fp"] = Get("fingerprint") ?? "",
                };
                return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ToJsonString()));
            }
            case TunnelType.Shadowsocks:
            {
                var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Get("method")}:{t.Password}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var plugin = Get("plugin") is { } pl ? "/?plugin=" + E(Get("pluginopts") is { } po ? $"{pl};{po}" : pl) : "";
                return $"ss://{userInfo}@{host}:{t.Port}{plugin}{name}";
            }
            case TunnelType.Hysteria2:
                return $"hysteria2://{E(t.Password ?? "")}@{host}:{t.Port}" + Query(new (string, string?)[]
                {
                    ("sni", Get("sni")), ("alpn", Get("alpn")), ("insecure", Get("insecure") is "true" ? "1" : null),
                    ("obfs", Get("obfspassword") is null ? null : "salamander"), ("obfs-password", Get("obfspassword")),
                    ("up", Get("upmbps")), ("down", Get("downmbps")),
                }) + name;
            case TunnelType.Tuic:
                return $"tuic://{Get("uuid")}:{E(t.Password ?? "")}@{host}:{t.Port}" + Query(new (string, string?)[]
                {
                    ("sni", Get("sni")), ("alpn", Get("alpn")), ("congestion_control", Get("congestion")),
                    ("allow_insecure", Get("insecure") is "true" ? "1" : null),
                }) + name;
            default:
            {
                var scheme = t.Type switch
                {
                    TunnelType.Ssh => "ssh", TunnelType.Socks4 => t.Socks4aRemoteResolve ? "socks4a" : "socks4",
                    TunnelType.Https => "https", TunnelType.Http => "http", _ => "socks5",
                };
                var auth = t.Username is { Length: > 0 } u ? E(u) + (t.Password is { Length: > 0 } pw ? ":" + E(pw) : "") + "@" : "";
                return $"{scheme}://{auth}{host}:{t.Port}{name}";
            }
        }
    }
}
