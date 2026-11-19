using Sluice.Core.Engine;

namespace Sluice.Tests;

/// <summary>
/// Log retention (#39, part of #7): the file log keeps its last <see cref="FileLog.RetainDays"/> days of daily files,
/// deleting older ones when it opens its first file and when it rolls over to a new day; other files and the current
/// file are never touched.
/// </summary>
public sealed class FileLogRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sluice-log-{Guid.NewGuid():N}");
    private DateTime _now = new(2026, 11, 19, 10, 0, 0);

    public FileLogRetentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Day(int daysAgo) => $"Sluice-{DateOnly.FromDateTime(_now).AddDays(-daysAgo):yyyy-MM-dd}.log";

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "old\n");

    private bool Exists(string name) => File.Exists(Path.Combine(_dir, name));

    private (ConnectionTracker Tracker, FileLog Log) Open(int? retainDays = null)
    {
        var tracker = new ConnectionTracker();
        var log = new FileLog(tracker, _dir, LogLevel.Normal) { Clock = () => _now };
        if (retainDays is { } d) log.RetainDays = d;
        return (tracker, log);
    }

    [Fact]
    public void Default_KeepsSevenDays_TodayIncluded()
    {
        for (var i = 1; i <= 10; i++) Touch(Day(i));
        var (tracker, log) = Open();
        using (log)
        {
            Assert.Equal(7, log.RetainDays);
            tracker.Write(LogLevel.Normal, "started");
        }
        Assert.True(Exists(Day(0)));
        for (var i = 1; i <= 6; i++) Assert.True(Exists(Day(i)), Day(i));
        for (var i = 7; i <= 10; i++) Assert.False(Exists(Day(i)), Day(i));
    }

    [Fact]
    public void OtherFiles_AreNeverDeleted()
    {
        string[] others =
        [
            "Sluice-2020-01-01.txt",      // not .log
            "Sluice-old.log",             // not a date
            "Sluice-2020-01-01.log.bak",
            "sluice-diag.log",
            "notes.log",
            "Sluice-2020-13-01.log",      // not a real date
        ];
        foreach (var f in others) Touch(f);
        var (tracker, log) = Open(retainDays: 1);
        using (log) tracker.Write(LogLevel.Normal, "started");
        foreach (var f in others) Assert.True(Exists(f), f);
    }

    [Fact]
    public void Zero_KeepsEverything()
    {
        Touch(Day(400));
        var (tracker, log) = Open(retainDays: 0);
        using (log) tracker.Write(LogLevel.Normal, "started");
        Assert.True(Exists(Day(400)));
    }

    [Fact]
    public void RetentionSetAfterCreation_IsUsedForTheFirstFile()
    {
        Touch(Day(20));
        var (tracker, log) = Open(retainDays: 30);         // the app sets it after constructing the log
        using (log) tracker.Write(LogLevel.Normal, "started");
        Assert.True(Exists(Day(20)));
    }

    [Fact]
    public void RollingOverToANewDay_DeletesTheDayThatFellOut()
    {
        var (tracker, log) = Open(retainDays: 2);
        using (log)
        {
            tracker.Write(LogLevel.Normal, "day 1");
            var first = log.CurrentFile!;
            _now = _now.AddDays(1);
            tracker.Write(LogLevel.Normal, "day 2");
            Assert.True(File.Exists(first));                // yesterday is kept with 2 days
            _now = _now.AddDays(1);
            tracker.Write(LogLevel.Normal, "day 3");
            Assert.False(File.Exists(first));               // now 2 days back: deleted
            Assert.True(File.Exists(log.CurrentFile!));
        }
    }

    [Fact]
    public void CurrentFile_IsNeverDeleted_EvenWithOneDay()
    {
        var (tracker, log) = Open(retainDays: 1);
        string current;
        using (log)
        {
            tracker.Write(LogLevel.Normal, "one");
            tracker.Write(LogLevel.Normal, "two");
            current = log.CurrentFile!;
        }
        Assert.Equal(2, File.ReadAllLines(current).Length);
    }

    [Theory]
    [InlineData("Sluice-2026-11-19.log", true)]
    [InlineData("Sluice-2026-1-19.log", false)]
    [InlineData("Sluice-2026-11-19.log.1", false)]
    [InlineData("xSluice-2026-11-19.log", false)]
    public void FileNames_AreParsedExactly(string name, bool ok) =>
        Assert.Equal(ok, FileLog.TryParseDay(name, out _));
}
