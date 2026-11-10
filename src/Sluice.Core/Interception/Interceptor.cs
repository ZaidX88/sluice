using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
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
/// </summary>
public sealed class Interceptor : IAsyncDisposable
{
    private const int MaxPacket = 0xFFFF + 40;

    private readonly RoutingEngine _engine;
    private readonly ConcurrentDictionary<(int LocalPort, IPEndPoint Remote), (int Pid, DateTime At)> _socketPids = new();
    private readonly CancellationTokenSource _cts = new();
    private IntPtr _net = IntPtr.Zero, _sock = IntPtr.Zero;
    private Thread? _netThread, _sockThread;
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
    private (Profile Profile, DnsPolicy Policy)? _policy;
    private UdpNat? _udp;

    /// <summary>Log the headers of this many packets at Debug level (troubleshooting).</summary>
    public int DiagnosticPackets;

    public Interceptor(RoutingEngine engine) => _engine = engine;

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

    public void Start()
    {
        // Relay on the dual-stack wildcard: reflected packets arrive addressed to the machine's own IP.
        var relay = new TransparentInbound(_engine, new IPEndPoint(IPAddress.IPv6Any, 0), new LateBoundTable(() => _reflector, _fakeIps));
        _relay = relay;
        var port = (ushort)relay.LocalEndPoint.Port;
        _reflector = new PacketReflector(port, Decide);
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
        _net = OpenOrThrow(captureUdp
                ? "outbound and !loopback and (tcp or udp)"
                : "outbound and !loopback and (tcp or (udp and udp.DstPort == 53))",
            WinDivertNative.Layer.Network, WinDivertNative.OpenFlags.None);
        if (captureUdp) _udp = new UdpNat(_engine, _fakeIps, InjectInbound);

        var policy = CurrentPolicy();
        if (policy.Enabled)
        {
            FlushOsDnsCache();
            _engine.Tracker.Write(LogLevel.Normal, "Name resolution through tunnels is ON (fake IPs from 198.18.0.0/15)");
        }

        _sockThread = new Thread(SocketLoop) { IsBackground = true, Name = "Sluice WinDivert socket" };
        _netThread = new Thread(NetworkLoop) { IsBackground = true, Name = "Sluice WinDivert network" };
        _sockThread.Start();
        _netThread.Start();
        _startedAt = DateTime.UtcNow;
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

    private DnsPolicy CurrentPolicy()
    {
        var profile = _engine.Profile;
        var cached = _policy;
        if (cached is { } c && ReferenceEquals(c.Profile, profile)) return c.Policy;
        var p = new DnsPolicy(profile.Options.Resolve, profile.Options.LeakPreventionMode);
        _policy = (profile, p);
        return p;
    }

    /// <summary>Called on the SYN of every new outbound flow.</summary>
    private (bool Redirect, int Pid) Decide(IPEndPoint local, IPEndPoint remote)
    {
        var pid = 0;
        if (_socketPids.TryRemove((local.Port, Norm(remote)), out var hit)) pid = hit.Pid;
        if (pid == 0) pid = ProcessLookup.FindTcpOwner(local, remote);

        if (pid == Environment.ProcessId) return (false, pid);

        // DNS over TCP (fallback for large answers, or a browser's own resolver) goes to the local network's
        // resolver: never into a tunnel, whose server cannot reach this LAN. UDP DNS is answered locally.
        if (remote.Port == 53 && !FakeIpPool.IsFake(remote.Address)) return (false, pid);

        // A fake address is unreachable by itself: always hand such flows to the engine,
        // which connects by hostname (directly or through the proxy, per the rules).
        var fake = _fakeIps.TryGetHost(remote.Address, out var hostname);
        if (!fake && FakeIpPool.IsFake(remote.Address))
            _engine.Tracker.Write(LogLevel.Error, $"Connection to unknown fake address {remote} (stale DNS cache?)");
        if (!fake && pid == 0 && !_engine.Profile.Options.ProcessServices) return (false, 0);

        var path = pid == 0 ? null : ProcessLookup.GetProcessPath(pid);
        var host = fake ? hostname : remote.Address.ToString();
        var match = _engine.Classify(new InboundRequest(host, remote.Port, pid, path));
        var redirect = fake || match.Action.Type != RuleActionType.Direct || _engine.Profile.Options.HandleDirectConnections;
        if (redirect) Interlocked.Increment(ref _redirectedFlows);
        _engine.Tracker.Write(LogLevel.Debug, $"SYN {local} -> {host}:{remote.Port} pid {pid} ({Path.GetFileName(path) ?? "?"}) rule {match.Rule.Name} => {(redirect ? "redirect" : "pass")}");
        if (!redirect)
            _engine.Tracker.Write(LogLevel.Verbose, $"{Path.GetFileName(path) ?? "?"} ({pid}) - {remote} direct (rule {match.Rule.Name}, not intercepted)");
        return (redirect, pid);
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

    private void NetworkLoop()
    {
        var buf = new byte[MaxPacket];
        var addr = new WinDivertNative.Address();
        while (!_cts.IsCancellationRequested)
        {
            if (!WinDivertNative.Recv(_net, buf, (uint)buf.Length, out var len, ref addr))
            {
                if (_cts.IsCancellationRequested) break;
                var err = Marshal.GetLastWin32Error();
                if (Interlocked.Increment(ref _recvErrors) <= 5)
                    _engine.Tracker.Write(LogLevel.Error, $"WinDivertRecv (network) failed: {new Win32Exception(err).Message} ({err})");
                if (err == 6 /* invalid handle */) break;
                continue;
            }

            if (DiagnosticPackets > 0 && Interlocked.Decrement(ref DiagnosticPackets) >= 0)
                _engine.Tracker.Write(LogLevel.Debug,
                    $"pkt len={len} outbound={addr.Outbound} loop={addr.Loopback} v6={addr.IPv6} head={Convert.ToHexString(buf, 0, (int)Math.Min(len, 48u))}");

            // DNS query: answer locally with a fake IP and drop the original.
            if ((buf[0] >> 4 == 4 && buf[9] == 17) || (buf[0] >> 4 == 6 && buf[6] == 17))
            {
                byte[]? reply = null;
                try { reply = _dns!.TryAnswer(buf.AsSpan(0, (int)len)); }
                catch (Exception ex) { _engine.Tracker.Write(LogLevel.Error, $"DNS handling failed, passing through: {ex.Message}"); }

                if (reply is not null)
                {
                    var raddr = addr;
                    raddr.SetOutbound(false);
                    WinDivertNative.CalcChecksums(reply, (uint)reply.Length, ref raddr, 0);
                    WinDivertNative.Send(_net, reply, (uint)reply.Length, out _, ref raddr);
                    Interlocked.Increment(ref _dnsAnswered);
                    continue;
                }
                if (_udp is not null)
                {
                    try
                    {
                        if (_udp.ProcessOutbound(buf.AsSpan(0, (int)len), addr) == UdpNat.Verdict.Drop) continue;
                    }
                    catch (Exception ex)
                    {
                        _engine.Tracker.Write(LogLevel.Error, $"UDP handling failed, passing through: {ex.Message}");
                    }
                }
                WinDivertNative.Send(_net, buf, len, out _, ref addr);
                continue;
            }

            try
            {
                if (_reflector!.ProcessOutbound(buf.AsSpan(0, (int)len)) == PacketReflector.Verdict.Reflected)
                {
                    addr.SetOutbound(false);
                    WinDivertNative.CalcChecksums(buf, len, ref addr, 0);
                    Interlocked.Increment(ref _reflected);
                }
                else
                {
                    Interlocked.Increment(ref _passed);
                }
            }
            catch (Exception ex)
            {
                _engine.Tracker.Write(LogLevel.Error, $"Packet handling failed, passing through: {ex.Message}");
            }

            WinDivertNative.Send(_net, buf, len, out _, ref addr);
        }
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

    private async Task SweepLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                _engine.Tracker.Write(LogLevel.Debug,
                    $"Interception stats: {PacketsReflected:N0} reflected, {PacketsPassed:N0} passed, {_reflector?.FlowCount ?? 0} flows, " +
                    $"{DnsQueriesAnswered:N0} DNS answered, {_fakeIps.Count} fake IPs, {Interlocked.Read(ref _recvErrors)} recv errors" +
                    (_udp is null ? "" : $", UDP {_udp.FlowCount} flows / {_udp.DatagramsTunneled:N0} tunneled / {_udp.DatagramsDropped:N0} dropped"));
                _udp?.Sweep(TimeSpan.FromSeconds(60));
                _reflector?.Sweep(TimeSpan.FromMinutes(10));
                CheckRelayReachable();
                var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(1);
                foreach (var (k, v) in _socketPids)
                    if (v.At < cutoff) _socketPids.TryRemove(k, out _);
            }
        }
        catch (OperationCanceledException) { }
    }

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
        foreach (var h in new[] { _net, _sock })
            if (h != IntPtr.Zero) WinDivertNative.ShutdownHandle(h, WinDivertNative.Shutdown.Both);
        _netThread?.Join(2000);
        _sockThread?.Join(2000);
        foreach (var h in new[] { _net, _sock })
            if (h != IntPtr.Zero) WinDivertNative.Close(h);
        _net = _sock = IntPtr.Zero;
        if (_sweeper is not null) await _sweeper;
        if (_relay is not null) await _relay.DisposeAsync();
        if (_udp is not null) await _udp.DisposeAsync();
        _engine.HostResolver = null;
        _resolver?.Dispose();
        // Fake answers are meaningless once we stop: drop them from the OS cache.
        if (_fakeIps.Count > 0) FlushOsDnsCache();
        _engine.Tracker.Write(LogLevel.Normal, $"Interception stopped ({PacketsReflected:N0} packets reflected, {DnsQueriesAnswered:N0} DNS queries answered)");
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool DnsFlushResolverCache();

    private void FlushOsDnsCache()
    {
        try
        {
            if (!DnsFlushResolverCache()) _engine.Tracker.Write(LogLevel.Error, "Could not flush the Windows DNS cache");
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
