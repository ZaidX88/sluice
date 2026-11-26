using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Sluice.Platform.Linux;

/// <summary>
/// The desktop's colour scheme as an unprivileged process of the signed-in user sees it (#266, #253's Coordination):
/// xdg-desktop-portal's <c>org.freedesktop.appearance</c> / <c>color-scheme</c> setting. Sluice can't ask the portal
/// itself while it has capture rights: the portal looks into the caller's <c>/proc/&lt;pid&gt;/root</c> first and
/// refuses a process with more capabilities than it has ("Unable to open /proc/&lt;pid&gt;/root"), the same refusal
/// that broke the portal file chooser (#126). So a small helper asks instead, started with every capability cleared
/// (<see cref="Native.Libc.ClearPassedOn"/>), as <c>notify-send</c> is: <c>gdbus</c> (GLib), or <c>busctl</c>
/// (systemd) where GLib's tools aren't installed. Each tries <c>ReadOne</c>, then <c>Read</c> (portals before 1.18).
/// The helper also runs in the user's own group (#284): while capture is on, Sluice runs in its network group, and the
/// portal may open a caller's <c>/proc/&lt;pid&gt;/root</c> only when the caller's user and group IDs match its own.
/// </summary>
internal static class DesktopTheme
{
    private const string Dest = "org.freedesktop.portal.Desktop", ObjectPath = "/org/freedesktop/portal/desktop";
    private const string Iface = "org.freedesktop.portal.Settings", Ns = "org.freedesktop.appearance", Key = "color-scheme";

    /// <summary>How long all the queries together may take ("returns within about a second").</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

    /// <summary>The helpers to ask, in order.</summary>
    internal static readonly IReadOnlyList<(string File, string[] Args)> Queries =
    [
        ("gdbus", ["call", "--session", "--timeout", "1", "-d", Dest, "-o", ObjectPath, "-m", Iface + ".ReadOne", Ns, Key]),
        ("gdbus", ["call", "--session", "--timeout", "1", "-d", Dest, "-o", ObjectPath, "-m", Iface + ".Read", Ns, Key]),
        ("busctl", ["--user", "--timeout=1", "call", Dest, ObjectPath, Iface, "ReadOne", "ss", Ns, Key]),
        ("busctl", ["--user", "--timeout=1", "call", Dest, ObjectPath, Iface, "Read", "ss", Ns, Key]),
    ];

    /// <summary>True dark, false light, null not known. Never throws.</summary>
    public static bool? Read() => Read(Queries, Budget, CurrentUser());

    /// <summary><see cref="Read()"/> with other helpers and time (tests). The first helper that answers with a value
    /// wins; one that is missing, fails or prints something else is skipped. <paramref name="user"/> is the user whose
    /// groups the helper runs in first (<see cref="Read()"/>: the signed-in user; null: Sluice's own).</summary>
    internal static bool? Read(IEnumerable<(string File, string[] Args)> queries, TimeSpan budget, string? user = null)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            foreach (var (file, args) in queries)
            {
                var left = budget - clock.Elapsed;
                if (left <= TimeSpan.Zero) break;
                if (Run(file, args, left, user) is { } output && ParseColorScheme(output) is { } scheme)
                    return ToDark(scheme);
            }
        }
        catch (Exception) { /* never throws: the app then leaves the theme to Avalonia */ }
        return null;
    }

    /// <summary>The portal's <c>color-scheme</c>: 1 prefers dark, 2 prefers light, 0 no preference. No preference is
    /// light: it is how GNOME stores its "Light" style, and the app's default look (and Avalonia's) is light (#284).
    /// A value the portal spec doesn't define is not known.</summary>
    internal static bool? ToDark(uint scheme) => scheme switch { 1 => true, 0 or 2 => false, _ => null };

    // gdbus: "(<uint32 1>,)", or "(<<uint32 1>>,)" from Read; busctl: "v u 1", or "v v u 1" from Read.
    private static readonly Regex Value = new(@"(?:\buint32 |^(?:v )+u )(\d+)\b", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>The number in a helper's answer, or null when it holds none.</summary>
    internal static uint? ParseColorScheme(string output) =>
        Value.Match(output.Trim()) is { Success: true } m && uint.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v : null;

    /// <summary>The signed-in user's name, or null when it can't be told.</summary>
    private static string? CurrentUser()
    {
        try { return Environment.UserName is { Length: > 0 } name ? name : null; }
        catch (Exception) { return null; }
    }

    /// <summary>What the helper printed when it exited with 0 within <paramref name="timeout"/>; otherwise null.</summary>
    private static string? Run(string file, string[] args, TimeSpan timeout, string? user)
    {
        // On this thread, right before Start: the helper gets no capabilities, so the portal answers it. If they
        // can't be cleared, nothing is started.
        if (!Native.Libc.ClearPassedOn(out _)) return null;
        // In the user's own groups first (.NET's child sets them before exec, with this thread's CAP_SETGID): while
        // capture is on, Sluice's network group would make the portal refuse the helper (#284). If that can't start
        // (the user isn't found, or no right to set the groups), the helper starts as before.
        var p = user is null ? null : Start(file, args, user);
        p ??= Start(file, args, null);
        if (p is null) return null;
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                return null;
            }
            return p.ExitCode == 0 && stdout.Wait(TimeSpan.FromMilliseconds(200)) ? stdout.Result : null;
        }
    }

    /// <summary>Starts the helper (as <paramref name="user"/>, when given); null when it can't be started.</summary>
    private static Process? Start(string file, string[] args, string? user)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (user is not null) psi.UserName = user;
        try { return Process.Start(psi); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;                              // not installed, the user not found, or the groups not set
        }
    }
}
