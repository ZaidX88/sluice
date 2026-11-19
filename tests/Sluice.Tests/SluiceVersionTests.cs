using Sluice.Core;
using Sluice.Core.Engine;

namespace Sluice.Tests;

/// <summary>One version for every build (#91): set in Directory.Build.props, shown the same way everywhere.</summary>
public class SluiceVersionTests
{
    [Fact]
    public void Text_comes_from_Directory_Build_props()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var start = props.IndexOf("<Version>", StringComparison.Ordinal) + "<Version>".Length;
        var version = props[start..props.IndexOf("</Version>", start, StringComparison.Ordinal)];

        Assert.StartsWith(version, SluiceVersion.Text);
        var meta = SluiceVersion.Text[version.Length..];
        Assert.True(meta.Length == 0 || (meta[0] == '+' && meta.Length <= 8), SluiceVersion.Text);   // "+abc1234" at most
    }

    [Theory]
    [InlineData("1.0.0-rc.1+0123456789abcdef0123456789abcdef01234567", "1.0.0-rc.1+0123456")]
    [InlineData("1.0.0+abc1234", "1.0.0+abc1234")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0+local.build.metadata", "1.0.0+local.build.metadata")]   // not a commit: kept
    public void The_commit_is_shortened_to_seven_characters(string informational, string shown) =>
        Assert.Equal(shown, SluiceVersion.Shorten(informational));

    [Fact]
    public void Banner_names_the_version_and_the_system()
    {
        Assert.StartsWith($"Sluice {SluiceVersion.Text} on ", SluiceVersion.Banner);
        if (OperatingSystem.IsWindows()) Assert.Matches(@" on Windows \d+\.\d+\.\d+$", SluiceVersion.Banner);
    }

    [Fact]
    public void Each_log_file_starts_with_the_banner()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sluice-ver-{Guid.NewGuid():N}");
        try
        {
            var tracker = new ConnectionTracker();
            string file;
            using (var log = new FileLog(tracker, dir, LogLevel.Normal))
            {
                tracker.Write(LogLevel.Normal, "started");
                file = log.CurrentFile!;
            }
            var lines = File.ReadAllLines(file);
            Assert.EndsWith(SluiceVersion.Banner, lines[0]);
            Assert.EndsWith("started", lines[1]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) return d.FullName;
        throw new DirectoryNotFoundException("Directory.Build.props not found above the test output");
    }
}
