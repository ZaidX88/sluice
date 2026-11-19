# Command line, tests, build and packaging

Part of the [architecture reference](../ARCHITECTURE.md). How to build and run step by step is in
[windows/BUILD.md](../windows/BUILD.md) and [linux/BUILD.md](../linux/BUILD.md); this page explains how the pieces are
put together.

## `Sluice.Cli`

`src/Sluice.Cli/Program.cs` (top-level statements, `net10.0`, references `Sluice.Core` and `Sluice.Platform.Linux`) is
the headless front end. Commands:

| Command | What it does |
|---|---|
| `Sluice.Cli --version` | Prints this build's version (`SluiceVersion.Text`, from `<Version>` in `Directory.Build.props`) |
| `Sluice.Cli [profile] [--socks ADDR] [--intercept] [--udp MODE] [--log LEVEL] [--log-file DIR] [--duration N]` | Prints `Sluice <version> on <system>`, then runs the engine: `ConnectionTracker` printing to the console, optional `FileLog`, `RoutingEngine`, a `Socks5Inbound`, and with `--intercept` a capture. Ctrl+C (or `--duration`) disconnects everything, stops capture and prints `Statistics` per application and target |
| `Sluice.Cli check <profile or share link>` | `TunnelChecker` on each tunnel (or one link) |
| `Sluice.Cli import <profile.sluice> <link / file / --subscription URL>` | `Subscriptions.ImportLinks` / `AddSubscription` + `UpdateAsync`, then saves |
| `Sluice.Cli update <profile.sluice>` | refreshes every subscription |

Unlike the desktop app, the CLI is **not multi-targeted** yet: it picks its capture with a run-time branch
(`OperatingSystem.IsLinux()` → `LinuxPlatform.CreateCapture`, otherwise `new Interceptor(engine)`). Moving it onto the
platform modules is in [REQUIRED.md §1](../REQUIRED.md#1-move-windows-only-code-out-of-core).

## Tests

xUnit 2.9, split by area ([CONTRIBUTING.md → Tests](../CONTRIBUTING.md#tests), issue #18):

| Project | Target | References | Run by |
|---|---|---|---|
| `tests/Sluice.Tests` (shared) | `net10.0` | Core, Support | both filters |
| `tests/Sluice.Tests.Linux` | `net10.0` | Core, `Sluice.Platform.Linux`, Support | `Sluice.Linux.slnf` |
| `tests/Sluice.Tests.Windows` | `net10.0-windows10.0.19041.0` | Core, `Sluice.Platform.Windows`, Support | `Sluice.Windows.slnf` |
| `tests/Sluice.Tests.Support` (library) | `net10.0` | Core | the three above |

`dotnet test Sluice.Linux.slnf` (on Linux) and `dotnet test Sluice.Windows.slnf` (on Windows) build only Core, that
OS's platform module and the tests, so the Linux filter needs no Windows targeting. Core and `Sluice.Platform.Linux`
grant `InternalsVisibleTo` to the test projects that use their internals. `[WindowsFact]` / `[LinuxFact]` (Support)
skip a test on the other OS.

Kinds of tests (file names; the platform projects are marked):

| Kind | Files | Needs |
|---|---|---|
| Pure logic | `RuleEngineTests`, `DnsTests`, `PacketReflectorTests` (Windows), `CaptureFilterTests` (Windows), `PasswordEncryptionTests` (DPAPI mode: `WindowsPasswordEncryptionTests`), `PpxSerializerTests`, `SluiceProfileTests`, `ShareLinkTests`, `TunnelTransportTests`, `SingBoxConfigTests` (in `SingBoxTests.cs`), most of `LinuxCaptureTests` (Linux; nft script, netlink messages, `/proc` parsing) | nothing |
| Loopback end to end | `EngineTests` (own-process and loop-prevention cases: `WindowsEngineTests`), `ProxyTests`, `RelayTests`, `UdpTests` (`UdpNatTests`: Windows), `LinuxRelayTests` (Linux), `HappyEyeballsTests`, `CheckerAndStatsTests`, `StaleFakeAddressTests` | local test servers from `Sluice.Tests.Support` (`EchoServer`, `Socks5TestServer`, `Socks4TestServer`, `HttpConnectTestServer`) |
| Live sing-box | `LiveSingBoxTests` (in `SingBoxTests.cs`), parts of `SshSingBoxTests` | `sing-box.exe` next to the tests (the Windows build puts it there; the tests look for that name, so elsewhere they pass trivially): a throwaway sing-box **server** on 127.0.0.1 for every protocol, Sluice's client side through `TunnelBackends`. REALITY also needs `SLUICE_TEST_INTERNET=1` |
| Live SSH | `LiveSshTests` | opt-in: `SLUICE_LIVE_SSH_PORT`, `_USER`, `_KEY` for an sshd on 127.0.0.1; otherwise they pass trivially |
| Linux only | `LinuxCaptureTests.Socket_owner_is_found_through_proc` | returns early elsewhere |

Real capture is tested outside xUnit: `scripts/linux/capture-livetest.sh` (runs `Sluice.Cli --intercept` as root with
a profile from `scripts/linux/make-test-profile.cs` and `scripts/linux/test-socks5.py` as a stand-in tunnel, then checks
that curl is tunneled, wget blocked, DNS faked and the nft table gone after stop), and by hand on Windows
(`Sluice.Cli --intercept`). Tests that misbehave on a developer machine (a running capture, timing under load)
are listed in [core/FIXES.md §2](../core/FIXES.md#2-tests-that-depend-on-the-developer-machine).

## Build-time dependencies: `build/Dependencies.targets`

No third-party binaries are committed. `Sluice.Core.csproj` imports `build/Dependencies.targets`, so every project that
references Core gets them next to its output:

```
first build ─▶ SluiceFetchDependencies (before AssignTargetPaths)
                 ├─ sing-box 1.14.4 for the target OS (zip / tar.gz)   ─┐ DownloadFile → GetFileHash (SHA-256)
                 └─ WinDivert 2.2.2-A (Windows only)                     ─┘ mismatch → delete + build error
                 extracted into .deps/ (git-ignored), reused by later builds (offline)
every build ─▶ copy sing-box(.exe), WinDivert.dll, WinDivert64.sys and their licences to the output
```

The OS comes from the runtime identifier (`-r win-x64` / `linux-x64`), otherwise from the build machine.
`-p:SluiceFetchDependencies=false` turns fetching off (offline builds, the Linux release package); Sluice still builds,
and capture on Windows or encrypted tunnels then report their helper as missing. `scripts/get-sing-box.ps1` /
`scripts/get-sing-box.sh` fetch sing-box by hand.

Other dependencies are NuGet packages: Avalonia 12 (desktop), SSH.NET (Core), xUnit (tests).

## Packaging and releases

**Release packaging lives on its own branches, `release/linux` and `release/windows`, never on `main`;** `main` holds
code, tests and docs only, and release branches are not merged back. They are kept up to date by merging `main` into
them. The owner has plans for `scripts/` and packaging: discuss before changing them.

What the release branches hold:

- **Linux** (`release/linux`): `scripts/package-linux.sh` stages a framework-dependent publish of `Sluice.Desktop` (`-f net10.0`) and
  `Sluice.Cli` into `sluice-linux-x64.tar.gz`, drops Windows-only files and sing-box, and adds
  `packaging/linux/install.sh` / `uninstall.sh` / `sluice.desktop` / `README.txt`. `install.sh` installs .NET 10,
  nftables, libcap and desktop libraries with the system's package manager, downloads the pinned sing-box (or uses an
  archive next to it), and grants the capture capabilities with `setcap`. User guide: [linux/INSTALL.md](../linux/INSTALL.md).
- **Windows** (`release/windows`): an Inno Setup installer (`packaging/windows/`), not yet reviewed for release
  ([REQUIRED.md §4](../REQUIRED.md#4-windows-release-package)).
- Remaining release work and CI: [REQUIRED.md §5–6](../REQUIRED.md#5-linux-release-remaining).
