using System.Diagnostics;

namespace Sluice.Platform.Linux;

/// <summary>
/// Gives a copy of Sluice that runs without the installer the capabilities capture needs (#69): <c>pkexec setcap</c> on
/// the launcher next to it (<c>sluice</c>, docs/linux/BUILD.md → The launcher), the same as <c>install.sh</c> does, and
/// <c>setcap -r</c> on the apps if an older install left capabilities on them. Offered only where the launcher would
/// then run: root alone can change its folder (and everything in it) and the .NET folder the app loads; otherwise anything
/// running as the user could change what gets the capabilities.
/// </summary>
internal static class LinuxCaptureRights
{
    /// <summary>What <c>install.sh</c> grants: nftables and NFQUEUE, Sluice's network group, and owners of connections.</summary>
    public const string Capabilities = "cap_net_admin,cap_setgid,cap_sys_ptrace,cap_dac_read_search+ep";

    /// <summary>The launcher's file name, next to the apps.</summary>
    public const string LauncherName = "sluice";

    private static readonly string[] CapabilityNames = ["cap_net_admin", "cap_setgid", "cap_sys_ptrace", "cap_dac_read_search"];

    /// <summary>The apps the launcher starts; they must carry no capabilities themselves.</summary>
    private static readonly string[] AppBinaries = ["Sluice.Desktop", "Sluice.Cli"];

    private static readonly string[] ToolFolders = ["/usr/sbin", "/sbin", "/usr/bin", "/bin"];

    public static string? FindTool(string name) =>
        ToolFolders.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    /// <summary>
    /// The binary this process runs from, when it is Sluice's own (the app host next to Sluice's DLLs). Null when Sluice
    /// runs through <c>dotnet Sluice.Desktop.dll</c>: the binary is then <c>dotnet</c> itself.
    /// </summary>
    public static string? AppBinary(string? processPath, string baseDirectory)
    {
        if (string.IsNullOrEmpty(processPath) || !Path.IsPathRooted(processPath)) return null;
        if (!AppBinaries.Contains(Path.GetFileName(processPath), StringComparer.Ordinal)) return null;
        var dir = Path.GetDirectoryName(processPath);
        return dir is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)), StringComparison.Ordinal)
            ? processPath : null;
    }

    /// <summary>The launcher next to <paramref name="appBinary"/>; null when this copy has none (an older package).</summary>
    public static string? Launcher(string appBinary)
    {
        var launcher = Path.Combine(Path.GetDirectoryName(appBinary)!, LauncherName);
        return File.Exists(launcher) ? launcher : null;
    }

    /// <summary>The apps next to <paramref name="appBinary"/> (itself included) that still carry capabilities.</summary>
    public static string[] AppsWithCapabilities(string appBinary, Func<string, bool> hasAny)
    {
        var folder = Path.GetDirectoryName(appBinary)!;
        return [.. AppBinaries.Select(b => Path.Combine(folder, b)).Where(p => File.Exists(p) && hasAny(p))];
    }

    /// <summary>
    /// setcap's arguments: the capabilities on the launcher, and <c>-r</c> for each app that still has some (setcap takes
    /// one file per <c>-r</c>, and fails on a file that has none, so only those).
    /// </summary>
    public static List<string> SetcapArguments(string launcher, IEnumerable<string> appsToClear)
    {
        List<string> args = [Capabilities, launcher];
        foreach (var app in appsToClear) { args.Add("-r"); args.Add(app); }
        return args;
    }

    // ---------- the .NET folder, found as the app host (and the launcher) finds it ----------

    /// <summary>
    /// The .NET folder the app will load: the first line of <c>/etc/dotnet/install_location_x64</c>, then of
    /// <c>/etc/dotnet/install_location</c>, otherwise <c>/usr/share/dotnet</c>. The launcher refuses to run unless root
    /// alone can change it.
    /// </summary>
    public static string RuntimeFolder(Func<string, string?> firstLine)
    {
        foreach (var file in new[] { "/etc/dotnet/install_location_x64", "/etc/dotnet/install_location" })
            if (firstLine(file) is { Length: > 0 } folder) return folder;
        return "/usr/share/dotnet";
    }

    /// <summary>The first line of <paramref name="file"/> without trailing blanks; null when it can't be read.</summary>
    public static string? FirstLine(string file)
    {
        try { return File.Exists(file) ? File.ReadLines(file).FirstOrDefault()?.TrimEnd() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---------- who can change a folder ----------

    /// <summary>One file's owner and permissions, following symbolic links; null when it can't be read.</summary>
    public readonly record struct FileOwner(uint Uid, UnixFileMode Mode);

    /// <summary>
    /// The first path in <paramref name="folder"/> (the folder itself first, then everything in it) that someone other
    /// than root could change: not owned by root, or writable by its group or others. Null when there is none.
    /// </summary>
    public static string? FirstChangeablePath(string folder, Func<string, FileOwner?> owner)
    {
        if (!IsRootOnly(owner(folder))) return folder;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 }))
                if (!IsRootOnly(owner(path))) return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return folder; }
        return null;
    }

    private static bool IsRootOnly(FileOwner? f) =>
        f is { Uid: 0 } o && (o.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;

    /// <summary>The real owner and permissions of <paramref name="path"/> (statx, following links).</summary>
    public static FileOwner? ReadOwner(string path)
    {
        try
        {
            var buffer = new byte[256];
            if (Native.Libc.statx(Native.Libc.AT_FDCWD, path, 0, Native.Libc.STATX_UID | StatxMode, buffer) != 0) return null;
            var uid = BitConverter.ToUInt32(buffer, 20);
            var mode = BitConverter.ToUInt16(buffer, 28);   // stx_mode: type bits and permissions
            return new FileOwner(uid, (UnixFileMode)(mode & 0xFFF));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private const uint StatxMode = 0x2;   // STATX_MODE

    // ---------- getcap, pkexec setcap ----------

    /// <summary>True when <c>getcap</c> shows all four capabilities, effective and permitted, on <paramref name="binary"/>.</summary>
    public static bool HasFileCapabilities(string binary) => Getcap(binary) is { } output && ShowsCapabilities(output);

    /// <summary>True when <c>getcap</c> shows any capabilities on <paramref name="binary"/>.</summary>
    public static bool HasAnyFileCapabilities(string binary) => Getcap(binary) is { } output && ShowsAnyCapabilities(output);

    /// <summary>getcap's output for <paramref name="binary"/>; null when it can't be run.</summary>
    private static string? Getcap(string binary)
    {
        if (FindTool("getcap") is not { } getcap) return null;
        try
        {
            var psi = new ProcessStartInfo(getcap) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(binary);
            if (!Native.Libc.ClearPassedOn(out _)) return null;   // getcap gets no capabilities, or doesn't start
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? output : null;
        }
        catch (SystemException) { return null; }
    }

    /// <summary>
    /// Whether a line of <c>getcap</c> output grants all four capabilities effective and permitted, in either format:
    /// <c>path cap_a,cap_b=ep</c> (libcap 2.x) or <c>path = cap_a,cap_b+ep</c> (older).
    /// </summary>
    internal static bool ShowsCapabilities(string getcapOutput) =>
        CapabilityFields(getcapOutput).Any(f => f.Flags.Contains('e') && f.Flags.Contains('p')
            && CapabilityNames.All(n => f.Names.Contains(n, StringComparer.Ordinal)));

    /// <summary>Whether <c>getcap</c> output shows any capability at all.</summary>
    internal static bool ShowsAnyCapabilities(string getcapOutput) => CapabilityFields(getcapOutput).Any();

    private static IEnumerable<(string[] Names, string Flags)> CapabilityFields(string getcapOutput)
    {
        foreach (var line in getcapOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The capabilities are the last field; a path may hold spaces.
            var field = line[(line.LastIndexOf(' ') + 1)..];
            var flagsAt = field.LastIndexOfAny(['=', '+']);
            if (flagsAt <= 0) continue;
            yield return (field[..flagsAt].Split(','), field[(flagsAt + 1)..]);
        }
    }

    /// <summary>
    /// Runs <c>pkexec setcap</c> with <paramref name="setcapArguments"/>: the desktop's password prompt, then the
    /// capabilities. True when it worked and <paramref name="launcher"/> shows them. Never throws (a cancelled prompt:
    /// false). pkexec gets none of Sluice's own capabilities.
    /// </summary>
    public static async Task<bool> GrantAsync(string pkexec, string setcap, string launcher, IReadOnlyList<string> setcapArguments)
    {
        try
        {
            var psi = new ProcessStartInfo(pkexec) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(setcap);
            foreach (var a in setcapArguments) psi.ArgumentList.Add(a);
            if (!Native.Libc.ClearPassedOn(out _)) return false;   // on this thread, right before the start
            using var p = Process.Start(psi);
            if (p is null) return false;
            var drain = Task.WhenAll(p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync());
            await p.WaitForExitAsync().ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            return p.ExitCode == 0 && HasFileCapabilities(launcher);   // 126: prompt dismissed, 127: not authorised
        }
        catch (SystemException) { return false; }
    }
}
