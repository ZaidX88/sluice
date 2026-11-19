using Sluice.Platform.Linux.Capture;
using Sluice.Platform.Linux.Native;

namespace Sluice.Tests;

/// <summary>
/// Under setcap, Sluice raises CAP_NET_ADMIN as an ambient capability so nft gets it, then lowers it again (#99).
/// Left raised, every thread and program started later from that thread gets it, and GTK's sandboxed image loader
/// (bwrap) refuses to run, which aborts the in-process file dialog.
/// <para>
/// The tests that raise the capability need it in the permitted set and do nothing without it (a normal
/// <c>dotnet test</c>, or root, where raising is a no-op). To run them for real without setcap, start the tests in a
/// user namespace that keeps its capabilities:
/// <c>unshare --user --map-current-user --keep-caps dotnet test tests/Sluice.Tests.Linux --filter LinuxAmbientCapabilityTests</c>.
/// </para>
/// </summary>
public class LinuxAmbientCapabilityTests
{
    private static bool CanRaise => !Environment.IsPrivilegedProcess && Libc.IsPermitted(Libc.CAP_NET_ADMIN);

    /// <summary>Runs <paramref name="body"/> on a thread of its own: capabilities belong to the thread.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? failed = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { failed = ex; } });
        t.Start();
        t.Join();
        if (failed is not null) throw new Xunit.Sdk.XunitException($"failed on its thread: {failed}");
    }

    private static bool OnNewThread(Func<bool> check)
    {
        var result = false;
        var t = new Thread(() => result = check());
        t.Start();
        t.Join();
        return result;
    }

    [LinuxFact]
    public void Lowering_what_was_never_raised_is_harmless() => OnOwnThread(() =>
    {
        Assert.True(Libc.LowerAmbient(Libc.CAP_NET_ADMIN, out var problem), problem);
        Assert.False(Libc.IsAmbient(Libc.CAP_NET_ADMIN));
        Assert.False(Libc.IsInheritable(Libc.CAP_NET_ADMIN));
    });

    [LinuxFact]
    public void Raise_then_lower_leaves_the_thread_without_it() => OnOwnThread(() =>
    {
        if (!CanRaise) return;
        Assert.True(Libc.RaiseAmbient(Libc.CAP_NET_ADMIN, out var problem), problem);
        Assert.True(Libc.IsAmbient(Libc.CAP_NET_ADMIN));

        Assert.True(Libc.LowerAmbient(Libc.CAP_NET_ADMIN, out problem), problem);
        Assert.False(Libc.IsAmbient(Libc.CAP_NET_ADMIN));
        Assert.False(Libc.IsInheritable(Libc.CAP_NET_ADMIN));
        Assert.True(Libc.IsPermitted(Libc.CAP_NET_ADMIN));        // still Sluice's own, for nft next time
    });

    [LinuxFact]
    public void Running_nft_leaves_nothing_behind_for_later_threads() => OnOwnThread(() =>
    {
        if (!CanRaise || NftRules.FindNft() is null) return;
        Libc.LowerAmbient(Libc.CAP_NET_ADMIN, out _);              // a clean start (a user namespace raises everything)

        try { NftRules.Run("list tables\n"); }                      // reads only; may be refused outside setcap
        catch (InvalidOperationException) { }

        Assert.False(Libc.IsAmbient(Libc.CAP_NET_ADMIN));
        Assert.False(Libc.IsInheritable(Libc.CAP_NET_ADMIN));
        Assert.False(OnNewThread(() => Libc.IsAmbient(Libc.CAP_NET_ADMIN)));   // e.g. GTK's dialog thread
    });
}
