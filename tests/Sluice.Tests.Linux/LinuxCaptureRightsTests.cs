using Sluice.Platform.Linux;
using FileOwner = Sluice.Platform.Linux.LinuxCaptureRights.FileOwner;

namespace Sluice.Tests;

/// <summary>
/// "Grant capture rights" (#69): the launcher next to the app gets the capabilities (and apps lose any), only when it
/// is safe (nothing in its folder or the .NET folder changeable by anyone but root), and reading getcap's answer.
/// </summary>
public sealed class LinuxCaptureRightsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sluice-rights-" + Guid.NewGuid().ToString("N"));

    public LinuxCaptureRightsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly FileOwner RootOnly = new(0, (UnixFileMode)0b111_101_101);   // root, 755

    [Fact]
    public void Only_Sluices_own_binary_in_its_own_folder_is_granted()
    {
        Assert.Equal("/opt/sluice/Sluice.Desktop", LinuxCaptureRights.AppBinary("/opt/sluice/Sluice.Desktop", "/opt/sluice/"));
        Assert.Equal("/opt/sluice/Sluice.Cli", LinuxCaptureRights.AppBinary("/opt/sluice/Sluice.Cli", "/opt/sluice"));
        Assert.Null(LinuxCaptureRights.AppBinary("/usr/lib/dotnet/dotnet", "/opt/sluice/"));            // dotnet Sluice.Desktop.dll
        Assert.Null(LinuxCaptureRights.AppBinary("/opt/other/Sluice.Desktop", "/opt/sluice/"));
        Assert.Null(LinuxCaptureRights.AppBinary("Sluice.Desktop", "/opt/sluice/"));
        Assert.Null(LinuxCaptureRights.AppBinary(null, "/opt/sluice/"));
    }

    [Fact]
    public void The_launcher_next_to_the_app_gets_the_rights()
    {
        var desktop = Path.Combine(_folder, "Sluice.Desktop");
        File.WriteAllText(desktop, "");
        Assert.Null(LinuxCaptureRights.Launcher(desktop));                                    // an older package: none

        var launcher = Path.Combine(_folder, "sluice");
        File.WriteAllText(launcher, "");
        Assert.Equal(launcher, LinuxCaptureRights.Launcher(desktop));
    }

    /// <summary>setcap takes one file per -r and fails on a file without capabilities, so only apps that have some.</summary>
    [Fact]
    public void Setcap_puts_the_rights_on_the_launcher_and_removes_them_from_apps_that_have_some()
    {
        Assert.Equal([LinuxCaptureRights.Capabilities, "/opt/s/sluice"], LinuxCaptureRights.SetcapArguments("/opt/s/sluice", []));
        Assert.Equal([LinuxCaptureRights.Capabilities, "/opt/s/sluice", "-r", "/opt/s/Sluice.Desktop", "-r", "/opt/s/Sluice.Cli"],
            LinuxCaptureRights.SetcapArguments("/opt/s/sluice", ["/opt/s/Sluice.Desktop", "/opt/s/Sluice.Cli"]));

        var desktop = Path.Combine(_folder, "Sluice.Desktop");
        var cli = Path.Combine(_folder, "Sluice.Cli");
        File.WriteAllText(desktop, "");
        File.WriteAllText(cli, "");
        Assert.Equal([], LinuxCaptureRights.AppsWithCapabilities(desktop, _ => false));
        Assert.Equal([cli], LinuxCaptureRights.AppsWithCapabilities(desktop, p => p == cli));
        Assert.Equal([desktop, cli], LinuxCaptureRights.AppsWithCapabilities(cli, _ => true));
    }

    /// <summary>The .NET folder as the app host finds it, which the launcher (and so the offer) checks.</summary>
    [Fact]
    public void The_dotnet_folder_is_found_as_the_app_host_finds_it()
    {
        var files = new Dictionary<string, string?>();
        string? Line(string f) => files.TryGetValue(f, out var l) ? l : null;

        Assert.Equal("/usr/share/dotnet", LinuxCaptureRights.RuntimeFolder(Line));
        files["/etc/dotnet/install_location"] = "/usr/lib/dotnet";
        Assert.Equal("/usr/lib/dotnet", LinuxCaptureRights.RuntimeFolder(Line));
        files["/etc/dotnet/install_location_x64"] = "/opt/dotnet";
        Assert.Equal("/opt/dotnet", LinuxCaptureRights.RuntimeFolder(Line));
        files["/etc/dotnet/install_location_x64"] = "";                                       // empty: the next one
        Assert.Equal("/usr/lib/dotnet", LinuxCaptureRights.RuntimeFolder(Line));

        var file = Path.Combine(_folder, "install_location");
        File.WriteAllText(file, "/usr/lib/dotnet  \nsecond line\n");
        Assert.Equal("/usr/lib/dotnet", LinuxCaptureRights.FirstLine(file));
        Assert.Null(LinuxCaptureRights.FirstLine(Path.Combine(_folder, "missing")));
    }

    [Fact]
    public void A_folder_is_safe_only_when_root_alone_can_change_it_and_everything_in_it()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "runtimes", "linux-x64"));
        var dll = Path.Combine(_folder, "Sluice.Core.dll");
        var nested = Path.Combine(_folder, "runtimes", "linux-x64", "libSkiaSharp.so");
        File.WriteAllText(dll, "");
        File.WriteAllText(nested, "");
        var owners = new Dictionary<string, FileOwner?>
        {
            [_folder] = RootOnly,
            [Path.Combine(_folder, "runtimes")] = RootOnly,
            [Path.Combine(_folder, "runtimes", "linux-x64")] = RootOnly,
            [dll] = RootOnly,
            [nested] = RootOnly,
        };
        FileOwner? Owner(string p) => owners.TryGetValue(p, out var o) ? o : null;

        Assert.Null(LinuxCaptureRights.FirstChangeablePath(_folder, Owner));

        owners[nested] = new FileOwner(0, (UnixFileMode)0b110_110_100);                 // root's, but group-writable
        Assert.Equal(nested, LinuxCaptureRights.FirstChangeablePath(_folder, Owner));
        owners[nested] = new FileOwner(0, (UnixFileMode)0b110_100_110);                 // writable by everyone
        Assert.Equal(nested, LinuxCaptureRights.FirstChangeablePath(_folder, Owner));
        owners[nested] = RootOnly;

        owners[dll] = new FileOwner(1000, (UnixFileMode)0b110_100_100);                 // the user's own file
        Assert.Equal(dll, LinuxCaptureRights.FirstChangeablePath(_folder, Owner));
        owners[dll] = null;                                                             // can't be read
        Assert.Equal(dll, LinuxCaptureRights.FirstChangeablePath(_folder, Owner));
        owners[dll] = RootOnly;

        owners[_folder] = new FileOwner(1000, (UnixFileMode)0b111_101_101);             // e.g. unpacked in ~/Downloads
        Assert.Equal(_folder, LinuxCaptureRights.FirstChangeablePath(_folder, Owner));
    }

    [LinuxFact]
    public void Owners_are_read_from_the_file_system()
    {
        var root = LinuxCaptureRights.ReadOwner("/usr/bin");
        Assert.Equal(0u, root?.Uid);
        Assert.Equal((UnixFileMode)0, root!.Value.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));

        var mine = Path.Combine(_folder, "file");
        File.WriteAllText(mine, "");
        File.SetUnixFileMode(mine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite, LinuxCaptureRights.ReadOwner(mine)?.Mode);
        if (!Environment.IsPrivilegedProcess) Assert.NotEqual(0u, LinuxCaptureRights.ReadOwner(mine)?.Uid);
        Assert.Null(LinuxCaptureRights.ReadOwner(Path.Combine(_folder, "missing")));
        Assert.Equal(_folder, LinuxCaptureRights.FirstChangeablePath(_folder, LinuxCaptureRights.ReadOwner));
    }

    [Theory]
    [InlineData("/opt/sluice/Sluice.Desktop cap_dac_read_search,cap_net_admin,cap_setgid,cap_sys_ptrace=ep", true)]
    [InlineData("/opt/sluice/Sluice.Desktop = cap_net_admin,cap_setgid,cap_sys_ptrace,cap_dac_read_search+ep", true)]
    [InlineData("/home/a b/Sluice.Desktop cap_dac_read_search,cap_net_admin,cap_setgid,cap_sys_ptrace=eip", true)]
    [InlineData("/opt/sluice/Sluice.Desktop cap_net_admin,cap_setgid=ep", false)]                                   // two missing
    [InlineData("/opt/sluice/Sluice.Desktop cap_dac_read_search,cap_net_admin,cap_setgid,cap_sys_ptrace=p", false)] // not effective
    [InlineData("", false)]
    public void Getcap_output_is_read_in_both_formats(string output, bool granted) =>
        Assert.Equal(granted, LinuxCaptureRights.ShowsCapabilities(output));

    [Theory]
    [InlineData("/opt/sluice/Sluice.Desktop cap_net_admin=ep", true)]
    [InlineData("/opt/sluice/Sluice.Desktop = cap_setgid+p", true)]
    [InlineData("", false)]
    [InlineData("\n", false)]
    public void Any_capability_is_noticed(string output, bool any) =>
        Assert.Equal(any, LinuxCaptureRights.ShowsAnyCapabilities(output));
}
