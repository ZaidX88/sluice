using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Core.Tunnels;

public enum SessionState
{
    /// <summary>Not running; starts on first use.</summary>
    NotStarted,
    Connecting,
    Connected,
    /// <summary>Lost or failed; Sluice will try again shortly.</summary>
    Reconnecting,
    /// <summary>Running, but a health check just failed; checked again in a few seconds, restarted if it fails again.</summary>
    NotResponding,
    /// <summary>Gave up after repeated failures; stays stopped until the user checks or re-enables it.</summary>
    Failed,
    /// <summary>Stopped because nothing used it for a while; starts again on the next connection.</summary>
    Idle,
    /// <summary>Turned off by the user.</summary>
    Disabled,
}

/// <summary>Live state of an encrypted tunnel's session, for display.</summary>
public sealed record SessionStatus(SessionState State, string? Detail, DateTime Since)
{
    public static readonly SessionStatus None = new(SessionState.NotStarted, null, DateTime.MinValue);
}

/// <summary>
/// Runs the client side of encrypted tunnels: SSH (sing-box's SSH client, or SSH.NET) and Shadowsocks / VMess /
/// VLESS / Trojan / Hysteria2 / TUIC through sing-box. Each is exposed to the router as a local SOCKS5 endpoint.
///
/// Lifecycle, run by a monitor loop:
///  - sessions start on first use (or when capture starts, for the rules' tunnels) and are reused;
///  - connections hold a lease; a session without leases and unused for <see cref="IdleTimeout"/> is stopped;
///  - a connected session is health-checked every <see cref="HealthInterval"/>; one that dies or fails its checks
///    is reconnected with growing delays while it is wanted, and after <see cref="MaxAttempts"/> failures in a row
///    Sluice gives up (state Failed) until the user checks or re-enables the tunnel.
/// </summary>
public sealed class TunnelBackends : IAsyncDisposable
{
    private sealed class TunnelHealth
    {
        public int Failures;
        public DateTime NextAttempt;
        public bool PendingReconnect;
        public bool GivenUp;
        public string? LastError;
        public int ProbeFailures;
        public DateTime LastProbe;
        public int Probing;
        public int Leases;
        public DateTime LastUrgentProbe;
        public long LastUseTicks = DateTime.UtcNow.Ticks;
    }


    private readonly ConcurrentDictionary<int, IBackendSession> _sessions = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<int, SessionStatus> _status = new();
    private readonly ConcurrentDictionary<int, TunnelHealth> _health = new();
    private readonly ConcurrentDictionary<int, Tunnel> _tunnels = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _monitor;

    public TunnelBackends(Action<string>? log = null)
    {
        Log = log ?? (_ => { });
        _network = NetworkSnapshot();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _monitor = Task.Run(MonitorLoop);
    }

    // ------------------------------------------------------------------ network changes

    private string _network;
    private long _networkChangedTicks;

    /// <summary>React to network changes (new Wi-Fi, IP change, resume from sleep).</summary>
    public bool RestartOnNetworkChange { get; set; } = true;

    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        Interlocked.Exchange(ref _networkChangedTicks, DateTime.UtcNow.Ticks);

    /// <summary>Addresses and gateways of the interfaces that are up: what decides whether old connections still work.</summary>
    internal static string NetworkSnapshot()
    {
        try
        {
            var parts = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n =>
                {
                    var ip = n.GetIPProperties();
                    return ip.UnicastAddresses.Select(a => a.Address.ToString())
                        .Concat(ip.GatewayAddresses.Select(g => "gw " + g.Address));
                })
                .Where(s => !s.StartsWith("fe80", StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s, StringComparer.Ordinal);
            return string.Join(",", parts);
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return "";
        }
    }

    /// <summary>
    /// After the network changed (debounced, and only if addresses or gateways really differ), connections made
    /// over the old network are dead or soon will be: restart every running session now instead of waiting for checks.
    /// </summary>
    internal async Task<bool> CheckNetworkChangeAsync(bool force = false)
    {
        var changedAt = Interlocked.Read(ref _networkChangedTicks);
        if (!force && (changedAt == 0 || DateTime.UtcNow.Ticks - changedAt < TimeSpan.FromSeconds(3).Ticks)) return false;
        Interlocked.CompareExchange(ref _networkChangedTicks, 0, changedAt);
        var now = NetworkSnapshot();
        if (!force && now == _network) return false;
        _network = now;
        if (!RestartOnNetworkChange && !force) return false;
        foreach (var (id, s) in _sessions)
        {
            if (!_tunnels.TryGetValue(id, out var t) || !_sessions.TryRemove(new KeyValuePair<int, IBackendSession>(id, s))) continue;
            await s.DisposeAsync();
            var h = HealthOf(id);
            if (Wanted(h, DateTime.UtcNow))
            {
                Log($"{t.Type.DisplayName()} {t}: network changed, reconnecting");
                h.Failures = 0;
                h.PendingReconnect = true;
                h.NextAttempt = DateTime.UtcNow;
                SetStatus(t, SessionState.Reconnecting, "network changed");
            }
            else SetStatus(t, SessionState.Idle, "network changed; starts again when needed");
        }
        return true;
    }

    // ------------------------------------------------------------------ problems seen by real connections

    /// <summary>
    /// A connection through <paramref name="tunnelId"/> failed in a way that points at the tunnel (handshake timed
    /// out, local endpoint gone). Check the tunnel now rather than at the next scheduled check; if that check fails
    /// too, restart it at once.
    /// </summary>
    public void ReportProblem(int tunnelId, string reason)
    {
        if (!_tunnels.TryGetValue(tunnelId, out var t) || !_sessions.TryGetValue(tunnelId, out var s)) return;
        var h = HealthOf(tunnelId);
        if (DateTime.UtcNow - h.LastUrgentProbe < TimeSpan.FromSeconds(5)) return;   // one check covers a burst
        if (Interlocked.CompareExchange(ref h.Probing, 1, 0) != 0) return;
        h.LastUrgentProbe = DateTime.UtcNow;
        h.LastProbe = DateTime.UtcNow;
        Log($"{t.Type.DisplayName()} {t}: {reason}; checking the tunnel now");
        _ = ProbeAsync(t, s, h, urgent: true);
    }

    public Action<string> Log { get; set; }

    /// <summary>Resolver for tunnel server names (must bypass Sluice's fake DNS answers).</summary>
    public TunnelTransport.Resolver? Resolver { get; set; }

    /// <summary>Path of sing-box.exe (default: next to the running program).</summary>
    public string SingBoxPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");

    /// <summary>Stop a session nothing has used for this long (Zero = never).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long one test connection may take before the tunnel counts as not responding.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>How often a connected session is checked by opening a test connection through it (Zero = never).</summary>
    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Only call a session connected once a test connection through it works. sing-box starts listening even
    /// when its server is unreachable (it connects lazily), so without this a dead server would look "connected".
    /// </summary>
    public bool VerifyOnStart { get; set; } = true;

    /// <summary>Waits before attempt 2, 3, … after failures (the last one repeats).</summary>
    public TimeSpan[] Backoff { get; set; } =
        [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2)];

    /// <summary>Failed connection attempts in a row before giving up.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Raised whenever a tunnel's session state changes (tunnel, new status).</summary>
    public event Action<Tunnel, SessionStatus>? StatusChanged;

    /// <summary>
    /// Raised when an SSH host key is trusted for the first time (tunnel, display fingerprint, full public key or
    /// null); the app saves them in the profile.
    /// </summary>
    public event Action<Tunnel, string, string?>? HostKeyLearned;

    public SessionStatus GetStatus(int tunnelId) => _status.TryGetValue(tunnelId, out var st) ? st : SessionStatus.None;

    private void SetStatus(Tunnel t, SessionState state, string? detail = null)
    {
        if (_status.TryGetValue(t.Id, out var old) && old.State == state && old.Detail == detail) return;
        var st = new SessionStatus(state, detail, DateTime.Now);
        _status[t.Id] = st;
        StatusChanged?.Invoke(t, st);
    }

    private TunnelHealth HealthOf(int id) => _health.GetOrAdd(id, _ => new TunnelHealth());

    /// <summary>Which SSH client carries a tunnel: setting "sshclient" = auto (default) | singbox | sshnet.</summary>
    internal static string SshClientOf(Tunnel t) =>
        t.Settings.TryGetValue("sshclient", out var c) && c is "singbox" or "sshnet" ? c : "auto";

    public static bool Handles(TunnelType t) => t == TunnelType.Ssh || SingBoxConfig.Handles(t);

    /// <summary>
    /// True for helper processes Sluice started (sing-box). Their own connections to tunnel servers must
    /// go direct, or they would be captured and sent back into the tunnel (an infinite loop).
    /// </summary>
    public bool IsHelperProcess(int pid) =>
        pid != 0 && (SingBoxSession.RunningHelpers.ContainsKey(pid) || _sessions.Values.Any(s => s.ProcessId == pid));

    // ------------------------------------------------------------------ use

    /// <summary>
    /// Marks a tunnel as in use for as long as the returned lease is held (one per application connection).
    /// A tunnel with leases is never stopped for being idle.
    /// </summary>
    public IDisposable Lease(int tunnelId)
    {
        var h = HealthOf(tunnelId);
        Interlocked.Increment(ref h.Leases);
        Touch(h);
        return new LeaseHandle(() =>
        {
            Interlocked.Decrement(ref h.Leases);
            Touch(h);
        });
    }

    private sealed class LeaseHandle(Action release) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) release(); }
    }

    private static void Touch(TunnelHealth h) => Interlocked.Exchange(ref h.LastUseTicks, DateTime.UtcNow.Ticks);

    /// <summary>Forget earlier failures (Check, re-enable, capture start): the next use tries again at once.</summary>
    public void ResetFailures(int tunnelId)
    {
        if (!_health.TryGetValue(tunnelId, out var h)) return;
        h.Failures = 0;
        h.GivenUp = false;
        h.PendingReconnect = false;
        h.NextAttempt = default;
        if (_tunnels.TryGetValue(tunnelId, out var t) && GetStatus(tunnelId).State is SessionState.Failed or SessionState.Reconnecting)
            SetStatus(t, SessionState.NotStarted, "will try again on next use");
    }

    /// <summary>A SOCKS5 tunnel pointing at the local endpoint that carries <paramref name="t"/>.</summary>
    public async Task<Tunnel> AsLocalSocksAsync(Tunnel t, CancellationToken ct)
    {
        var ep = await GetLocalSocksAsync(t, ct);
        return new Tunnel { Id = t.Id, Type = TunnelType.Socks5, Address = ep.Address.ToString(), Port = ep.Port, Label = t.Label ?? t.ToString() };
    }

    public async Task<IPEndPoint> GetLocalSocksAsync(Tunnel t, CancellationToken ct)
    {
        if (!Handles(t.Type)) throw new ProxyException($"{t.Type.DisplayName()} tunnels are not supported by this version of Sluice yet");
        if (!t.Enabled) throw new ProxyException($"Tunnel \"{t}\" is turned off");
        _tunnels[t.Id] = t;
        var h = HealthOf(t.Id);
        Touch(h);
        var fp = Fingerprint(t);
        if (_sessions.TryGetValue(t.Id, out var s) && s.Fingerprint == fp && s.IsAlive) return s.SocksEndPoint!;

        // Don't hammer a server that keeps failing: report the state instead of trying on every connection.
        if (h.GivenUp)
            throw new ProxyException($"Tunnel \"{t}\" stopped after {h.Failures} failed attempts ({h.LastError}). Press Check, or turn it off and on, to try again.");
        if (h.Failures > 0 && DateTime.UtcNow < h.NextAttempt)
            throw new ProxyException($"Tunnel \"{t}\" is reconnecting (next attempt in {(h.NextAttempt - DateTime.UtcNow).TotalSeconds:F0} s): {h.LastError}");

        var gate = _locks.GetOrAdd(t.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(t.Id, out s))
            {
                if (s.Fingerprint == fp && s.IsAlive) return s.SocksEndPoint!;
                _sessions.TryRemove(t.Id, out _);
                await s.DisposeAsync();
            }
            if (h.GivenUp) throw new ProxyException($"Tunnel \"{t}\" stopped after repeated failures ({h.LastError})");

            var attempt = h.Failures + 1;
            Log($"{t.Type.DisplayName()} {t}: starting session" + (attempt > 1 ? $" (attempt {attempt} of {MaxAttempts})" : ""));
            SetStatus(t, SessionState.Connecting, attempt > 1 ? $"attempt {attempt} of {MaxAttempts}" : null);
            IBackendSession session;
            try
            {
                session = await StartSessionAsync(t, fp, ct);
                if (VerifyOnStart && !await ProbeSessionAsync(t, session, ct))
                {
                    await session.DisposeAsync();
                    throw new ProxyException("the tunnel started but no test connection through it worked");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                SetStatus(t, SessionState.NotStarted);
                throw;
            }
            catch (Exception ex)
            {
                RecordFailure(t, h, ex.Message);
                throw;
            }
            _sessions[t.Id] = session;
            h.Failures = 0;
            h.PendingReconnect = false;
            h.ProbeFailures = 0;
            h.LastProbe = DateTime.UtcNow;
            SetStatus(t, SessionState.Connected, $"local SOCKS {session.SocksEndPoint}");
            return session.SocksEndPoint!;
        }
        finally
        {
            gate.Release();
        }
    }

    private void RecordFailure(Tunnel t, TunnelHealth h, string error)
    {
        h.Failures++;
        h.LastError = error;
        Log($"{t.Type.DisplayName()} {t}: could not connect: {error}");
        if (h.Failures >= MaxAttempts)
        {
            h.GivenUp = true;
            h.PendingReconnect = false;
            Log($"{t.Type.DisplayName()} {t}: giving up after {h.Failures} failed attempts");
            SetStatus(t, SessionState.Failed, $"gave up after {h.Failures} attempts: {error}");
            return;
        }
        var delay = Backoff[Math.Min(h.Failures - 1, Backoff.Length - 1)];
        h.NextAttempt = DateTime.UtcNow + delay;
        h.PendingReconnect = true;
        SetStatus(t, SessionState.Reconnecting, $"attempt {h.Failures} of {MaxAttempts} failed: {error}; next in {delay.TotalSeconds:F0} s");
    }

    // ------------------------------------------------------------------ monitor

    private async Task MonitorLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                try
                {
                    await CheckNetworkChangeAsync();
                    await MonitorOnceAsync();
                }
                catch (Exception ex) { Log($"Tunnel monitor: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One pass of the lifecycle rules (internal for tests).</summary>
    internal async Task MonitorOnceAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, t) in _tunnels)
        {
            var h = HealthOf(id);
            var wanted = Wanted(h, now);
            _sessions.TryGetValue(id, out var s);

            // A session that died: reconnect if something still wants it, otherwise just let it go.
            if (s is not null && !s.IsAlive)
            {
                if (_sessions.TryRemove(new KeyValuePair<int, IBackendSession>(id, s))) await s.DisposeAsync();
                s = null;
                if (wanted && t.Enabled)
                {
                    Log($"{t.Type.DisplayName()} {t}: connection lost, reconnecting");
                    h.PendingReconnect = true;
                    h.NextAttempt = now;
                    SetStatus(t, SessionState.Reconnecting, "connection lost");
                }
                else SetStatus(t, SessionState.Idle, "connection closed; starts again when needed");
            }

            // Idle: nothing open through it and nothing new for a while.
            if (s is not null && IdleTimeout > TimeSpan.Zero && Volatile.Read(ref h.Leases) == 0 &&
                now - new DateTime(Interlocked.Read(ref h.LastUseTicks), DateTimeKind.Utc) > IdleTimeout)
            {
                if (_sessions.TryRemove(new KeyValuePair<int, IBackendSession>(id, s))) await s.DisposeAsync();
                Log($"{t.Type.DisplayName()} {t}: stopped after {IdleTimeout.TotalMinutes:F0} min without connections");
                SetStatus(t, SessionState.Idle, $"stopped after {IdleTimeout.TotalMinutes:F0} min unused; starts again when needed");
                continue;
            }

            // Retry a failed or lost session while it is wanted.
            if (s is null && h.PendingReconnect && !h.GivenUp && t.Enabled && now >= h.NextAttempt)
            {
                if (!wanted)
                {
                    h.PendingReconnect = false;
                    h.Failures = 0;
                    SetStatus(t, SessionState.Idle, "not in use; starts again when needed");
                }
                else _ = TryStartAsync(t);
                continue;
            }

            // Health check of a connected session.
            if (s is not null && HealthInterval > TimeSpan.Zero && now - h.LastProbe >= HealthInterval &&
                Interlocked.CompareExchange(ref h.Probing, 1, 0) == 0)
            {
                h.LastProbe = now;
                _ = ProbeAsync(t, s, h);
            }
        }
    }

    private bool Wanted(TunnelHealth h, DateTime now) =>
        Volatile.Read(ref h.Leases) > 0 ||
        IdleTimeout <= TimeSpan.Zero ||
        now - new DateTime(Interlocked.Read(ref h.LastUseTicks), DateTimeKind.Utc) <= IdleTimeout;

    private async Task TryStartAsync(Tunnel t)
    {
        try { await GetLocalSocksAsync(t, _cts.Token); }
        catch { /* recorded and logged */ }
    }

    private async Task ProbeAsync(Tunnel t, IBackendSession s, TunnelHealth h, bool urgent = false)
    {
        try
        {
            if (await ProbeSessionAsync(t, s, _cts.Token, ProbeTimeout))
            {
                h.ProbeFailures = 0;
                if (GetStatus(t.Id).State == SessionState.NotResponding && _sessions.TryGetValue(t.Id, out var cur) && cur == s)
                    SetStatus(t, SessionState.Connected, $"responding again; local SOCKS {s.SocksEndPoint}");
                return;
            }
            h.ProbeFailures++;
            Log($"{t.Type.DisplayName()} {t}: health check failed ({h.ProbeFailures} in a row)");
            // A failure seen by a real connection plus a failed check is enough. A scheduled check gets a second
            // chance a few seconds later: one lost packet should not tear down a working tunnel.
            if (!urgent && h.ProbeFailures < 2)
            {
                SetStatus(t, SessionState.NotResponding, "health check failed; checking again");
                h.LastProbe = DateTime.UtcNow - HealthInterval + TimeSpan.FromSeconds(5);
                return;
            }
            if (_sessions.TryRemove(new KeyValuePair<int, IBackendSession>(t.Id, s)))
            {
                await s.DisposeAsync();
                h.ProbeFailures = 0;
                h.PendingReconnect = true;
                h.NextAttempt = DateTime.UtcNow;
                Log($"{t.Type.DisplayName()} {t}: not passing traffic, reconnecting");
                SetStatus(t, SessionState.Reconnecting, "not passing traffic");
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Exchange(ref h.Probing, 0);
        }
    }

    /// <summary>
    /// A test connection through the session. Several targets, so one blocked site is not taken for a dead tunnel.
    /// Targets that need no name lookup come first (some servers resolve names slowly): for SSH the server's own
    /// SSH port, then 1.1.1.1, then the server's own address and a well-known site. Each gets half the timeout,
    /// so a stuck tunnel is recognised within about 2 x timeout.
    /// </summary>
    internal static async Task<bool> ProbeSessionAsync(Tunnel t, IBackendSession s, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (s.SocksEndPoint is not { } ep) return false;
        var perTarget = (timeout ?? TimeSpan.FromSeconds(15)) / 2;
        var targets = new List<(string, int)>();
        if (t.Type == TunnelType.Ssh) targets.Add(("127.0.0.1", 22));
        targets.Add(("1.1.1.1", 443));
        targets.Add((t.Address, t.Port));     // the server reaching itself: works without the open internet
        targets.Add(("www.google.com", 443));
        foreach (var (host, port) in targets)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(perTarget);
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(ep, cts.Token);
                await ProxyHandshake.Socks5Async(c.GetStream(), host, port, null, null, cts.Token);
                return true;
            }
            catch (Exception ex) when (ex is ProxyException or IOException or SocketException ||
                                       (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // refused, unreachable from the server, or no answer in time: try the next target
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ start

    private async Task<IBackendSession> StartSessionAsync(Tunnel t, string fp, CancellationToken ct)
    {
        if (t.Type != TunnelType.Ssh) return await StartAsync(new SingBoxSession(t, fp, SingBoxPath, Resolver, Log), ct);

        var choice = SshClientOf(t);
        var useSingBox = choice == "singbox" || (choice == "auto" && File.Exists(SingBoxPath));
        if (useSingBox)
        {
            try
            {
                return await StartAsync(new SshSingBoxSession(t, fp, SingBoxPath, Resolver, Log,
                    (tun, fingerprint, key) => HostKeyLearned?.Invoke(tun, fingerprint, key)), ct);
            }
            catch (SshLoginException ex) when (choice == "auto")
            {
                // e.g. a server that only allows keyboard-interactive logins, which sing-box does not speak.
                Log($"SSH {t}: sing-box could not log in ({ex.Message}); using the built-in SSH client instead");
            }
        }
        else if (choice == "auto")
        {
            Log($"SSH {t}: sing-box.exe not found; using the built-in SSH client (one slow connection can slow the others)");
        }
        return await StartAsync(new SshSession(t, fp, Resolver, Log, (tun, fingerprint) => HostKeyLearned?.Invoke(tun, fingerprint, null)), ct);
    }

    private static async Task<IBackendSession> StartAsync(IBackendSession session, CancellationToken ct)
    {
        try
        {
            await session.StartAsync(ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    // ------------------------------------------------------------------ profile changes

    /// <summary>Close sessions of tunnels that were removed, edited or turned off; track the current tunnels.</summary>
    public async Task PruneAsync(Profile profile)
    {
        var current = profile.Tunnels.Where(t => Handles(t.Type)).ToDictionary(t => t.Id);
        foreach (var id in _tunnels.Keys.Where(id => !current.ContainsKey(id)).ToList())
        {
            _tunnels.TryRemove(id, out _);
            _health.TryRemove(id, out _);
            _status.TryRemove(id, out _);
        }
        foreach (var (id, t) in current)
        {
            var known = _tunnels.TryGetValue(id, out var old);
            _tunnels[id] = t;
            if (!t.Enabled)
            {
                if (GetStatus(id).State != SessionState.Disabled) SetStatus(t, SessionState.Disabled, "turned off");
            }
            else if (GetStatus(id).State == SessionState.Disabled || (known && Fingerprint(old!) != Fingerprint(t)))
            {
                ResetFailures(id);   // turned back on, or edited: start fresh
                SetStatus(t, SessionState.NotStarted);
            }
        }
        foreach (var (id, s) in _sessions)
        {
            if (current.TryGetValue(id, out var t) && t.Enabled && Fingerprint(t) == s.Fingerprint) continue;
            if (_sessions.TryRemove(new KeyValuePair<int, IBackendSession>(id, s))) await s.DisposeAsync();
        }
    }

    /// <summary>Identity of a tunnel's connection settings (the learned host key does not count).</summary>
    internal static string Fingerprint(Tunnel t)
    {
        var sb = new StringBuilder()
            .Append(t.Type).Append('|').Append(t.Address).Append('|').Append(t.Port).Append('|')
            .Append(t.Username).Append('|').Append(t.Password).Append('|');
        foreach (var (k, v) in t.Settings.Where(kv => !kv.Key.Equals("hostkey", StringComparison.OrdinalIgnoreCase) &&
                                                      !kv.Key.Equals("hostpubkey", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(k.ToLowerInvariant()).Append('=').Append(v).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public async ValueTask DisposeAsync()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _cts.Cancel();
        try { await _monitor; } catch { /* stopping */ }
        foreach (var s in _sessions.Values) await s.DisposeAsync();
        _sessions.Clear();
    }
}
