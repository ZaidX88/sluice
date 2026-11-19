using System.Globalization;
using Sluice.Core.Tunnels;

namespace Sluice.Platform.Linux;

/// <summary>
/// Makes the helpers Sluice starts (sing-box) end with Sluice: the Linux counterpart of the kill-on-close job object
/// on Windows (#57). Linux has nothing like it for a process that is already running (<c>PR_SET_PDEATHSIG</c> can only
/// be set by the helper itself, before it runs sing-box, and Core starts the helper), so:
/// <list type="bullet">
/// <item>When Sluice exits (a signal, an unhandled exception, or an exit without stopping its tunnels), the helpers it
/// tied get SIGTERM (<see cref="EndAll"/>).</item>
/// <item>Every tied helper is also written to a file in the helper folder, named after this Sluice process. A Sluice
/// killed with SIGKILL, or one that crashed in native code, runs none of that: the next Sluice ends the helpers it left
/// (<see cref="EndLeftovers"/>).</item>
/// </list>
/// A process is known by its pid and its start time (<c>/proc/&lt;pid&gt;/stat</c>), and a file by the boot it was
/// written in, so a reused pid is never signalled.
/// </summary>
internal sealed class HelperTies
{
    public const string Extension = ".helpers";

    /// <summary>A process: its pid and start time (clock ticks after boot).</summary>
    public readonly record struct Proc(int Pid, ulong StartTime);

    private readonly string _folder;
    private readonly Proc _self;
    private readonly string _bootId;
    private readonly Func<int, ulong?> _startTime;
    private readonly Func<int, bool> _terminate;
    private readonly List<Proc> _tied = new();
    private readonly object _lock = new();
    private bool _exiting;

    /// <param name="folder">Where the files go (the helper folder, owner-only).</param>
    /// <param name="self">This Sluice process.</param>
    /// <param name="bootId">This boot (<c>/proc/sys/kernel/random/boot_id</c>): a file from an earlier boot names no live process.</param>
    /// <param name="startTime">A process's start time, null when there is no such process.</param>
    /// <param name="terminate">Sends a process SIGTERM; true when it was sent.</param>
    public HelperTies(string folder, Proc self, string bootId, Func<int, ulong?> startTime, Func<int, bool> terminate)
    {
        _folder = folder;
        _self = self;
        _bootId = bootId;
        _startTime = startTime;
        _terminate = terminate;
    }

    /// <summary>For this Sluice process, with the real <c>/proc</c> and <c>kill</c>. Null when <c>/proc</c> can't be read.</summary>
    public static HelperTies? ForThisProcess()
    {
        try
        {
            var pid = Environment.ProcessId;
            if (ReadStartTime(pid) is not { } start) return null;
            var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            return new HelperTies(HelperConfigFolder.Default, new Proc(pid, start), boot, p => ReadStartTime(p),
                p => Native.Libc.kill(p, Native.Libc.SIGTERM) == 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>This process's file.</summary>
    public string FilePath => Path.Combine(_folder, string.Create(CultureInfo.InvariantCulture, $"{_self.Pid}-{_self.StartTime}{Extension}"));

    /// <summary>The helpers tied and not yet ended or gone.</summary>
    public IReadOnlyList<Proc> Tied { get { lock (_lock) return _tied.ToArray(); } }

    /// <summary>Ties the helper <paramref name="pid"/> to this process. Nothing happens when it is already gone. Once
    /// Sluice is exiting (<see cref="EndAll"/> ran), the helper gets SIGTERM at once: while Sluice shuts down, a tunnel
    /// whose helper just ended may start a new one.</summary>
    public void Tie(int pid)
    {
        if (_startTime(pid) is not { } start) return;
        var helper = new Proc(pid, start);
        lock (_lock)
        {
            if (!_exiting)
            {
                _tied.Add(helper);
                Save();
                return;
            }
        }
        Terminate(helper);
    }

    /// <summary>The helper <paramref name="pid"/> ended on its own (or Sluice stopped it): forget it.</summary>
    public void Untie(int pid)
    {
        lock (_lock)
        {
            if (_tied.RemoveAll(p => p.Pid == pid) > 0) Save();
        }
    }

    /// <summary>Sluice is exiting: SIGTERM to every tied helper still running, and to every helper tied from now on.
    /// Returns how many got it. Never throws.</summary>
    public int EndAll()
    {
        try
        {
            Proc[] ending;
            lock (_lock)
            {
                _exiting = true;
                ending = _tied.ToArray();
                _tied.Clear();
                Save();
            }
            return ending.Count(Terminate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Ends the helpers left by Sluice processes that are gone (killed with SIGKILL, or crashed) and removes their
    /// files. Files of Sluice processes still running are left alone. Returns how many helpers got SIGTERM. Never throws.
    /// </summary>
    public int EndLeftovers()
    {
        var ended = 0;
        try
        {
            if (!Directory.Exists(_folder)) return 0;
            foreach (var file in Directory.EnumerateFiles(_folder, "*" + Extension))
            {
                try
                {
                    if (string.Equals(file, FilePath, StringComparison.Ordinal)) continue;
                    if (ParseOwner(Path.GetFileName(file)) is not { } owner) continue;   // not a file of ours
                    var (boot, helpers) = Parse(File.ReadAllText(file));
                    var sameBoot = string.Equals(boot, _bootId, StringComparison.Ordinal);
                    if (sameBoot && IsRunning(owner)) continue;                          // another Sluice, still running
                    if (sameBoot) ended += helpers.Count(Terminate);
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next file */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        return ended;
    }

    private bool IsRunning(Proc p) => _startTime(p.Pid) == p.StartTime;

    private bool Terminate(Proc p) => IsRunning(p) && _terminate(p.Pid);

    /// <summary>Writes the file (or removes it when nothing is tied). Called under the lock.</summary>
    private void Save()
    {
        try
        {
            if (_tied.Count == 0)
            {
                File.Delete(FilePath);
                return;
            }
            HelperConfigFolder.Ensure(_folder);
            var text = Format(_bootId, _tied);
            var temp = FilePath + ".new";
            File.WriteAllText(temp, text);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* only the crash clean-up is lost */ }
    }

    // ---------- the file format: "boot <id>", then one "<pid> <start time>" line per helper ----------

    internal static string Format(string bootId, IEnumerable<Proc> helpers) =>
        string.Concat(helpers.Select(h => string.Create(CultureInfo.InvariantCulture, $"{h.Pid} {h.StartTime}\n")).Prepend($"boot {bootId}\n"));

    internal static (string? BootId, List<Proc> Helpers) Parse(string text)
    {
        string? boot = null;
        var helpers = new List<Proc>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("boot ", StringComparison.Ordinal)) { boot = line[5..].Trim(); continue; }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
                && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
                helpers.Add(new Proc(pid, start));
        }
        return (boot, helpers);
    }

    /// <summary>The Sluice process a file belongs to, from its name ("&lt;pid&gt;-&lt;start time&gt;.helpers").</summary>
    internal static Proc? ParseOwner(string fileName)
    {
        if (!fileName.EndsWith(Extension, StringComparison.Ordinal)) return null;
        var parts = fileName[..^Extension.Length].Split('-');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
            && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            ? new Proc(pid, start) : null;
    }

    /// <summary>A process's start time from <c>/proc/&lt;pid&gt;/stat</c>; null when there is no such process.</summary>
    public static ulong? ReadStartTime(int pid, string procRoot = "/proc")
    {
        try { return ParseStartTime(File.ReadAllText(Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture), "stat"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Field 22 of <c>/proc/&lt;pid&gt;/stat</c>. The name (field 2) is in parentheses and may hold spaces and
    /// parentheses itself, so fields are counted from the last ')'.</summary>
    internal static ulong? ParseStartTime(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);   // fields[0] is field 3
        return fields.Length > 19 && ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            ? start : null;
    }
}
