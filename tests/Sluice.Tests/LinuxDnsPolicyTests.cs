using Sluice.Core.Profiles;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>
/// Linux capture's DNS policy follows the profile's settings while capture runs. The desktop app's Settings page
/// edits the profile in place (same object), so the policy must notice changed values, not only a new profile.
/// </summary>
public class LinuxDnsPolicyTests
{
    [Fact]
    public void Turning_on_resolve_through_tunnel_in_place_takes_effect()
    {
        var profile = Profile.CreateDefault();
        var cache = new DnsPolicyCache();
        Assert.False(cache.Get(profile, out _).ShouldFake("example.com"));

        profile.Options.Resolve.ViaProxy = true;     // what the Settings switch does

        var policy = cache.Get(profile, out var changed);
        Assert.True(changed);
        Assert.True(policy.Enabled);
        Assert.True(policy.ShouldFake("example.com"));
    }

    [Fact]
    public void Turning_on_leak_prevention_in_place_takes_effect()
    {
        var profile = Profile.CreateDefault();
        var cache = new DnsPolicyCache();
        Assert.False(cache.Get(profile, out _).BlockNonATypes);

        profile.Options.LeakPreventionMode = true;

        var policy = cache.Get(profile, out var changed);
        Assert.True(changed);
        Assert.True(policy.Enabled);
        Assert.True(policy.BlockNonATypes);
    }

    [Fact]
    public void Editing_the_local_names_list_in_place_takes_effect()
    {
        var profile = Profile.CreateDefault();
        profile.Options.Resolve.ViaProxy = true;
        var cache = new DnsPolicyCache();
        Assert.True(cache.Get(profile, out _).ShouldFake("intranet.example"));

        profile.Options.Resolve.ExclusionList += "; *.example";

        Assert.False(cache.Get(profile, out var changed).ShouldFake("intranet.example"));
        Assert.True(changed);
    }

    [Fact]
    public void Unchanged_settings_keep_the_same_policy()
    {
        var profile = Profile.CreateDefault();
        var cache = new DnsPolicyCache();
        var first = cache.Get(profile, out var changedFirst);

        var second = cache.Get(profile, out var changedSecond);

        Assert.False(changedFirst);       // the first policy is not a change
        Assert.False(changedSecond);
        Assert.Same(first, second);
    }

    [Fact]
    public void A_new_profile_with_the_same_settings_is_not_a_change()
    {
        var cache = new DnsPolicyCache();
        cache.Get(Profile.CreateDefault(), out _);

        cache.Get(Profile.CreateDefault(), out var changed);

        Assert.False(changed);
    }
}
