using System.Net;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sluice.Core.Profiles;
using ProxyException = Sluice.Core.Proxy.ProxyException;

namespace Sluice.Core.Tunnels;

/// <summary>
/// One live SSH connection for an SSH tunnel. Applications' connections become SSH "direct-tcpip"
/// channels through a dynamic forward exposed as a local SOCKS5 port, so the router can use it like
/// any SOCKS tunnel. The raw link to the server goes through a local bridge so that HTTP Injector
/// style transports (payload, remote proxy, TLS with SNI) and Sluice's own DNS resolver apply.
/// </summary>
internal sealed class SshSession : IAsyncDisposable
{
    private readonly Tunnel _tunnel;
    private readonly TransportOptions _transport;
    private readonly TunnelTransport.Resolver? _resolve;
    private readonly Action<string> _log;
    private readonly Action<Tunnel, string>? _hostKeyLearned;
    private readonly TcpListener _bridge = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private SshClient? _client;
    private ForwardedPortDynamic? _forward;
    private Task? _bridgeLoop;

    public IPEndPoint? SocksEndPoint { get; private set; }
    public string Fingerprint { get; }

    public bool IsAlive => _client is { IsConnected: true } && _forward is { IsStarted: true };

    public SshSession(Tunnel tunnel, string fingerprint, TunnelTransport.Resolver? resolve, Action<string> log,
        Action<Tunnel, string>? hostKeyLearned)
    {
        _tunnel = tunnel;
        Fingerprint = fingerprint;
        _transport = TransportOptions.From(tunnel.Settings);
        _resolve = resolve;
        _log = log;
        _hostKeyLearned = hostKeyLearned;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_tunnel.Username))
            throw new ProxyException("SSH tunnel needs a username");

        // 1. Local bridge: SSH.NET connects here; we open the real transport to the server.
        _bridge.Start(1);
        var bridgePort = ((IPEndPoint)_bridge.LocalEndpoint).Port;
        var bridgeReady = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _bridgeLoop = Task.Run(() => BridgeOnceAsync(bridgeReady, _cts.Token));

        // 2. SSH client over the bridge.
        var info = new ConnectionInfo("127.0.0.1", bridgePort, _tunnel.Username, AuthMethods())
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        var expected = _tunnel.Settings.TryGetValue("hostkey", out var hk) && hk.Length > 0 ? hk : null;
        string? seen = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}";
            e.CanTrust = expected is null || string.Equals(expected, seen, StringComparison.Ordinal);
        };

        try
        {
            await client.ConnectAsync(ct);
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException or ProxyException or TimeoutException)
        {
            client.Dispose();
            var bridgeError = bridgeReady.Task.IsCompleted ? await bridgeReady.Task : null;
            if (bridgeError is not null) throw bridgeError is ProxyException ? bridgeError : new ProxyException($"SSH transport failed: {bridgeError.Message}");
            if (expected is not null && seen is not null && seen != expected)
                throw new ProxyException($"SSH host key changed! Expected {expected} but the server presented {seen}. " +
                                         "If the server was reinstalled, clear the saved host key in the tunnel settings.");
            throw ex is SshAuthenticationException
                ? new ProxyException("SSH authentication failed: wrong username, password or key")
                : new ProxyException($"SSH connection failed: {ex.Message}");
        }

        if (expected is null && seen is not null)
        {
            _log($"SSH {_tunnel}: trusting host key {seen} (first connection)");
            _hostKeyLearned?.Invoke(_tunnel, seen);
        }

        // 3. Dynamic forward = local SOCKS server whose connections become SSH channels.
        var port = FreeLoopbackPort();
        var forward = new ForwardedPortDynamic("127.0.0.1", (uint)port);
        forward.Exception += (_, e) =>
        {
            if (!_cts.IsCancellationRequested) _log($"SSH {_tunnel}: channel error: {e.Exception.Message}");
        };
        client.AddForwardedPort(forward);
        forward.Start();
        client.ErrorOccurred += (_, e) =>
        {
            if (!_cts.IsCancellationRequested) _log($"SSH {_tunnel}: connection error: {e.Exception.Message}");
        };

        _client = client;
        _forward = forward;
        SocksEndPoint = new IPEndPoint(IPAddress.Loopback, port);
        _log($"SSH {_tunnel}: connected as {_tunnel.Username} ({TransportOptions.ModeName(_transport.Mode)}), channels via {SocksEndPoint}");
    }

    private AuthenticationMethod[] AuthMethods()
    {
        var user = _tunnel.Username!;
        var methods = new List<AuthenticationMethod>();
        if (_tunnel.Settings.TryGetValue("privatekey", out var key) && !string.IsNullOrWhiteSpace(key))
        {
            _tunnel.Settings.TryGetValue("passphrase", out var passphrase);
            var file = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(key.Trim() + "\n")),
                string.IsNullOrEmpty(passphrase) ? null : passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(user, file));
        }
        if (!string.IsNullOrEmpty(_tunnel.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(user, _tunnel.Password));
            // Many servers only offer keyboard-interactive for passwords.
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, e) => { foreach (var p in e.Prompts) p.Response = _tunnel.Password; };
            methods.Add(kbd);
        }
        if (methods.Count == 0) throw new ProxyException("SSH tunnel needs a password or a private key");
        return methods.ToArray();
    }

    /// <summary>Accepts SSH.NET's single connection and pipes it to the real transport.</summary>
    private async Task BridgeOnceAsync(TaskCompletionSource<Exception?> ready, CancellationToken ct)
    {
        try
        {
            using var local = await _bridge.AcceptTcpClientAsync(ct);
            _bridge.Stop();
            Stream remote;
            try
            {
                remote = await TunnelTransport.ConnectAsync(_tunnel.Address, _tunnel.Port, _transport, _resolve,
                    line => _log($"SSH {_tunnel}: {line}"), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ready.TrySetResult(ex);
                throw;
            }
            ready.TrySetResult(null);
            await using (remote)
            {
                var a = local.GetStream();
                var up = a.CopyToAsync(remote, ct);
                var down = remote.CopyToAsync(a, ct);
                await Task.WhenAny(up, down);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || ready.Task.IsCompleted)
        {
            // closing, or the error was already reported
        }
        finally
        {
            ready.TrySetResult(null);
        }
    }

    private static int FreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _forward?.Stop(); } catch { /* already stopped */ }
        try { _client?.Disconnect(); } catch { /* already gone */ }
        _client?.Dispose();
        _bridge.Stop();
        if (_bridgeLoop is not null)
        {
            try { await _bridgeLoop; } catch { }
        }
    }
}
