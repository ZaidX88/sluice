using Sluice.Core.Dns;
using Sluice.Core.Profiles;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// The DNS policy for the current profile, rebuilt when a setting it is made from changes. Keyed by the setting
/// values, not the profile object: the desktop app's Settings page edits the profile in place, so a reference
/// check would keep the policy from when capture started.
/// </summary>
internal sealed class DnsPolicyCache
{
    private readonly Lock _lock = new();
    private (Key Key, DnsPolicy Policy)? _cached;

    /// <summary>Everything <see cref="DnsPolicy"/>'s constructor reads. Add new inputs here too.</summary>
    private readonly record struct Key(bool ViaProxy, bool OnlyFromList, bool BlockNonATypes, string ExclusionList, bool LeakPrevention);

    /// <summary>
    /// The policy for <paramref name="profile"/>. <paramref name="changed"/> is true when it was rebuilt because a
    /// setting differs from the previous call (not on the first call).
    /// </summary>
    public DnsPolicy Get(Profile profile, out bool changed)
    {
        var o = profile.Options;
        var r = o.Resolve;
        var key = new Key(r.ViaProxy, r.OnlyFromListMode, r.BlockNonATypes, r.ExclusionList ?? "", o.LeakPreventionMode);
        lock (_lock)
        {
            if (_cached is { } c && c.Key == key)
            {
                changed = false;
                return c.Policy;
            }
            changed = _cached is not null;
            var policy = new DnsPolicy(r, o.LeakPreventionMode, Environment.MachineName);
            _cached = (key, policy);
            return policy;
        }
    }
}
