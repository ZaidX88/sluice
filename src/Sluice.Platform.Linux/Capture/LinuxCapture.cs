using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
/// UDP flows Sluice takes are marked too and reach <see cref="UdpFlows"/> through a tproxy (<see cref="TransparentUdp"/>);
/// with UDP set to go direct (<see cref="UdpMode.Bypass"/>), only flows to name placeholders are queued and taken (#209).
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
    private const uint RejectMark = 0x5C1CF;      // refused at once: IPv6 through a route Core marked (RoutingEngine.Ipv6Fallback)
    private const uint UdpMark = 0x5C1D0;         // a UDP flow Sluice takes: rerouted through lo to TransparentUdp (tproxy)
    private static readonly TimeSpan UdpVerdictTime = TimeSpan.FromSeconds(30);

    private readonly RoutingEngine _engine;
    private readonly IProcessLookup _lookup;
    private readonly FakeIpPool _fakeIps = new();
    private readonly FlowRecords _flows = new();
    private Conntrack? _conntrack;
    private bool _conntrackWarned;
    /// <summary>What happens to a new UDP flow: sent on unchanged, dropped, or taken by Sluice (<see cref="UdpFlows"/>).</summary>
    internal enum UdpChoice { Pass, Drop, Take }

    private readonly ConcurrentDictionary<(IPEndPoint, IPEndPoint), (UdpChoice Choice, DateTime At)> _udpVerdicts = new();
    private TransparentUdp? _tproxy;
    private UdpFlows? _udpFlows;
    private bool _routesAdded;
    /// <summary>False when IPv6 UDP can't reach the transparent socket (no IPv6 socket or no IPv6 route, #216): IPv6
    /// flows are then dropped instead of taken, since a marked IPv6 datagram would otherwise go out past the tunnel.</summary>
    private bool _udpIpv6;
    private readonly CancellationTokenSource _cts = new();
    private DnsResolver? _resolver;
    private LinuxDnsServer? _dns;
    private LoopbackRelay? _relay;
    private NfQueue? _queue;
    private Thread? _thread;
    private VerdictWorkers? _workers;
    private readonly ConcurrentDictionary<(int Protocol, IPEndPoint Source, IPEndPoint Destination), byte> _deciding = new();
    private Task? _sweeper;
    private (uint Real, uint Effective, uint Saved)? _previousGid;
    private bool _rulesApplied;
    private readonly DnsPolicyCache _policies;
    private Task? _policyWatch;
    private PosixSignalRegistration[]? _exitSignals;
    private long _redirected, _passed, _udpDropped, _udpTaken, _refused;

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
        // Real, effective and saved all three: if real and effective differed, the kernel would treat every program
        // Sluice starts as setgid and strip the capability nft needs under setcap; if the saved group differed, GTK would
        // take Sluice for a setgid program and refuse to start, so the in-process file dialog couldn't open (#28).
        // Switching back on stop needs CAP_SETGID, which capture needs to switch here anyway.
        var before = CurrentGroupIds();
        if (Libc.setresgid(gid, gid, gid) != 0)
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

        // The relay finds each connection's original addresses in conntrack (issue #38); without it, by source only.
        _conntrack = Conntrack.TryOpen(out var conntrackProblem);
        if (_conntrack is null)
            _engine.Tracker.Write(LogLevel.Normal, $"Connection tracking lookup unavailable ({conntrackProblem}): connections are matched by source address only");

        // Loopback only (127.0.0.1 and ::1, one port): nftables redirects local connections there, nothing else needs it.
        _relay = LoopbackRelay.Bind(_engine, new FlowTable(this));
        _relay.Start();

        _queue = NfQueue.Open(QueueNumber);
        UdpMode = _engine.Profile.Options.Udp;
        // In Bypass too: UDP to a placeholder can't go out as it is, UdpFlows sends it direct by name (#209).
        StartUdpTproxy();
        NftRules.Apply(UdpRules(new NftRules.Settings(QueueNumber, RedirectMark, gid, _relay.Port, dnsPort, QueueUdp: false, RejectMark),
            UdpMode, _tproxy?.Port));
        _rulesApplied = true;
        _exitSignals = [.. new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP }
            .Select(s => PosixSignalRegistration.Create(s, OnExitSignal))];

        var queue = _queue;
        var workers = new VerdictWorkers(Decide, queue.Verdict,
            ex => _engine.Tracker.Write(LogLevel.Error, $"Packet handling failed, letting it through: {ex.Message}"));
        workers.Overflow += () => _engine.Tracker.Write(LogLevel.Normal,
            $"New connections arrive faster than Sluice decides them: some go direct uncaptured until it catches up ({workers.Overflowed:N0} so far)");
        _workers = workers;
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
                UdpMode.Tunnel => _tproxy is null
                    ? "tunneling unavailable (see above), so UDP of tunneled apps is blocked"
                    : $"tunneled through transparent UDP port {_tproxy.Port}",
                UdpMode.BlockQuic => "QUIC of tunneled apps blocked",
                UdpMode.BlockAll => "blocked for tunneled apps",
                _ => _tproxy is null
                    ? "not captured"
                    : $"not captured, except to name placeholders (sent direct by name through transparent UDP port {_tproxy.Port})",
            }})");
    }

    /// <summary>
    /// The UDP part of the rules for <paramref name="mode"/>, <paramref name="tproxyPort"/> being the transparent UDP
    /// socket's port (null: it couldn't be set up). Every new UDP flow is queued, except in <see cref="UdpMode.Bypass"/>:
    /// only flows to placeholders, and none without the socket (they'd have nowhere to go but out, as before #209).
    /// </summary>
    internal static NftRules.Settings UdpRules(NftRules.Settings s, UdpMode mode, int? tproxyPort) =>
        s with
        {
            QueueUdp = mode != UdpMode.Bypass || tproxyPort is not null,
            UdpToPlaceholdersOnly = mode == UdpMode.Bypass,
            UdpMark = tproxyPort is null ? 0 : LinuxCapture.UdpMark,
            UdpPort = tproxyPort ?? 0,
        };

    /// <summary>
    /// Sets up what carries the UDP flows Sluice takes: <see cref="UdpFlows"/> (Core) behind a transparent socket, and
    /// the policy routing that brings marked packets to it. If any part fails (an old kernel without tproxy), capture
    /// still starts and those flows are dropped, as before UDP could be tunneled on Linux.
    /// </summary>
    private void StartUdpTproxy()
    {
        try
        {
            _udpFlows = new UdpFlows(_engine, _fakeIps, new ReplySender(this), ep => _lookup.FindUdpOwner(ep));
            _tproxy = TransparentUdp.Open(OnTakenDatagram, m => _engine.Tracker.Write(LogLevel.Error, m));
            PolicyRoutes.Add(UdpMark, out var ipv6Problem);
            _routesAdded = true;
            var why = ipv6Problem ?? _tproxy.Ipv6Problem;
            _udpIpv6 = why is null;
            if (why is not null && UdpMode != UdpMode.Bypass)   // placeholders are IPv4
                _engine.Tracker.Write(LogLevel.Normal, $"UDP is taken for IPv4 only ({why}); IPv6 UDP of tunneled apps is blocked");
            _tproxy.Start();
        }
        catch (InvalidOperationException ex)
        {
            _engine.Tracker.Write(LogLevel.Error, UdpMode == UdpMode.Bypass
                ? $"UDP to name placeholders can't be sent direct by name: {ex.Message}. Apps' UDP to names looked up through tunnels goes nowhere."
                : $"UDP can't be taken through tunnels: {ex.Message}. UDP of tunneled apps is blocked, so browsers fall back from QUIC to TCP.");
            StopUdpTproxy();
        }
    }

    private void StopUdpTproxy()
    {
        if (_routesAdded) PolicyRoutes.Remove(UdpMark);
        _routesAdded = false;
        _udpIpv6 = false;
        _tproxy?.Dispose();
        _tproxy = null;
    }

    /// <summary>A datagram of a flow Sluice took, as the transparent socket received it.</summary>
    private void OnTakenDatagram(IPEndPoint app, IPEndPoint destination, ReadOnlyMemory<byte> payload)
    {
        if (_udpFlows is not { } flows) return;
        // Already out of the network: a flow UdpFlows would now pass (the profile changed since it was marked) can only
        // be dropped. Its conntrack entry keeps the mark until the flow is idle, then the next packet is decided again.
        if (flows.Handle(new AppDatagram(app, destination, payload, null)) == UdpFlows.Verdict.Pass)
            Interlocked.Increment(ref _udpDropped);
    }

    /// <summary>Delivers <see cref="UdpFlows"/>' replies through the transparent socket.</summary>
    private sealed class ReplySender(LinuxCapture owner) : IDatagramSender
    {
        public void SendToApp(IPEndPoint from, IPEndPoint app, ReadOnlyMemory<byte> payload, object? context) =>
            owner._tproxy?.SendToApp(from, app, payload.Span);
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

    /// <summary>Receives queued packets and hands them to the workers; it never decides anything itself.</summary>
    private void QueueLoop()
    {
        var q = _queue!;
        var workers = _workers!;
        try
        {
            while (q.Receive(workers.Post)) { }
        }
        catch (Exception ex)
        {
            _engine.Tracker.Write(LogLevel.Error, $"Capture stopped working: {ex.Message}. Connections now go direct; turn capture off and on again.");
        }
    }

    /// <summary>
    /// Decides one packet on a worker thread (<see cref="VerdictWorkers"/>). The same flow may be decided twice at
    /// once (a retransmitted SYN, two packets of a new UDP flow): harmless, since the decision is the same and the
    /// tables are concurrent, but only the first one logs.
    /// </summary>
    private (uint Verdict, uint? Mark) Decide(NfQueue.Packet p)
    {
        if (!PacketInfo.TryParse(p.Payload, out var f)) return (NfQueue.Accept, null);
        var key = (f.Protocol, Norm(f.Source), Norm(f.Destination));
        var quiet = !_deciding.TryAdd(key, 0);
        try
        {
            if (f.Protocol == PacketInfo.Tcp)
                return (NfQueue.Accept, DecideTcp(f.Source, f.Destination, quiet));
            return DecideUdp(f.Source, f.Destination, quiet) switch
            {
                UdpChoice.Take => (NfQueue.Accept, UdpMark),
                UdpChoice.Drop => (NfQueue.Drop, null),
                _ => (NfQueue.Accept, null),
            };
        }
        finally
        {
            if (!quiet) _deciding.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// The mark for this new TCP connection's SYN: <see cref="RedirectMark"/> to send it to the relay,
    /// <see cref="RejectMark"/> to refuse it, null to let it go direct.
    /// </summary>
    private uint? DecideTcp(IPEndPoint local, IPEndPoint remote, bool quiet = false)
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
                _flows.Add(local, remote, new OriginalDestination(remote, pid), DateTime.UtcNow);
                Interlocked.Increment(ref _redirected);
                return RedirectMark;
            }
            if (pid == 0 && !_engine.Profile.Options.ProcessServices) return Pass();
        }

        var path = pid == 0 ? null : _lookup.GetProcessPath(pid);
        var host = fake ? hostname : remote.Address.ToString();
        var match = _engine.Classify(new InboundRequest(host, remote.Port, pid, path));
        var redirect = fake || match.Action.Type != RuleActionType.Direct || _engine.Profile.Options.HandleDirectConnections;
        if (!quiet) _engine.Tracker.Write(LogLevel.Debug, $"SYN {local} -> {host}:{remote.Port} pid {pid} ({Path.GetFileName(path) ?? "?"}) rule {match.Rule.Name} => {(redirect ? "redirect" : "pass")}");
        if (!redirect)
        {
            if (!quiet) _engine.Tracker.Write(LogLevel.Verbose, $"{Path.GetFileName(path) ?? "?"} ({pid}) - {remote} direct (rule {match.Rule.Name}, not intercepted)");
            return Pass();
        }
        if (!fake && ShouldRefuseIpv6(_engine, remote.Address, match.Action, out var via))
        {
            _engine.Tracker.Write(LogLevel.Verbose, $"{Path.GetFileName(path) ?? "?"} ({pid}) - {remote} refused: IPv6 fails through {via}, so the app falls back to IPv4");
            Interlocked.Increment(ref _refused);
            return RejectMark;
        }
        _flows.Add(local, remote, new OriginalDestination(remote, pid, fake ? hostname : null), DateTime.UtcNow);
        Interlocked.Increment(ref _redirected);
        return RedirectMark;
    }

    /// <summary>
    /// True to refuse a new connection to the real address <paramref name="remote"/>: it is IPv6 and the rule sends it
    /// through a tunnel or chain that Core marked as unable to reach IPv6 (<see cref="RoutingEngine.Ipv6Fallback"/>).
    /// Refusing the SYN makes the app's connect() fail at once, so it uses the site's IPv4 address instead. Linux never
    /// marks a route; Core does. <paramref name="via"/> is the route as <see cref="RoutingEngine.Describe"/> gives it.
    /// </summary>
    internal static bool ShouldRefuseIpv6(RoutingEngine engine, IPAddress remote, RuleAction action, out string via)
    {
        via = "";
        if (remote.AddressFamily != AddressFamily.InterNetworkV6 || remote.IsIPv4MappedToIPv6) return false;
        if (action.Type is not (RuleActionType.Tunnel or RuleActionType.Chain)) return false;
        via = engine.Describe(action);
        return engine.Ipv6Fallback.ShouldRefuse(via);
    }

    private uint? Pass()
    {
        Interlocked.Increment(ref _passed);
        return null;
    }


    /// <summary>What to do with this new UDP flow (its first packets, until the decision is cached).</summary>
    private UdpChoice DecideUdp(IPEndPoint local, IPEndPoint remote, bool quiet = false)
    {
        var key = (Norm(local), Norm(remote));
        if (_udpVerdicts.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < UdpVerdictTime) return Count(cached.Choice);

        var pid = _lookup.FindUdpOwner(local);
        var fake = _fakeIps.TryGetHost(remote.Address, out var hostname);
        UdpChoice choice;
        if (!fake && FakeIpPool.IsFake(remote.Address)) choice = UdpChoice.Drop;   // a placeholder from before a restart: leads nowhere
        else if (IsNeverTaken(remote)) choice = UdpChoice.Pass;                   // as UdpFlows: multicast, broadcast, DHCP, mDNS…
        else if (!fake && (pid == Environment.ProcessId || _engine.Backends.IsHelperProcess(pid))) choice = UdpChoice.Pass;
        else if (!fake && pid == 0 && !_engine.Profile.Options.ProcessServices) choice = UdpChoice.Pass;
        else
        {
            var path = pid == 0 ? null : _lookup.GetProcessPath(pid);
            var route = _engine.ClassifyUdp(new InboundRequest(fake ? hostname : remote.Address.ToString(), remote.Port, pid, path));
            choice = Choose(route.Verdict, fake, canTake: CanTake(_tproxy is not null, _udpIpv6, remote.Address));
            if (!quiet) _engine.Tracker.Write(choice == UdpChoice.Drop ? LogLevel.Verbose : LogLevel.Debug,
                $"{Path.GetFileName(path) ?? "?"} ({pid}) - UDP {(fake ? hostname : remote.Address.ToString())}:{remote.Port} " +
                $"{choice switch { UdpChoice.Take => "taken", UdpChoice.Drop => "blocked", _ => "direct" }} (rule {route.Match.Rule.Name})");
        }
        _udpVerdicts[key] = (choice, DateTime.UtcNow);
        return Count(choice);
    }

    /// <summary>
    /// Whether a flow to <paramref name="destination"/> can be taken: the transparent socket is up and, for an IPv6
    /// destination, IPv6 reaches it too (<paramref name="ipv6Usable"/>). Placeholders are IPv4, so Direct-by-name flows
    /// never depend on IPv6.
    /// </summary>
    internal static bool CanTake(bool tproxyUp, bool ipv6Usable, IPAddress destination) =>
        tproxyUp && (ipv6Usable || destination.AddressFamily == AddressFamily.InterNetwork || destination.IsIPv4MappedToIPv6);

    /// <summary>
    /// The choice for a flow the rules gave <paramref name="verdict"/>. A tunnel flow is taken; so is a Direct flow to a
    /// placeholder (<paramref name="fake"/>), which can't go out as it is: <see cref="UdpFlows"/> sends it by name. Both
    /// are dropped when Sluice can't take UDP (<paramref name="canTake"/> false).
    /// </summary>
    internal static UdpChoice Choose(UdpVerdict verdict, bool fake, bool canTake) => verdict switch
    {
        UdpVerdict.Block => UdpChoice.Drop,
        UdpVerdict.Tunnel => canTake ? UdpChoice.Take : UdpChoice.Drop,
        _ => !fake ? UdpChoice.Pass : canTake ? UdpChoice.Take : UdpChoice.Drop,
    };

    /// <summary>The destinations <see cref="UdpFlows"/> always leaves alone (multicast, broadcast, link-local, DNS/DHCP/mDNS…).</summary>
    internal static bool IsNeverTaken(IPEndPoint dst)
    {
        if (dst.Port is 53 or 67 or 68 or 5353 or 5355 or 1900) return true;
        var a = dst.Address.IsIPv4MappedToIPv6 ? dst.Address.MapToIPv4() : dst.Address;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] >= 224 || (b[0] == 169 && b[1] == 254) || b[0] == 127 || b[0] == 0;
        }
        return a.IsIPv6Multicast || a.IsIPv6LinkLocal || IPAddress.IsLoopback(a);
    }

    private UdpChoice Count(UdpChoice choice)
    {
        if (choice == UdpChoice.Drop) Interlocked.Increment(ref _udpDropped);
        else if (choice == UdpChoice.Take) Interlocked.Increment(ref _udpTaken);
        return choice;
    }

    private static IPEndPoint Norm(IPEndPoint ep) => IRedirectTable.Normalize(ep);

    /// <summary>
    /// Original destination of a redirected connection. The relay only knows the client endpoint it sees, which
    /// conntrack may have renumbered; the original tuple from conntrack (found by the reply tuple relay → client)
    /// gives the app's own source and the real destination (<see cref="FlowRecords"/>, issue #38).
    /// </summary>
    private sealed class FlowTable(LinuxCapture owner) : IRedirectTable
    {
        public bool TryGetOriginal(IPEndPoint clientEndPoint, out OriginalDestination original)
        {
            var found = owner._flows.TryTake(clientEndPoint, owner._conntrack is null ? null : owner.LookUpOriginal, out original, out var how);
            if (how != "conntrack" && how != "by source")
                owner._engine.Tracker.Write(found ? LogLevel.Verbose : LogLevel.Debug, $"Redirect record for {clientEndPoint}: {how}");
            return found;
        }
    }

    /// <summary>The original tuple of the connection the relay accepted from <paramref name="client"/>, via conntrack.</summary>
    private bool LookUpOriginal(IPEndPoint client, out IPEndPoint source, out IPEndPoint destination)
    {
        source = destination = null!;
        if (_conntrack is not { } ct || _relay is not { } relay) return false;
        var relayAddress = client.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        if (ct.TryGetOriginal(new Conntrack.Tuple(new IPEndPoint(relayAddress, relay.Port), client), out var orig, out var errno))
        {
            source = orig.Source;
            destination = orig.Destination;
            return true;
        }
        if (errno != Conntrack.ENOENT && !_conntrackWarned)
        {
            _conntrackWarned = true;
            _engine.Tracker.Write(LogLevel.Normal, $"Connection tracking lookup failed ({Libc.Error(errno)}): matching connections by source address only");
        }
        return false;
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
                _flows.Sweep(now, TimeSpan.FromMinutes(1));
                foreach (var (k, v) in _udpVerdicts)
                    if (now - v.At > UdpVerdictTime) _udpVerdicts.TryRemove(k, out _);
                _udpFlows?.Sweep(TimeSpan.FromSeconds(60));
                _tproxy?.SweepReplies(TimeSpan.FromSeconds(120));
                _engine.Tracker.Write(LogLevel.Debug,
                    $"Capture stats: {Interlocked.Read(ref _redirected):N0} redirected, {Interlocked.Read(ref _passed):N0} passed, " +
                    $"{Interlocked.Read(ref _refused):N0} IPv6 refused, " +
                    $"{Interlocked.Read(ref _udpDropped):N0} UDP dropped, {Interlocked.Read(ref _udpTaken):N0} UDP flows taken ({_udpFlows?.FlowCount ?? 0} open, {_tproxy?.ReplySockets ?? 0} reply sockets), {_workers?.Overflowed ?? 0:N0} accepted unmarked (workers behind), DNS {_dns?.Answered ?? 0:N0} answered / {_dns?.Forwarded ?? 0:N0} forwarded, {_fakeIps.Count} fake IPs");
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>systemd-resolved may have cached answers from before (or fake ones from Sluice): drop them.</summary>
    /// <remarks>Also the platform's <see cref="LinuxPlatform.FlushDnsCache"/>, so it never throws.</remarks>
    internal static void FlushResolverCache()
    {
        try
        {
            if (!Native.Libc.ClearPassedOn(out _)) return;   // resolvectl gets no capabilities, or doesn't start
            using var p = Process.Start(new ProcessStartInfo("resolvectl", "flush-caches") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
            p?.WaitForExit(3000);
        }
        catch (SystemException) { /* no systemd-resolved, or it failed */ }
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
        if (_routesAdded) PolicyRoutes.Remove(UdpMark);
        _routesAdded = false;
        FlushResolverCache();
    }

    private void Cleanup()
    {
        _cts.Cancel();
        if (_exitSignals is not null)
            foreach (var r in _exitSignals) r.Dispose();
        _exitSignals = null;
        if (_rulesApplied) NftRules.Remove();     // first: no new packets are queued, redirected or marked
        _rulesApplied = false;
        StopUdpTproxy();                          // then the routes (only marked packets used them) and the socket
        _queue?.Stop();
        _thread?.Join(2000);                      // no more packets are handed to the workers
        _workers?.Stop(TimeSpan.FromSeconds(2));  // every queued packet gets its verdict before the socket closes
        _workers = null;
        _queue?.Dispose();
        _queue = null;
        _conntrack?.Dispose();
        _conntrack = null;
        if (_previousGid is { } g && Libc.setresgid(g.Real, g.Effective, g.Saved) != 0)
            _engine.Tracker.Write(LogLevel.Error, $"Could not switch back to the original group: {Libc.Error(Marshal.GetLastPInvokeError())}");
        _previousGid = null;
    }

    /// <summary>The process's real, effective and saved group IDs, from <c>/proc/self/status</c>.</summary>
    private static (uint Real, uint Effective, uint Saved) CurrentGroupIds()
    {
        try
        {
            if (ParseGroupIds(File.ReadAllText("/proc/self/status")) is { } ids) return ids;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var effective = Libc.getegid();
        return (Libc.getgid(), effective, effective);    // the saved group is the effective one unless set otherwise
    }

    /// <summary>Reads the "Gid:" line of a <c>/proc/&lt;pid&gt;/status</c> text: real, effective, saved, filesystem.</summary>
    internal static (uint Real, uint Effective, uint Saved)? ParseGroupIds(string status)
    {
        foreach (var line in status.Split('\n'))
        {
            if (!line.StartsWith("Gid:", StringComparison.Ordinal)) continue;
            var f = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return f.Length >= 3
                && uint.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var real)
                && uint.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var effective)
                && uint.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var saved)
                ? (real, effective, saved) : null;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        Cleanup();
        if (_sweeper is not null) await _sweeper;
        if (_policyWatch is not null) await _policyWatch;
        if (_relay is not null) await _relay.DisposeAsync();
        if (_dns is not null) await _dns.DisposeAsync();
        if (_udpFlows is not null) await _udpFlows.DisposeAsync();
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
