using Sluice.Core.Engine;

namespace Sluice.Tests;

/// <summary>
/// A failing log file (#226): disk full, the folder deleted or denied. Nothing may throw out of
/// <see cref="ConnectionTracker.Write"/> (it runs on timer callbacks, where an exception ends the process); the file log
/// reports the failure once, the other log handlers keep getting every line, and the file is tried again after
/// <see cref="FileLog.RetryAfter"/>.
/// </summary>
public sealed class FileLogFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sluice-logfail-{Guid.NewGuid():N}");
    private DateTime _now = new(2026, 11, 24, 10, 0, 0);
    private readonly ConnectionTracker _tracker = new();
    private readonly List<(LogLevel Level, string Message)> _screen = [];

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.Exists(_dir) ? Directory.GetFiles(_dir) : [])
                File.SetAttributes(f, FileAttributes.Normal);
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            else if (File.Exists(_dir)) File.Delete(_dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A file log, then a second handler standing in for the on-screen log (subscribed after it).</summary>
    private FileLog Open()
    {
        var log = new FileLog(_tracker, _dir, LogLevel.Normal) { Clock = () => _now };
        _tracker.Log += (level, message) => { lock (_screen) _screen.Add((level, message)); };
        return log;
    }

    private string[] Errors() => _screen.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToArray();

    /// <summary>The current file's lines, read while the log still has it open.</summary>
    private static string[] FileLines(FileLog log)
    {
        using var stream = new FileStream(log.CurrentFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    /// <summary>Puts a file where the log folder was, so the folder can't be created or opened.</summary>
    private void ReplaceFolderWithFile()
    {
        Directory.Delete(_dir, recursive: true);
        File.WriteAllText(_dir, "not a folder");
    }

    [Fact]
    public void FolderDeleted_BeforeTheFirstLine_IsCreatedAgain()
    {
        using var log = Open();
        Directory.Delete(_dir, recursive: true);
        _tracker.Write(LogLevel.Normal, "hello");
        Assert.False(log.Failing);
        Assert.Empty(Errors());
        Assert.Contains(FileLines(log), l => l.EndsWith("hello"));
    }

    [Fact]
    public void FolderUnusable_ReportsOnce_KeepsTheScreenLog_AndRecovers()
    {
        using var log = Open();
        ReplaceFolderWithFile();

        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 5; i++) _tracker.Write(LogLevel.Normal, $"line {i}");
        });
        Assert.Null(ex);
        Assert.True(log.Failing);
        var error = Assert.Single(Errors());
        Assert.StartsWith("Log file stopped:", error);
        for (var i = 0; i < 5; i++) Assert.Contains(_screen, e => e.Message == $"line {i}");

        // Still failing at the next try: no second report.
        _now += FileLog.RetryAfter;
        _tracker.Write(LogLevel.Normal, "still broken");
        Assert.Single(Errors());
        Assert.True(log.Failing);

        // The folder can be created again: the next try writes the file and says lines are missing.
        File.Delete(_dir);
        _tracker.Write(LogLevel.Normal, "too early");          // before RetryAfter since the last try: skipped
        Assert.True(log.Failing);
        _now += FileLog.RetryAfter;
        _tracker.Write(LogLevel.Normal, "back");
        Assert.False(log.Failing);
        Assert.Single(Errors());
        Assert.Contains(_screen, e => e.Level == LogLevel.Normal && e.Message.StartsWith("Log file is written again"));
        var lines = FileLines(log);
        Assert.Contains(lines, l => l.Contains("Log file is written again"));
        Assert.Contains(lines, l => l.EndsWith("back"));
        Assert.DoesNotContain(lines, l => l.EndsWith("too early"));
    }

    [Fact]
    public void FullDisk_WhileWriting_ReportsOnce_AndRecovers()
    {
        using var log = Open();
        var disk = new FullDiskStream();
        log.OpenFile = _ => disk;
        _tracker.Write(LogLevel.Normal, "fits");
        Assert.False(log.Failing);

        disk.Full = true;
        var ex = Record.Exception(() =>
        {
            _tracker.Write(LogLevel.Normal, "no room");
            _tracker.Write(LogLevel.Error, "no room either");
        });
        Assert.Null(ex);
        Assert.True(log.Failing);
        var error = Assert.Single(Errors(), e => e.StartsWith("Log file stopped:"));
        Assert.Contains("not enough space", error);
        Assert.Contains(_screen, e => e.Message == "no room");
        Assert.Contains(_screen, e => e.Message == "no room either");

        log.OpenFile = path => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _now += FileLog.RetryAfter;
        _tracker.Write(LogLevel.Normal, "room again");
        Assert.False(log.Failing);
        Assert.Contains(FileLines(log), l => l.EndsWith("room again"));
    }

    [Fact]
    public void ReadOnlyFile_ReportsOnce_AndDoesNotThrow()
    {
        using var log = Open();
        var file = Path.Combine(_dir, $"Sluice-{DateOnly.FromDateTime(_now):yyyy-MM-dd}.log");
        File.WriteAllText(file, "");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        if (CanWrite(file)) return;                             // root on Linux ignores the permission

        var ex = Record.Exception(() =>
        {
            _tracker.Write(LogLevel.Normal, "one");
            _tracker.Write(LogLevel.Normal, "two");
        });
        Assert.Null(ex);
        Assert.True(log.Failing);
        Assert.Single(Errors());
        Assert.Contains(_screen, e => e.Message == "two");
    }

    [Fact]
    public void Dispose_AfterAFailedWrite_DoesNotThrow()
    {
        var log = Open();
        var disk = new FullDiskStream();
        log.OpenFile = _ => disk;
        _tracker.Write(LogLevel.Normal, "fits");
        disk.FailFlush = true;                                  // the writer is open: its final flush fails
        Assert.Null(Record.Exception(log.Dispose));
    }

    private static bool CanWrite(string file)
    {
        try { using var _ = new FileStream(file, FileMode.Append, FileAccess.Write); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>A stream that throws like a full disk once <see cref="Full"/> is set.</summary>
    private sealed class FullDiskStream : MemoryStream
    {
        public bool Full { get; set; }
        public bool FailFlush { get; set; }

        private static IOException NoSpace() => new("There is not enough space on the disk.");

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Full) throw NoSpace();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Full) throw NoSpace();
            base.Write(buffer);
        }

        public override void Flush()
        {
            if (FailFlush) throw NoSpace();
            base.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            // Stays usable after the log closes it, like a file on the same full disk would be opened again.
        }
    }
}
