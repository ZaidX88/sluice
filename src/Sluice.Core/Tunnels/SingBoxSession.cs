using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Core.Tunnels;

/// <summary>Common shape of a running encrypted-tunnel client.</summary>
internal interface IBackendSession : IAsyncDisposable
{
    IPEndPoint? SocksEndPoint { get; }
    string Fingerprint { get; }
    bool IsAlive { get; }
    /// <summary>Helper process id, if the session runs one (its traffic must never be routed back into tunnels).</summary>
    int? ProcessId { get; }
    Task StartAsync(CancellationToken ct);
}

/// <summary>
/// Runs sing-box for one tunnel (Shadowsocks, VMess, VLESS, Trojan, Hysteria2, TUIC) with a SOCKS5
/// inbound on 127.0.0.1. The process belongs to a kill-on-close job object, so it never outlives Sluice.
/// </summary>
internal sealed class SingBoxSession : IBackendSession
{
    /// <summary>
    /// Every sing-box helper Sluice is running, from the moment it starts. Their connections to tunnel servers
    /// must never be captured, including while the session is still starting (SSH logs in during start).
    /// </summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> RunningHelpers = new();

    private readonly Tunnel _tunnel;
    private readonly string _exe;
    private readonly TunnelTransport.Resolver? _resolve;
    private readonly Action<string> _log;
    private readonly Func<int, string?, string>? _buildConfig;
    private Process? _process;
    private volatile bool _closing;
    private readonly List<string> _recent = new();

    /// <summary>The last lines sing-box printed (errors explain failed logins etc.).</summary>
    public string[] RecentOutput { get { lock (_recent) return _recent.ToArray(); } }

    public IPEndPoint? SocksEndPoint { get; private set; }
    public string Fingerprint { get; }
    public int? ProcessId => _process?.Id;
    public bool IsAlive => _process is { HasExited: false } && SocksEndPoint is not null;

    /// <param name="buildConfig">Custom config (socks port, resolved server IP → JSON); default: <see cref="SingBoxConfig.Build"/>.</param>
    public SingBoxSession(Tunnel tunnel, string fingerprint, string exe, TunnelTransport.Resolver? resolve, Action<string> log,
        Func<int, string?, string>? buildConfig = null)
    {
        _buildConfig = buildConfig;
        _tunnel = tunnel;
        Fingerprint = fingerprint;
        _exe = exe;
        _resolve = resolve;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!File.Exists(_exe))
            throw new ProxyException($"sing-box was not found at {_exe}. Run scripts/get-sing-box.ps1 (Windows) or scripts/get-sing-box.sh (Linux), or place the sing-box binary next to Sluice.");

        // Resolve the server ourselves: sing-box would ask Windows, whose cache may hold Sluice's fake addresses.
        string? ip = null;
        if (!IPAddress.TryParse(_tunnel.Address, out _))
        {
            try
            {
                var addrs = _resolve is not null ? await _resolve(_tunnel.Address, ct) : await System.Net.Dns.GetHostAddressesAsync(_tunnel.Address, ct);
                // sing-box gets one address: prefer IPv4, since a listed IPv6 address may not be routable
                // (e.g. NAT64 answers on a network without working IPv6).
                ip = (addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addrs.FirstOrDefault())?.ToString();
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
                throw new ProxyException($"Cannot resolve {_tunnel.Address}: {ex.Message}");
            }
        }

        var port = FreeLoopbackPort();
        var json = _buildConfig is not null ? _buildConfig(port, ip) : SingBoxConfig.Build(_tunnel, port, ip);
        var configDir = HelperConfigFolder.Ensure(HelperConfigFolder.Default);
        var configPath = Path.Combine(configDir, $"{_tunnel.Id}-{Guid.NewGuid():N}.json");
        await WritePrivateAsync(configPath, json, ct);

        var errors = new List<string>();
        try
        {
            var psi = new ProcessStartInfo(_exe)
            {
                ArgumentList = { "run", "-c", configPath, "--disable-color" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = configDir,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                lock (errors) if (errors.Count < 20) errors.Add(line.Trim());
                lock (_recent) { _recent.Add(line.Trim()); if (_recent.Count > 30) _recent.RemoveAt(0); }
                if (!_closing) _log($"sing-box {_tunnel}: {line.Trim()}");
            }
            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.Exited += (_, _) =>
            {
                if (!_closing) _log($"sing-box {_tunnel}: helper exited (code {SafeExitCode(p)})");
            };
            if (!p.Start()) throw new ProxyException("sing-box could not be started");
            RunningHelpers[p.Id] = 0;
            var helperId = p.Id;
            p.Exited += (_, _) => RunningHelpers.TryRemove(helperId, out _);
            Job.Assign(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;

            // Ready when the SOCKS inbound accepts connections.
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                if (p.HasExited)
                {
                    string detail;
                    lock (errors) detail = errors.Count > 0 ? string.Join(" | ", errors.TakeLast(3)) : $"exit code {SafeExitCode(p)}";
                    throw new ProxyException($"sing-box stopped: {detail}");
                }
                if (await CanConnect(port, ct)) break;
                if (DateTime.UtcNow > until) throw new ProxyException("sing-box did not start listening within 10 s");
                await Task.Delay(50, ct);
            }
            SocksEndPoint = new IPEndPoint(IPAddress.Loopback, port);
            _log($"sing-box {_tunnel}: {_tunnel.Type.DisplayName()} to {_tunnel.Address}:{_tunnel.Port} ready (helper pid {p.Id}, local {SocksEndPoint})");
        }
        finally
        {
            // The config holds secrets; sing-box has read it once it is listening (or has failed).
            try { File.Delete(configPath); } catch { /* best effort */ }
        }
    }

    private static async Task WritePrivateAsync(string path, string text, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var f = new FileStream(path, options);
        await using var w = new StreamWriter(f);
        await w.WriteAsync(text.AsMemory(), ct);
    }

    private static async Task<bool> CanConnect(int port, CancellationToken ct)
    {
        using var c = new TcpClient();
        try
        {
            await c.ConnectAsync(IPAddress.Loopback, port, ct).AsTask().WaitAsync(TimeSpan.FromMilliseconds(300), ct);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            return false;
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private static int FreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public ValueTask DisposeAsync()
    {
        _closing = true;
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        try { if (_process is not null) RunningHelpers.TryRemove(_process.Id, out _); } catch { /* no id */ }
        _process?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Kill-on-close job object: helpers die with Sluice, even if Sluice crashes.</summary>
    private static class Job
    {
        private static readonly IntPtr Handle = Create();

        public static void Assign(Process p)
        {
            if (Handle != IntPtr.Zero) AssignProcessToJobObject(Handle, p.Handle);
        }

        private static IntPtr Create()
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
            var h = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(h, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return h;   // intentionally never closed: closing it is what kills the helpers
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
