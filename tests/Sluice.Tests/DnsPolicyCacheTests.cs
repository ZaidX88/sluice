using Sluice.Core.Dns;
using Sluice.Core.Profiles;

namespace Sluice.Tests;

/// <summary>
/// Name-resolution settings changed while capture runs take effect (docs/core/FIXES.md §5, issue #5): the Settings page edits the
/// profile in place, so the cached policy must follow the setting values, not the profile object.
/// </summary>
public class DnsPolicyCacheTests
{
    [Fact]
    public void In_place_edits_rebuild_the_policy_and_report_the_change()
    {
        var profile = Profile.CreateDefault();
        var cache = new DnsPolicyCache(() => profile, "pc");
        var changes = new List<(bool Now, bool Before)>();
        cache.Changed += (now, before) => changes.Add((now.Enabled, before.Enabled));

        var first = cache.Current;
        Assert.False(first.Enabled);
        Assert.False(first.ShouldFake("example.com"));
        Assert.Same(first, cache.Current);                    // nothing changed: same policy, no event
        Assert.Empty(changes);

        profile.Options.Resolve.ViaProxy = true;               // what the Settings page does
        Assert.True(cache.Current.ShouldFake("example.com"));
        Assert.Equal([(true, false)], changes);

        profile.Options.Resolve.ExclusionList += "; *.example.com";
        Assert.False(cache.Current.ShouldFake("www.example.com"));
        Assert.Equal(2, changes.Count);

        profile.Options.Resolve.ViaProxy = false;
        profile.Options.LeakPreventionMode = true;             // leak prevention also turns faking on
        Assert.True(cache.Current.Enabled);
        Assert.True(cache.Current.BlockNonATypes);
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void A_new_profile_with_the_same_settings_keeps_the_policy()
    {
        var profile = Profile.CreateDefault();
        var cache = new DnsPolicyCache(() => profile, "pc");
        var raised = 0;
        cache.Changed += (_, _) => raised++;
        var first = cache.Current;

        profile = Profile.CreateDefault();                     // e.g. the same profile opened again
        Assert.Same(first, cache.Current);

        profile.Options.Resolve.OnlyFromListMode = true;
        Assert.NotSame(first, cache.Current);
        Assert.Equal(1, raised);
    }
}
