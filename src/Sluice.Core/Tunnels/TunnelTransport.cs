using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Sluice.Core.Proxy;

namespace Sluice.Core.Tunnels;

/// <summary>How the raw connection to an SSH server is made (HTTP Injector's connection modes).</summary>
public enum TransportMode
{
    /// <summary>Plain TCP to the server.</summary>
    Direct,
    /// <summary>TCP to the server (or a remote HTTP proxy), send the payload, then SSH.</summary>
    Payload,
    /// <summary>TLS to the server with a chosen SNI, then SSH ("SSH-SSL").</summary>
    Tls,
    /// <summary>TLS with SNI, then the payload inside TLS (e.g. a WebSocket upgrade), then SSH.</summary>
    TlsPayload,
    /// <summary>Payload first (e.g. CONNECT through a proxy), then TLS with SNI, then SSH.</summary>
    PayloadTls,
}

/// <summary>Transport settings, read from a tunnel's settings dictionary.</summary>
public sealed record TransportOptions
{
    public TransportMode Mode { get; init; } = TransportMode.Direct;
    public string Payload { get; init; } = "";
    /// <summary>Optional remote HTTP proxy to connect to first ("host:port").</summary>
    public string? RemoteProxy { get; init; }
    /// <summary>SNI for TLS modes; defaults to the server name.</summary>
    public string? Sni { get; init; }
    /// <summary>Do not wait for an HTTP reply after sending the payload.</summary>
    public bool SkipResponse { get; init; }
    public string UserAgent { get; init; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    public bool UsesPayload => Mode is TransportMode.Payload or TransportMode.TlsPayload or TransportMode.PayloadTls;
    public bool UsesTls => Mode is TransportMode.Tls or TransportMode.TlsPayload or TransportMode.PayloadTls;

    public static TransportOptions From(IReadOnlyDictionary<string, string> s) => new()
    {
        Mode = s.TryGetValue("mode", out var m) ? ParseMode(m) : TransportMode.Direct,
        Payload = s.TryGetValue("payload", out var p) ? p : "",
        RemoteProxy = s.TryGetValue("remoteproxy", out var rp) && !string.IsNullOrWhiteSpace(rp) ? rp.Trim() : null,
        Sni = s.TryGetValue("sni", out var sni) && !string.IsNullOrWhiteSpace(sni) ? sni.Trim() : null,
        SkipResponse = s.TryGetValue("skipresponse", out var sr) && sr.Equals("true", StringComparison.OrdinalIgnoreCase),
    };

    public static TransportMode ParseMode(string s) => s.Trim().ToLowerInvariant().Replace("_", "-") switch
    {
        "payload" => TransportMode.Payload,
        "tls" or "ssl" => TransportMode.Tls,
        "tls-payload" or "ssl-payload" => TransportMode.TlsPayload,
        "payload-tls" or "payload-ssl" => TransportMode.PayloadTls,
        _ => TransportMode.Direct,
    };

    public static string ModeName(TransportMode m) => m switch
    {
        TransportMode.Payload => "payload",
        TransportMode.Tls => "tls",
        TransportMode.TlsPayload => "tls-payload",
        TransportMode.PayloadTls => "payload-tls",
        _ => "direct",
    };
}

/// <summary>Opens the byte stream an SSH client talks over, per <see cref="TransportOptions"/>.</summary>
public static class TunnelTransport
{
    public delegate Task<IPAddress[]> Resolver(string host, CancellationToken ct);

    /// <summary>
    /// Connects to <paramref name="host"/>:<paramref name="port"/> (or the remote proxy), applies payload / TLS,
    /// and returns the stream ready for SSH. <paramref name="log"/> receives progress lines.
    /// </summary>
    public static async Task<Stream> ConnectAsync(string host, int port, TransportOptions o, Resolver? resolve,
        Action<string>? log, CancellationToken ct)
    {
        var (firstHost, firstPort) = (host, port);
        if (o.UsesPayload && o.RemoteProxy is { } rp && o.Mode != TransportMode.TlsPayload)
            (firstHost, firstPort) = SplitHostPort(rp, 8080);

        var tcp = await OpenTcpAsync(firstHost, firstPort, resolve, ct);
        Stream stream = tcp.GetStream();
        try
        {
            log?.Invoke($"transport: TCP connected to {firstHost}:{firstPort}");
            switch (o.Mode)
            {
                case TransportMode.Payload:
                    await SendPayloadAsync(stream, o, host, port, log, ct);
                    break;
                case TransportMode.Tls:
                    stream = await StartTlsAsync(stream, o.Sni ?? host, log, ct);
                    break;
                case TransportMode.TlsPayload:
                    stream = await StartTlsAsync(stream, o.Sni ?? host, log, ct);
                    await SendPayloadAsync(stream, o, host, port, log, ct);
                    break;
                case TransportMode.PayloadTls:
                    await SendPayloadAsync(stream, o, host, port, log, ct);
                    stream = await StartTlsAsync(stream, o.Sni ?? host, log, ct);
                    break;
            }
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            tcp.Dispose();
            throw;
        }
    }

    // ---------------- payload ----------------

    /// <summary>
    /// Expands HTTP Injector style placeholders: [host] [port] [host_port] [protocol] [ua] [raw]
    /// [crlf] [lf] [cr] [cr][lf], and [split] (sends the parts separately).
    /// </summary>
    public static IReadOnlyList<string> RenderPayload(string payload, string host, int port, string userAgent)
    {
        var text = payload
            .Replace("[raw]", "CONNECT [host_port] [protocol]", StringComparison.OrdinalIgnoreCase)
            .Replace("[host_port]", $"{host}:{port}", StringComparison.OrdinalIgnoreCase)
            .Replace("[host]", host, StringComparison.OrdinalIgnoreCase)
            .Replace("[port]", port.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("[protocol]", "HTTP/1.1", StringComparison.OrdinalIgnoreCase)
            .Replace("[ua]", userAgent, StringComparison.OrdinalIgnoreCase)
            .Replace("[crlf]", "\r\n", StringComparison.OrdinalIgnoreCase)
            .Replace("[lf]", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("[cr]", "\r", StringComparison.OrdinalIgnoreCase);
        return text.Split("[split]", StringSplitOptions.None).Where(p => p.Length > 0).ToList();
    }

    private static async Task SendPayloadAsync(Stream s, TransportOptions o, string host, int port, Action<string>? log, CancellationToken ct)
    {
        var parts = RenderPayload(o.Payload, host, port, o.UserAgent);
        if (parts.Count == 0) return;
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) await Task.Delay(150, ct);
            await s.WriteAsync(Encoding.ASCII.GetBytes(parts[i]), ct);
        }
        await s.FlushAsync(ct);
        log?.Invoke($"transport: payload sent ({parts.Count} part{(parts.Count == 1 ? "" : "s")})");
        if (o.SkipResponse) return;

        var status = await ReadHttpResponseAsync(s, ct);
        log?.Invoke($"transport: server replied \"{status}\"");
        var code = status.Split(' ').ElementAtOrDefault(1);
        if (code is null || !(code.StartsWith('1') || code.StartsWith('2')))
            throw new ProxyException($"Payload was refused: \"{status}\" (expected a 1xx/2xx reply; set \"skip response\" if your server sends none)");
    }

    /// <summary>Reads one HTTP response header block; returns the status line.</summary>
    internal static async Task<string> ReadHttpResponseAsync(Stream s, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var header = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            int n;
            try { n = await s.ReadAsync(one, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProxyException("No reply to the payload within 15 s");
            }
            if (n == 0) throw new ProxyException("The server closed the connection after the payload");
            header.Add(one[0]);
            var c = header.Count;
            if (c >= 4 && header[c - 4] == '\r' && header[c - 3] == '\n' && header[c - 2] == '\r' && header[c - 1] == '\n') break;
            if (c > 32 * 1024) throw new ProxyException("Payload reply header too large");
        }
        return Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0];
    }

    // ---------------- TLS ----------------

    private static async Task<Stream> StartTlsAsync(Stream inner, string sni, Action<string>? log, CancellationToken ct)
    {
        // The SSH host key, not the TLS certificate, authenticates the server, so the TLS layer
        // accepts any certificate (stunnel-style SSH-SSL servers are usually self-signed).
        var tls = new SslStream(inner, leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = sni,
            ApplicationProtocols = null,
        }, ct);
        log?.Invoke($"transport: TLS {tls.SslProtocol} established (SNI {sni})");
        return tls;
    }

    // ---------------- TCP ----------------

    private static async Task<TcpClient> OpenTcpAsync(string host, int port, Resolver? resolve, CancellationToken ct)
    {
        IPAddress[] addrs = IPAddress.TryParse(host, out var ip) ? [ip]
            : resolve is not null ? await resolve(host, ct)
            : await System.Net.Dns.GetHostAddressesAsync(host, ct);
        var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        tcp.Client.DualMode = true;
        try
        {
            await tcp.ConnectAsync(addrs, port, ct);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public static (string Host, int Port) SplitHostPort(string s, int defaultPort)
    {
        s = s.Trim();
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            var h = s[1..end];
            return end + 2 < s.Length && s[end + 1] == ':' ? (h, int.Parse(s[(end + 2)..])) : (h, defaultPort);
        }
        var colon = s.LastIndexOf(':');
        return colon > 0 && s.IndexOf(':') == colon && int.TryParse(s[(colon + 1)..], out var p) ? (s[..colon], p) : (s, defaultPort);
    }
}
