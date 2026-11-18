using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Interception;
using Sluice.Core.Platform;
using Sluice.Core.Profiles;
using Sluice.Platform.Linux.Native;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// Linux traffic capture: nftables queues the first packet of each new outbound flow to Sluice (NFQUEUE), which
/// looks up the owning process and asks the rules, as the Windows capture does on each SYN. Connections to be
/// handled are marked and redirected by nftables to the transparent relay; the rest are never touched again.
/// DNS (UDP/53) is redirected to <see cref="LinuxDnsServer"/>, which hands out fake addresses for hostnames
/// that go through tunnels. See <see cref="NftRules"/> for the ruleset.
///
/// Sluice's own sockets are recognised by group: on start the process switches its effective group to a
/// private GID (inherited by helpers it starts), and nftables skips sockets of that group.
/// </summary>
public sealed class LinuxCapture : ITrafficCapture
{
    private const ushort QueueNumber = 0x5C1;     // 1473
    private const uint RedirectMark = 0x5C1CE;
    private static readonly TimeSpan UdpVerdictTime = TimeSpan.FromSeconds(30);

    private readonly RoutingEngine _engine;
    private readonly IProcessLookup _lookup;
    private readonly FakeIpPool _fakeIps = new();
    private readonly ConcurrentDictionary<IPEndPoint, (OriginalDestination Dest, DateTime At)> _flows = new();
    private readonly ConcurrentDictionary<(IPEndPoint, IPEndPoint), (bool Drop, DateTime At)> _udpVerdicts = new();
    private readonly CancellationTokenSource _cts = new();
    private DnsResolver? _resolver;
    private LinuxDnsServer? _dns;
    private LoopbackRelay? _relay;
    private NfQueue? _queue;
    private Thread? _thread;
    private Task? _sweeper;
    private (uint Real, uint Effective)? _previousGid;
    private bool _rulesApplied, _udpTunnelWarned;
    private readonly DnsPolicyCache _policies;
    private Task? _policyWatch;
    private PosixSignalRegistration[]? _exitSignals;
    private long _redirected, _passed, _udpDropped;

    public LinuxCapture(RoutingEngine engine, IProcessLookup lookup)
    {
        _engine = engine;
        _lookup = lookup;
        // Shared with the Windows capture (Sluice.Core.Dns): follows the setting values, not the profile object.
        _policies = new DnsPolicyCache(() => _engine.Profile, Environment.MachineName);
        _policies.Changed += OnPolicyChanged;
    }

    public UdpMode UdpMode { get; private set; }
    public FakeIpPool FakeIps => _fakeIps;

    /// <summary>A firewall does not stand between nftables' redirect and a loopback relay, so this is never raised.</summary>
    public event Action? RelayUnreachable { add { } remove { } }

    public bool NeedsRestartFor(Profile profile) => (profile.Options.Udp == UdpMode.Bypass) != (UdpMode == UdpMode.Bypass);

    public void Start()
    {
        try
        {
            StartCore();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>Where the name placeholders are kept between runs (null: not kept).</summary>
    public string? FakeIpStatePath { get; init; } = FakeIpPool.DefaultStatePath;

    private void RestoreFakeIps()
    {
        if (FakeIpStatePath is not { } path) return;
        var n = _fakeIps.Load(path);
        if (n > 0) _engine.Tracker.Write(LogLevel.Normal, $"Restored {n:N0} name placeholders from the last run, so apps' cached answers still work");
    }

    private void SaveFakeIps()
    {
        if (FakeIpStatePath is not { } path || !_fakeIps.IsDirty) return;
        try { _fakeIps.Save(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _engine.Tracker.Write(LogLevel.Error, $"Could not save the name placeholders to {path}: {ex.Message}");
        }
    }

    private void StartCore()
    {
        RestoreFakeIps();
        if (!LinuxPlatform.CheckCaptureRights()) throw new InvalidOperationException(LinuxPlatform.RightsHint);
        if (NftRules.FindNft() is null) throw new InvalidOperationException("nftables is not installed (no nft command). Install the \"nftables\" package.");

        // From here on, sockets Sluice (and helpers it starts) creates carry this group and are left alone by the rules.
        var gid = SluiceGroup.Pick();
        // Real and effective both: if they differed, the kernel would treat every program Sluice starts as setgid
        // and strip the capability nft needs under setcap. The saved group keeps the original for switching back.
        var before = (Libc.getgid(), Libc.getegid());
        if (Libc.setresgid(gid, gid, Libc.Unchanged) != 0)
            throw new InvalidOperationException($"Could not switch to Sluice's network group: {Libc.Error(Marshal.GetLastPInvokeError())}");
        _previousGid = before;

        // Sluice's own lookups go straight to the real upstream servers (the local stub would be redirected back to us).
        _resolver = new DnsResolver(() => LinuxDnsServer.UpstreamServers());
        _engine.HostResolver = _resolver.ResolveAsync;
        var redirector = new DnsRedirector(_fakeIps, CurrentPolicy, _resolver.LocalPort);
        redirector.Answered += (name, type) =>
            _engine.Tracker.Write(LogLevel.Debug, $"DNS {name} type {type} answered locally ({(type == 1 ? _fakeIps.GetOrAdd(name) : "no data")})");

        int? dnsPort = null;
        if (LinuxDnsServer.UpstreamServers().Count > 0)
        {
            _dns = new LinuxDnsServer(redirector, () => LinuxDnsServer.UpstreamServers(), m => _engine.Tracker.Write(LogLevel.Error, m));
            _dns.Start();
            dnsPort = _dns.Port;
        }
        else
        {
            _engine.Tracker.Write(LogLevel.Error,
                "No upstream DNS server found (/run/systemd/resolve/resolv.conf, /etc/resolv.conf): DNS is not captured, so host rules only match connections by address.");
        }

        // Loopback only (127.0.0.1 and ::1, one port): nftables redirects local connections there, nothing else needs it.
        _relay = LoopbackRelay.Bind(_engine, new FlowTable(this));
        _relay.Start();

        _queue = NfQueue.Open(QueueNumber);
        UdpMode = _engine.Profile.Options.Udp;
        NftRules.Apply(new NftRules.Settings(QueueNumber, RedirectMark, gid, _relay.Port, dnsPort, QueueUdp: UdpMode != UdpMode.Bypass));
        _rulesApplied = true;
        _exitSignals = [.. new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP }
            .Select(s => PosixSignalRegistration.Create(s, OnExitSignal))];

        _thread = new Thread(QueueLoop) { IsBackground = true, Name = "Sluice NFQUEUE" };
        _thread.Start();
        _sweeper = Task.Run(SweepLoop);
        FlushResolverCache();

        if (CurrentPolicy().Enabled)
            _engine.Tracker.Write(LogLevel.Normal, "Name resolution through tunnels is ON (fake IPs from 198.18.0.0/15)");
        _policyWatch = Task.Run(PolicyWatchLoop);
        _engine.Tracker.Write(LogLevel.Normal, $"Capture started (nftables + NFQUEUE {QueueNumber}, relay port {_relay.Port}" +
            $"{(dnsPort is null ? "" : $", DNS port {dnsPort}")}; UDP: {UdpMode switch
            {
                UdpMode.Tunnel => "tunneling is not available on Linux yet, so UDP of tunneled apps is blocked",
                UdpMode.BlockQuic => "QUIC of tunneled apps blocked",
                UdpMode.BlockAll => "blocked for tunneled apps",
                _ => "not captured",
            }})");
    }

    private DnsPolicy CurrentPolicy() => _policies.Current;

    /// <summary>
    /// A DNS setting changed while capturing. systemd-resolved still holds answers given under the old policy (real
    /// addresses, or placeholders), and answers from its cache never reach Sluice: drop them so the next lookup
    /// gets the new behaviour. Off the calling thread, which may be the DNS server's. Same log lines as Windows.
    /// </summary>
    private void OnPolicyChanged(DnsPolicy now, DnsPolicy before)
    {
        if (_cts.IsCancellationRequested) return;
        _engine.Tracker.Write(LogLevel.Normal, now.Enabled == before.Enabled
            ? "Name resolution settings changed; applied to new lookups"
            : now.Enabled
                ? "Name resolution through tunnels is now ON (fake IPs from 198.18.0.0/15)"
                : "Name resolution through tunnels is now OFF");
        _ = Task.Run(FlushResolverCache);
    }

    /// <summary>
    /// Picks up DNS setting changes within a second even when no query reaches Sluice (names already in
    /// systemd-resolved's cache are answered there). The engine has no change event, and comparing the settings is cheap.
    /// </summary>
    private async Task PolicyWatchLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token)) CurrentPolicy();
        }
        catch (OperationCanceledException) { }
    }

    // ---------------------------------------------------------------- per-flow decisions

    private void QueueLoop()
    {
        var q = _queue!;
        try
        {
            while (q.Receive(OnPacket)) { }
        }
        catch (Exception ex)
        {
            _engine.Tracker.Write(LogLevel.Error, $"Capture stopped working: {ex.Message}. Connections now go direct; turn capture off and on again.");
        }
    }

    private void OnPacket(NfQueue.Packet p)
    {
        uint verdict = NfQueue.Accept;
        uint? mark = null;
        try
        {
            if (PacketInfo.TryParse(p.Payload, out var f))
            {
                if (f.Protocol == PacketInfo.Tcp)
                {
                    if (DecideTcp(f.Source, f.Destination)) mark = RedirectMark;
                }
                else if (DecideUdpDrop(f.Source, f.Destination)) verdict = NfQueue.Drop;
            }
        }
        catch (Exception ex)
        {
            _engine.Tracker.Write(LogLevel.Error, $"Packet handling failed, letting it through: {ex.Message}");
        }
        _queue!.Verdict(p.Id, verdict, mark);
    }

    /// <summary>True to redirect this new TCP connection to the relay.</summary>
    private bool DecideTcp(IPEndPoint local, IPEndPoint remote)
    {
        var pid = _lookup.FindTcpOwner(local, remote);
        var fake = _fakeIps.TryGetHost(remote.Address, out var hostname);

        // A fake address only works through the relay (which connects by name), whoever uses it.
        if (!fake)
        {
            if (pid == Environment.ProcessId || _engine.Backends.IsHelperProcess(pid)) return Pass();
            // DNS over TCP goes to the real resolver, never into a tunnel (whose server cannot reach this LAN).
            if (remote.Port == 53) return Pass();
            if (FakeIpPool.IsFake(remote.Address))
            {
                // A placeholder from before Sluice restarted (an app's own DNS cache): the relay resets it at once
                // (RoutingEngine.HandleAsync), so the app looks the name up again.
                _flows[Norm(local)] = (new OriginalDestination(remote, pid), DateTime.UtcNow);
                Interlocked.Increment(ref _redirected);
                return true;
            }
            if (pid == 0 && !_engine.Profile.Options.ProcessServices) return Pass();
        }

        var path = pid == 0 ? null : _lookup.GetProcessPath(pid);
        var host = fake ? hostname : remote.Address.ToString();
        var match = _engine.Classify(new InboundRequest(host, remote.Port, pid, path));
        var redirect = fake || match.Action.Type != RuleActionType.Direct || _engine.Profile.Options.HandleDirectConnections;
        _engine.Tracker.Write(LogLevel.Debug, $"SYN {local} -> {host}:{remote.Port} pid {pid} ({Path.GetFileName(path) ?? "?"}) rule {match.Rule.Name} => {(redirect ? "redirect" : "pass")}");
        if (!redirect)
        {
            _engine.Tracker.Write(LogLevel.Verbose, $"{Path.GetFileName(path) ?? "?"} ({pid}) - {remote} direct (rule {match.Rule.Name}, not intercepted)");
            return Pass();
        }
        _flows[Norm(local)] = (new OriginalDestination(remote, pid, fake ? hostname : null), DateTime.UtcNow);
        Interlocked.Increment(ref _redirected);
        return true;
    }

    private bool Pass()
    {
        Interlocked.Increment(ref _passed);
        return false;
    }

    /// <summary>True to drop this new UDP flow. UDP is not tunneled on Linux yet: flows meant for a tunnel are blocked.</summary>
    private bool DecideUdpDrop(IPEndPoint local, IPEndPoint remote)
    {
        var key = (Norm(local), Norm(remote));
        if (_udpVerdicts.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < UdpVerdictTime) return Count(cached.Drop);

        var pid = _lookup.FindUdpOwner(local);
        var fake = _fakeIps.TryGetHost(remote.Address, out var hostname);
        bool drop;
        if (!fake && FakeIpPool.IsFake(remote.Address)) drop = true;   // a placeholder from before a restart: leads nowhere
        else if (!fake && (pid == Environment.ProcessId || _engine.Backends.IsHelperProcess(pid))) drop = false;
        else if (!fake && pid == 0 && !_engine.Profile.Options.ProcessServices) drop = false;
        else
        {
            var path = pid == 0 ? null : _lookup.GetProcessPath(pid);
            var route = _engine.ClassifyUdp(new InboundRequest(fake ? hostname : remote.Address.ToString(), remote.Port, pid, path));
            drop = fake || route.Verdict != UdpVerdict.Pass;   // a fake address is unreachable without a tunnel
            if (route.Verdict == UdpVerdict.Tunnel && !_udpTunnelWarned)
            {
                _udpTunnelWarned = true;
                _engine.Tracker.Write(LogLevel.Normal,
                    "UDP through tunnels is not available on Linux yet: UDP of tunneled apps is blocked, so browsers fall back from QUIC to TCP.");
            }
            _engine.Tracker.Write(drop ? LogLevel.Verbose : LogLevel.Debug,
                $"{Path.GetFileName(path) ?? "?"} ({pid}) - UDP {(fake ? hostname : remote.Address.ToString())}:{remote.Port} {(drop ? "blocked" : "direct")} (rule {route.Match.Rule.Name})");
        }
        _udpVerdicts[key] = (drop, DateTime.UtcNow);
        return Count(drop);
    }

    private bool Count(bool drop)
    {
        if (drop) Interlocked.Increment(ref _udpDropped);
        return drop;
    }

    private static IPEndPoint Norm(IPEndPoint ep) => IRedirectTable.Normalize(ep);

    /// <summary>Original destination of a redirected connection, by the app's endpoint (REDIRECT keeps the source).</summary>
    private sealed class FlowTable(LinuxCapture owner) : IRedirectTable
    {
        public bool TryGetOriginal(IPEndPoint clientEndPoint, out OriginalDestination original)
        {
            if (owner._flows.TryRemove(Norm(clientEndPoint), out var f))
            {
                original = f.Dest;
                return true;
            }
            original = null!;
            return false;
        }
    }

    private async Task SweepLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                SaveFakeIps();
                var now = DateTime.UtcNow;
                foreach (var (k, v) in _flows)
                    if (now - v.At > TimeSpan.FromMinutes(1)) _flows.TryRemove(k, out _);
                foreach (var (k, v) in _udpVerdicts)
                    if (now - v.At > UdpVerdictTime) _udpVerdicts.TryRemove(k, out _);
                _engine.Tracker.Write(LogLevel.Debug,
                    $"Capture stats: {Interlocked.Read(ref _redirected):N0} redirected, {Interlocked.Read(ref _passed):N0} passed, " +
                    $"{Interlocked.Read(ref _udpDropped):N0} UDP dropped, DNS {_dns?.Answered ?? 0:N0} answered / {_dns?.Forwarded ?? 0:N0} forwarded, {_fakeIps.Count} fake IPs");
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>systemd-resolved may have cached answers from before (or fake ones from Sluice): drop them.</summary>
    private static void FlushResolverCache()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("resolvectl", "flush-caches") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
            p?.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* no systemd-resolved */ }
    }

    /// <summary>
    /// The app is being stopped by a signal (kill, systemctl, logout, Ctrl+C) while capturing. The nftables table
    /// outlives the process: left behind, it keeps sending DNS to Sluice's dead DNS port, and name resolution stops
    /// for every app until someone runs <c>nft delete table inet sluice</c>. Remove it, and drop placeholder answers from
    /// systemd-resolved's cache. The signal then ends the process as usual (not cancelled).
    /// </summary>
    private void OnExitSignal(PosixSignalContext context)
    {
        if (!_rulesApplied) return;
        _engine.Tracker.Write(LogLevel.Normal, $"{context.Signal} received while capturing: removing the nftables rules before exit");
        NftRules.Remove();
        _rulesApplied = false;
        FlushResolverCache();
    }

    private void Cleanup()
    {
        _cts.Cancel();
        if (_exitSignals is not null)
            foreach (var r in _exitSignals) r.Dispose();
        _exitSignals = null;
        if (_rulesApplied) NftRules.Remove();     // first: no new packets are queued or redirected
        _rulesApplied = false;
        _queue?.Stop();
        _thread?.Join(2000);
        _queue?.Dispose();
        _queue = null;
        if (_previousGid is { } g) Libc.setresgid(g.Real, g.Effective, Libc.Unchanged);
        _previousGid = null;
    }

    public async ValueTask DisposeAsync()
    {
        Cleanup();
        if (_sweeper is not null) await _sweeper;
        if (_policyWatch is not null) await _policyWatch;
        if (_relay is not null) await _relay.DisposeAsync();
        if (_dns is not null) await _dns.DisposeAsync();
        SaveFakeIps();
        _engine.HostResolver = null;
        _resolver?.Dispose();
        if (_fakeIps.Count > 0) FlushResolverCache();
        _engine.Tracker.Write(LogLevel.Normal,
            $"Capture stopped ({Interlocked.Read(ref _redirected):N0} connections redirected, {_dns?.Answered ?? 0:N0} DNS queries answered)");
    }
}

/// <summary>Protocol and endpoints of a captured IPv4/IPv6 packet.</summary>
internal static class PacketInfo
{
    public const int Tcp = 6, Udp = 17;

    public readonly record struct Flow(int Protocol, IPEndPoint Source, IPEndPoint Destination);

    public static bool TryParse(ReadOnlySpan<byte> p, out Flow flow)
    {
        flow = default;
        if (p.Length < 20) return false;
        int ipLen, proto;
        IPAddress src, dst;
        switch (p[0] >> 4)
        {
            case 4:
                ipLen = (p[0] & 0xF) * 4;
                proto = p[9];
                src = new IPAddress(p.Slice(12, 4));
                dst = new IPAddress(p.Slice(16, 4));
                break;
            case 6:
                if (p.Length < 40) return false;
                if (!SkipIpv6ExtensionHeaders(p, out proto, out ipLen)) return false;
                src = new IPAddress(p.Slice(8, 16));
                dst = new IPAddress(p.Slice(24, 16));
                break;
            default:
                return false;
        }
        if (proto is not (Tcp or Udp) || p.Length < ipLen + 4) return false;
        var sp = BinaryPrimitives.ReadUInt16BigEndian(p[ipLen..]);
        var dp = BinaryPrimitives.ReadUInt16BigEndian(p[(ipLen + 2)..]);
        flow = new Flow(proto, new IPEndPoint(src, sp), new IPEndPoint(dst, dp));
        return true;
    }

    private const int HopByHop = 0, Routing = 43, Fragment = 44, Esp = 50, Ah = 51, NoNextHeader = 59, DestinationOptions = 60,
        Mobility = 135, Hip = 139, Shim6 = 140, Experimental1 = 253, Experimental2 = 254;

    /// <summary>
    /// Follows the IPv6 extension headers (RFC 8200 §4) to the upper-layer protocol: its number and where its header starts.
    /// nftables' <c>meta l4proto</c> looks past them too, so such packets do reach the queue (Hop-by-Hop or Destination
    /// Options set by an app, a Routing header, AH). False when the upper layer can't be read here: ESP (encrypted),
    /// "no next header", a fragment other than the first (it carries no TCP/UDP header), a Routing header with segments
    /// left (the header's destination isn't the real one), or a truncated chain.
    /// </summary>
    internal static bool SkipIpv6ExtensionHeaders(ReadOnlySpan<byte> p, out int protocol, out int offset)
    {
        protocol = p[6];
        offset = 40;
        for (var hops = 0; hops < 16; hops++)       // a real chain is short; this also stops a crafted endless one
        {
            switch (protocol)
            {
                case HopByHop or Routing or DestinationOptions or Mobility or Hip or Shim6 or Experimental1 or Experimental2:
                    if (p.Length < offset + 8) return false;
                    // A Routing header with segments left: the IPv6 destination is only the next hop, the real one is
                    // inside the header. Redirecting would open the wrong address, so let it pass as before.
                    if (protocol == Routing && p[offset + 3] != 0) return false;
                    protocol = p[offset];
                    offset += (p[offset + 1] + 1) * 8;            // length in 8-octet units, not counting the first 8
                    break;
                case Fragment:
                    if (p.Length < offset + 8) return false;
                    if ((BinaryPrimitives.ReadUInt16BigEndian(p[(offset + 2)..]) & 0xFFF8) != 0) return false;   // not the first fragment
                    protocol = p[offset];
                    offset += 8;
                    break;
                case Ah:
                    if (p.Length < offset + 8) return false;
                    protocol = p[offset];
                    offset += (p[offset + 1] + 2) * 4;            // length in 4-octet units, minus 2
                    break;
                case Esp or NoNextHeader:
                    return false;
                default:
                    return p.Length >= offset;                    // TCP, UDP or another upper layer: the caller checks which
            }
        }
        return false;
    }
}

/// <summary>The private group Sluice's sockets carry while capturing (nftables matches it with meta skgid).</summary>
internal static class SluiceGroup
{
    private const uint Preferred = 0x5C1CE;   // 377294: far above normal user and system groups

    /// <summary>A GID not used by any group in /etc/group.</summary>
    public static uint Pick(string groupFile = "/etc/group")
    {
        var used = new HashSet<uint>();
        try
        {
            foreach (var line in File.ReadLines(groupFile))
            {
                var f = line.Split(':');
                if (f.Length > 2 && uint.TryParse(f[2], out var g)) used.Add(g);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var gid = Preferred;
        while (used.Contains(gid)) gid++;
        return gid;
    }
}
