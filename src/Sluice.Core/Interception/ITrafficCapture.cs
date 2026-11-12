using Sluice.Core.Profiles;

namespace Sluice.Core.Interception;

/// <summary>
/// System-wide capture of other applications' connections into the routing engine.
/// Windows: <see cref="Interceptor"/> (WinDivert). Linux: nftables + NFQUEUE in Sluice.Platform.Linux.
/// </summary>
public interface ITrafficCapture : IAsyncDisposable
{
    /// <summary>Starts capturing; throws <see cref="InvalidOperationException"/> with a readable reason on failure.</summary>
    void Start();

    /// <summary>True when <paramref name="profile"/> needs a different capture setup (restart capture to apply).</summary>
    bool NeedsRestartFor(Profile profile);

    /// <summary>Raised once when connections are redirected but none reaches Sluice (a firewall drops them).</summary>
    event Action? RelayUnreachable;
}
