using System.Diagnostics;
using System.Net;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Platform;
using Sluice.Core.Profiles;

namespace Sluice.Tests;

/// <summary>Changes process-wide state (the platform in use, the process-lookup backend): never in parallel with others.</summary>
[CollectionDefinition(nameof(PlatformServicesTests), DisableParallelization = true)]
public sealed class PlatformServicesCollection;

/// <summary>
/// The Core side of the platform interfaces (#104, docs/architecture/platforms.md §6): how Core reaches the OS module,
/// and the default members a platform that doesn't implement them yet falls back to.
/// </summary>
[Collection(nameof(PlatformServicesTests))]
public class PlatformServicesTests
{
    /// <summary>Implements only the members every platform has today, like WindowsPlatform and LinuxPlatform.</summary>
    private sealed class BarePlatform : IPlatformServices
    {
        public string Name => "bare";
        public void OnAppStart() { }
        public void Notify(string title, string message, bool isError) { }
        public string? CaptureUnavailableReason => null;
        public bool HasCaptureRights => true;
        public string CaptureRightsHint => "";
        public bool CanRestartElevated => false;
        public ITrafficCapture CreateCapture(RoutingEngine engine) => throw new NotSupportedException();
    }

    private sealed class FakeLookup : IProcessLookup
    {
        public int FindTcpOwner(IPEndPoint local, IPEndPoint remote) => 4242;
        public int FindUdpOwner(IPEndPoint local) => 4242;
        public HashSet<int> FindListeners(int port) => [4242];
        public string? GetProcessPath(int pid) => "/opt/example/app";
    }

    private sealed class FullPlatform(IProcessLookup lookup) : IPlatformServices
    {
        public string Name => "full";
        public void OnAppStart() { }
        public void Notify(string title, string message, bool isError) { }
        public string? CaptureUnavailableReason => null;
        public bool HasCaptureRights => true;
        public string CaptureRightsHint => "";
        public bool CanRestartElevated => false;
        public ITrafficCapture CreateCapture(RoutingEngine engine) => throw new NotSupportedException();
        public IProcessLookup? ProcessLookup => lookup;
    }

    private static void Restoring(Action test)
    {
        var platform = PlatformServices.Current;
        var backend = ProcessLookup.Backend;
        try { test(); }
        finally
        {
            PlatformServices.Use(platform);
            ProcessLookup.Backend = backend;
        }
    }

    [Fact]
    public void Default_members_do_nothing()
    {
        IPlatformServices p = new BarePlatform();
        Assert.Null(p.ProcessLookup);
        Assert.Null(p.SecretProtector);
        p.FlushDnsCache();                                  // no exception
        using var self = Process.GetCurrentProcess();
        p.TieChildProcess(self);                            // no exception, nothing tied
    }

    [Fact]
    public async Task Capture_rights_and_autostart_are_off_by_default()
    {
        IPlatformServices p = new BarePlatform();
        Assert.False(p.CanGrantCaptureRights);
        Assert.False(await p.TryGrantCaptureRightsAsync());
        Assert.False(p.AutostartSupported);
        p.Autostart = true;                                 // ignored where unsupported
        Assert.False(p.Autostart);
    }

    [Fact]
    public void Use_sets_the_platform_and_the_process_lookup_backend() => Restoring(() =>
    {
        var lookup = new FakeLookup();
        var platform = new FullPlatform(lookup);
        PlatformServices.Use(platform);

        Assert.Same(platform, PlatformServices.Current);
        Assert.Same(lookup, ProcessLookup.Backend);
        Assert.Equal(4242, ProcessLookup.FindTcpOwner(new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2)));
    });

    [Fact]
    public void A_platform_without_a_lookup_leaves_the_backend_alone() => Restoring(() =>
    {
        var lookup = new FakeLookup();
        ProcessLookup.Backend = lookup;
        PlatformServices.Use(new BarePlatform());
        Assert.Same(lookup, ProcessLookup.Backend);
    });

    [Fact]
    public void Use_rejects_null() =>
        Assert.Throws<ArgumentNullException>(() => PlatformServices.Use(null!));
}
