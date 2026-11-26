using System.Diagnostics;
using Sluice.Platform.Linux;

namespace Sluice.Tests;

/// <summary>#266: reading the desktop's colour scheme from a helper (<see cref="DesktopTheme"/>). The helpers here are
/// stand-ins (<c>/bin/sh</c> printing what gdbus or busctl would), so no portal is needed.</summary>
public class LinuxDesktopThemeTests
{
    [Theory]
    [InlineData("(<uint32 1>,)", 1u)]          // gdbus, ReadOne
    [InlineData("(<<uint32 2>>,)", 2u)]        // gdbus, Read (portals before 1.18)
    [InlineData("(<uint32 0>,)\n", 0u)]
    [InlineData("v u 1", 1u)]                  // busctl, ReadOne
    [InlineData("v v u 2\n", 2u)]              // busctl, Read
    [InlineData("", null)]
    [InlineData("Error: GDBus.Error:org.freedesktop.portal.Error.NotFound: Requested setting not found", null)]
    [InlineData("(<'dark'>,)", null)]
    [InlineData("v s \"1\"", null)]
    public void The_helpers_answer_is_parsed(string output, uint? expected) =>
        Assert.Equal(expected, DesktopTheme.ParseColorScheme(output));

    [Theory]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(0u, false)]                    // no preference: GNOME's "Light" (#284)
    [InlineData(7u, null)]                     // a value the portal spec doesn't define
    public void The_scheme_maps_to_dark_light_or_not_known(uint scheme, bool? expected) =>
        Assert.Equal(expected, DesktopTheme.ToDark(scheme));

    private static (string, string[]) Sh(string script) => ("/bin/sh", ["-c", script]);

    [Fact]
    public void The_first_helper_with_an_answer_wins()
    {
        Assert.True(DesktopTheme.Read([Sh("echo '(<uint32 1>,)'")], TimeSpan.FromSeconds(1)));
        Assert.False(DesktopTheme.Read([Sh("echo 'v u 2'")], TimeSpan.FromSeconds(1)));
        Assert.False(DesktopTheme.Read([Sh("echo '(<uint32 0>,)'")], TimeSpan.FromSeconds(1)));
        Assert.Null(DesktopTheme.Read([Sh("echo '(<uint32 7>,)'")], TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Missing_or_failing_helpers_are_skipped()
    {
        var answer = DesktopTheme.Read(
        [
            ("/nonexistent/gdbus", ["call"]),                                     // not installed
            Sh("echo 'Error: GDBus.Error:org.freedesktop.DBus.Error.UnknownMethod' >&2; exit 1"),   // ReadOne unknown
            Sh("echo 'garbage'"),                                                 // exits 0, no value
            Sh("echo '(<<uint32 1>>,)'"),                                         // Read answers
        ], TimeSpan.FromSeconds(2));
        Assert.True(answer);
    }

    [Fact]
    public void The_helper_starts_in_the_users_groups_or_else_as_before()
    {
        // #284: asked as the signed-in user, the helper runs in that user's groups; where that can't be done (here, no
        // CAP_SETGID; or a user that doesn't exist) it starts as before, so the answer never depends on it.
        Assert.True(DesktopTheme.Read([Sh("echo '(<uint32 1>,)'")], TimeSpan.FromSeconds(2), Environment.UserName));
        Assert.False(DesktopTheme.Read([Sh("echo '(<uint32 0>,)'")], TimeSpan.FromSeconds(2), "sluice-no-such-user"));
        Assert.Null(DesktopTheme.Read([("/nonexistent/gdbus", [])], TimeSpan.FromSeconds(1), Environment.UserName));
    }

    [Fact]
    public void Nothing_answering_is_not_known()
    {
        Assert.Null(DesktopTheme.Read([], TimeSpan.FromSeconds(1)));
        Assert.Null(DesktopTheme.Read([("/nonexistent/gdbus", []), Sh("exit 1")], TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_hanging_helper_is_given_up_on_within_the_budget()
    {
        var clock = Stopwatch.StartNew();
        var answer = DesktopTheme.Read([Sh("sleep 10; echo '(<uint32 1>,)'"), Sh("echo '(<uint32 1>,)'")], TimeSpan.FromSeconds(1));
        clock.Stop();
        Assert.Null(answer);                                                        // the budget went to the first
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
    }

    [Fact]
    public void The_real_helpers_ask_the_portal_for_color_scheme()
    {
        Assert.Equal(["gdbus", "gdbus", "busctl", "busctl"], DesktopTheme.Queries.Select(q => q.File));
        Assert.All(DesktopTheme.Queries, q => Assert.Contains("org.freedesktop.appearance", q.Args));
        Assert.All(DesktopTheme.Queries, q => Assert.Contains("color-scheme", q.Args));
        Assert.Contains("org.freedesktop.portal.Settings.ReadOne", DesktopTheme.Queries[0].Args);
        Assert.Contains("ReadOne", DesktopTheme.Queries[2].Args);
    }
}
