namespace Sluice.Core.Tunnels;

/// <summary>
/// Where Sluice writes sing-box configs for a moment while a helper starts (they hold tunnel passwords and keys).
///
/// Not the temp folder: users and cleanup tools empty it, and a folder deleted under a running Sluice made every later
/// tunnel start fail ("Could not find a part of the path …\Temp\sluice-sing-box-…"). Instead:
/// <list type="bullet">
/// <item>Windows: <c>%LOCALAPPDATA%\Sluice\helpers</c>, which only the user (and administrators) can read.</item>
/// <item>Linux: <c>$XDG_RUNTIME_DIR/sluice</c> (per user, 0700, in memory, cleared at logout), or
/// <c>$XDG_STATE_HOME/sluice/helpers</c> (<c>~/.local/state/…</c>) when there is no runtime folder (e.g. under sudo).</item>
/// </list>
/// The folder is (re)created before every helper start, so deleting it never breaks a running Sluice; each config is
/// deleted as soon as sing-box has read it, and leftovers from a crash are removed on the next start.
/// </summary>
public static class HelperConfigFolder
{
    /// <summary>A config older than this is a leftover (a helper reads its config within seconds).</summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    public static string Default => For(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
        Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The folder for these environment values (separate from <see cref="Default"/> for tests).</summary>
    public static string For(bool windows, string localAppData, string? xdgRuntimeDir, string? xdgStateHome, string home)
    {
        if (windows) return Path.Combine(localAppData, "Sluice", "helpers");
        if (!string.IsNullOrEmpty(xdgRuntimeDir) && Path.IsPathRooted(xdgRuntimeDir) && Directory.Exists(xdgRuntimeDir))
            return Path.Combine(xdgRuntimeDir, "sluice");
        var state = !string.IsNullOrEmpty(xdgStateHome) && Path.IsPathRooted(xdgStateHome)
            ? xdgStateHome
            : Path.Combine(home, ".local", "state");
        return Path.Combine(state, "sluice", "helpers");
    }

    /// <summary>Creates <paramref name="dir"/> if missing (owner-only on Linux), removes leftover configs, returns it.</summary>
    public static string Ensure(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(dir, ownerOnly);
            // An existing folder may have been created with other permissions: tighten it (fails if someone else owns it).
            if ((File.GetUnixFileMode(dir) & ~ownerOnly) != 0) File.SetUnixFileMode(dir, ownerOnly);
        }
        RemoveLeftovers(dir);
        return dir;
    }

    private static void RemoveLeftovers(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow - LeftoverAge;
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use / gone */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
