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
    public static bool? Read() => Read(Queries, Budget);

    /// <summary><see cref="Read()"/> with other helpers and time (tests). The first helper that answers with a value
    /// wins; one that is missing, fails or prints something else is skipped.</summary>
    internal static bool? Read(IEnumerable<(string File, string[] Args)> queries, TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            foreach (var (file, args) in queries)
            {
                var left = budget - clock.Elapsed;
                if (left <= TimeSpan.Zero) break;
                if (Run(file, args, left) is { } output && ParseColorScheme(output) is { } scheme)
                    return ToDark(scheme);
            }
        }
        catch (Exception) { /* never throws: the app then leaves the theme to Avalonia */ }
        return null;
    }

    /// <summary>The portal's <c>color-scheme</c>: 1 prefers dark, 2 prefers light, 0 (or anything else) no preference.</summary>
    internal static bool? ToDark(uint scheme) => scheme switch { 1 => true, 2 => false, _ => null };

    // gdbus: "(<uint32 1>,)", or "(<<uint32 1>>,)" from Read; busctl: "v u 1", or "v v u 1" from Read.
    private static readonly Regex Value = new(@"(?:\buint32 |^(?:v )+u )(\d+)\b", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>The number in a helper's answer, or null when it holds none.</summary>
    internal static uint? ParseColorScheme(string output) =>
        Value.Match(output.Trim()) is { Success: true } m && uint.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v : null;

    /// <summary>What the helper printed when it exited with 0 within <paramref name="timeout"/>; otherwise null.</summary>
    private static string? Run(string file, string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // On this thread, right before Start: the helper gets no capabilities, so the portal answers it. If they
        // can't be cleared, nothing is started.
        if (!Native.Libc.ClearPassedOn(out _)) return null;
        Process? p;
        try { p = Process.Start(psi); }
        catch (Win32Exception) { return null; }       // not installed
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
}
