using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Core.Tunnels;

/// <summary>
/// Runs the client side of encrypted tunnels (SSH now; sing-box protocols later). Each such tunnel is
/// exposed to the router as a local SOCKS5 endpoint, so the connector, chains and checker treat it
/// exactly like a SOCKS tunnel. Sessions start on first use and are reused.
/// </summary>
public sealed class TunnelBackends : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, SshSession> _ssh = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public TunnelBackends(Action<string>? log = null) => Log = log ?? (_ => { });

    public Action<string> Log { get; set; }

    /// <summary>Resolver for tunnel server names (must bypass Sluice's fake DNS answers).</summary>
    public TunnelTransport.Resolver? Resolver { get; set; }

    /// <summary>Raised when an SSH host key is trusted for the first time; the app saves it in the profile.</summary>
    public event Action<Tunnel, string>? HostKeyLearned;

    public static bool Handles(TunnelType t) => t == TunnelType.Ssh;

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
        if (_ssh.TryGetValue(t.Id, out var s) && s.Fingerprint == fp && s.IsAlive) return s.SocksEndPoint!;

        var gate = _locks.GetOrAdd(t.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_ssh.TryGetValue(t.Id, out s))
            {
                if (s.Fingerprint == fp && s.IsAlive) return s.SocksEndPoint!;
                _ssh.TryRemove(t.Id, out _);
                if (s.Fingerprint == fp) Log($"SSH {t}: connection lost, reconnecting");
                await s.DisposeAsync();
            }
            var session = new SshSession(t, fp, Resolver, Log, (tun, key) => HostKeyLearned?.Invoke(tun, key));
            try
            {
                await session.StartAsync(ct);
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
            _ssh[t.Id] = session;
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
        foreach (var (id, s) in _ssh)
        {
            if (current.TryGetValue(id, out var fp) && fp == s.Fingerprint) continue;
            if (_ssh.TryRemove(id, out var gone)) await gone.DisposeAsync();
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
        foreach (var s in _ssh.Values) await s.DisposeAsync();
        _ssh.Clear();
    }
}
