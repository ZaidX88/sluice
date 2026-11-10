using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sluice.Tests;

/// <summary>Loopback TCP server running a per-connection handler until disposed.</summary>
public abstract class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port { get; }
    public int Accepted;
    /// <summary>Last destination requested from this proxy ("host:port").</summary>
    public string? LastTarget { get; protected set; }

    protected LoopbackServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            Interlocked.Increment(ref Accepted);
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    try { await HandleAsync(c.GetStream(), _cts.Token); } catch { /* test server */ }
                }
            });
        }
    }

    protected abstract Task HandleAsync(NetworkStream s, CancellationToken ct);

    /// <summary>Connect to host:port and pump bytes both ways.</summary>
    protected static async Task RelayAsync(Stream client, string host, int port, CancellationToken ct)
    {
        using var up = new TcpClient();
        await up.ConnectAsync(host, port, ct);
        var u = up.GetStream();
        await Task.WhenAny(client.CopyToAsync(u, ct), u.CopyToAsync(client, ct));
    }

    protected static async Task<byte[]> Read(Stream s, int n, CancellationToken ct)
    {
        var b = new byte[n];
        await s.ReadExactlyAsync(b, ct);
        return b;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
        GC.SuppressFinalize(this);
    }
}

/// <summary>Echoes everything back, prefixed with nothing.</summary>
public sealed class EchoServer : LoopbackServer
{
    protected override Task HandleAsync(NetworkStream s, CancellationToken ct) => s.CopyToAsync(s, ct);
}

public sealed class Socks5TestServer(string? user = null, string? pass = null) : LoopbackServer
{
    protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
    {
        var head = await Read(s, 2, ct);
        var methods = await Read(s, head[1], ct);
        var wantAuth = user is not null;
        if (wantAuth && !methods.Contains((byte)0x02)) { await s.WriteAsync(new byte[] { 5, 0xFF }, ct); return; }
        await s.WriteAsync(new byte[] { 5, wantAuth ? (byte)2 : (byte)0 }, ct);
        if (wantAuth)
        {
            var ver = await Read(s, 2, ct);
            var u = Encoding.UTF8.GetString(await Read(s, ver[1], ct));
            var pl = (await Read(s, 1, ct))[0];
            var p = Encoding.UTF8.GetString(await Read(s, pl, ct));
            var ok = u == user && p == pass;
            await s.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 }, ct);
            if (!ok) return;
        }
        var req = await Read(s, 4, ct);
        string host = req[3] switch
        {
            1 => new IPAddress(await Read(s, 4, ct)).ToString(),
            4 => new IPAddress(await Read(s, 16, ct)).ToString(),
            3 => Encoding.ASCII.GetString(await Read(s, (await Read(s, 1, ct))[0], ct)),
            _ => throw new IOException("bad atyp"),
        };
        var pb = await Read(s, 2, ct);
        var port = (pb[0] << 8) | pb[1];
        LastTarget = $"{host}:{port}";
        if (req[1] != 1)   // CONNECT only
        {
            await s.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }
        if (host == "refuse.test")
        {
            await s.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }
        var target = host == "echo.test" ? "127.0.0.1" : host;
        await s.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
        await RelayAsync(s, target, port, ct);
    }
}

public sealed class Socks4TestServer : LoopbackServer
{
    protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
    {
        var head = await Read(s, 8, ct);
        var port = (head[2] << 8) | head[3];
        await ReadZ(s, ct); // user id
        string host;
        if (head[4] == 0 && head[5] == 0 && head[6] == 0 && head[7] != 0)
            host = await ReadZ(s, ct); // 4a
        else
            host = new IPAddress(head[4..8]).ToString();
        LastTarget = $"{host}:{port}";
        var target = host == "echo.test" ? "127.0.0.1" : host;
        await s.WriteAsync(new byte[] { 0, 0x5A, 0, 0, 0, 0, 0, 0 }, ct);
        await RelayAsync(s, target, port, ct);
    }

    private static async Task<string> ReadZ(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        while (true)
        {
            await s.ReadExactlyAsync(b, ct);
            if (b[0] == 0) return sb.ToString();
            sb.Append((char)b[0]);
        }
    }
}

public sealed class HttpConnectTestServer(string? user = null, string? pass = null) : LoopbackServer
{
    protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n"))
        {
            await s.ReadExactlyAsync(b, ct);
            sb.Append((char)b[0]);
        }
        var lines = sb.ToString().Split("\r\n");
        var authority = lines[0].Split(' ')[1];
        if (user is not null)
        {
            var expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
            if (!lines.Contains(expected))
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n"), ct);
                return;
            }
        }
        LastTarget = authority;
        var idx = authority.LastIndexOf(':');
        var host = authority[..idx].Trim('[', ']');
        var port = int.Parse(authority[(idx + 1)..]);
        await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), ct);
        await RelayAsync(s, host == "echo.test" ? "127.0.0.1" : host, port, ct);
    }
}
