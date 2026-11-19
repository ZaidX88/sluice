using System.Globalization;
using System.Text;

namespace Sluice.Core.Engine;

/// <summary>
/// Writes the tracker's log to a daily file ("Sluice-yyyy-MM-dd.log") in a directory,
/// with its own level, independent of the on-screen log. Keeps the last <see cref="RetainDays"/> days of files.
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
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(now);
            if (_writer is null || today != _day)
            {
                _writer?.Dispose();
                _day = today;
                CurrentFile = Path.Combine(_directory, $"Sluice-{today:yyyy-MM-dd}.log");
                _writer = new StreamWriter(new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                DeleteOldFiles(today);
            }
            _writer.WriteLine($"[{now:yyyy-MM-dd HH:mm:ss.fff}] {Tag(level)} {message}");
        }
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
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
