using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
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

    private readonly ProxyEngine _engine;
    private readonly ConcurrentDictionary<(int LocalPort, IPEndPoint Remote), (int Pid, DateTime At)> _socketPids = new();
    private readonly CancellationTokenSource _cts = new();
    private IntPtr _net = IntPtr.Zero, _sock = IntPtr.Zero;
    private Thread? _netThread, _sockThread;
    private Task? _sweeper;
    private TransparentInbound? _relay;
    private PacketReflector? _reflector;
    private long _reflected, _passed, _recvErrors;

    /// <summary>Log the headers of this many packets at Debug level (troubleshooting).</summary>
    public int DiagnosticPackets;

    public Interceptor(ProxyEngine engine) => _engine = engine;

    public long PacketsReflected => Interlocked.Read(ref _reflected);
    public long PacketsPassed => Interlocked.Read(ref _passed);
    public IPEndPoint? RelayEndPoint => _relay?.LocalEndPoint;

    public void Start()
    {
        // Relay on the dual-stack wildcard: reflected packets arrive addressed to the machine's own IP.
        var relay = new TransparentInbound(_engine, new IPEndPoint(IPAddress.IPv6Any, 0), new LateBoundTable(() => _reflector));
        _relay = relay;
        var port = (ushort)relay.LocalEndPoint.Port;
        _reflector = new PacketReflector(port, Decide);
        relay.Start();

        var self = Environment.ProcessId;
        _sock = OpenOrThrow($"event == CONNECT and tcp and !loopback and processId != {self}",
            WinDivertNative.Layer.Socket, WinDivertNative.OpenFlags.Sniff | WinDivertNative.OpenFlags.RecvOnly);
        _net = OpenOrThrow("outbound and tcp and !loopback", WinDivertNative.Layer.Network, WinDivertNative.OpenFlags.None);

        _sockThread = new Thread(SocketLoop) { IsBackground = true, Name = "Sluice WinDivert socket" };
        _netThread = new Thread(NetworkLoop) { IsBackground = true, Name = "Sluice WinDivert network" };
        _sockThread.Start();
        _netThread.Start();
        _sweeper = Task.Run(SweepLoop);

        _engine.Tracker.Write(LogLevel.Normal, $"Interception started (relay port {port})");
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

    /// <summary>Called on the SYN of every new outbound flow.</summary>
    private (bool Redirect, int Pid) Decide(IPEndPoint local, IPEndPoint remote)
    {
        var pid = 0;
        if (_socketPids.TryRemove((local.Port, Norm(remote)), out var hit)) pid = hit.Pid;
        if (pid == 0) pid = ProcessLookup.FindTcpOwner(local, remote);

        if (pid == Environment.ProcessId) return (false, pid);
        if (pid == 0 && !_engine.Profile.Options.ProcessServices) return (false, 0);

        var path = pid == 0 ? null : ProcessLookup.GetProcessPath(pid);
        var match = _engine.Classify(new InboundRequest(remote.Address.ToString(), remote.Port, pid, path));
        var redirect = match.Action.Type != RuleActionType.Direct || _engine.Profile.Options.HandleDirectConnections;
        _engine.Tracker.Write(LogLevel.Debug, $"SYN {local} -> {remote} pid {pid} ({Path.GetFileName(path) ?? "?"}) rule {match.Rule.Name} => {(redirect ? "redirect" : "pass")}");
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

    private async Task SweepLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                _engine.Tracker.Write(LogLevel.Debug,
                    $"Interception stats: {PacketsReflected:N0} reflected, {PacketsPassed:N0} passed, {_reflector?.FlowCount ?? 0} flows, {Interlocked.Read(ref _recvErrors)} recv errors");
                _reflector?.Sweep(TimeSpan.FromMinutes(10));
                var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(1);
                foreach (var (k, v) in _socketPids)
                    if (v.At < cutoff) _socketPids.TryRemove(k, out _);
            }
        }
        catch (OperationCanceledException) { }
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
        _engine.Tracker.Write(LogLevel.Normal, $"Interception stopped ({PacketsReflected:N0} packets reflected)");
    }

    /// <summary>The relay must exist before the reflector (it needs the port), so bind lazily.</summary>
    private sealed class LateBoundTable(Func<PacketReflector?> get) : IRedirectTable
    {
        public bool TryGetOriginal(IPEndPoint clientEndPoint, out OriginalDestination original)
        {
            if (get() is { } r) return r.TryGetOriginal(clientEndPoint, out original);
            original = null!;
            return false;
        }
    }
}
