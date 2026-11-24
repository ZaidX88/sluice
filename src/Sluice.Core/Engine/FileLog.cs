using System.Globalization;
using System.Text;

namespace Sluice.Core.Engine;

/// <summary>
/// Writes the tracker's log to a daily file ("Sluice-yyyy-MM-dd.log") in a directory,
/// with its own level, independent of the on-screen log. Each file starts with <see cref="SluiceVersion.Banner"/>. Keeps the last <see cref="RetainDays"/> days of files.
/// A file that can't be opened or written (disk full, folder deleted or denied) never throws into the tracker: the log
/// reports it once, stops writing the file and tries again every <see cref="RetryAfter"/> (issue #226).
/// </summary>
public sealed class FileLog : IDisposable
{
    private readonly ConnectionTracker _tracker;
    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public LogLevel Level { get; set; }
    public string? CurrentFile { get; private set; }

    /// <summary>
    /// How many days of log files to keep, today included (default 7); 0 keeps everything. Applied when the log opens
    /// its first file and whenever it rolls over to a new day: older <c>Sluice-yyyy-MM-dd.log</c> files in the folder
    /// are deleted. Other files and the current file are never touched.
    /// </summary>
    public int RetainDays { get; set; } = DefaultRetainDays;

    public const int DefaultRetainDays = 7;

    /// <summary>The local clock (tests replace it).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public FileLog(ConnectionTracker tracker, string directory, LogLevel level)
    {
        _tracker = tracker;
        _directory = directory;
        Level = level;
        Directory.CreateDirectory(directory);
        // The tracker only emits messages up to its own level; make sure ours are included.
        if (tracker.Level < level) tracker.Level = level;
        tracker.Log += OnLog;
    }

    private void OnLog(LogLevel level, string message)
    {
        if (level > Level) return;
        var now = Clock();
        string? report = null;
        var reportLevel = LogLevel.Error;
        lock (_gate)
        {
            // After a failure, wait before trying again; this also skips the failure report itself (issue #226).
            if (_failedAt is not null && now < _retryAt) return;
            try
            {
                var today = DateOnly.FromDateTime(now);
                if (_writer is null || today != _day)
                {
                    CloseWriter();
                    _day = today;
                    CurrentFile = Path.Combine(_directory, $"Sluice-{today:yyyy-MM-dd}.log");
                    Directory.CreateDirectory(_directory);         // the folder may have been deleted since
                    _writer = new StreamWriter(OpenFile(CurrentFile), new UTF8Encoding(false)) { AutoFlush = true };
                    // Every file, and every start appending to one, begins with the build that wrote it (issue #91).
                    _writer.WriteLine($"[{now:yyyy-MM-dd HH:mm:ss.fff}] {Tag(LogLevel.Normal)} {SluiceVersion.Banner}");
                    DeleteOldFiles(today);
                }
                if (_failedAt is { } since)
                {
                    _failedAt = null;
                    report = $"Log file is written again ({CurrentFile}); lines from {since:HH:mm:ss} to {now:HH:mm:ss} are missing from it.";
                    reportLevel = LogLevel.Normal;
                }
                _writer.WriteLine($"[{now:yyyy-MM-dd HH:mm:ss.fff}] {Tag(level)} {message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or ArgumentException or System.Security.SecurityException)
            {
                // Disk full, folder deleted or denied: stop writing the file, keep the rest of the log working,
                // and try to open the file again after RetryAfter.
                CloseWriter();
                _retryAt = now + RetryAfter;
                if (_failedAt is null)
                {
                    _failedAt = now;
                    report = $"Log file stopped: {ex.Message} Trying again every {RetryAfter.TotalSeconds:0} seconds; the log on screen is not affected.";
                    reportLevel = LogLevel.Error;
                }
                else
                {
                    report = null;                                 // already reported: once per outage
                }
            }
        }
        // Outside the lock: the tracker calls every handler, this one included (it returns early while failing).
        if (report is not null) _tracker.Write(reportLevel, report);
    }

    /// <summary>
    /// How long the log waits after a failed open or write before trying the file again (issue #226).
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>When the current outage started (null while the file is written).</summary>
    private DateTime? _failedAt;
    private DateTime _retryAt;

    /// <summary>True while the file can't be written (after a failure, until a retry succeeds).</summary>
    public bool Failing
    {
        get { lock (_gate) return _failedAt is not null; }
    }

    /// <summary>Opens the day's file for appending (tests replace it to simulate a full disk).</summary>
    internal Func<string, Stream> OpenFile { get; set; } =
        path => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

    /// <summary>Disposes the writer, ignoring errors (its final flush fails again on a full disk).</summary>
    private void CloseWriter()
    {
        var writer = _writer;
        _writer = null;
        if (writer is null) return;
        try { writer.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    /// <summary>Deletes this log's daily files older than <see cref="RetainDays"/> days before <paramref name="today"/>.</summary>
    private void DeleteOldFiles(DateOnly today)
    {
        var keep = RetainDays;
        if (keep <= 0) return;
        var oldest = today.AddDays(1 - keep);              // e.g. 7 days: today and the 6 days before it
        string[] files;
        try { files = Directory.GetFiles(_directory, "Sluice-*.log"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var file in files)
        {
            if (!TryParseDay(Path.GetFileName(file), out var day) || day >= oldest) continue;
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(CurrentFile!), StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use (another Sluice) or read-only: next time */ }
        }
    }

    /// <summary>"Sluice-2026-11-19.log" → 2026-11-19; any other name → false.</summary>
    internal static bool TryParseDay(string fileName, out DateOnly day) =>
        DateOnly.TryParseExact(fileName, "'Sluice-'yyyy-MM-dd'.log'", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static string Tag(LogLevel l) => l switch
    {
        LogLevel.Error => "ERROR  ",
        LogLevel.Normal => "INFO   ",
        LogLevel.Verbose => "VERBOSE",
        _ => "DEBUG  ",
    };

    public void Dispose()
    {
        _tracker.Log -= OnLog;
        lock (_gate) CloseWriter();
    }
}
