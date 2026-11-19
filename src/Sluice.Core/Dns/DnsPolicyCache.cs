using Sluice.Core.Profiles;

namespace Sluice.Core.Dns;

/// <summary>
/// The current <see cref="DnsPolicy"/> for a profile, rebuilt whenever the settings it depends on change.
///
/// The desktop app's Settings page edits <see cref="Profile.Options"/> in place and reloads the engine with the same
/// profile object, so a policy cached by profile reference never noticed a change (docs/core/FIXES.md §5, issue #5). This cache
/// compares the setting values instead. It is cheap enough to ask on every DNS query.
/// </summary>
public sealed class DnsPolicyCache(Func<Profile> profile, string? computerName = null)
{
    /// <summary>The settings a <see cref="DnsPolicy"/> is built from.</summary>
    public readonly record struct Settings(bool ViaProxy, bool LeakPrevention, bool OnlyFromList, bool BlockNonATypes, string ExclusionList)
    {
        public static Settings Of(ProfileOptions o) => new(
            o.Resolve.ViaProxy, o.LeakPreventionMode, o.Resolve.OnlyFromListMode, o.Resolve.BlockNonATypes, o.Resolve.ExclusionList ?? "");
    }

    private sealed record Entry(Settings Settings, DnsPolicy Policy);

    private volatile Entry? _entry;

    /// <summary>
    /// Raised after the policy was rebuilt because a setting changed (not for the first build), with the new and the
    /// previous policy. Raised on the thread that asked; handlers must be quick.
    /// </summary>
    public event Action<DnsPolicy, DnsPolicy>? Changed;

    public DnsPolicy Current
    {
        get
        {
            var o = profile().Options;
            var settings = Settings.Of(o);
            var entry = _entry;
            if (entry is not null && entry.Settings == settings) return entry.Policy;

            var policy = new DnsPolicy(o.Resolve, o.LeakPreventionMode, computerName);
            var next = new Entry(settings, policy);
            // Two threads may notice the same change at once: only the one that swaps in its entry reports it.
            if (Interlocked.CompareExchange(ref _entry, next, entry) == entry && entry is not null)
                Changed?.Invoke(policy, entry.Policy);
            return _entry?.Policy ?? policy;
        }
    }
}
