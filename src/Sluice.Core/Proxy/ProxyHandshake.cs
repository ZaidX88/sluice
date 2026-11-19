using System.Net;
using System.Net.Sockets;
using System.Text;
using Sluice.Core.Profiles;

namespace Sluice.Core.Proxy;

public sealed class ProxyException(string message) : IOException(message)
{
    /// <summary>The SOCKS5 reply code when a SOCKS5 server refused the request (e.g. 0x01 general failure).</summary>
    public byte? Socks5Reply { get; init; }
}

/// <summary>
/// Client side of the proxy protocols. Each handshake runs over a stream that is
/// already connected to the proxy and, on success, leaves it tunnelled to host:port.
/// </summary>
public static class ProxyHandshake
{
    public static Task ConnectAsync(Stream s, Tunnel proxy, string host, int port, CancellationToken ct = default) =>
        proxy.Type switch
        {
            TunnelType.Socks5 => Socks5Async(s, host, port, proxy.Username, proxy.Password, ct),
            TunnelType.Socks4 => Socks4Async(s, host, port, proxy.Username, proxy.Socks4aRemoteResolve, ct),
            TunnelType.Https or TunnelType.Http => HttpConnectAsync(s, host, port, proxy.Username, proxy.Password, ct),
            _ => throw new ProxyException($"{proxy.Type.DisplayName()} tunnels are not supported by this version of Sluice yet"),
        };

    // ---------------- SOCKS5 (RFC 1928 / RFC 1929) ----------------

    public static Task Socks5Async(Stream s, string host, int port, string? user, string? pass, CancellationToken ct) =>
        Socks5RequestAsync(s, 0x01, host, port, user, pass, ct);

    /// <summary>
    /// Greeting, optional user/password login, then one request (1 = CONNECT, 3 = UDP ASSOCIATE).
    /// Returns the address and port from the server's reply (for UDP ASSOCIATE: where to send datagrams).
    /// </summary>
    public static async Task<(string Host, int Port)> Socks5RequestAsync(Stream s, byte command, string host, int port,
        string? user, string? pass, CancellationToken ct)
    {
        var useAuth = !string.IsNullOrEmpty(user);
        byte[] greeting = useAuth ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00];
        await s.WriteAsync(greeting, ct);

        var sel = await ReadExactAsync(s, 2, ct);
        if (sel[0] != 0x05) throw new ProxyException("Not a SOCKS5 server");
        switch (sel[1])
        {
            case 0x00: break;
            case 0x02:
                if (!useAuth) throw new ProxyException("SOCKS5 proxy requires authentication");
                var u = Encoding.UTF8.GetBytes(user!);
                var p = Encoding.UTF8.GetBytes(pass ?? "");
                if (u.Length > 255 || p.Length > 255) throw new ProxyException("SOCKS5 credentials too long");
                var auth = new byte[3 + u.Length + p.Length];
                auth[0] = 0x01; auth[1] = (byte)u.Length;
                u.CopyTo(auth, 2);
                auth[2 + u.Length] = (byte)p.Length;
                p.CopyTo(auth, 3 + u.Length);
                await s.WriteAsync(auth, ct);
                var ar = await ReadExactAsync(s, 2, ct);
                if (ar[1] != 0x00) throw new ProxyException("SOCKS5 authentication failed");
                break;
            case 0xFF: throw new ProxyException("SOCKS5 proxy rejected all authentication methods");
            default: throw new ProxyException($"SOCKS5 proxy selected unsupported method 0x{sel[1]:X2}");
        }

        var req = new List<byte> { 0x05, command, 0x00 };
        AppendSocks5Address(req, host);
        req.Add((byte)(port >> 8));
        req.Add((byte)port);
        await s.WriteAsync(req.ToArray(), ct);

        var head = await ReadExactAsync(s, 4, ct);
        if (head[1] != 0x00)
            throw new ProxyException("SOCKS5: " + (command == 0x03 && head[1] == 0x07 ? "the server does not support UDP" : Socks5Error(head[1])))
            {
                Socks5Reply = head[1],
            };
        string boundHost;
        switch (head[3])
        {
            case 0x01: boundHost = new IPAddress(await ReadExactAsync(s, 4, ct)).ToString(); break;
            case 0x04: boundHost = new IPAddress(await ReadExactAsync(s, 16, ct)).ToString(); break;
            case 0x03:
                var n = (await ReadExactAsync(s, 1, ct))[0];
                boundHost = Encoding.ASCII.GetString(await ReadExactAsync(s, n, ct));
                break;
            default: throw new ProxyException("SOCKS5: bad address type in reply");
        }
        var bp = await ReadExactAsync(s, 2, ct);
        return (boundHost, (bp[0] << 8) | bp[1]);
    }

    internal static void AppendSocks5Address(List<byte> buf, string host)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            buf.Add(ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)0x04 : (byte)0x01);
            buf.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var h = Encoding.ASCII.GetBytes(host);
            if (h.Length > 255) throw new ProxyException("Hostname too long for SOCKS5");
            buf.Add(0x03);
            buf.Add((byte)h.Length);
            buf.AddRange(h);
        }
    }

    private static string Socks5Error(byte code) => code switch
    {
        0x01 => "general SOCKS server failure",
        0x02 => "connection not allowed by ruleset",
        0x03 => "network unreachable",
        0x04 => "host unreachable",
        0x05 => "connection refused",
        0x06 => "TTL expired",
        0x07 => "command not supported",
        0x08 => "address type not supported",
        _ => $"error 0x{code:X2}",
    };

    // ---------------- SOCKS4 / SOCKS4a ----------------

    public static async Task Socks4Async(Stream s, string host, int port, string? user, bool allow4a, CancellationToken ct)
    {
        var req = new List<byte> { 0x04, 0x01, (byte)(port >> 8), (byte)port };
        string? remoteHost = null;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily != AddressFamily.InterNetwork) throw new ProxyException("SOCKS4 supports IPv4 only");
            req.AddRange(ip.GetAddressBytes());
        }
        else
        {
            if (!allow4a) throw new ProxyException("SOCKS4 needs an IP address; enable SOCKS4a for hostnames");
            req.AddRange([0, 0, 0, 1]); // 0.0.0.x signals SOCKS4a
            remoteHost = host;
        }
        req.AddRange(Encoding.ASCII.GetBytes(user ?? ""));
        req.Add(0);
        if (remoteHost is not null)
        {
            req.AddRange(Encoding.ASCII.GetBytes(remoteHost));
            req.Add(0);
        }
        await s.WriteAsync(req.ToArray(), ct);

        var reply = await ReadExactAsync(s, 8, ct);
        if (reply[1] != 0x5A)
            throw new ProxyException(reply[1] switch
            {
                0x5B => "SOCKS4: request rejected or failed",
                0x5C => "SOCKS4: identd unreachable",
                0x5D => "SOCKS4: identd user mismatch",
                _ => $"SOCKS4: error 0x{reply[1]:X2}",
            });
    }

    // ---------------- HTTP(S) CONNECT ----------------

    public static async Task HttpConnectAsync(Stream s, string host, int port, string? user, string? pass, CancellationToken ct)
    {
        var authority = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";
        var sb = new StringBuilder()
            .Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(authority).Append("\r\n");
        if (!string.IsNullOrEmpty(user))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
            sb.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
        }
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);

        // Read headers byte by byte so we never consume tunnel data past "\r\n\r\n".
        var header = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0) throw new ProxyException("HTTP proxy closed the connection");
            header.Add(one[0]);
            var n = header.Count;
            if (n >= 4 && header[n - 4] == '\r' && header[n - 3] == '\n' && header[n - 2] == '\r' && header[n - 1] == '\n')
                break;
            if (n > 16 * 1024) throw new ProxyException("HTTP proxy response header too large");
        }

        var statusLine = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0];
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/") || !int.TryParse(parts[1], out var code))
            throw new ProxyException($"Bad HTTP proxy response: {statusLine}");
        if (code == 407) throw new ProxyException("HTTP proxy authentication required or failed (407)");
        if (code is < 200 or > 299) throw new ProxyException($"HTTP proxy refused CONNECT: {statusLine}");
    }

    // ---------------- util ----------------

    internal static async Task<byte[]> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        try
        {
            await s.ReadExactlyAsync(buf, ct);
        }
        catch (EndOfStreamException)
        {
            throw new ProxyException("Proxy closed the connection during handshake");
        }
        return buf;
    }
}
