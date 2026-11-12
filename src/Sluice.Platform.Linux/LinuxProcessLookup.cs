using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Sluice.Core.Platform;

namespace Sluice.Platform.Linux;

/// <summary>
/// Which process owns a socket, from /proc: /proc/net/{tcp,tcp6,udp,udp6} map an endpoint to a socket inode,
/// and /proc/PID/fd links ("socket:[inode]") map the inode to a process. Seeing other users' processes needs
/// root (or CAP_SYS_PTRACE); otherwise only this user's apps are found.
/// </summary>
public sealed class LinuxProcessLookup : IProcessLookup
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, (int Pid, DateTime At)> _inodes = new();
    private readonly LinkedList<int> _recent = new();   // processes that owned sockets lately: searched first
    private readonly ConcurrentDictionary<int, (string? Path, DateTime At)> _paths = new();
    private readonly string _proc;

    public LinuxProcessLookup(string procRoot = "/proc") => _proc = procRoot;

    public int FindTcpOwner(IPEndPoint local, IPEndPoint remote)
    {
        foreach (var file in new[] { "tcp", "tcp6" })
            foreach (var e in ReadTable(Path.Combine(_proc, "net", file)))
                if (e.LocalPort == local.Port && e.RemotePort == remote.Port && Same(e.Local, local.Address, false) && Same(e.Remote, remote.Address, false))
                    return e.Inode == 0 ? 0 : PidForInode(e.Inode);
        return 0;
    }

    public int FindUdpOwner(IPEndPoint local)
    {
        ulong wildcard = 0;
        foreach (var file in new[] { "udp", "udp6" })
            foreach (var e in ReadTable(Path.Combine(_proc, "net", file)))
            {
                if (e.LocalPort != local.Port || e.Inode == 0) continue;
                if (Same(e.Local, local.Address, false)) return PidForInode(e.Inode);
                if (wildcard == 0 && IsAny(e.Local)) wildcard = e.Inode;
            }
        return wildcard == 0 ? 0 : PidForInode(wildcard);
    }

    public HashSet<int> FindListeners(int port)
    {
        var pids = new HashSet<int>();
        foreach (var file in new[] { "tcp", "tcp6" })
            foreach (var e in ReadTable(Path.Combine(_proc, "net", file)))
                if (e.State == 0x0A && e.LocalPort == port && e.Inode != 0 && PidForInode(e.Inode) is var pid and > 0)
                    pids.Add(pid);
        return pids;
    }

    public string? GetProcessPath(int pid)
    {
        if (pid <= 0) return null;
        if (_paths.TryGetValue(pid, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(30)) return c.Path;
        string? path = null;
        try
        {
            path = new FileInfo(Path.Combine(_proc, pid.ToString(CultureInfo.InvariantCulture), "exe")).LinkTarget;
            if (path?.EndsWith(" (deleted)", StringComparison.Ordinal) == true) path = path[..^10];   // updated since it started
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _paths[pid] = (path, DateTime.UtcNow);
        return path;
    }

    // ---------------------------------------------------------------- /proc/net tables

    public readonly record struct Entry(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int State, ulong Inode);

    /// <summary>Rows of a /proc/net/{tcp,udp}[6] file.</summary>
    public static IEnumerable<Entry> ReadTable(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var line in lines.Skip(1))
            if (TryParseLine(line, out var e)) yield return e;
    }

    /// <summary>"  0: 0100007F:0277 00000000:0000 0A 00000000:00000000 00:00000000 00000000  0  0 12345 …"</summary>
    public static bool TryParseLine(string line, out Entry entry)
    {
        entry = default;
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 10) return false;
        if (!TryEndpoint(f[1], out var la, out var lp) || !TryEndpoint(f[2], out var ra, out var rp)) return false;
        if (!int.TryParse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var st)) return false;
        if (!ulong.TryParse(f[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode)) return false;
        entry = new Entry(la, lp, ra, rp, st, inode);
        return true;
    }

    /// <summary>Kernel prints each 32-bit word of the address in host (little-endian) order, then ":PORT" in hex.</summary>
    private static bool TryEndpoint(string s, out IPAddress addr, out int port)
    {
        addr = IPAddress.None;
        port = 0;
        var colon = s.IndexOf(':');
        if (colon < 0 || !int.TryParse(s.AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out port)) return false;
        var hex = s.AsSpan(0, colon);
        if (hex.Length is not (8 or 32)) return false;
        var bytes = new byte[hex.Length / 2];
        for (var w = 0; w < hex.Length / 8; w++)
        {
            if (!uint.TryParse(hex.Slice(w * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var word)) return false;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(w * 4), word);
        }
        addr = new IPAddress(bytes);
        return true;
    }

    private static bool IsAny(IPAddress a) => a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);

    private static bool Same(IPAddress a, IPAddress b, bool wildcardMatches)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
        return a.Equals(b) || (wildcardMatches && IsAny(a));
    }

    // ---------------------------------------------------------------- inode → pid

    /// <summary>
    /// Scans /proc/PID/fd for "socket:[inode]". Processes that owned sockets recently are searched first and
    /// every socket seen on the way is remembered, so a burst of new connections from one app costs one scan.
    /// </summary>
    public int PidForInode(ulong inode)
    {
        lock (_gate)
        {
            if (_inodes.TryGetValue(inode, out var hit) && Directory.Exists(Path.Combine(_proc, hit.Pid.ToString(CultureInfo.InvariantCulture))))
                return Touch(hit.Pid);

            var order = new List<int>(_recent);
            var seen = new HashSet<int>(order);
            foreach (var pid in AllPids().OrderByDescending(p => p))   // newest processes next
                if (seen.Add(pid)) order.Add(pid);

            foreach (var pid in order)
            {
                if (ScanProcess(pid, inode)) return Touch(pid);
            }
            return 0;
        }
    }

    private bool ScanProcess(int pid, ulong wanted)
    {
        var found = false;
        var now = DateTime.UtcNow;
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(_proc, pid.ToString(CultureInfo.InvariantCulture), "fd")))
            {
                string? target;
                try { target = new FileInfo(fd).LinkTarget; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (target is null || !target.StartsWith("socket:[", StringComparison.Ordinal)) continue;
                if (!ulong.TryParse(target.AsSpan(8, target.Length - 9), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ino)) continue;
                _inodes[ino] = (pid, now);
                if (ino == wanted) found = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* exited, or another user's process */ }
        if (_inodes.Count > 50_000) Prune(now);
        return found;
    }

    private void Prune(DateTime now)
    {
        foreach (var (k, v) in _inodes.ToList())
            if (now - v.At > TimeSpan.FromMinutes(2)) _inodes.Remove(k);
    }

    private int Touch(int pid)
    {
        _recent.Remove(pid);
        _recent.AddFirst(pid);
        while (_recent.Count > 32) _recent.RemoveLast();
        return pid;
    }

    private IEnumerable<int> AllPids()
    {
        foreach (var d in Directory.EnumerateDirectories(_proc))
            if (int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                yield return pid;
    }
}
