using Sluice.Platform.Windows;

namespace Sluice.Tests;

/// <summary>The Start Menu shortcut that names Sluice's notifications (#62): written with the app ID and a clean tooltip.</summary>
public class WindowsStartMenuShortcutTests
{
    [Fact]
    public void A_written_shortcut_has_the_target_the_app_id_and_the_description()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("sluice-shortcut-");
        try
        {
            var lnk = Path.Combine(dir.FullName, "Sluice.lnk");
            var exe = Environment.ProcessPath!;
            StartMenuShortcut.Write(lnk, exe);

            Assert.Equal(exe, StartMenuShortcut.ReadTarget(lnk), ignoreCase: true);
            Assert.Equal(StartMenuShortcut.AppId, StartMenuShortcut.ReadAppId(lnk));
            Assert.Equal(StartMenuShortcut.Description, StartMenuShortcut.ReadDescription(lnk));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void The_description_has_a_real_dash()
    {
        Assert.Equal("Sluice — route applications through tunnels", StartMenuShortcut.Description);
        Assert.DoesNotContain("â", StartMenuShortcut.Description);   // "â", the start of the old mis-encoding
    }
}
