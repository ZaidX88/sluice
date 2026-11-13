using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Core.Tunnels;

/// <summary>
/// Builds a sing-box config for one tunnel: a SOCKS5 inbound on 127.0.0.1 and one outbound speaking
/// the tunnel's protocol. Setting keys (lowercase, in <see cref="Tunnel.Settings"/>):
///
///   uuid           VMess / VLESS / TUIC user id
///   method         Shadowsocks cipher (e.g. 2022-blake3-aes-128-gcm, chacha20-ietf-poly1305)
///   plugin, pluginopts   Shadowsocks SIP003 plugin (obfs-local or v2ray-plugin) and its options
///   cipher         VMess security (default auto)
///   flow           VLESS flow (e.g. xtls-rprx-vision)
///   security       none | tls | reality  (Trojan, Hysteria2 and TUIC always use TLS)
///   sni, alpn (comma list), fingerprint (uTLS, e.g. chrome), insecure (true = skip cert check)
///   publickey, shortid   REALITY
///   transport      tcp | ws | grpc | http | httpupgrade;  path, host, servicename
///   obfspassword   Hysteria2 salamander obfuscation;  upmbps, downmbps
///   congestion     TUIC congestion control (default bbr)
///
/// The password field of the tunnel is the Shadowsocks / Trojan / Hysteria2 / TUIC password.
/// </summary>
public static class SingBoxConfig
{
    public static bool Handles(TunnelType t) =>
        t is TunnelType.Shadowsocks or TunnelType.VMess or TunnelType.VLess or TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic;

    /// <param name="serverIp">The server address already resolved by Sluice (so sing-box never sees fake DNS answers); null = use the name.</param>
    public static string Build(Tunnel t, int socksPort, string? serverIp = null, string logLevel = "warn")
    {
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = logLevel, ["timestamp"] = false },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = socksPort,
            }),
            ["outbounds"] = new JsonArray(Outbound(t, serverIp)),
            ["route"] = new JsonObject { ["final"] = "out" },
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// SSH through sing-box's client: SOCKS inbound + "ssh" outbound to <paramref name="server"/>:<paramref name="port"/>
    /// (the real server, or Sluice's local transport bridge), pinned to <paramref name="hostKey"/>.
    /// </summary>
    public static string BuildSsh(Tunnel t, int socksPort, string server, int port, string hostKey, string hostKeyAlgorithm, string logLevel = "warn")
    {
        var o = new JsonObject
        {
            ["type"] = "ssh",
            ["tag"] = "out",
            ["server"] = server,
            ["server_port"] = port,
            ["user"] = t.Username,
            ["host_key"] = new JsonArray(hostKey),
            ["host_key_algorithms"] = new JsonArray(hostKeyAlgorithm),
        };
        if (!string.IsNullOrEmpty(t.Password)) o["password"] = t.Password;
        if (t.Settings.TryGetValue("privatekey", out var key) && !string.IsNullOrWhiteSpace(key))
        {
            o["private_key"] = key.Trim() + "\n";
            if (t.Settings.TryGetValue("passphrase", out var pass) && pass.Length > 0) o["private_key_passphrase"] = pass;
        }
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = logLevel, ["timestamp"] = false },
            ["inbounds"] = new JsonArray(new JsonObject { ["type"] = "socks", ["tag"] = "in", ["listen"] = "127.0.0.1", ["listen_port"] = socksPort }),
            ["outbounds"] = new JsonArray(o),
            ["route"] = new JsonObject { ["final"] = "out" },
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static JsonObject Outbound(Tunnel t, string? serverIp = null)
    {
        var s = t.Settings;
        string? Get(string k) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        string Need(string k, string what) => Get(k) ?? throw new ProxyException($"{t.Type.DisplayName()} tunnel \"{t}\" needs {what}");
        string NeedPassword() => !string.IsNullOrEmpty(t.Password) ? t.Password! : throw new ProxyException($"{t.Type.DisplayName()} tunnel \"{t}\" needs a password");

        var o = new JsonObject
        {
            ["type"] = t.Type switch
            {
                TunnelType.Shadowsocks => "shadowsocks",
                TunnelType.VMess => "vmess",
                TunnelType.VLess => "vless",
                TunnelType.Trojan => "trojan",
                TunnelType.Hysteria2 => "hysteria2",
                TunnelType.Tuic => "tuic",
                _ => throw new ProxyException($"{t.Type.DisplayName()} is not a sing-box protocol"),
            },
            ["tag"] = "out",
            ["server"] = serverIp ?? t.Address,
            ["server_port"] = t.Port,
        };

        switch (t.Type)
        {
            case TunnelType.Shadowsocks:
                o["method"] = Need("method", "a cipher (method)");
                o["password"] = NeedPassword();
                if (Get("plugin") is { } plugin)
                {
                    // sing-box has these two SIP003 plugins built in.
                    o["plugin"] = plugin switch
                    {
                        "obfs-local" or "simple-obfs" => "obfs-local",
                        "v2ray-plugin" => "v2ray-plugin",
                        _ => throw new ProxyException($"Shadowsocks plugin \"{plugin}\" is not supported (use obfs-local or v2ray-plugin)"),
                    };
                    if (Get("pluginopts") is { } opts) o["plugin_opts"] = opts;
                }
                break;
            case TunnelType.VMess:
                o["uuid"] = Need("uuid", "a UUID");
                o["security"] = Get("cipher") ?? "auto";
                o["alter_id"] = 0;
                break;
            case TunnelType.VLess:
                o["uuid"] = Need("uuid", "a UUID");
                if (Get("flow") is { } flow) o["flow"] = flow;
                break;
            case TunnelType.Trojan:
                o["password"] = NeedPassword();
                break;
            case TunnelType.Hysteria2:
                o["password"] = NeedPassword();
                if (int.TryParse(Get("upmbps"), out var up)) o["up_mbps"] = up;
                if (int.TryParse(Get("downmbps"), out var down)) o["down_mbps"] = down;
                if (Get("obfspassword") is { } obfs) o["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = obfs };
                break;
            case TunnelType.Tuic:
                o["uuid"] = Need("uuid", "a UUID");
                o["password"] = NeedPassword();
                o["congestion_control"] = Get("congestion") ?? "bbr";
                break;
        }

        // TLS / REALITY
        var alwaysTls = t.Type is TunnelType.Trojan or TunnelType.Hysteria2 or TunnelType.Tuic;
        var security = (Get("security") ?? (alwaysTls ? "tls" : "none")).ToLowerInvariant();
        if (alwaysTls && security == "none") security = "tls";
        if (t.Type == TunnelType.Shadowsocks && security != "none")
            throw new ProxyException("Shadowsocks does not use TLS; set security to none");
        if (security is "tls" or "reality")
        {
            var tls = new JsonObject { ["enabled"] = true };
            // Like v2rayN/Xray: with no explicit SNI, the HTTP host (the real site behind a CDN address) comes before the address.
            var sni = Get("sni") ?? Get("host") ?? (IPAddress.TryParse(t.Address, out _) ? null : t.Address);
            if (sni is not null) tls["server_name"] = sni;
            if (Get("insecure") is "true" or "1") tls["insecure"] = true;
            var alpn = Get("alpn")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       ?? (t.Type == TunnelType.Tuic ? ["h3"] : null);
            if (alpn is { Length: > 0 }) tls["alpn"] = new JsonArray(alpn.Select(a => (JsonNode)a).ToArray());
            // Only fingerprints sing-box knows. Xray links also carry values like "unsafe" (= no uTLS) or "chrome_psk",
            // which would stop sing-box from starting; those mean plain TLS here. REALITY needs uTLS: default chrome.
            var fingerprint = Get("fingerprint")?.ToLowerInvariant() is { } fp && SingBoxFingerprints.Contains(fp) ? fp : null;
            if (fingerprint is null && security == "reality") fingerprint = "chrome";
            if (fingerprint is not null) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fingerprint };
            if (security == "reality")
            {
                tls["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = Need("publickey", "the REALITY public key"),
                    ["short_id"] = Get("shortid") ?? "",
                };
            }
            o["tls"] = tls;
        }
        else if (security != "none")
        {
            throw new ProxyException($"Unknown security \"{security}\" (use none, tls or reality)");
        }

        // Transport (VMess / VLESS / Trojan)
        var transport = (Get("transport") ?? "tcp").ToLowerInvariant();
        if (transport != "tcp")
        {
            if (t.Type is not (TunnelType.VMess or TunnelType.VLess or TunnelType.Trojan))
                throw new ProxyException($"{t.Type.DisplayName()} does not support the {transport} transport");
            // The HTTP Host defaults to the SNI: a CDN routes by it, and the address may be a bare IP.
            var host = Get("host") ?? (security is "tls" ? Get("sni") : null);
            o["transport"] = transport switch
            {
                "ws" => new JsonObject
                {
                    ["type"] = "ws",
                    ["path"] = Get("path") ?? "/",
                    ["headers"] = host is null ? new JsonObject() : new JsonObject { ["Host"] = host },
                },
                "grpc" => new JsonObject { ["type"] = "grpc", ["service_name"] = Get("servicename") ?? "" },
                "http" => new JsonObject
                {
                    ["type"] = "http",
                    ["path"] = Get("path") ?? "/",
                    ["host"] = host is null ? new JsonArray() : new JsonArray(host),
                },
                "httpupgrade" => new JsonObject
                {
                    ["type"] = "httpupgrade",
                    ["path"] = Get("path") ?? "/",
                    ["host"] = host ?? "",
                },
                _ => throw new ProxyException($"Unknown transport \"{transport}\" (use tcp, ws, grpc, http or httpupgrade)"),
            };
        }
        return o;
    }

    /// <summary>uTLS fingerprints sing-box accepts (option tls.utls.fingerprint).</summary>
    private static readonly HashSet<string> SingBoxFingerprints =
        ["chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized"];
}
