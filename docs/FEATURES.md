# Features and testing

Ideas not built yet (roughly by value), performance work, and how to build and test.

## Features

1. **SSH client selector** in the tunnel dialog (`src/Sluice.Desktop/Views/TunnelDialog.axaml`). The setting
   `sshclient` = `auto | singbox | sshnet` exists but can only be edited in the profile file.
2. **UDP for the local SOCKS5 listener:** `Socks5Inbound` has no UDP ASSOCIATE, so apps pointed at `127.0.0.1:1080` get
   no UDP.
3. **Chains editor page.** Chains work in the engine and import from `.ppx`, but the UI can't create or edit them.
4. **Kill switch:** when every tunnel a rule uses is down (`TunnelBackends` states), block that rule's traffic instead of
   failing per connection, with "allow LAN" as an exception.
5. **Faster failover:** redundancy chains skip tunnels the health monitor knows are down.
6. **Restore after sleep:** reconnect tunnels and flush stale fake-DNS entries on resume, not only on network change.
7. **Service / daemon mode:** a Windows service and a systemd unit run the engine with the needed rights; the UI becomes a
   client over a local pipe/socket. Fixes start-at-login with rights on both OSes and the root-GUI problems on Linux.
8. **Local DNS option per tunnel:** resolve on the PC and send the server an IP, for servers with slow DNS (one SSH server
   took ~5 s per first visit). Opt-in, because DNS then leaves the tunnel.
9. **Rules from domain lists** (geosite / plain text import).
10. **Per-app quick toggle** on the Connections page (right-click: always direct / via tunnel X) that creates the rule.
11. **Speed and latency test for all tunnels**, sortable; auto-pick the fastest for load-balancing chains.
12. **Scheduled subscription updates** (e.g. every 12 h) with a notification summary.
13. **Traffic history** saved to disk (per day, per app) with charts on the Statistics page.
14. **Tray quick switching** (profile, tunnel) and a global hotkey for capture.
15. **DNS over HTTPS** for Sluice's own resolver.
16. **Capture WSL / VMs on Windows** (see [FIXES.md](FIXES.md#1-windows-wsl--vms-get-fake-dns-answers-they-cant-use--high)).
17. **Demo traffic for screenshot mode** (`--screenshot DIR --demo-traffic`): real traffic through the SOCKS listener
    (headless browser with its own profile, rate-limited `curl` downloads, a blocked tracker, a stand-in SOCKS5 tunnel via
    `Sluice.Cli --socks 127.0.0.1:6002`) so screenshots look alive without a real profile.
18. **Diagnostics bundle:** one button that zips logs, settings and the profile with secrets removed.

## Performance

- **Windows capture is single-threaded:** the WinDivert loop measured ~16–35 MB/s locally versus ~400 MB/s through the
  SOCKS listener. Use several worker threads with `WinDivertRecvEx` batching.
- **Process lookup runs on the packet thread** (both OSes) and holds up other packets. Move it to a worker and give
  verdicts asynchronously (NFQUEUE allows out-of-order verdicts; WinDivert needs re-injection from the worker).
- **UDP "through tunnels" on Windows** sends every UDP packet through Sluice. Exclude LAN, multicast and always-direct
  ports in the WinDivert filter.

## Building

Needs the .NET 10 SDK. The first build downloads WinDivert and sing-box into `.deps/` (pinned versions, SHA-256 checked;
see `build/Dependencies.targets`); add `-p:SluiceFetchDependencies=false` to skip that.

```bash
dotnet build src/Sluice.Desktop -f net10.0-windows10.0.19041.0   # Windows app
dotnet build src/Sluice.Desktop -f net10.0                       # Linux app (builds on Windows too)
dotnet build src/Sluice.Cli
```

OS-specific notes: [windows/BUILD.md](windows/BUILD.md), [linux/BUILD.md](linux/BUILD.md).

## Testing

```bash
dotnet test tests/Sluice.Tests                                         # all 265 tests, incl. live sing-box/SSH tests
dotnet test tests/Sluice.Tests --filter "FullyQualifiedName!~Live"     # without the live ones
```

- **Live tests** start real sing-box servers on 127.0.0.1 for every protocol (TCP and UDP) and connect through Sluice;
  they need the sing-box binary the build fetches. REALITY additionally needs `SLUICE_TEST_INTERNET=1`.
- **UI check:** `Sluice.Desktop --screenshot <dir> [--theme dark]` renders every page and dialog off-screen. After UI
  changes, compare both themes on Windows and Linux.
- **Real servers:** `Sluice.Cli check "<share link>"` runs the three-step tunnel check. Public lists of free servers are
  useful for compatibility testing (expect many dead entries; failures need a second look before blaming Sluice).
- **Linux capture:** `scripts/linux/capture-livetest.sh` (root, WSL works) checks routing decisions, DNS and clean-up;
  see [linux/BUILD.md](linux/BUILD.md). Always test capture from an *installed* package before handing one out.
- **Environment pitfalls:**
  - while a Sluice capture runs on the same machine, tests that make real connections are affected (fake DNS, routing);
  - NuGet restores of large packages through a slow tunnel time out and restart forever: route `dotnet.exe` Direct, run
    `dotnet restore --disable-parallel`, or download the `.nupkg` with a resumable `curl -C -` and pass
    `-p:RestoreAdditionalProjectSources=<folder>`.
