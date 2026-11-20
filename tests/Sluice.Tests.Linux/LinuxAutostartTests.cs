using Sluice.Platform.Linux;

namespace Sluice.Tests;

/// <summary>Start at login (#69): the XDG autostart entry, its path, and Exec quoting for unusual install folders.</summary>
public sealed class LinuxAutostartTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sluice-autostart-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_entry_goes_to_the_config_folder()
    {
        Assert.Equal("/home/alice/.config/autostart/sluice.desktop", LinuxAutostart.EntryPathFor(null, "/home/alice"));
        Assert.Equal("/home/alice/.config/autostart/sluice.desktop", LinuxAutostart.EntryPathFor("relative/dir", "/home/alice"));
        Assert.Equal("/srv/cfg/autostart/sluice.desktop", LinuxAutostart.EntryPathFor("/srv/cfg", "/home/alice"));
        Assert.False(new LinuxAutostart(LinuxAutostart.EntryPathFor(null, ""), "/opt/sluice/Sluice.Desktop").Supported);   // no home
        Assert.False(new LinuxAutostart("/home/alice/.config/autostart/sluice.desktop", null).Supported);
    }

    [Theory]
    [InlineData("/opt/sluice/Sluice.Desktop")]
    [InlineData("/home/alice/My Apps/sluice/Sluice.Desktop")]
    [InlineData("/home/alice/100% \"odd\" $dir `x` back\\slash/Sluice.Desktop")]
    public void An_entry_starts_the_binary_it_was_written_for(string binary)
    {
        var entry = LinuxAutostart.Entry(binary);
        Assert.Contains("[Desktop Entry]\nType=Application\n", entry);
        Assert.True(LinuxAutostart.StartsBinary(entry, binary));
        Assert.False(LinuxAutostart.StartsBinary(entry, "/opt/other/Sluice.Desktop"));
    }

    [Fact]
    public void Exec_arguments_are_quoted_only_when_needed()
    {
        Assert.Equal("/opt/sluice/Sluice.Desktop", LinuxAutostart.QuoteExecArgument("/opt/sluice/Sluice.Desktop"));
        Assert.Equal("\"/a b/S\"", LinuxAutostart.QuoteExecArgument("/a b/S"));
        Assert.Equal("\"/a\\$b/\\\"q\\\"\"", LinuxAutostart.QuoteExecArgument("/a$b/\"q\""));
        Assert.Equal("/100%%/S", LinuxAutostart.QuoteExecArgument("/100%/S"));
        Assert.Equal("/opt/sluice/Sluice.Desktop", LinuxAutostart.FirstExecArgument("/opt/sluice/Sluice.Desktop --intercept"));
        Assert.Equal("/a b/S", LinuxAutostart.FirstExecArgument("\"/a b/S\" %U"));
        Assert.Null(LinuxAutostart.FirstExecArgument("\"/a b/S"));
    }

    [Theory]
    [InlineData("Hidden=true")]
    [InlineData("X-GNOME-Autostart-enabled=false")]
    public void An_entry_switched_off_by_the_desktop_is_off(string line)
    {
        var entry = LinuxAutostart.Entry("/opt/sluice/Sluice.Desktop") + line + "\n";
        Assert.False(LinuxAutostart.StartsBinary(entry, "/opt/sluice/Sluice.Desktop"));
    }

    [Fact]
    public void Keys_of_other_groups_are_ignored()
    {
        const string entry = "[Desktop Entry]\nExec=/opt/sluice/Sluice.Desktop\n\n[Desktop Action x]\nHidden=true\nExec=/other\n";
        Assert.True(LinuxAutostart.StartsBinary(entry, "/opt/sluice/Sluice.Desktop"));
    }

    [Fact]
    public void Turning_it_on_writes_the_entry_and_off_removes_it()
    {
        var path = LinuxAutostart.EntryPathFor(null, _home);
        var mine = new LinuxAutostart(path, "/opt/sluice/Sluice.Desktop");
        Assert.True(mine.Supported);
        Assert.False(mine.Enabled);

        mine.Enabled = true;                                        // creates ~/.config/autostart too
        Assert.True(mine.Enabled);
        Assert.True(File.Exists(path));

        var other = new LinuxAutostart(path, "/opt/other/Sluice.Desktop");
        Assert.False(other.Enabled);                                // the entry starts another copy
        other.Enabled = true;
        Assert.True(other.Enabled);
        Assert.False(mine.Enabled);

        mine.Enabled = false;                                       // off removes the one entry
        Assert.False(File.Exists(path));
        Assert.False(other.Enabled);
        mine.Enabled = false;                                       // already off: nothing to do
    }
}
