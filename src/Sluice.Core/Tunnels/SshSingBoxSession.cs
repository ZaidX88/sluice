using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sluice.Core.Profiles;
using ProxyException = Sluice.Core.Proxy.ProxyException;

namespace Sluice.Core.Tunnels;

/// <summary>
/// SSH tunnel carried by sing-box's SSH client (Go's x/crypto/ssh). Unlike SSH.NET it has per-channel flow
/// control, so one slow or idle connection (download managers keep several open) cannot stall the others.
///
///  - Direct mode: sing-box connects to the server address Sluice resolved.
///    Payload / TLS modes: sing-box connects to a local bridge that runs Sluice's transport to the server.
///  - The server's host key is pinned by its full public key ("hostpubkey"), fetched once with SSH.NET.
///  - sing-box logs in lazily, so the start opens one test channel to prove the login works.
/// </summary>
internal sealed class SshSingBoxSession : IBackendSession
{
    private readonly Tunnel _tunnel;
    private readonly string _exe;
    private readonly TransportOptions _transport;
    private readonly TunnelTransport.Resolver? _resolve;
    private readonly Action<string> _log;
    private readonly Action<Tunnel, string, string>? _hostKeyLearned;
    private readonly CancellationTokenSource _cts = new();
    private TransportBridge? _bridge;
    private SingBoxSession? _box;

    public IPEndPoint? SocksEndPoint => _box?.SocksEndPoint;
    public string Fingerprint { get; }
    public bool IsAlive => _box is { IsAlive: true };
    public int? ProcessId => _box?.ProcessId;

    public SshSingBoxSession(Tunnel tunnel, string fingerprint, string exe, TunnelTransport.Resolver? resolve, Action<string> log,
        Action<Tunnel, string, string>? hostKeyLearned)
    {
        _tunnel = tunnel;
        Fingerprint = fingerprint;
        _exe = exe;
        _transport = TransportOptions.From(tunnel.Settings);
        _resolve = resolve;
        _log = log;
        _hostKeyLearned = hostKeyLearned;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_tunnel.Username)) throw new ProxyException("SSH tunnel needs a username");
        var hasKey = _tunnel.Settings.TryGetValue("privatekey", out var key) && !string.IsNullOrWhiteSpace(key);
        if (!hasKey && string.IsNullOrEmpty(_tunnel.Password)) throw new ProxyException("SSH tunnel needs a password or a private key");

        // 1. Where sing-box connects: the real server (direct) or the local transport bridge.
        string server;
        int serverPort;
        if (_transport.Mode == TransportMode.Direct)
        {
            server = await ResolveAsync(_tunnel.Address, ct);
            serverPort = _tunnel.Port;
        }
        else
        {
            _bridge = new TransportBridge(() => TunnelTransport.ConnectAsync(_tunnel.Address, _tunnel.Port, _transport, _resolve,
                line => _log($"SSH {_tunnel}: {line}"), _cts.Token), _log);
            server = "127.0.0.1";
            serverPort = _bridge.Port;
        }

        // 2. Host key: the full public key pins the server for sing-box.
        var hostKey = await EnsureHostKeyAsync(server, serverPort, ct);

        // 3. sing-box with a SOCKS inbound and the SSH outbound.
        _box = new SingBoxSession(_tunnel, Fingerprint, _exe, _resolve, _log,
            (socksPort, _) => SingBoxConfig.BuildSsh(_tunnel, socksPort, server, serverPort, hostKey.Line, hostKey.Algorithm));
        await _box.StartAsync(ct);

        // 4. sing-box only logs in when the first connection is made: make one now so a wrong password is
        //    reported here (and SSH.NET can be tried instead) rather than on the user's first connection.
        await VerifyLoginAsync(ct);
        _log($"SSH {_tunnel}: connected as {_tunnel.Username} ({TransportOptions.ModeName(_transport.Mode)}, sing-box SSH client), channels via {SocksEndPoint}");
    }

    private async Task<string> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        try
        {
            var addrs = _resolve is not null ? await _resolve(host, ct) : await System.Net.Dns.GetHostAddressesAsync(host, ct);
            var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault()
                     ?? throw new ProxyException($"Cannot resolve {host}");
            return ip.ToString();
        }
        catch (SocketException ex)
        {
            throw new ProxyException($"Cannot resolve {host}: {ex.Message}");
        }
    }

    private async Task<SshHostKey> EnsureHostKeyAsync(string server, int port, CancellationToken ct)
    {
        _tunnel.Settings.TryGetValue("hostkey", out var pinnedFingerprint);
        if (_tunnel.Settings.TryGetValue("hostpubkey", out var pub) && SshHostKey.TryParseLine(pub, out var known))
        {
            if (string.IsNullOrEmpty(pinnedFingerprint) || known.Fingerprint == pinnedFingerprint) return known;
            // The two pins disagree (edited profile): fetch again and let the fingerprint decide.
        }

        var seen = await SshHostKey.FetchAsync(server, port, ct);
        if (!string.IsNullOrEmpty(pinnedFingerprint) && pinnedFingerprint != seen.Fingerprint)
            throw new ProxyException($"SSH host key changed! Expected {pinnedFingerprint} but the server presented {seen.Fingerprint}. " +
                                     "If the server was reinstalled, clear the saved host key in the tunnel settings.");
        if (string.IsNullOrEmpty(pinnedFingerprint)) _log($"SSH {_tunnel}: trusting host key {seen.Fingerprint} (first connection)");
        // The app saves both pins in the profile (on its own thread; the tunnel object is shared).
        _hostKeyLearned?.Invoke(_tunnel, seen.Fingerprint, seen.Stored);
        return seen;
    }

    /// <summary>Opens one channel through the session (to the server's own SSH port) and checks how sing-box fared.</summary>
    private async Task VerifyLoginAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        Exception? error = null;
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(SocksEndPoint!, cts.Token);
            await Proxy.ProxyHandshake.Socks5Async(c.GetStream(), "127.0.0.1", 22, null, null, cts.Token);
        }
        catch (Exception ex) when (ex is Proxy.ProxyException or IOException or SocketException or OperationCanceledException)
        {
            error = ex;
        }
        var output = string.Join(" | ", _box!.RecentOutput.TakeLast(3));
        if (output.Contains("unable to authenticate", StringComparison.OrdinalIgnoreCase))
            throw new SshLoginException($"SSH authentication failed for {_tunnel.Username}: {LastError(output)}");
        if (output.Contains("handshake failed", StringComparison.OrdinalIgnoreCase) || output.Contains("host key", StringComparison.OrdinalIgnoreCase))
            throw new SshLoginException($"SSH connection failed: {LastError(output)}");
        if (error is OperationCanceledException && !ct.IsCancellationRequested)
            throw new ProxyException("SSH login did not finish within 25 s");
        // Anything else (e.g. the test target refused) still means the login worked.
    }

    private static string LastError(string output)
    {
        var i = output.LastIndexOf("ssh: ", StringComparison.Ordinal);
        return i >= 0 ? output[i..] : output;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_box is not null) await _box.DisposeAsync();
        _bridge?.Dispose();
    }
}

/// <summary>sing-box could not log in: the caller may fall back to the SSH.NET client.</summary>
internal sealed class SshLoginException(string message) : IOException(message);

/// <summary>An SSH server host key: authorized_keys line, the algorithm to ask for, and a display fingerprint.</summary>
internal sealed record SshHostKey(string Line, string Algorithm, string Fingerprint)
{
    /// <summary>"ssh-ed25519 AAAA..." from the key blob; the key type is the first string inside the blob.</summary>
    public static SshHostKey FromBlob(string algorithm, byte[] blob)
    {
        var type = KeyType(blob) ?? algorithm;
        var fp = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(blob)).TrimEnd('=');
        return new SshHostKey($"{type} {Convert.ToBase64String(blob)}", algorithm, $"{algorithm} SHA256:{fp}");
    }

    /// <summary>Stored form: "&lt;algorithm&gt; &lt;key type&gt; &lt;base64&gt;" (the algorithm can differ from the key type for RSA).</summary>
    public string Stored => $"{Algorithm} {Line}";

    public static bool TryParseLine(string? stored, out SshHostKey key)
    {
        key = null!;
        var parts = stored?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 3 }) return false;
        byte[] blob;
        try { blob = Convert.FromBase64String(parts[2]); }
        catch (FormatException) { return false; }
        key = FromBlob(parts[0], blob);
        return true;
    }

    private static string? KeyType(byte[] blob)
    {
        if (blob.Length < 4) return null;
        var n = BinaryPrimitives.ReadInt32BigEndian(blob);
        return n > 0 && n < 64 && blob.Length >= 4 + n ? Encoding.ASCII.GetString(blob, 4, n) : null;
    }

    /// <summary>Reads the host key with an SSH.NET key exchange that is abandoned before authentication.</summary>
    public static async Task<SshHostKey> FetchAsync(string host, int port, CancellationToken ct)
    {
        SshHostKey? seen = null;
        var info = new ConnectionInfo(host, port, "sluice-hostkey-probe", new NoneAuthenticationMethod("sluice-hostkey-probe"))
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        using var client = new SshClient(info);
        client.HostKeyReceived += (_, e) =>
        {
            seen = FromBlob(e.HostKeyName, e.HostKey);
            e.CanTrust = false;   // got what we came for; stop before logging in
        };
        try
        {
            await client.ConnectAsync(ct);
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException or TimeoutException)
        {
            if (seen is null) throw new ProxyException($"SSH connection failed: {ex.Message}");
        }
        return seen ?? throw new ProxyException("SSH server sent no host key");
    }
}

/// <summary>Local TCP port whose every connection is piped to a fresh transport connection to the server.</summary>
internal sealed class TransportBridge : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<Task<Stream>> _connect;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public TransportBridge(Func<Task<Stream>> connect, Action<string> log)
    {
        _connect = connect;
        _log = log;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient local;
            try { local = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(async () =>
            {
                using (local)
                {
                    try
                    {
                        await using var remote = await _connect();
                        var a = local.GetStream();
                        await Task.WhenAny(a.CopyToAsync(remote, _cts.Token), remote.CopyToAsync(a, _cts.Token));
                    }
                    catch (Exception ex) when (!_cts.IsCancellationRequested)
                    {
                        _log($"SSH transport: {ex.Message}");
                    }
                    catch { /* closing */ }
                }
            });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
