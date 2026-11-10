using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Core.Tunnels;

public enum SessionState { NotStarted, Connecting, Connected, Failed, Lost }

/// <summary>Live state of an encrypted tunnel's session, for display.</summary>
public sealed record SessionStatus(SessionState State, string? Detail, DateTime Since)
{
    public static readonly SessionStatus None = new(SessionState.NotStarted, null, DateTime.MinValue);
}

/// <summary>
/// Runs the client side of encrypted tunnels: SSH inside Sluice, and Shadowsocks / VMess / VLESS /
/// Trojan / Hysteria2 / TUIC through a sing-box helper process. Each is exposed to the router as a
/// local SOCKS5 endpoint, so the connector, chains and checker treat it like a SOCKS tunnel.
/// Sessions start on first use and are reused.
/// </summary>
public sealed class TunnelBackends : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, IBackendSession> _sessions = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<int, SessionStatus> _status = new();

    /// <summary>Raised whenever a tunnel's session state changes (tunnel id, new status).</summary>
    public event Action<int, SessionStatus>? StatusChanged;

    /// <summary>Current session state; a session that died since it connected reports Lost.</summary>
    public SessionStatus GetStatus(int tunnelId)
    {
        if (!_status.TryGetValue(tunnelId, out var st)) return SessionStatus.None;
        if (st.State == SessionState.Connected && _sessions.TryGetValue(tunnelId, out var s) && !s.IsAlive)
            return st with { State = SessionState.Lost, Detail = "connection lost; reconnects on next use" };
        return st;
    }

    private void SetStatus(int id, SessionState state, string? detail = null)
    {
        var st = new SessionStatus(state, detail, DateTime.Now);
        _status[id] = st;
        StatusChanged?.Invoke(id, st);
    }

    public TunnelBackends(Action<string>? log = null) => Log = log ?? (_ => { });

    public Action<string> Log { get; set; }

    /// <summary>Resolver for tunnel server names (must bypass Sluice's fake DNS answers).</summary>
    public TunnelTransport.Resolver? Resolver { get; set; }

    /// <summary>Path of sing-box.exe (default: next to the running program).</summary>
    public string SingBoxPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");

    /// <summary>Raised when an SSH host key is trusted for the first time; the app saves it in the profile.</summary>
    public event Action<Tunnel, string>? HostKeyLearned;

    public static bool Handles(TunnelType t) => t == TunnelType.Ssh || SingBoxConfig.Handles(t);

    /// <summary>
    /// True for helper processes Sluice started (sing-box). Their own connections to tunnel servers must
    /// go direct, or they would be captured and sent back into the tunnel (an infinite loop).
    /// </summary>
    public bool IsHelperProcess(int pid) => pid != 0 && _sessions.Values.Any(s => s.ProcessId == pid);

    /// <summary>A SOCKS5 tunnel pointing at the local endpoint that carries <paramref name="t"/>.</summary>
    public async Task<Tunnel> AsLocalSocksAsync(Tunnel t, CancellationToken ct)
    {
        var ep = await GetLocalSocksAsync(t, ct);
        return new Tunnel { Id = t.Id, Type = TunnelType.Socks5, Address = ep.Address.ToString(), Port = ep.Port, Label = t.Label ?? t.ToString() };
    }

    public async Task<IPEndPoint> GetLocalSocksAsync(Tunnel t, CancellationToken ct)
    {
        if (!Handles(t.Type)) throw new ProxyException($"{t.Type.DisplayName()} tunnels are not supported by this version of Sluice yet");
        var fp = Fingerprint(t);
        if (_sessions.TryGetValue(t.Id, out var s) && s.Fingerprint == fp && s.IsAlive) return s.SocksEndPoint!;

        var gate = _locks.GetOrAdd(t.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(t.Id, out s))
            {
                if (s.Fingerprint == fp && s.IsAlive) return s.SocksEndPoint!;
                _sessions.TryRemove(t.Id, out _);
                if (s.Fingerprint == fp) Log($"{t.Type.DisplayName()} {t}: connection lost, reconnecting");
                await s.DisposeAsync();
            }
            IBackendSession session = t.Type == TunnelType.Ssh
                ? new SshSession(t, fp, Resolver, Log, (tun, key) => HostKeyLearned?.Invoke(tun, key))
                : new SingBoxSession(t, fp, SingBoxPath, Resolver, Log);
            Log($"{t.Type.DisplayName()} {t}: starting session");
            SetStatus(t.Id, SessionState.Connecting);
            try
            {
                await session.StartAsync(ct);
            }
            catch (Exception ex)
            {
                await session.DisposeAsync();
                if (ex is not OperationCanceledException) Log($"{t.Type.DisplayName()} {t}: could not connect: {ex.Message}");
                SetStatus(t.Id, ex is OperationCanceledException ? SessionState.NotStarted : SessionState.Failed, ex.Message);
                throw;
            }
            _sessions[t.Id] = session;
            SetStatus(t.Id, SessionState.Connected, $"local SOCKS {session.SocksEndPoint}");
            return session.SocksEndPoint!;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Close sessions for tunnels that were removed or edited.</summary>
    public async Task PruneAsync(Profile profile)
    {
        var current = profile.Tunnels.Where(t => Handles(t.Type)).ToDictionary(t => t.Id, Fingerprint);
        foreach (var (id, s) in _sessions)
        {
            if (current.TryGetValue(id, out var fp) && fp == s.Fingerprint) continue;
            if (_sessions.TryRemove(id, out var gone))
            {
                await gone.DisposeAsync();
                SetStatus(id, SessionState.NotStarted, "closed (tunnel edited or removed)");
            }
        }
    }

    /// <summary>Identity of a tunnel's connection settings (the learned host key does not count).</summary>
    internal static string Fingerprint(Tunnel t)
    {
        var sb = new StringBuilder()
            .Append(t.Type).Append('|').Append(t.Address).Append('|').Append(t.Port).Append('|')
            .Append(t.Username).Append('|').Append(t.Password).Append('|');
        foreach (var (k, v) in t.Settings.Where(kv => !kv.Key.Equals("hostkey", StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(k.ToLowerInvariant()).Append('=').Append(v).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values) await s.DisposeAsync();
        _sessions.Clear();
    }
}
