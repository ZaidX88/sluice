using System.Text;

namespace Sluice.Core.Engine;

/// <summary>
/// Writes the tracker's log to a daily file ("Sluice-yyyy-MM-dd.log") in a directory,
/// with its own level, independent of the on-screen log.
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
        var now = DateTime.Now;
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
            }
            _writer.WriteLine($"[{now:yyyy-MM-dd HH:mm:ss.fff}] {Tag(level)} {message}");
        }
    }

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
