using Sluice.Core.Dns;

namespace Sluice.Tests;

/// <summary>
/// Where capture keeps its fake-address mappings (#115): always an absolute path in the user's settings folder, also on a
/// Linux account whose ~/.config doesn't exist yet. Changes an environment variable, so never in parallel with others.
/// </summary>
[Collection(nameof(PlatformServicesTests))]
public class FakeIpStatePathTests
{
    [Fact]
    public void The_state_file_is_an_absolute_path_in_the_Sluice_folder()
    {
        var path = FakeIpPool.DefaultStatePath;
        Assert.True(Path.IsPathRooted(path), path);
        Assert.Equal("fake-dns.json", Path.GetFileName(path));
        Assert.Equal("Sluice", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void A_missing_config_folder_still_gives_an_absolute_path()
    {
        if (!OperatingSystem.IsLinux()) return;   // Windows always has %APPDATA%
        var missing = Path.Combine(Path.GetTempPath(), "sluice-no-config-" + Guid.NewGuid().ToString("N"), ".config");
        var before = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", missing);
            Assert.Equal(Path.Combine(missing, "Sluice", "fake-dns.json"), FakeIpPool.DefaultStatePath);
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", before); }
    }
}
