using System.Diagnostics;
using System.Net;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Platform;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;
using Sluice.Core.Tunnels;

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

    /// <summary>Counts <see cref="IPlatformServices.PrepareHelperStart"/> calls and refuses each one.</summary>
    private sealed class RefusingPlatform : IPlatformServices
    {
        public int Prepared;
        public string Name => "refusing";
        public void OnAppStart() { }
        public void Notify(string title, string message, bool isError) { }
        public string? CaptureUnavailableReason => null;
        public bool HasCaptureRights => true;
        public string CaptureRightsHint => "";
        public bool CanRestartElevated => false;
        public ITrafficCapture CreateCapture(RoutingEngine engine) => throw new NotSupportedException();
        public void PrepareHelperStart()
        {
            Prepared++;
            throw new InvalidOperationException("thread not ready");
        }
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
        p.PrepareHelperStart();                             // no exception
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

    // #265: the desktop's theme is "not known" (null) until a platform reads it, so the app looks as it does today.
    [Fact]
    public void The_desktop_theme_is_not_known_by_default()
    {
        Assert.Null(new PlatformServices.NoPlatform().PrefersDarkTheme());   // the platform before Use
        IPlatformServices bare = new BarePlatform();
        Assert.Null(bare.PrefersDarkTheme());                                // a platform that doesn't implement it
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
    public async Task The_platform_prepares_the_thread_before_sing_box_starts_and_can_stop_it()
    {
        var previous = PlatformServices.Current;
        var exe = Path.GetTempFileName();                   // exists, so StartAsync gets as far as starting it
        var platform = new RefusingPlatform();
        try
        {
            PlatformServices.Use(platform);
            var tunnel = new Tunnel { Id = 1, Type = TunnelType.Shadowsocks, Address = "127.0.0.1", Port = 8388 };
            var session = new SingBoxSession(tunnel, "test", exe, null, _ => { }, (_, _) => "{}");
            var helpersBefore = SingBoxSession.RunningHelpers.Count;

            var ex = await Assert.ThrowsAsync<ProxyException>(() => session.StartAsync(CancellationToken.None));

            Assert.Equal(1, platform.Prepared);
            Assert.Contains("thread not ready", ex.Message);
            Assert.Null(session.ProcessId);                 // never started
            Assert.Equal(helpersBefore, SingBoxSession.RunningHelpers.Count);
        }
        finally
        {
            PlatformServices.Use(previous);
            File.Delete(exe);
        }
    }

    [Fact]
    public void Use_rejects_null() =>
        Assert.Throws<ArgumentNullException>(() => PlatformServices.Use(null!));
}
