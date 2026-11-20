using Sluice.Platform.Linux;
using FileOwner = Sluice.Platform.Linux.LinuxCaptureRights.FileOwner;
using FileKind = Sluice.Platform.Linux.LinuxCaptureRights.FileKind;
using FileSystemView = Sluice.Platform.Linux.LinuxCaptureRights.FileSystemView;

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

    /// <summary>A file system made up for the checks: owners by path, links (path → real path), folder contents.</summary>
    private sealed class FakeFiles
    {
        public readonly Dictionary<string, FileOwner> Owners = new();
        public readonly Dictionary<string, string> Links = new();

        public FakeFiles(params string[] rootOnlyFolders)
        {
            foreach (var d in rootOnlyFolders) Dir(d);
        }

        public void Dir(string path, uint uid = 0, int mode = 0b111_101_101) => Owners[path] = new(uid, (UnixFileMode)mode, FileKind.Directory);
        public void File(string path, uint uid = 0, int mode = 0b111_101_101) => Owners[path] = new(uid, (UnixFileMode)mode, FileKind.File);
        public void Link(string path, string target) { Owners[path] = new(0, (UnixFileMode)0b111_111_111, FileKind.Link); Links[path] = target; }

        private string Resolve(string path)
        {
            // Every link on the way, component by component.
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var p = "";
            for (int i = 0; i < parts.Length; i++)
            {
                p += "/" + parts[i];
                for (int n = 0; Links.TryGetValue(p, out var t) && n < 10; n++) p = t;
            }
            return p.Length == 0 ? "/" : p;
        }

        public FileSystemView View => new(
            p => Owners.TryGetValue(Resolve(p), out var o) ? o : null,
            p => Owners.TryGetValue(Path.Combine(Resolve(Path.GetDirectoryName(p) ?? "/"), Path.GetFileName(p)), out var o) ? o : null,
            p => Owners.ContainsKey(Resolve(p)) ? Resolve(p) : null,
            d => Owners.Keys.Where(k => k != d && Path.GetDirectoryName(k) == d).OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>A root-only system with the launcher in /opt/sluice and .NET in /usr/lib/dotnet.</summary>
    private static FakeFiles SafeInstall()
    {
        var f = new FakeFiles("/", "/opt", "/opt/sluice", "/usr", "/usr/lib", "/usr/lib/dotnet", "/usr/lib/dotnet/shared", "/etc", "/etc/dotnet");
        f.File("/opt/sluice/sluice");
        f.File("/opt/sluice/Sluice.Desktop");
        f.File("/usr/lib/dotnet/shared/libcoreclr.so");
        f.File("/etc/dotnet/install_location", mode: 0b110_100_100);
        return f;
    }

    private static string? Line(string file) => file == "/etc/dotnet/install_location" ? "/usr/lib/dotnet" : null;

    [Fact]
    public void A_root_only_install_can_be_granted()
    {
        var f = SafeInstall();
        Assert.Null(LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
    }

    /// <summary>The owner's example: copied with sudo into ~/apps/sluice. Everything in it is root's, ~/apps isn't, so the
    /// user could swap the folder (and the launcher for a link) while the password prompt is open.</summary>
    [Fact]
    public void A_folder_above_the_launcher_that_the_user_can_change_is_refused()
    {
        var f = SafeInstall();
        f.Dir("/home");
        f.Dir("/home/alice", uid: 1000);
        f.Dir("/home/alice/apps", uid: 1000);
        f.Dir("/home/alice/apps/sluice");
        f.File("/home/alice/apps/sluice/sluice");
        Assert.Equal("/home/alice/apps", LinuxCaptureRights.FirstChangeablePath("/home/alice/apps/sluice/sluice", Line, f.View));

        f.Dir("/opt", mode: 0b111_111_101);                                                       // group-writable /opt
        Assert.Equal("/opt", LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
    }

    [Fact]
    public void The_launcher_must_not_be_a_link()
    {
        var f = SafeInstall();
        f.File("/usr/bin/python3");
        f.Link("/opt/sluice/sluice", "/usr/bin/python3");
        Assert.Equal("/opt/sluice/sluice", LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
    }

    /// <summary>A link on the way to the folder is fine when both the path as written and the real path are root-only,
    /// as the launcher checks both.</summary>
    [Fact]
    public void A_folder_reached_through_a_link_is_checked_as_written_and_as_its_real_path()
    {
        var f = SafeInstall();
        f.Dir("/srv");
        f.Dir("/srv/sluice");
        f.File("/srv/sluice/sluice");
        f.Link("/opt/app", "/srv/sluice");
        Assert.Null(LinuxCaptureRights.FirstChangeablePath("/opt/app/sluice", Line, f.View));

        f.Dir("/srv", uid: 1000);                                                                  // the real path isn't safe
        Assert.Equal("/srv", LinuxCaptureRights.FirstChangeablePath("/opt/app/sluice", Line, f.View));
    }

    [Fact]
    public void Inside_the_launchers_folder_anything_changeable_or_any_link_is_refused()
    {
        var f = SafeInstall();
        f.Dir("/opt/sluice/runtimes");
        f.File("/opt/sluice/runtimes/libSkiaSharp.so", mode: 0b110_110_100);                      // root's, group-writable
        Assert.Equal("/opt/sluice/runtimes/libSkiaSharp.so", LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
        f.File("/opt/sluice/runtimes/libSkiaSharp.so");

        f.File("/opt/sluice/Sluice.Core.dll", uid: 1000);                                         // the user's own file
        Assert.Equal("/opt/sluice/Sluice.Core.dll", LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
        f.File("/opt/sluice/Sluice.Core.dll");

        f.Link("/opt/sluice/Sluice.Cli", "/opt/sluice/Sluice.Desktop");                           // even to a safe file inside
        Assert.Equal("/opt/sluice/Sluice.Cli", LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
    }

    /// <summary>The launcher's check_runtime: links in the .NET folder only to root-only files inside it; the folders above
    /// it; /etc/dotnet and both location files.</summary>
    [Fact]
    public void The_dotnet_folder_follows_the_launchers_rules()
    {
        var f = SafeInstall();
        f.File("/usr/lib/dotnet/shared/real.so");
        f.Link("/usr/lib/dotnet/shared/link.so", "/usr/lib/dotnet/shared/real.so");             // inside: allowed
        Assert.Null(LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));

        f.Dir("/home");
        f.File("/home/evil.so", uid: 1000);
        f.Link("/usr/lib/dotnet/shared/link.so", "/home/evil.so");                                // outside: refused
        Assert.Equal("/usr/lib/dotnet/shared/link.so", LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
        f.File("/home/evil.so");                                                                  // even when it's root's
        Assert.Equal("/usr/lib/dotnet/shared/link.so", LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
        f.Link("/usr/lib/dotnet/shared/link.so", "/usr/lib/dotnet/shared/real.so");

        f.Dir("/usr/lib", uid: 1000);                                                             // a folder above it
        Assert.Equal("/usr/lib", LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
        f.Dir("/usr/lib");

        f.File("/etc/dotnet/install_location_x64", uid: 1000);                                    // read or not, both checked
        Assert.Equal("/etc/dotnet/install_location_x64", LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
        f.Owners.Remove("/etc/dotnet/install_location_x64");

        f.Dir("/etc/dotnet", mode: 0b111_111_111);
        Assert.Equal("/etc/dotnet", LinuxCaptureRights.FirstChangeablePath("/opt/sluice/sluice", Line, f.View));
        f.Dir("/etc/dotnet");

        Assert.Equal("dotnet", LinuxCaptureRights.FirstChangeableRuntimePath(_ => "dotnet", f.View));   // not absolute
    }

    [LinuxFact]
    public void Owners_types_and_real_paths_are_read_from_the_file_system()
    {
        var root = LinuxCaptureRights.ReadOwner("/usr/bin");
        Assert.Equal(0u, root?.Uid);
        Assert.Equal(FileKind.Directory, root?.Kind);
        Assert.Equal((UnixFileMode)0, root!.Value.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));

        var mine = Path.Combine(_folder, "file");
        System.IO.File.WriteAllText(mine, "");
        System.IO.File.SetUnixFileMode(mine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite, LinuxCaptureRights.ReadOwner(mine)?.Mode);
        Assert.Equal(FileKind.File, LinuxCaptureRights.ReadOwner(mine)?.Kind);
        if (!Environment.IsPrivilegedProcess) Assert.NotEqual(0u, LinuxCaptureRights.ReadOwner(mine)?.Uid);
        Assert.Null(LinuxCaptureRights.ReadOwner(Path.Combine(_folder, "missing")));

        var link = Path.Combine(_folder, "link");
        System.IO.File.CreateSymbolicLink(link, mine);
        Assert.Equal(FileKind.Link, LinuxCaptureRights.ReadOwner(link, follow: false)?.Kind);
        Assert.Equal(FileKind.File, LinuxCaptureRights.ReadOwner(link)?.Kind);
        Assert.Equal(LinuxCaptureRights.ResolveRealPath(mine), LinuxCaptureRights.ResolveRealPath(link));
        Assert.Null(LinuxCaptureRights.ResolveRealPath(Path.Combine(_folder, "missing")));

        // The temporary folder is the user's: refused, with the real file system.
        Assert.NotNull(LinuxCaptureRights.FirstChangeableInFolder(_folder, allowLinks: false, FileSystemView.Real));
        Assert.Null(LinuxCaptureRights.FirstChangeableUp("/usr/bin", FileSystemView.Real));
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
