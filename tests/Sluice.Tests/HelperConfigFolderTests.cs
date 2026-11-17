using Sluice.Core.Tunnels;

namespace Sluice.Tests;

/// <summary>Where sing-box configs are written, and that deleting the folder doesn't break the next tunnel start.</summary>
public class HelperConfigFolderTests
{
    [Fact]
    public void Windows_uses_local_app_data_not_temp()
    {
        var dir = HelperConfigFolder.For(windows: true, @"C:\Users\u\AppData\Local", null, null, @"C:\Users\u");
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Local", "Sluice", "helpers"), dir);
    }

    [Fact]
    public void Linux_prefers_the_runtime_folder_then_state_home()
    {
        var runtime = Directory.CreateTempSubdirectory("xdg-runtime").FullName;
        try
        {
            Assert.Equal(Path.Combine(runtime, "sluice"), HelperConfigFolder.For(false, "/home/u/.local/share", runtime, null, "/home/u"));
            Assert.Equal(Path.Combine("/home/u", ".local", "state", "sluice", "helpers"),
                HelperConfigFolder.For(false, "/home/u/.local/share", null, null, "/home/u"));
            Assert.Equal(Path.Combine("/srv/state", "sluice", "helpers"),
                HelperConfigFolder.For(false, "/home/u/.local/share", "/no/such/runtime/dir", "/srv/state", "/home/u"));
            // A relative XDG value is ignored, as the spec says.
            Assert.Equal(Path.Combine("/home/u", ".local", "state", "sluice", "helpers"),
                HelperConfigFolder.For(false, "/home/u/.local/share", "relative", "relative", "/home/u"));
        }
        finally
        {
            Directory.Delete(runtime);
        }
    }

    [Fact]
    public void Deleted_folder_is_recreated_on_the_next_start()
    {
        var root = Directory.CreateTempSubdirectory("sluice-helpers-test").FullName;
        var dir = Path.Combine(root, "Sluice", "helpers");
        try
        {
            Assert.Equal(dir, HelperConfigFolder.Ensure(dir));
            Directory.Delete(root, recursive: true);           // the user empties their temp / app data folder

            HelperConfigFolder.Ensure(dir);
            File.WriteAllText(Path.Combine(dir, "100-x.json"), "{}");   // the next config can be written

            Assert.True(File.Exists(Path.Combine(dir, "100-x.json")));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Leftover_configs_are_removed_and_fresh_ones_kept()
    {
        var dir = Directory.CreateTempSubdirectory("sluice-helpers-test").FullName;
        try
        {
            var old = Path.Combine(dir, "101-old.json");
            var fresh = Path.Combine(dir, "102-new.json");
            File.WriteAllText(old, "{\"password\":\"x\"}");
            File.WriteAllText(fresh, "{}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow - HelperConfigFolder.LeftoverAge - TimeSpan.FromMinutes(1));

            HelperConfigFolder.Ensure(dir);

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
