using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Platform;
using Sluice.Core.Profiles;

namespace Sluice.Core.Interception;

/// <summary>
/// Transparent TCP interception with WinDivert. Requires Administrator rights and
/// WinDivert.dll + WinDivert64.sys next to the executable.
///
///  - Socket layer (sniff): learns which PID opened each outgoing connection.
///    Sluice's own PID is excluded in the filter, so its upstream sockets are never touched.
///  - Network layer: on each new flow's SYN, asks the rule engine; non-direct flows are
///    reflected to the <see cref="TransparentInbound"/> relay (see <see cref="PacketReflector"/>).
///    Only packets that may need rewriting are captured (<see cref="CaptureFilter"/>): a main handle for SYNs, the relay's
///    packets and the fake-address range, and a followers' handle for redirected flows to real addresses.
/// </summary>
public sealed class Interceptor : ITrafficCapture
{
    private const int MaxPacket = 0xFFFF + 40;

    private readonly RoutingEngine _engine;
    private readonly ConcurrentDictionary<(int LocalPort, IPEndPoint Remote), (int Pid, DateTime At)> _socketPids = new();
    private readonly CancellationTokenSource _cts = new();
    private IntPtr _net = IntPtr.Zero, _sock = IntPtr.Zero, _vmDns = IntPtr.Zero;
    private Thread? _netThread, _sockThread, _vmDnsThread;

    // Later packets of redirected flows to real (non-fake) addresses come on a second handle whose filter lists those
    // addresses (CaptureFilter.Followers). It is reopened when the list changes: the new handle opens first, then the
    // old one is shut down for receiving, drains what it already queued and closes itself.
    private readonly object _followGate = new();
    private readonly Dictionary<IPAddress, DateTime> _followed = new();   // address → when it was added
    private IntPtr _follow = IntPtr.Zero;
    private string? _followFilter;
    private short _followPriority = 1;
    private long _followReopens;
    private readonly List<Thread> _followThreads = [];
    private Task? _sweeper;
    private TransparentInbound? _relay;
    private PacketReflector? _reflector;
    private long _reflected, _passed, _recvErrors, _dnsAnswered, _redirectedFlows;
    private DateTime _startedAt;
    private bool _relayWarned;

    /// <summary>
    /// Raised once when flows are being redirected but none reaches the relay: something on this PC
    /// (almost always Windows Firewall) drops the redirected connections before Sluice can accept them.
    /// </summary>
    public event Action? RelayUnreachable;

    private readonly FakeIpPool _fakeIps = new();
    private DnsResolver? _resolver;
    private DnsRedirector? _dns;
    private readonly DnsPolicyCache _policies;
    private UdpNat? _udp;

    /// <summary>Log the headers of this many packets at Debug level (troubleshooting).</summary>
    public int DiagnosticPackets;

    // New flows' first SYNs, decided off the packet thread (process lookup and rules can take milliseconds,
    // and every packet on the machine would wait for them).
    private const int DecisionWorkers = 4;
    private readonly BlockingCollection<(byte[] Packet, WinDivertNative.Address Addr)> _deferred = new();
    private Thread[] _deciders = [];

    // Measurements (see docs/STATUS.md "How capture works"): how long packets wait in WinDivert's queue before
    // Sluice reads them, how long Sluice holds each one, and how long a new connection waits for its decision.
    private readonly LatencyStats _queueWait = new(), _handling = new() { SlowMs = 0.5 }, _decisions = new() { SlowMs = 5 };
    private long _ownerFromSniff, _ownerFromTable;

    // New IPv6 connections refused because their route can't reach IPv6 (RoutingEngine.Ipv6Fallback, #31): Decide
    // notes the flow, and every SYN of it is answered with a TCP reset instead of being sent on. Not only the first:
    // Windows retries a connect() that got a reset (two more SYNs about 0.5 s apart), and the reflector treats those
    // as the same, already decided flow, so they would otherwise go out directly. Kept as long as the reflector
    // treats a repeated SYN as the same flow (30 s), then swept.
    private readonly ConcurrentDictionary<(IPEndPoint Local, IPEndPoint Remote), long> _refuse = new();
    private long _refused;

    /// <summary>
    /// Local UDP ports of Windows' DNS proxy for WSL, Hyper-V and Docker (the "SharedAccess" service). Its upstream
    /// queries are asked for a virtual machine, whose connections are forwarded by Windows' NAT and never reach the relay,
    /// so they get real answers; its other UDP isn't an app's either (docs/windows/FIXES.md §1). Refreshed every 5 s.
    /// </summary>
    private volatile HashSet<int> _hostProxyPorts = [];
    private int _hostProxyPid;

    public LatencyStats QueueWait => _queueWait;
    public LatencyStats PacketHandling => _handling;
    public LatencyStats Decisions => _decisions;

    public Interceptor(RoutingEngine engine)
    {
        _engine = engine;
        _policies = new DnsPolicyCache(() => _engine.Profile);
        _policies.Changed += OnDnsPolicyChanged;
    }

    public long PacketsReflected => Interlocked.Read(ref _reflected);
    public long PacketsPassed => Interlocked.Read(ref _passed);
    public long DnsQueriesAnswered => Interlocked.Read(ref _dnsAnswered);
    public IPEndPoint? RelayEndPoint => _relay?.LocalEndPoint;
    public FakeIpPool FakeIps => _fakeIps;

    /// <summary>The UDP mode capture was started with (UDP is only captured when it is not Bypass).</summary>
    public UdpMode UdpMode { get; private set; }

    public long UdpDatagramsTunneled => _udp?.DatagramsTunneled ?? 0;

    /// <summary>True when <paramref name="profile"/> needs a different capture filter (restart interception to apply).</summary>
    public bool NeedsRestartFor(Profile profile) => (profile.Options.Udp == UdpMode.Bypass) != (UdpMode == UdpMode.Bypass);

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

    public void Start()
    {
        RestoreFakeIps();
        // Relay on the dual-stack wildcard: reflected packets arrive addressed to the machine's own IP.
        var relay = new TransparentInbound(_engine, new IPEndPoint(IPAddress.IPv6Any, 0), new LateBoundTable(() => _reflector, _fakeIps));
        _relay = relay;
        var port = (ushort)relay.LocalEndPoint.Port;
        _reflector = new PacketReflector(port, Decide) { DeferDecisions = true };
        relay.Start();

        // Sluice's own lookups must bypass the OS DNS cache (which will hold fake addresses).
        _resolver = new DnsResolver();
        _engine.HostResolver = _resolver.ResolveAsync;
        _dns = new DnsRedirector(_fakeIps, CurrentPolicy, _resolver.LocalPort);
        _dns.Answered += (name, type) =>
            _engine.Tracker.Write(LogLevel.Debug, $"DNS {name} type {type} answered locally ({(type == 1 ? _fakeIps.GetOrAdd(name) : "no data")})");

        var self = Environment.ProcessId;
        _sock = OpenOrThrow($"event == CONNECT and tcp and !loopback and processId != {self}",
            WinDivertNative.Layer.Socket, WinDivertNative.OpenFlags.Sniff | WinDivertNative.OpenFlags.RecvOnly);
        UdpMode = _engine.Profile.Options.Udp;
        var captureUdp = UdpMode != UdpMode.Bypass;
        // Only packets Sluice may rewrite (see CaptureFilter): direct traffic and the tunnel helpers' own connections
        // stay in the kernel.
        _net = OpenOrThrow(CaptureFilter.Main(port, captureUdp), WinDivertNative.Layer.Network, WinDivertNative.OpenFlags.None);
        EnlargeQueue(_net);
        if (captureUdp) _udp = new UdpNat(_engine, _fakeIps, InjectInbound);

        var policy = CurrentPolicy();
        if (policy.Enabled)
        {
            FlushOsDnsCache("capture started");
            _engine.Tracker.Write(LogLevel.Normal, "Name resolution through tunnels is ON (fake IPs from 198.18.0.0/15)");
        }

        // DNS questions from WSL and virtual machines to Windows' DNS proxy (docs/windows/FIXES.md §1).
        _vmDns = OpenOrThrow(VmDnsFilter, WinDivertNative.Layer.Network, WinDivertNative.OpenFlags.None);
        _vmDnsThread = new Thread(VmDnsLoop) { IsBackground = true, Name = "Sluice DNS for virtual machines" };
        _sockThread = new Thread(SocketLoop) { IsBackground = true, Name = "Sluice WinDivert socket" };
        var main = _net;
        _netThread = new Thread(() => NetworkLoop(main, follower: false)) { IsBackground = true, Name = "Sluice WinDivert network" };
        _deciders = Enumerable.Range(0, DecisionWorkers)
            .Select(i => new Thread(DecisionLoop) { IsBackground = true, Name = $"Sluice connection decisions {i}" })
            .ToArray();
        foreach (var t in _deciders) t.Start();
        _sockThread.Start();
        _netThread.Start();
        _vmDnsThread.Start();
        _startedAt = DateTime.UtcNow;
        RefreshHostDnsProxy();
        _sweeper = Task.Run(SweepLoop);

        _engine.Tracker.Write(LogLevel.Normal, $"Interception started (relay port {port}; UDP: {UdpMode switch
        {
            UdpMode.Tunnel => "through tunnels",
            UdpMode.BlockQuic => "QUIC of tunneled apps blocked",
            UdpMode.BlockAll => "blocked for tunneled apps",
            _ => "not captured",
        }})");
        WarnAboutConflicts();
    }

    /// <summary>
    /// Other transparent proxies redirect connections in the kernel before WinDivert sees them,
    /// so their traffic silently bypasses Sluice.
    /// </summary>
    private void WarnAboutConflicts()
    {
        string[] known = ["Proxifier", "ProxyCap", "SocksCap", "Netch", "ProxiFyre", "Clash", "v2rayN"];
        try
        {
            var running = System.Diagnostics.Process.GetProcesses()
                .Select(p => p.ProcessName)
                .Where(n => known.Any(k => n.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
                .Distinct()
                .ToList();
            if (running.Count > 0)
                _engine.Tracker.Write(LogLevel.Error,
                    $"Another traffic redirector is running ({string.Join(", ", running)}). Connections it captures will bypass Sluice; quit it for Sluice to work.");
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Default queue is 4096 packets / 4 MB: a burst beyond that while Sluice is busy is dropped silently, and TCP then
    /// retransmits and halves its window. Use WinDivert's maximum instead.
    /// </summary>
    private void EnlargeQueue(IntPtr h)
    {
        if (!WinDivertNative.SetParam(h, WinDivertNative.Param.QueueLength, WinDivertNative.QueueLengthMax) |
            !WinDivertNative.SetParam(h, WinDivertNative.Param.QueueSize, WinDivertNative.QueueSizeMax))
            _engine.Tracker.Write(LogLevel.Error, $"Could not enlarge the WinDivert queue ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
    }

    /// <summary>
    /// Makes sure the later packets of a flow redirected to <paramref name="remote"/> are captured. Called on a decision
    /// thread before the flow's SYN is sent on, so the app's next packet is already caught.
    /// </summary>
    private void Follow(IPAddress remote)
    {
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        lock (_followGate)
        {
            if (_followed.ContainsKey(remote) || _cts.IsCancellationRequested) return;
            _followed[remote] = DateTime.UtcNow;
            ReopenFollowers();
        }
    }

    /// <summary>Stops following addresses with no redirected flow left (and added over a minute ago).</summary>
    private void PruneFollowed()
    {
        if (_reflector is null) return;
        var live = _reflector.RedirectedRemotes();
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(1);
        lock (_followGate)
        {
            var gone = _followed.Where(kv => kv.Value < cutoff && !live.Contains(kv.Key)).Select(kv => kv.Key).ToList();
            if (gone.Count == 0 || _cts.IsCancellationRequested) return;
            foreach (var a in gone) _followed.Remove(a);
            ReopenFollowers();
        }
    }

    /// <summary>Opens a follower handle for the current address list and retires the previous one. Caller holds _followGate.</summary>
    private void ReopenFollowers()
    {
        var port = (ushort)_relay!.LocalEndPoint.Port;
        var filter = CaptureFilter.Followers(port, _followed.Keys);
        if (filter is not null && filter != CaptureFilter.FollowersBroad(port) &&
            WinDivertNative.CheckFilter(filter, WinDivertNative.Layer.Network) is { } why)
        {
            _engine.Tracker.Write(LogLevel.Verbose, $"Capture filter for {_followed.Count} addresses rejected ({why}); capturing all later packets instead");
            filter = CaptureFilter.FollowersBroad(port);
        }
        if (filter == _followFilter) return;

        var next = IntPtr.Zero;
        if (filter is not null)
        {
            // Alternate priorities: while both are open, a packet the higher one passes on unchanged reaches the other,
            // which passes it again (rewritten packets become inbound and no longer match).
            _followPriority = (short)(_followPriority == 1 ? 2 : 1);
            next = WinDivertNative.Open(filter, WinDivertNative.Layer.Network, _followPriority, WinDivertNative.OpenFlags.None);
            if (next == WinDivertNative.InvalidHandle)
            {
                _engine.Tracker.Write(LogLevel.Error, $"Could not update the capture filter ({new Win32Exception(Marshal.GetLastWin32Error()).Message}); some redirected connections may fail");
                return;
            }
            EnlargeQueue(next);
            var h = next;
            var t = new Thread(() => NetworkLoop(h, follower: true)) { IsBackground = true, Name = "Sluice WinDivert followers" };
            lock (_followThreads)
            {
                _followThreads.RemoveAll(x => !x.IsAlive);
                _followThreads.Add(t);
            }
            t.Start();
        }

        var old = _follow;
        _follow = next;
        _followFilter = filter;
        Interlocked.Increment(ref _followReopens);
        // The old handle's thread reads what is still queued, then gets ERROR_NO_DATA and closes the handle.
        if (old != IntPtr.Zero) WinDivertNative.ShutdownHandle(old, WinDivertNative.Shutdown.Recv);
        if (_engine.Tracker.Level >= LogLevel.Verbose)
            _engine.Tracker.Write(LogLevel.Verbose, filter == CaptureFilter.FollowersBroad(port)
                ? $"Capturing all later TCP packets ({_followed.Count} redirected addresses)"
                : $"Capturing later packets for {_followed.Count} redirected address(es)");
    }

    private static IntPtr OpenOrThrow(string filter, WinDivertNative.Layer layer, WinDivertNative.OpenFlags flags)
    {
        IntPtr h;
        try
        {
            h = WinDivertNative.Open(filter, layer, 0, flags);
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException("WinDivert.dll not found. Put WinDivert.dll and WinDivert64.sys next to Sluice.");
        }
        if (h == WinDivertNative.InvalidHandle)
        {
            var err = Marshal.GetLastWin32Error();
            var hint = err switch
            {
                2 => " (WinDivert64.sys not found next to WinDivert.dll)",
                5 => " (run as Administrator)",
                577 => " (driver signature rejected)",
                87 => " (invalid filter)",
                _ => "",
            };
            throw new InvalidOperationException($"WinDivertOpen failed for {layer}: {new Win32Exception(err).Message}{hint}");
        }
        return h;
    }

    private DnsPolicy CurrentPolicy() => _policies.Current;

    /// <summary>
    /// A name-resolution setting changed while capturing (noticed on the next DNS query or within 5 s by the sweep).
    /// The Windows DNS cache still holds answers made under the old settings: real addresses that should now be faked,
    /// or fake ones that should now be real. Flush it so apps ask again.
    /// </summary>
    private void OnDnsPolicyChanged(DnsPolicy now, DnsPolicy before)
    {
        if (_cts.IsCancellationRequested) return;
        _engine.Tracker.Write(LogLevel.Normal, now.Enabled == before.Enabled
            ? "Name resolution settings changed; applied to new lookups"
            : now.Enabled
                ? "Name resolution through tunnels is now ON (fake IPs from 198.18.0.0/15)"
                : "Name resolution through tunnels is now OFF");
        _ = Task.Run(() => FlushOsDnsCache("name resolution settings changed"));   // not on the packet thread: the DNS client service can take a while
    }

    /// <summary>Called on the SYN of every new outbound flow.</summary>
    private (bool Redirect, int Pid) Decide(IPEndPoint local, IPEndPoint remote)
    {
        var pid = OwnerFromSniff(local, remote);
        if (pid == 0)
        {
            Interlocked.Increment(ref _ownerFromTable);
            pid = ProcessLookup.FindTcpOwner(local, remote);
        }
        else Interlocked.Increment(ref _ownerFromSniff);

        if (pid == Environment.ProcessId) return (false, pid);

        // DNS over TCP (fallback for large answers, or a browser's own resolver) goes to the local network's
        // resolver: never into a tunnel, whose server cannot reach this LAN. UDP DNS is answered locally.
        if (remote.Port == 53 && !FakeIpPool.IsFake(remote.Address)) return (false, pid);

        // A fake address is unreachable by itself: always hand such flows to the engine,
        // which connects by hostname (directly or through the proxy, per the rules).
        var fake = _fakeIps.TryGetHost(remote.Address, out var hostname);
        if (!fake && FakeIpPool.IsFake(remote.Address))
        {
            // A placeholder from before Sluice restarted, still in the app's own DNS cache: unreachable by itself.
            // The relay resets it at once (RoutingEngine.HandleAsync), so the app looks the name up again.
            Interlocked.Increment(ref _redirectedFlows);
            return (true, pid);
        }
        if (!fake && pid == 0 && !_engine.Profile.Options.ProcessServices) return (false, 0);

        var path = pid == 0 ? null : ProcessLookup.GetProcessPath(pid);
        var host = fake ? hostname : remote.Address.ToString();
        var match = _engine.Classify(new InboundRequest(host, remote.Port, pid, path));
        var redirect = fake || match.Action.Type != RuleActionType.Direct || _engine.Profile.Options.HandleDirectConnections;
        if (redirect && !fake && ShouldRefuseIpv6(_engine, remote.Address, match.Action, out var via))
        {
            // Not redirected and not sent on: DecisionLoop answers the SYN with a reset, so the app's connect() fails
            // at once and Happy Eyeballs uses the site's IPv4 address.
            _refuse[(Norm(local), Norm(remote))] = DateTime.UtcNow.Ticks;
            if (_engine.Tracker.Level >= LogLevel.Verbose)
                _engine.Tracker.Write(LogLevel.Verbose, $"{Path.GetFileName(path) ?? "?"} ({pid}) - {remote} refused: IPv6 fails through {via}, so the app falls back to IPv4");
            return (false, pid);
        }
        if (redirect) Interlocked.Increment(ref _redirectedFlows);
        // Fake addresses are always captured; a real one must be added to the followers' filter before the SYN goes on.
        if (redirect && !fake) Follow(remote.Address);
        var level = _engine.Tracker.Level;
        if (level >= LogLevel.Debug)
            _engine.Tracker.Write(LogLevel.Debug, $"SYN {local} -> {host}:{remote.Port} pid {pid} ({Path.GetFileName(path) ?? "?"}) rule {match.Rule.Name} => {(redirect ? "redirect" : "pass")}");
        if (!redirect && level >= LogLevel.Verbose)
            _engine.Tracker.Write(LogLevel.Verbose, $"{Path.GetFileName(path) ?? "?"} ({pid}) - {remote} direct (rule {match.Rule.Name}, not intercepted)");
        return (redirect, pid);
    }

    /// <summary>
    /// True to refuse a new connection to the real address <paramref name="remote"/>: it is IPv6 and the rule sends it
    /// through a tunnel or chain that Core marked as unable to reach IPv6 (<see cref="RoutingEngine.Ipv6Fallback"/>).
    /// Windows never marks a route; Core does. <paramref name="via"/> is the route as <see cref="RoutingEngine.Describe"/>
    /// gives it.
    /// </summary>
    internal static bool ShouldRefuseIpv6(RoutingEngine engine, IPAddress remote, RuleAction action, out string via)
    {
        via = "";
        if (remote.AddressFamily != AddressFamily.InterNetworkV6 || remote.IsIPv4MappedToIPv6) return false;
        if (action.Type is not (RuleActionType.Tunnel or RuleActionType.Chain)) return false;
        via = engine.Describe(action);
        return engine.Ipv6Fallback.ShouldRefuse(via);
    }

    /// <summary>
    /// The reset that refuses an outbound SYN: an IPv4 or IPv6 + TCP packet from the SYN's destination back to its
    /// source, flags RST+ACK, acknowledging the SYN (seq + 1), so the app's stack fails the connect() at once.
    /// Checksums are left for WinDivert to fill in. Null when <paramref name="syn"/> isn't a plain TCP SYN.
    /// </summary>
    internal static byte[]? BuildSynReset(ReadOnlySpan<byte> syn)
    {
        if (syn.Length < 20) return null;
        int version = syn[0] >> 4, ipLen, tcpAt;
        if (version == 4)
        {
            tcpAt = (syn[0] & 0x0F) * 4;
            if (tcpAt < 20 || syn[9] != 6) return null;
            ipLen = 20;
        }
        else if (version == 6)
        {
            if (syn.Length < 40 || syn[6] != 6) return null;  // extension headers aren't followed (as in TcpPacket)
            tcpAt = ipLen = 40;
        }
        else return null;
        if (syn.Length < tcpAt + 20 || (syn[tcpAt + 13] & 0x16) != 0x02) return null;   // a SYN without ACK or RST

        var r = new byte[ipLen + 20];
        if (version == 4)
        {
            r[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), (ushort)r.Length);
            r[8] = 64;                                        // TTL
            r[9] = 6;
            syn.Slice(16, 4).CopyTo(r.AsSpan(12));             // source = the SYN's destination
            syn.Slice(12, 4).CopyTo(r.AsSpan(16));
        }
        else
        {
            r[0] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), 20);   // payload length
            r[6] = 6;
            r[7] = 64;                                        // hop limit
            syn.Slice(24, 16).CopyTo(r.AsSpan(8));
            syn.Slice(8, 16).CopyTo(r.AsSpan(24));
        }
        var t = r.AsSpan(ipLen);
        syn.Slice(tcpAt + 2, 2).CopyTo(t);                    // source port = the SYN's destination port
        syn.Slice(tcpAt, 2).CopyTo(t[2..]);
        BinaryPrimitives.WriteUInt32BigEndian(t[8..], BinaryPrimitives.ReadUInt32BigEndian(syn[(tcpAt + 4)..]) + 1);   // ack = seq + 1
        t[12] = 5 << 4;                                       // data offset: 20 bytes, no options
        t[13] = 0x14;                                         // RST + ACK
        return r;
    }

    /// <summary>
    /// The owning PID as the socket layer reported it. That event comes on another handle and thread, so it can
    /// arrive a moment after the SYN: wait up to 3 ms for it before falling back to scanning the TCP table.
    /// </summary>
    private int OwnerFromSniff(IPEndPoint local, IPEndPoint remote)
    {
        var key = (local.Port, Norm(remote));
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3 / 1000;
        var spin = new SpinWait();
        while (true)
        {
            if (_socketPids.TryRemove(key, out var hit)) return hit.Pid;
            if (Stopwatch.GetTimestamp() > deadline) return 0;
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    private void DecisionLoop()
    {
        try
        {
            foreach (var (packet, original) in _deferred.GetConsumingEnumerable(_cts.Token))
            {
                var addr = original;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    if (_reflector!.CompleteDeferred(packet) == PacketReflector.Verdict.Reflected)
                    {
                        addr.SetOutbound(false);
                        WinDivertNative.CalcChecksums(packet, (uint)packet.Length, ref addr, 0);
                        Interlocked.Increment(ref _reflected);
                    }
                    else if (TryRefuse(packet, packet.Length, original))
                    {
                        _decisions.Add(Stopwatch.GetTimestamp() - started);
                        continue;                                 // the SYN is answered, not sent on
                    }
                    else Interlocked.Increment(ref _passed);
                }
                catch (Exception ex)
                {
                    // Decide failed before rewriting anything: let the connection go out as it was.
                    _engine.Tracker.Write(LogLevel.Error, $"Connection decision failed, passing through: {ex.Message}");
                    addr = original;
                }
                _decisions.Add(Stopwatch.GetTimestamp() - started);
                if (_net != IntPtr.Zero) WinDivertNative.Send(_net, packet, (uint)packet.Length, out _, ref addr);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SocketLoop()
    {
        var addr = new WinDivertNative.Address();
        while (!_cts.IsCancellationRequested)
        {
            if (!WinDivertNative.Recv(_sock, null, 0, out _, ref addr))
            {
                if (_cts.IsCancellationRequested) break;
                var err = Marshal.GetLastWin32Error();
                _engine.Tracker.Write(LogLevel.Error, $"WinDivertRecv (socket) failed: {new Win32Exception(err).Message} ({err})");
                break;
            }
            if (addr.Event != WinDivertNative.Event.SocketConnect) continue;
            var remote = new IPEndPoint(WinDivertNative.ToIPAddress(addr.RemoteAddr), addr.RemotePort);
            _socketPids[(addr.LocalPort, remote)] = ((int)addr.ProcessId, DateTime.UtcNow);
        }
    }

    /// <param name="follower">A followers' handle: this thread owns it and closes it once it has been shut down and drained.</param>
    private void NetworkLoop(IntPtr handle, bool follower)
    {
        var buf = new byte[MaxPacket];
        var addr = new WinDivertNative.Address();
        try
        {
            while (true)
            {
                if (!WinDivertNative.Recv(handle, buf, (uint)buf.Length, out var len, ref addr))
                {
                    var err = Marshal.GetLastWin32Error();
                    // ERROR_NO_DATA: shut down and drained (a replaced followers' handle, or stopping).
                    if (_cts.IsCancellationRequested || err is 232 or 6 /* invalid handle */) break;
                    if (Interlocked.Increment(ref _recvErrors) <= 5)
                        _engine.Tracker.Write(LogLevel.Error, $"WinDivertRecv (network) failed: {new Win32Exception(err).Message} ({err})");
                    continue;
                }

                // WinDivert stamps each packet with the performance counter (the Stopwatch clock) when it captured it.
                var received = Stopwatch.GetTimestamp();
                if (addr.Timestamp > 0) _queueWait.Add(received - addr.Timestamp);
                HandlePacket(handle, buf, len, ref addr);
                _handling.Add(Stopwatch.GetTimestamp() - received);
            }
        }
        finally
        {
            if (follower) WinDivertNative.Close(handle);
        }
    }

    /// <summary>
    /// If this is a SYN of a flow Decide refused (<see cref="_refuse"/>), injects the reset that answers it and returns
    /// true: the caller then drops the SYN. Called for the first SYN (decision worker) and for the app's retries
    /// (packet thread), which the reflector passes as an already decided flow.
    /// </summary>
    private bool TryRefuse(byte[] packet, int length, WinDivertNative.Address original)
    {
        if (_refuse.IsEmpty) return false;
        var syn = packet.AsSpan(0, length);
        var tcp = new TcpPacket(syn);
        if (!tcp.IsValid || !tcp.Syn || tcp.Ack) return false;
        var key = (Norm(new IPEndPoint(tcp.SrcAddr, tcp.SrcPort)), Norm(new IPEndPoint(tcp.DstAddr, tcp.DstPort)));
        if (!_refuse.ContainsKey(key)) return false;
        if (BuildSynReset(syn) is not { } reset) return false;
        InjectInbound(reset, original);
        Interlocked.Increment(ref _refused);
        return true;
    }

    private void HandlePacket(IntPtr handle, byte[] buf, uint len, ref WinDivertNative.Address addr)
    {
        if (DiagnosticPackets > 0 && Interlocked.Decrement(ref DiagnosticPackets) >= 0)
            _engine.Tracker.Write(LogLevel.Debug,
                $"pkt len={len} outbound={addr.Outbound} loop={addr.Loopback} v6={addr.IPv6} head={Convert.ToHexString(buf, 0, (int)Math.Min(len, 48u))}");

        // DNS query: answer locally with a fake IP and drop the original.
        if ((buf[0] >> 4 == 4 && buf[9] == 17) || (buf[0] >> 4 == 6 && buf[6] == 17))
        {
            if (FromHostDnsProxy(buf, len))
            {
                WinDivertNative.Send(handle, buf, len, out _, ref addr);
                return;
            }
            byte[]? reply = null;
            try { reply = _dns!.TryAnswer(buf.AsSpan(0, (int)len)); }
            catch (Exception ex) { _engine.Tracker.Write(LogLevel.Error, $"DNS handling failed, passing through: {ex.Message}"); }

            if (reply is not null)
            {
                var raddr = addr;
                raddr.SetOutbound(false);
                WinDivertNative.CalcChecksums(reply, (uint)reply.Length, ref raddr, 0);
                WinDivertNative.Send(handle, reply, (uint)reply.Length, out _, ref raddr);
                Interlocked.Increment(ref _dnsAnswered);
                return;
            }
            if (_udp is not null)
            {
                try
                {
                    if (_udp.ProcessOutbound(buf.AsSpan(0, (int)len), addr) == UdpNat.Verdict.Drop) return;
                }
                catch (Exception ex)
                {
                    _engine.Tracker.Write(LogLevel.Error, $"UDP handling failed, passing through: {ex.Message}");
                }
            }
            WinDivertNative.Send(handle, buf, len, out _, ref addr);
            return;
        }

        try
        {
            switch (_reflector!.ProcessOutbound(buf.AsSpan(0, (int)len)))
            {
                case PacketReflector.Verdict.Reflected:
                    addr.SetOutbound(false);
                    WinDivertNative.CalcChecksums(buf, len, ref addr, 0);
                    Interlocked.Increment(ref _reflected);
                    break;
                case PacketReflector.Verdict.Defer:
                    _deferred.Add((buf.AsSpan(0, (int)len).ToArray(), addr));
                    return;
                case PacketReflector.Verdict.Drop:
                    return;   // a repeated SYN while its decision is pending; the app sends it again
                default:
                    if (TryRefuse(buf, (int)len, addr)) return;   // the app's retry of a refused SYN
                    Interlocked.Increment(ref _passed);
                    break;
            }
        }
        catch (Exception ex)
        {
            _engine.Tracker.Write(LogLevel.Error, $"Packet handling failed, passing through: {ex.Message}");
        }

        WinDivertNative.Send(handle, buf, len, out _, ref addr);
    }

    /// <summary>Injects a reply datagram as if it had arrived on the interface the app's packet left by.</summary>
    private void InjectInbound(byte[] packet, object? context)
    {
        if (context is not WinDivertNative.Address original || _net == IntPtr.Zero) return;
        var a = original;
        a.SetOutbound(false);
        WinDivertNative.CalcChecksums(packet, (uint)packet.Length, ref a, 0);
        WinDivertNative.Send(_net, packet, (uint)packet.Length, out _, ref a);
    }

    // ---------- DNS for WSL and virtual machines (docs/windows/FIXES.md §1) ----------
    //
    // WSL (NAT mode), Hyper-V and Docker VMs ask Windows' DNS proxy (SharedAccess) on the host's virtual adapter. The
    // proxy asks the Windows DNS client, which can't tell a VM's question from a local app's, so the VM would get a fake
    // address. But a VM's connections are forwarded by Windows' NAT below anything WinDivert sees: they never reach the
    // relay, and a fake address leads nowhere. So the names Sluice fakes are answered here, for the VM, with real
    // addresses from Sluice's own resolver; the VM goes direct, as without Sluice. Every other question goes on to the
    // proxy unchanged, so local names (mshome.net, .local, the hosts file) and a VPN's DNS keep working.
    // Not covered: WSL with DNS tunneling (Windows 11 22H2+), whose questions don't pass through a network adapter.

    /// <summary>DNS questions arriving at this PC from another machine (a VM on a virtual switch, or the LAN).</summary>
    internal const string VmDnsFilter = "inbound and !loopback and !ipv6 and udp.DstPort == 53";

    /// <summary>TTL of the real answers given to VMs: short, so a VM asks again soon after capture stops.</summary>
    private const uint VmDnsTtl = 30;

    private readonly object _vmDnsGate = new();
    private long _vmDnsAnswered;

    private void VmDnsLoop()
    {
        var handle = _vmDns;
        var buf = new byte[65535];
        var addr = new WinDivertNative.Address();
        while (!_cts.IsCancellationRequested)
        {
            if (!WinDivertNative.Recv(handle, buf, (uint)buf.Length, out var len, ref addr)) break;
            var packet = buf.AsSpan(0, (int)len).ToArray();
            var original = addr;
            try
            {
                if (TryAnswerForVm(packet, original)) continue;
            }
            catch (Exception ex)
            {
                _engine.Tracker.Write(LogLevel.Error, $"DNS for a virtual machine failed, passing it to Windows: {ex.Message}");
            }
            WinDivertNative.Send(handle, packet, (uint)packet.Length, out _, ref original);
        }
    }

    /// <summary>
    /// Takes over <paramref name="packet"/> (a DNS question to Windows' DNS proxy) when Sluice would fake its name, and
    /// answers it with real addresses. False: pass it on to the proxy unchanged.
    /// </summary>
    private bool TryAnswerForVm(byte[] packet, WinDivertNative.Address original)
    {
        if (!_hostProxyPorts.Contains(53)) return false;   // port 53 isn't Windows' DNS proxy (or it isn't running)
        if (VmDnsQuestion(packet) is not { } q) return false;
        if (q.Query.Class != DnsMessage.ClassIN || IsProxyOwnName(q.Query.Name) || !CurrentPolicy().ShouldFake(q.Query.Name))
            return false;

        switch ((DnsType)q.Query.Type)
        {
            case DnsType.A:
                _ = AnswerForVmAsync(packet, original, q);
                return true;
            case DnsType.AAAA:
            case DnsType.HTTPS:
            case DnsType.SVCB:
                // As for local apps: IPv4 only, no address hints. The VM then uses the A record.
                SendToVm(packet, original, q, [], rcode: 0);
                return true;
            default:
                return false;
        }
    }

    private async Task AnswerForVmAsync(byte[] packet, WinDivertNative.Address original, (DnsQuery Query, int UdpOffset) q)
    {
        byte rcode = 0;
        IPAddress[] answers = [];
        try
        {
            var all = await _resolver!.ResolveAsync(q.Query.Name, _cts.Token);
            answers = all.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !FakeIpPool.IsFake(a)).ToArray();
        }
        catch (OperationCanceledException) { return; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound) { rcode = 3; }   // NXDOMAIN
        catch (Exception) { rcode = 2; }                                                                  // SERVFAIL
        _engine.Tracker.Write(LogLevel.Debug, rcode == 0
            ? $"DNS {q.Query.Name} for a virtual machine: real answer ({string.Join(", ", answers.Take(3))})"
            : $"DNS {q.Query.Name} for a virtual machine: {(rcode == 3 ? "no such name" : "lookup failed")}");
        try { SendToVm(packet, original, q, answers, rcode); }
        catch (Exception ex) { _engine.Tracker.Write(LogLevel.Error, $"DNS for a virtual machine failed: {ex.Message}"); }
    }

    private void SendToVm(byte[] packet, WinDivertNative.Address original, (DnsQuery Query, int UdpOffset) q,
        IReadOnlyList<IPAddress> answers, byte rcode)
    {
        var dns = packet.AsSpan(q.UdpOffset + 8);
        var reply = BuildVmDnsReply(packet, q.UdpOffset, DnsMessage.BuildResponse(dns, q.Query, answers, VmDnsTtl, rcode));
        var a = original;
        a.SetOutbound(true);
        lock (_vmDnsGate)
        {
            if (_vmDns == IntPtr.Zero) return;   // capture stopped meanwhile
            WinDivertNative.CalcChecksums(reply, (uint)reply.Length, ref a, 0);
            WinDivertNative.Send(_vmDns, reply, (uint)reply.Length, out _, ref a);
        }
        Interlocked.Increment(ref _vmDnsAnswered);
    }

    /// <summary>
    /// Names Windows' DNS proxy answers itself or a VM expects from its own network: the PC and other VMs under
    /// mshome.net, single-label names, Docker's *.internal, and home-network suffixes. They always go on to the proxy.
    /// </summary>
    internal static bool IsProxyOwnName(string name)
    {
        name = name.TrimEnd('.');
        if (!name.Contains('.')) return true;
        foreach (var suffix in ProxyOwnSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || name.Equals(suffix[1..], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly string[] ProxyOwnSuffixes = [".mshome.net", ".internal", ".lan", ".home", ".localdomain"];

    /// <summary>The DNS question in an IPv4/UDP packet to port 53, and where its UDP header starts; null if it isn't one.</summary>
    internal static (DnsQuery Query, int UdpOffset)? VmDnsQuestion(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 28 || packet[0] >> 4 != 4 || packet[9] != 17) return null;
        var udp = (packet[0] & 0x0F) * 4;
        if (packet.Length < udp + 8 + 12 || BinaryPrimitives.ReadUInt16BigEndian(packet[(udp + 2)..]) != 53) return null;
        return DnsMessage.TryParseQuery(packet[(udp + 8)..]) is { } q ? (q, udp) : null;
    }

    /// <summary>The reply packet to an IPv4/UDP question: addresses and ports swapped (checksums left to WinDivert).</summary>
    internal static byte[] BuildVmDnsReply(ReadOnlySpan<byte> question, int udpOffset, byte[] dns)
    {
        var r = new byte[udpOffset + 8 + dns.Length];
        question[..udpOffset].CopyTo(r);
        question.Slice(12, 4).CopyTo(r.AsSpan(16));
        question.Slice(16, 4).CopyTo(r.AsSpan(12));
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), (ushort)r.Length);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), 0x4000);   // DF, no fragments
        r[8] = 128;                                                   // TTL
        r[10] = r[11] = 0;                                            // header checksum (recomputed)
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(udpOffset), BinaryPrimitives.ReadUInt16BigEndian(question[(udpOffset + 2)..]));
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(udpOffset + 2), BinaryPrimitives.ReadUInt16BigEndian(question[udpOffset..]));
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(udpOffset + 4), (ushort)(8 + dns.Length));
        r[udpOffset + 6] = r[udpOffset + 7] = 0;
        dns.CopyTo(r, udpOffset + 8);
        return r;
    }

    /// <summary>True when this UDP packet leaves from one of the host DNS proxy's ports (<see cref="_hostProxyPorts"/>).</summary>
    private bool FromHostDnsProxy(byte[] buf, uint len)
    {
        var ports = _hostProxyPorts;
        if (ports.Count == 0) return false;
        var udp = buf[0] >> 4 == 4 ? (buf[0] & 0x0F) * 4 : 40;
        return len >= udp + 8 && ports.Contains(BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(udp)));
    }

    private void RefreshHostDnsProxy()
    {
        try
        {
            var pid = ProcessLookup.FindServicePid("SharedAccess");
            var ports = ProcessLookup.FindUdpPorts(pid);
            if (pid != _hostProxyPid)
                _engine.Tracker.Write(LogLevel.Normal, pid == 0
                    ? "Windows' DNS proxy for WSL and virtual machines isn't running"
                    : $"Leaving Windows' DNS proxy for WSL and virtual machines alone (svchost {pid}): its lookups get real answers");
            if (!ports.SetEquals(_hostProxyPorts))
                _engine.Tracker.Write(LogLevel.Debug, $"DNS proxy ports: {string.Join(", ", ports.Order())}");
            _hostProxyPid = pid;
            _hostProxyPorts = ports;
        }
        catch (Exception ex)
        {
            _engine.Tracker.Write(LogLevel.Error, $"Could not find Windows' DNS proxy for WSL and virtual machines: {ex.Message}");
        }
    }

    private async Task SweepLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                SaveFakeIps();
                _engine.Tracker.Write(LogLevel.Debug,
                    $"Interception stats: {PacketsReflected:N0} reflected, {PacketsPassed:N0} passed, " +
                    $"{Interlocked.Read(ref _refused):N0} IPv6 refused, {_reflector?.FlowCount ?? 0} flows, " +
                    $"{DnsQueriesAnswered:N0} DNS answered, {Interlocked.Read(ref _vmDnsAnswered):N0} for VMs, {_fakeIps.Count} fake IPs, {Interlocked.Read(ref _recvErrors)} recv errors" +
                    (_udp is null ? "" : $", UDP {_udp.FlowCount} flows / {_udp.DatagramsTunneled:N0} tunneled / {_udp.DatagramsDropped:N0} dropped"));
                LogTimings();
                _udp?.Sweep(TimeSpan.FromSeconds(60));
                _reflector?.Sweep(TimeSpan.FromMinutes(10));
                PruneFollowed();
                RefreshHostDnsProxy();
                _ = CurrentPolicy();   // apply changed DNS settings even when no app is looking names up (docs/core/FIXES.md §5)
                CheckRelayReachable();
                var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(1);
                foreach (var (k, v) in _socketPids)
                    if (v.At < cutoff) _socketPids.TryRemove(k, out _);
                var refuseCutoff = DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(30).Ticks;
                foreach (var (k, at) in _refuse)
                    if (at < refuseCutoff) _refuse.TryRemove(k, out _);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Every 5 s at Verbose, when packets were handled: where captured packets spend their time.</summary>
    private void LogTimings()
    {
        var wait = _queueWait.TakeInterval();
        var handling = _handling.TakeInterval();
        var decisions = _decisions.TakeInterval();
        if (handling.Count == 0 || _engine.Tracker.Level < LogLevel.Verbose) return;
        _engine.Tracker.Write(LogLevel.Verbose,
            $"Capture timing (5 s): queue wait {wait}; handling {handling}; new connections {decisions}; pending {_reflector?.PendingCount ?? 0}");
    }

    private string TimingSummary() =>
        $"queue wait {_queueWait.Total} ({_queueWait.Slow:N0} over 1 ms); handling {_handling.Total} ({_handling.Slow:N0} over 0.5 ms); " +
        $"new connections {_decisions.Total} ({_decisions.Slow:N0} over 5 ms); owner from socket event {Interlocked.Read(ref _ownerFromSniff):N0}, " +
        $"from TCP table {Interlocked.Read(ref _ownerFromTable):N0}; follower filter updates {Interlocked.Read(ref _followReopens):N0}";

    private void CheckRelayReachable()
    {
        if (_relayWarned || _relay is null) return;
        if (_relay.Accepted > 0) { _relayWarned = true; return; }   // working: stop checking
        if (Interlocked.Read(ref _redirectedFlows) < 3 || DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(8)) return;
        _relayWarned = true;
        _engine.Tracker.Write(LogLevel.Error,
            $"{Interlocked.Read(ref _redirectedFlows)} connections were redirected but none reached Sluice. Windows Firewall is probably blocking " +
            $"{Path.GetFileName(Environment.ProcessPath)} on this network: allow it for incoming connections ({Environment.ProcessPath}).");
        RelayUnreachable?.Invoke();
    }

    private static IPEndPoint Norm(IPEndPoint ep) => IRedirectTable.Normalize(ep);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var h in new[] { _net, _sock, _vmDns })
            if (h != IntPtr.Zero) WinDivertNative.ShutdownHandle(h, WinDivertNative.Shutdown.Both);
        lock (_followGate)
        {
            // Its thread closes it once Recv fails.
            if (_follow != IntPtr.Zero) WinDivertNative.ShutdownHandle(_follow, WinDivertNative.Shutdown.Both);
            _follow = IntPtr.Zero;
        }
        _netThread?.Join(2000);
        _sockThread?.Join(2000);
        _vmDnsThread?.Join(2000);
        _deferred.CompleteAdding();
        foreach (var t in _deciders) t.Join(2000);
        Thread[] followers;
        lock (_followThreads) followers = [.. _followThreads];
        foreach (var t in followers) t.Join(2000);
        lock (_vmDnsGate)
        {
            foreach (var h in new[] { _net, _sock, _vmDns })
                if (h != IntPtr.Zero) WinDivertNative.Close(h);
            _net = _sock = _vmDns = IntPtr.Zero;
        }
        if (_sweeper is not null) await _sweeper;
        if (_relay is not null) await _relay.DisposeAsync();
        if (_udp is not null) await _udp.DisposeAsync();
        SaveFakeIps();
        _engine.HostResolver = null;
        _resolver?.Dispose();
        // Fake answers are meaningless once we stop: drop them from the OS cache. Every stop path (the capture button,
        // the tray, quitting, a UDP-mode restart) ends here; the line in the log shows it ran (docs/windows/FIXES.md §2).
        if (_fakeIps.Count > 0) FlushOsDnsCache("capture stopped");
        else _engine.Tracker.Write(LogLevel.Debug, "No fake addresses handed out: the Windows DNS cache is left as it is");
        _engine.Tracker.Write(LogLevel.Normal, $"Interception stopped ({PacketsReflected:N0} packets reflected, {DnsQueriesAnswered:N0} DNS queries answered)");
        if (_handling.Total.Count > 0) _engine.Tracker.Write(LogLevel.Normal, $"Capture timing: {TimingSummary()}");
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool DnsFlushResolverCache();

    private void FlushOsDnsCache(string why)
    {
        try
        {
            if (DnsFlushResolverCache()) _engine.Tracker.Write(LogLevel.Normal, $"Flushed the Windows DNS cache ({why})");
            else _engine.Tracker.Write(LogLevel.Error, $"Could not flush the Windows DNS cache ({why})");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _engine.Tracker.Write(LogLevel.Error, "Could not flush the Windows DNS cache (ipconfig /flushdns)");
        }
    }

    /// <summary>
    /// The relay must exist before the reflector (it needs the port), so bind lazily.
    /// Adds the hostname for flows that targeted a fake IP.
    /// </summary>
    private sealed class LateBoundTable(Func<PacketReflector?> get, FakeIpPool fakes) : IRedirectTable
    {
        public bool TryGetOriginal(IPEndPoint clientEndPoint, out OriginalDestination original)
        {
            if (get() is { } r && r.TryGetOriginal(clientEndPoint, out original))
            {
                if (fakes.TryGetHost(original.Destination.Address, out var host))
                    original = original with { Hostname = host };
                return true;
            }
            original = null!;
            return false;
        }
    }
}
