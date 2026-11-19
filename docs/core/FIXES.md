# Known bugs: Core

Bugs and limits whose fix belongs to the **Core** area ([CONTRIBUTING.md → Areas](../CONTRIBUTING.md#areas)). The other
areas: [Linux](../linux/FIXES.md), [Windows](../windows/FIXES.md), [UI](../ui/FIXES.md).

Each entry: what was seen, why, what to do, and when it counts as fixed, with the GitHub issue that tracks it.
Priorities: **high** = breaks normal use. An entry is kept after the fix, marked **fixed**, when its cause or evidence
is worth keeping.

## 1. sing-box output logged at the wrong level

`SingBoxSession.cs` logs every helper line at Normal, so sing-box `ERROR[…]` lines show as INFO and are long and
repetitive. **To do:** parse sing-box's level prefix and map it to `LogLevel`; raw helper lines only at Verbose; at
Normal, one short line per distinct error, repeats collapsed.

## 2. Tests that depend on the developer machine

- `HappyEyeballsTests.DeadFirstAddress_DoesNotDelayTheWorkingOne` fails whenever a Sluice capture is running on the same
  machine (the relay accepts every connection). Make the test detect that and skip.
- `Sluice.Cli.exe` has no Windows Firewall allow rule on Private networks, so CLI capture tests fail there. Give the CLI
  `--allow-firewall` like the app's banner (`EngineHost.AllowThroughFirewall`), or document it.
- `scripts/linux/test-socks5.py` relays no data on Windows (fine for its Linux job). On Windows use
  `Sluice.Cli --socks 127.0.0.1:<port>` as the stand-in tunnel.
- Any test making real connections misbehaves while a capture runs on the same machine (fake DNS and routing apply to
  the test's helpers too).
- **Live sing-box tests are timing-sensitive under load:** in a full `dotnet test` run, 1–3 of them (different ones each
  time, e.g. `Udp_EchoThroughTunnel(vless-grpc)`, `Hysteria2WithObfs`, `Checker_WorksThroughSingBox…`,
  `Udp_EchoThroughTunnel(ss)`, `Lifecycle_IdleStop_ThenRestartOnUse`, `BusyTunnel_FailedCheck_IsNotRestarted`) sometimes
  fail, often at start-up with "the tunnel started but no test connection through it worked", while
  `--filter FullyQualifiedName~LiveSingBoxTests` passes on its own. Give their waits more headroom, or run the live
  class without parallelisation (`[Collection]` with `DisableParallelization`).
- **On Linux, 13 of 276 tests failed** (real machine, Ubuntu 26.04; every Linux test passed). The 12 Windows-specific
  ones moved to `tests/Sluice.Tests.Windows` when the tests were split by platform (issue #18); Linux versions are
  Linux work ([linux/WORK.md](../linux/WORK.md)). What was found:
  - 12 assume Windows ([issue #2](https://github.com/ZaidX88/sluice/issues/2)): 5 use DPAPI (`PasswordEncryptionTests`
    `CurrentUser`/`Account` modes, `SluiceProfileTests…(CurrentUser)`), 5 match `C:\…\*.exe` application paths
    (`RuleEngineTests.Applications` cases, `AllConditionsMustMatch`, a `UdpRoutingTests.ClassifyUdp` case),
    `EngineTests.LoopPrevention_LocalProxyHostGoesDirect` starts `powershell.exe`, and
    `EngineTests.ApplicationRule_MatchesThisTestProcess` gets PID 0 because the test never sets
    `ProcessLookup.Backend` to the Linux `/proc` lookup (the app does in `LinuxPlatform.OnAppStart`). Skip the
    Windows-only cases off Windows (or give them Linux paths) and set the backend in the engine test.
  - `RelayTests.SlowApp_DoesNotBlockUpstream_OnlyWithElasticBuffer(8 MB, 4 MB, drained)` fails every run, also alone
    ([issue #3](https://github.com/ZaidX88/sluice/issues/3)): the upstream isn't drained within 3 s although the elastic
    buffer should take all 4 MB. Not reproduced yet (no Linux SDK on the owner's PC). It matters beyond the test: SSH
    routes use this buffer on Linux too, so check first whether the relay or only the test misbehaves (count reads and
    `queued` during the test; compare Linux socket buffer autotuning). Split into #32 (Linux: investigate) and #33
    (Core: fix, blocked by #32).

## 3. Health check restarts a busy tunnel — fixed

**Seen:** during a speed test Sluice restarted a working tunnel, resetting every connection through it.

**Why:** the periodic check opens a new connection through the tunnel within `TunnelBackends.ProbeTimeout` (6 s, 3 s per
test target). Through a saturated SSH tunnel opening a new channel takes longer, so two checks failed in a row.

**Fix:** a failed check is ignored while the tunnel delivered data within `TunnelBackends.BusyGrace` (10 s); the relay
calls `TunnelBackends.NoteData` as data arrives. Live test: `BusyTunnel_FailedCheck_IsNotRestarted`.

## 4. Browsers' own Secure DNS bypasses Sluice's DNS — owner's decision

**Seen:** when captured, almost all of a Chromium-based browser's connections reach Sluice as bare IPs; through the
SOCKS listener the same browser sends hostnames.

**Why** (most likely): without a proxy, the browser resolves names itself with DNS over HTTPS ("Secure DNS"), so
Sluice's fake DNS never sees them. Hostname rules then don't match that browser's traffic, and its name lookups go
through the tunnel as ordinary HTTPS.

**Options** (the owner's choice, not decided): document that browsers' Secure DNS should be turned off while capturing;
or have Sluice block or redirect well-known DoH endpoints so browsers fall back to the system resolver.

## 5. DNS settings changed while capture runs are ignored until capture restarts — fixed

**Seen** (Linux, real machine; [issue #5](https://github.com/ZaidX88/sluice/issues/5)): turning on "Resolve hostnames
through the tunnel" (or leak prevention, the local-names list, "block non-A queries") while capture runs has no effect:
the switch shows on, but DNS answers stay real until capture is stopped and started again.

**Why** (confirmed in the code): both captures cache the `DnsPolicy` and rebuild it only when the engine's profile is a
different object (`ReferenceEquals` in `Interceptor.CurrentPolicy` and `LinuxCapture.CurrentPolicy`). The Settings page
edits `Profile.Options` in place and `EngineHost.CommitProfileEdit` → `RoutingEngine.Reload(Profile)` passes the same
object, so the old policy stays. `NeedsRestartFor` doesn't cover DNS settings either.

**Fix, Windows** (issue #5): `Dns/DnsPolicyCache` rebuilds the policy when the setting *values*
change (`DnsPolicyCache.Settings`: via-tunnel, leak prevention, only-from-list, block non-A, exclusion list) and raises
`Changed`. `Interceptor` uses it for every DNS query and also checks it every 5 s in `SweepLoop`, because apps holding
cached answers may not ask again; on a change it logs "Name resolution through tunnels is now ON/OFF" and flushes the
Windows DNS cache (off the packet thread). Tests: `DnsPolicyCacheTests`. Checked live: with capture running, switching
the setting off gave real addresses for names cached as fake a moment before; switching it on gave 198.18.x addresses
again within 5 s, and pages loaded through them.

**Fix, Linux** (PR #13, issue #12): `LinuxCapture` uses the same Core `DnsPolicyCache`, checks it once a second (names
in systemd-resolved's cache never reach Sluice), logs the same lines as Windows and runs `resolvectl flush-caches` on a
change. Checked live on Ubuntu 26.04: lookups switched between real and 198.18.x addresses within a second, without a
capture restart. (PR #13 first used a copy of the cache in the Linux module; it was replaced by the Core one.)

**Fixed when:** switching "Resolve hostnames through the tunnel" on during capture makes the next lookup return a
198.18.x address, and switching it off returns real addresses, on both OSes, without restarting capture. Passes on
both.

## 6. IPv6 destinations fail through a tunnel whose server has no IPv6 — medium

**Seen** ([issue #4](https://github.com/ZaidX88/sluice/issues/4)): with capture on and an SSH tunnel through sing-box,
connections to IPv6 addresses fail ("ssh: rejected: connect failed", then "SOCKS5: general SOCKS server failure") when
the SSH server has no IPv6. Seen for apps that resolved names before capture started (so they hold real IPv6 addresses,
not 198.18.x placeholders) and with fake DNS off. With "Resolve hostnames through the tunnel" on, new lookups get IPv4
placeholders and work.

**Why:** a bare IPv6 destination can only be opened by the tunnel server as IPv6; Sluice has no hostname to retry over
IPv4, and the app's own fallback (Happy Eyeballs) costs a round trip through the tunnel per attempt.

**Options:** reset such connections quickly so the app falls back at once; remember per tunnel that IPv6 targets fail
and refuse them immediately for a while; at least document it as a limit of tunnels without IPv6. Either way, flushing
the system DNS cache when capture starts (already done on Windows) reduces how often apps hold real addresses.

**Fixed when:** with an IPv4-only tunnel, an app holding an IPv6 address for a dual-stack site reaches it over IPv4
without a visible delay, or the limit is documented in QUICKSTART.

**Parts:** #29 (Core: shared IPv6 fallback tracker with a narrow trigger) first; then #30 (Linux, PR #22) and #31
(Windows), both blocked by #29.

**Core part done (#29):** `RoutingEngine.Ipv6Fallback` marks a tunnel or chain route for 10 minutes when connections
to at least 2 different IPv6 addresses failed before they opened with a SOCKS5 "general failure" / "network
unreachable" / "host unreachable" reply within 60 s, while the same route opened an IPv4 or hostname connection in
that time and no IPv6 one. sing-box and SSH.NET report the SSH "connect failed" of this issue as a general failure,
so the reply code alone can't be narrower. The captures ask `ShouldRefuse(route)` for each new IPv6 flow; see
[engine.md](../architecture/engine.md#ipv6-fallback-engineipv6fallbackcs).

## 7. Building on Linux needed `EnableWindowsTargeting` — fixed

**Seen** ([issue #1](https://github.com/ZaidX88/sluice/issues/1)): on Linux, `dotnet build src/Sluice.Desktop -f
net10.0`, `dotnet build src/Sluice.Cli` and `dotnet test tests/Sluice.Tests` fail at restore with NETSDK1100.

**Why:** `-f net10.0` limits the build, not the restore. `Sluice.Desktop` targets `net10.0-windows10.0.19041.0;net10.0`,
and restore also restores the Windows target and its reference to `Sluice.Platform.Windows`.

**Fix:** `Directory.Build.props` at the repository root sets `<EnableWindowsTargeting>true</EnableWindowsTargeting>`
for every project. It only allows restoring Windows targets; it doesn't change what gets built. The test filter
`Sluice.Linux.slnf` (issue #18) never needed it. `out/`, where the docs put build output, is now in `.gitignore`
([issue #6](https://github.com/ZaidX88/sluice/issues/6)).

**Fixed when:** the commands in `AGENTS.md`, `CONTRIBUTING.md` and `linux/BUILD.md` work as written on Linux. Builds on
Windows unchanged; still to be confirmed on a Linux machine.
