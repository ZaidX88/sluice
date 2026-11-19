using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Sluice.Core.Platform;
using Sluice.Platform.Linux.Native;

namespace Sluice.Platform.Linux;

/// <summary>
/// Which process owns a socket. The socket's inode (and its owner's uid) comes from the kernel's socket diagnostics
/// (<see cref="SockDiag"/>: one netlink request per connection, issue #82); where that isn't available, from
/// /proc/net/{tcp,tcp6,udp,udp6}. /proc/PID/fd links ("socket:[inode]") then map the inode to a process: from a cache,
/// or by scanning the processes of the socket's owner first. Seeing other users' processes needs root (or
/// CAP_SYS_PTRACE); otherwise only this user's apps are found.
/// <para>
/// Safe for several callers at once (capture decisions, the relay, the SOCKS listener): cache hits take no lock, and a
/// scan for one program doesn't hold up lookups for others.
/// </para>
/// </summary>
public sealed class LinuxProcessLookup : IProcessLookup, IDisposable
{
    private readonly ConcurrentDictionary<ulong, (int Pid, DateTime At)> _inodes = new();
    private readonly ConcurrentDictionary<int, long> _recent = new();       // pid → last hit: searched first on a miss
    private readonly ConcurrentDictionary<int, (string? Path, DateTime At)> _paths = new();
    private const int NewestFirst = 6;                                          // see PidForInode
    private readonly string _proc;
    private readonly bool _wantSockDiag;
    private readonly Lazy<SockDiag?> _diag;
    private long _hits;
    private int _logged;

    /// <param name="procRoot">/proc, or a fake tree for tests (then /proc/net is always parsed).</param>
    /// <param name="useSockDiag">Ask the kernel's socket diagnostics first (the default on the real /proc).</param>
    public LinuxProcessLookup(string procRoot = "/proc", bool? useSockDiag = null)
    {
        _proc = procRoot;
        _wantSockDiag = useSockDiag ?? procRoot == "/proc";
        _diag = new Lazy<SockDiag?>(OpenDiag, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Where to report, once, that the kernel's socket diagnostics can't be used (the capture log).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>True when sockets are found through sock_diag rather than /proc/net.</summary>
    public bool UsesSockDiag => _diag.Value is not null;

    private string? _unavailable;

    private SockDiag? OpenDiag()
    {
        if (!_wantSockDiag) return null;
        var d = SockDiag.TryOpen(out var problem);
        if (d is null) _unavailable = $"Socket owners are found through /proc/net, which is slower: {problem}";
        else d.Problem = m => Log?.Invoke(m);                 // pauses after timeouts: rare, at most every 30 s
        return d;
    }

    /// <summary>Logs, once and only where a log is attached, that sock_diag isn't available.</summary>
    private void ReportUnavailable()
    {
        if (_unavailable is { } m && Log is { } log && Interlocked.Exchange(ref _logged, 1) == 0) log(m);
    }

    public int FindTcpOwner(IPEndPoint local, IPEndPoint remote)
    {
        ReportUnavailable();
        if (_diag.Value is { } d && d.TryFindTcp(local, remote, out var s))
            return s.Inode == 0 ? 0 : PidForInode(s.Inode, s.Uid);
        foreach (var file in new[] { "tcp", "tcp6" })
            foreach (var e in ReadTable(Path.Combine(_proc, "net", file)))
                if (e.LocalPort == local.Port && e.RemotePort == remote.Port && Same(e.Local, local.Address) && Same(e.Remote, remote.Address))
                    return e.Inode == 0 ? 0 : PidForInode(e.Inode, e.Uid);
        return 0;
    }

    public int FindUdpOwner(IPEndPoint local)
    {
        ReportUnavailable();
        if (_diag.Value is { } d && d.TryFindUdp(local, out var s))
            return s.Inode == 0 ? 0 : PidForInode(s.Inode, s.Uid);
        Entry? wildcard = null;
        foreach (var file in new[] { "udp", "udp6" })
            foreach (var e in ReadTable(Path.Combine(_proc, "net", file)))
            {
                if (e.LocalPort != local.Port || e.Inode == 0) continue;
                if (Same(e.Local, local.Address)) return PidForInode(e.Inode, e.Uid);
                if (wildcard is null && IsAny(e.Local)) wildcard = e;
            }
        return wildcard is { } w ? PidForInode(w.Inode, w.Uid) : 0;
    }

    public HashSet<int> FindListeners(int port)
    {
        var pids = new HashSet<int>();
        foreach (var file in new[] { "tcp", "tcp6" })
            foreach (var e in ReadTable(Path.Combine(_proc, "net", file)))
                if (e.State == 0x0A && e.LocalPort == port && e.Inode != 0 && PidForInode(e.Inode, e.Uid) is var pid and > 0)
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

    // ---------------------------------------------------------------- /proc/net tables (the fallback)

    public readonly record struct Entry(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int State, ulong Inode, uint? Uid = null);

    /// <summary>Rows of a /proc/net/{tcp,udp}[6] file.</summary>
    public static IEnumerable<Entry> ReadTable(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var line in lines.Skip(1))
            if (TryParseLine(line, out var e)) yield return e;
    }

    /// <summary>"  0: 0100007F:0277 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000  0 12345 …"</summary>
    public static bool TryParseLine(string line, out Entry entry)
    {
        entry = default;
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 10) return false;
        if (!TryEndpoint(f[1], out var la, out var lp) || !TryEndpoint(f[2], out var ra, out var rp)) return false;
        if (!int.TryParse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var st)) return false;
        if (!ulong.TryParse(f[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode)) return false;
        uint? uid = uint.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : null;
        entry = new Entry(la, lp, ra, rp, st, inode, uid);
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
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(w * 4), word);
        }
        addr = new IPAddress(bytes);
        return true;
    }

    private static bool IsAny(IPAddress a) => a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);

    private static bool Same(IPAddress a, IPAddress b)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
        return a.Equals(b);
    }

    // ---------------------------------------------------------------- inode → pid

    /// <summary>
    /// The process holding socket <paramref name="inode"/>. A cached answer needs no lock. On a miss, processes are
    /// scanned for "socket:[inode]" in this order: the socket owner's (<paramref name="uid"/>, from /proc/PID's owner)
    /// few newest processes (a program that just started), those that owned sockets lately, the rest of the owner's
    /// newest first, then all others newest first (processes with file capabilities or setuid show root as /proc/PID's
    /// owner). Every socket seen on the way is cached, so a burst of
    /// new connections from one app costs one scan. Several scans may run at once.
    /// </summary>
    public int PidForInode(ulong inode, uint? uid = null)
    {
        if (_inodes.TryGetValue(inode, out var hit) && Directory.Exists(PidDir(hit.Pid)))
            return Touch(hit.Pid);

        var tried = new HashSet<int>();
        var all = AllPids().OrderByDescending(p => p).ToList();                // newest processes first
        var owners = new Dictionary<int, uint?>();
        uint? Owner(int pid) => owners.TryGetValue(pid, out var o) ? o : owners[pid] = DirOwner(pid);

        // A program that just started is among the owner's newest processes: try a few of those before the recent ones.
        if (uid is { } u)
            foreach (var pid in all.Where(p => Owner(p) == u).Take(NewestFirst))
                if (tried.Add(pid) && ScanProcess(pid, inode)) return Touch(pid);
        foreach (var pid in _recent.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList())
            if (tried.Add(pid) && ScanProcess(pid, inode)) return Touch(pid);
        if (uid is { } owner)
            foreach (var pid in all)
                if (!tried.Contains(pid) && Owner(pid) == owner && tried.Add(pid) && ScanProcess(pid, inode)) return Touch(pid);
        foreach (var pid in all)
            if (tried.Add(pid) && ScanProcess(pid, inode)) return Touch(pid);
        return 0;
    }

    private string PidDir(int pid) => Path.Combine(_proc, pid.ToString(CultureInfo.InvariantCulture));

    /// <summary>The uid owning /proc/PID (statx: one system call, no file read); null if it can't be read.</summary>
    private uint? DirOwner(int pid)
    {
        var buf = new byte[256];
        return Libc.statx(Libc.AT_FDCWD, PidDir(pid), 0, Libc.STATX_UID, buf) == 0 ? BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(20)) : null;
    }

    /// <summary>
    /// Reads /proc/PID/fd with getdents64 and readlinkat (a few system calls per descriptor, no managed objects per
    /// entry: much cheaper than FileInfo.LinkTarget for programs with hundreds of sockets) and caches every
    /// "socket:[inode]" seen. True if <paramref name="wanted"/> was among them.
    /// </summary>
    private bool ScanProcess(int pid, ulong wanted)
    {
        var found = false;
        var now = DateTime.UtcNow;
        var dir = Libc.open(Path.Combine(PidDir(pid), "fd"), Libc.O_RDONLY | Libc.O_DIRECTORY | Libc.O_CLOEXEC);
        if (dir < 0) return false;                                    // exited, or another user's process
        try
        {
            var entries = new byte[16 * 1024];
            var name = new byte[16];
            var link = new byte[64];
            nint n;
            while ((n = Libc.getdents64(dir, entries, entries.Length)) > 0)
            {
                for (var off = 0; off + 19 < n;)
                {
                    var reclen = BinaryPrimitives.ReadUInt16LittleEndian(entries.AsSpan(off + 16));
                    if (reclen == 0) break;
                    var nameStart = off + 19;
                    var nameLen = entries.AsSpan(nameStart, reclen - 19).IndexOf((byte)0);
                    off += reclen;
                    if (nameLen <= 0 || nameLen >= name.Length || entries[nameStart] is < (byte)'0' or > (byte)'9') continue;   // ".", ".."
                    entries.AsSpan(nameStart, nameLen).CopyTo(name);
                    name[nameLen] = 0;
                    var len = (int)Libc.readlinkat(dir, name, link, link.Length);
                    if (TryParseSocketLink(link.AsSpan(0, Math.Max(len, 0)), out var ino))
                    {
                        _inodes[ino] = (pid, now);
                        if (ino == wanted) found = true;
                    }
                }
            }
        }
        finally
        {
            Libc.close(dir);
        }
        if (_inodes.Count > 50_000) Prune(now);
        return found;
    }

    /// <summary>"socket:[12345]" → 12345.</summary>
    internal static bool TryParseSocketLink(ReadOnlySpan<byte> link, out ulong inode)
    {
        inode = 0;
        ReadOnlySpan<byte> prefix = "socket:["u8;
        if (link.Length < prefix.Length + 2 || !link.StartsWith(prefix) || link[^1] != (byte)']') return false;
        foreach (var c in link[prefix.Length..^1])
        {
            if (c is < (byte)'0' or > (byte)'9') return false;
            inode = inode * 10 + (ulong)(c - '0');
        }
        return true;
    }

    private void Prune(DateTime now)
    {
        foreach (var (k, v) in _inodes)
            if (now - v.At > TimeSpan.FromMinutes(2)) _inodes.TryRemove(k, out _);
    }

    /// <summary>Remembers <paramref name="pid"/> as one that owned a socket lately (the 32 latest are kept).</summary>
    private int Touch(int pid)
    {
        _recent[pid] = Interlocked.Increment(ref _hits);
        if (_recent.Count > 48)
            foreach (var old in _recent.OrderBy(kv => kv.Value).Take(_recent.Count - 32).Select(kv => kv.Key).ToList())
                _recent.TryRemove(old, out _);
        return pid;
    }

    private IEnumerable<int> AllPids()
    {
        foreach (var d in Directory.EnumerateDirectories(_proc))
            if (int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                yield return pid;
    }

    public void Dispose()
    {
        if (_diag.IsValueCreated) _diag.Value?.Dispose();
    }
}
