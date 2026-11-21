using Sluice.Platform.Linux;

namespace Sluice.Tests;

/// <summary>Notifications (#178): Sluice's own icon when install.sh installed it, the theme's VPN icon otherwise.</summary>
public sealed class LinuxNotifyTests
{
    [Fact]
    public void An_installed_icon_is_used_without_tying_the_notification_to_the_desktop_entry()
    {
        Assert.Equal(
            ["--app-name=Sluice", "--urgency=normal", "--icon=sluice", "Sluice is still running", "Rules keep applying."],
            LinuxPlatform.NotifyArguments("Sluice is still running", "Rules keep applying.", isError: false, iconInstalled: true));
    }

    [Fact]
    public void Without_the_icon_the_theme_icon_is_the_fallback()
    {
        Assert.Equal(
            ["--app-name=Sluice", "--urgency=normal", "--icon=network-vpn", "Title", "Message"],
            LinuxPlatform.NotifyArguments("Title", "Message", isError: false, iconInstalled: false));
    }

    [Theory]
    [InlineData(true, "--urgency=critical")]
    [InlineData(false, "--urgency=normal")]
    public void Errors_are_critical(bool isError, string urgency)
    {
        Assert.Equal(urgency, LinuxPlatform.NotifyArguments("t", "m", isError, iconInstalled: true)[1]);
    }

    [Fact]
    public void The_checked_path_and_name_match_what_install_sh_installs()
    {
        var repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "Sluice.slnx"))) repo = Path.GetDirectoryName(repo);
        Assert.NotNull(repo);
        var install = File.ReadAllText(Path.Combine(repo, "packaging", "linux", "install.sh"));
        Assert.Contains(LinuxPlatform.InstalledIconPath, install);
        Assert.Contains("/usr/share/applications/sluice.desktop", install);
        Assert.Contains("Icon=sluice", File.ReadAllLines(Path.Combine(repo, "packaging", "linux", "sluice.desktop")));
    }
}
