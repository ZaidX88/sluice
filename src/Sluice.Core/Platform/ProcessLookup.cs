using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Sluice.Core.Platform;

/// <summary>
/// Finds which process owns a local TCP endpoint (iphlpapi GetExtendedTcpTable),
/// and resolves a PID to its executable path.
/// </summary>
public static class ProcessLookup
{
    private const int AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, char[] exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly ConcurrentDictionary<int, (string? Path, DateTime At)> PathCache = new();

    /// <summary>PID owning the TCP socket whose local endpoint is <paramref name="local"/> and remote is <paramref name="remote"/>.</summary>
    public static int FindTcpOwner(IPEndPoint local, IPEndPoint remote)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var v6 = local.AddressFamily == AddressFamily.InterNetworkV6 && !local.Address.IsIPv4MappedToIPv6;
        foreach (var row in ReadTable(v6))
        {
            if (row.LocalPort == local.Port && row.RemotePort == remote.Port &&
                Same(row.Local, local.Address) && Same(row.Remote, remote.Address))
                return row.Pid;
        }
        return 0;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    private const int UDP_TABLE_OWNER_PID = 1;

    /// <summary>PID owning the UDP socket bound to <paramref name="local"/> (exact address or wildcard), or 0.</summary>
    public static int FindUdpOwner(IPEndPoint local)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var addr = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
        var v6 = addr.AddressFamily == AddressFamily.InterNetworkV6;
        int wildcard = 0;
        // A dual-stack (IPv6) socket can send IPv4 packets, so look in both tables.
        foreach (var table in v6 ? new[] { true } : new[] { false, true })
        {
            foreach (var (la, lp, pid) in ReadUdpTable(table))
            {
                if (lp != local.Port) continue;
                var a = la.IsIPv4MappedToIPv6 ? la.MapToIPv4() : la;
                if (a.Equals(addr)) return pid;
                if (wildcard == 0 && (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any))) wildcard = pid;
            }
            if (wildcard != 0) return wildcard;
        }
        return wildcard;
    }

    private static List<(IPAddress Local, int Port, int Pid)> ReadUdpTable(bool v6)
    {
        var rows = new List<(IPAddress, int, int)>();
        var size = 0;
        var af = v6 ? AF_INET6 : AF_INET;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
                if (rc == 122) continue;
                if (rc != 0) return rows;
                var count = Marshal.ReadInt32(buf);
                var ptr = buf + 4;
                if (!v6)
                {
                    // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid
                    for (var i = 0; i < count; i++, ptr += 12)
                        rows.Add((new IPAddress((uint)Marshal.ReadInt32(ptr)), PortFromNet(Marshal.ReadInt32(ptr + 4)), Marshal.ReadInt32(ptr + 8)));
                }
                else
                {
                    // MIB_UDP6ROW_OWNER_PID: localAddr[16], localScopeId, localPort, pid
                    for (var i = 0; i < count; i++, ptr += 28)
                        rows.Add((new IPAddress(Bytes(ptr, 16)), PortFromNet(Marshal.ReadInt32(ptr + 20)), Marshal.ReadInt32(ptr + 24)));
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return rows;
    }

    /// <summary>PIDs with a TCP socket listening on <paramref name="port"/> (IPv4 and IPv6 tables).</summary>
    public static HashSet<int> FindListeners(int port)
    {
        var pids = new HashSet<int>();
        if (!OperatingSystem.IsWindows()) return pids;
        foreach (var v6 in new[] { false, true })
            foreach (var row in ReadTable(v6))
                if (row.State == MIB_TCP_STATE_LISTEN && row.LocalPort == port)
                    pids.Add(row.Pid);
        return pids;
    }

    private const int MIB_TCP_STATE_LISTEN = 2;

    public static string? GetProcessPath(int pid)
    {
        if (pid <= 4) return pid == 4 ? "System" : null;
        if (PathCache.TryGetValue(pid, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(30)) return c.Path;

        string? path = null;
        if (OperatingSystem.IsWindows())
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var buf = new char[1024];
                    var size = buf.Length;
                    if (QueryFullProcessImageName(h, 0, buf, ref size)) path = new string(buf, 0, size);
                }
                finally { CloseHandle(h); }
            }
        }
        if (path is null)
        {
            try { path = Process.GetProcessById(pid).MainModule?.FileName; } catch { /* exited / denied */ }
        }
        PathCache[pid] = (path, DateTime.UtcNow);
        return path;
    }

    private static bool Same(IPAddress a, IPAddress b)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
        return a.Equals(b) || (IPAddress.Any.Equals(a) || IPAddress.IPv6Any.Equals(a));
    }

    private readonly record struct Row(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int Pid, int State);

    private static List<Row> ReadTable(bool v6)
    {
        var rows = new List<Row>();
        var size = 0;
        var af = v6 ? AF_INET6 : AF_INET;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
                if (rc == 122) continue; // ERROR_INSUFFICIENT_BUFFER: table grew, retry with new size
                if (rc != 0) return rows;
                var count = Marshal.ReadInt32(buf);
                var ptr = buf + 4;
                if (!v6)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 x DWORD)
                    for (var i = 0; i < count; i++, ptr += 24)
                    {
                        var la = new IPAddress((uint)Marshal.ReadInt32(ptr + 4));
                        var lp = PortFromNet(Marshal.ReadInt32(ptr + 8));
                        var ra = new IPAddress((uint)Marshal.ReadInt32(ptr + 12));
                        var rp = PortFromNet(Marshal.ReadInt32(ptr + 16));
                        rows.Add(new Row(la, lp, ra, rp, Marshal.ReadInt32(ptr + 20), Marshal.ReadInt32(ptr)));
                    }
                }
                else
                {
                    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                    for (var i = 0; i < count; i++, ptr += 56)
                    {
                        var la = new IPAddress(Bytes(ptr, 16));
                        var lp = PortFromNet(Marshal.ReadInt32(ptr + 20));
                        var ra = new IPAddress(Bytes(ptr + 24, 16));
                        var rp = PortFromNet(Marshal.ReadInt32(ptr + 44));
                        rows.Add(new Row(la, lp, ra, rp, Marshal.ReadInt32(ptr + 52), Marshal.ReadInt32(ptr + 48)));
                    }
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return rows;
    }

    private static int PortFromNet(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static byte[] Bytes(IntPtr p, int n)
    {
        var b = new byte[n];
        Marshal.Copy(p, b, 0, n);
        return b;
    }
}
