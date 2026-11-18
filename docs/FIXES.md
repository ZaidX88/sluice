# Known bugs

Each entry: what was seen, why, what to do, and when it counts as fixed. Priorities: **high** = breaks normal use.

## 1. Windows: WSL / VMs get fake DNS answers they can't use — high

**Seen:** inside WSL2 (likely also Hyper-V VMs and Docker), names resolve to 198.18.x.x while capture is on in Windows,
and connections to them time out (`dot.net → 198.18.0.57`). Direct IPs work.

**Why:** WSL sends DNS to the Windows host's DNS proxy, whose upstream query is captured and faked by `Interceptor`
(`DnsRedirector.TryAnswer` doesn't know who asked). WSL's TCP to the fake address is then *forwarded* through the Hyper-V
NAT and never reaches the relay (no owning PID; `PacketReflector` reflects local flows only).

**To do:**
1. Confirm how the forwarded SYN appears to WinDivert (`Sluice.Cli --intercept --log debug`, `DiagnosticPackets`).
2. Fix by **(a)** not faking DNS for the host DNS proxy (svchost "SharedAccess"/HNS) — simpler and safer — or **(b)**
   making forwarded flows to fake addresses reach the relay.
3. Later, an option "Capture WSL / virtual machines" if (b) works.

**Fixed when:** with Windows capture on, `curl https://dot.net` works inside WSL.

## 2. Fake DNS answers outliving capture — investigate

**Seen once** with the old WPF build: capture reported as paused, yet `Resolve-DnsName www.google.com` still returned
`198.18.0.25` after `ipconfig /flushdns`.

**To do:** in the Avalonia app, stop capture every way (capture button, tray "Capture traffic", quit). After each,
`Resolve-DnsName` must return a real address within a second. Confirm `Interceptor.DisposeAsync` (which flushes the OS
cache when fakes were handed out) runs on every stop path; add a log line.

## 3. Layout at narrower window sizes

**Seen** at about 1040×650 DIP: on **Tunnels** with the check panel open, the Status column disappears and Session is cut;
on **Statistics** the "Share of traffic" header is cut.

**To do:** let the check panel shrink or move below the list under a width threshold; hide Address and Session before
Status; shorten the header to "Share"; add `--screenshot DIR --size 1040x650` to keep it checked.

## 4. sing-box output logged at the wrong level

`SingBoxSession.cs` logs every helper line at Normal, so sing-box `ERROR[…]` lines show as INFO and are long and
repetitive. **To do:** parse sing-box's level prefix and map it to `LogLevel`; raw helper lines only at Verbose; at Normal,
one short line per distinct error, repeats collapsed.

## 5. Avalonia UI gaps (small)

- **Dark title bar on Windows** (the WPF app used DWM `DWMWA_USE_IMMERSIVE_DARK_MODE`): add it via `IPlatformServices`.
- **Screenshot mode** renders at 1× because `RenderTargetBitmap` mis-scales DataGrid content above 100% DPI (screenshots
  only; `SLUICE_SHOT_SCALE` overrides). For real-window captures use `PrintWindow` with `PW_RENDERFULLCONTENT` from a
  DPI-aware process.
- **Linux tray:** Avalonia uses StatusNotifierItem; GNOME needs the AppIndicator extension. Without a tray, closing the
  window hides the app with no way back except relaunching. Detect a missing tray and quit on close instead.

## 6. Linux capture limitations

- UDP of tunneled apps is dropped (see [REQUIRED.md](REQUIRED.md#2-udp-through-tunnels-on-linux)).
- IPv6 extension headers aren't parsed in `PacketInfo.TryParse`; such first packets pass uncaptured.
- With no non-loopback upstream DNS (e.g. dnsmasq without systemd-resolved), DNS isn't captured (a log line says so).
  TPROXY for UDP would solve it (the original destination comes with each datagram).
- `LinuxProcessLookup` scans `/proc/*/fd` on a miss. Fast in tests (20 parallel connections in ~0.15 s); on machines with
  tens of thousands of fds consider `NETLINK_SOCK_DIAG` + a background inode→pid cache.
- Running the GUI as root breaks notifications (no session bus) and Wayland may refuse it. The installer's `setcap` avoids
  that; long-term a privileged service (see [FEATURES.md](FEATURES.md)).

## 7. Tests that depend on the developer machine

- `HappyEyeballsTests.DeadFirstAddress_DoesNotDelayTheWorkingOne` fails whenever a Sluice capture is running on the same
  machine (the relay accepts every connection). Make the test detect that and skip.
- `Sluice.Cli.exe` has no Windows Firewall allow rule on Private networks, so CLI capture tests fail there. Give the CLI
  `--allow-firewall` like the app's banner (`EngineHost.AllowThroughFirewall`), or document it.
- `scripts/linux/test-socks5.py` relays no data on Windows (fine for its Linux job). On Windows use
  `Sluice.Cli --socks 127.0.0.1:<port>` as the stand-in tunnel.
- Any test making real connections misbehaves while a capture runs on the same machine (fake DNS and routing apply to the
  test's helpers too).
- **Live sing-box tests are timing-sensitive under load:** in a full `dotnet test` run, 1–3 of them (different ones each
  time, e.g. `Udp_EchoThroughTunnel(vless-grpc)`, `Hysteria2WithObfs`, `Checker_WorksThroughSingBox…`,
  `Udp_EchoThroughTunnel(ss)`, `Lifecycle_IdleStop_ThenRestartOnUse`, `BusyTunnel_FailedCheck_IsNotRestarted`) sometimes
  fail, often at start-up with "the tunnel started but no test connection through it worked", while
  `--filter FullyQualifiedName~LiveSingBoxTests` passes on its own. Give their waits more headroom, or run the live class
  without parallelisation (`[Collection]` with `DisableParallelization`).
- **On Linux, 13 of 276 tests fail** (real machine, Ubuntu 26.04; every Linux test passes). Fix in the tests (Core):
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
    `queued` during the test; compare Linux socket buffer autotuning).

## 8. Windows capture seemed slower than the SOCKS listener — fixed

**Status:** the main cause, Sluice's health check restarting a busy tunnel (§9), is fixed (merged
2026-11-17). Sluice's per-packet path is measured and no longer limits throughput at this tunnel's speed. With the same
server and the browser's DNS going through Sluice, Chromium and curl get the same speed captured as through the SOCKS
listener. The owner's remaining browser speed-test gap is explained by the test setup (run 4). Final confirmation: the
owner runs the browser speed test on the same hand-picked server in both modes.

**Seen** (same SSH tunnel, ~1.9 Mbps):
- Run 1, browser speed test: SOCKS listener ~1.97/1.99 Mbps (loaded ping 2.2 s); capture 0.86/1.08 Mbps (loaded ping
  7.1 s); `ssh -D` and Proxifier ~2.0/2.0 Mbps.
- Run 2 (2026-11-16, first build from `perf/capture-throughput`): capture 1.43/1.15 Mbps; SOCKS 2.04/1.77 Mbps. The
  tunnel was torn down twice during the capture test (cause 1).
- Run 3 (2026-11-17, with the §9 fix; no tunnel restarts): browser: capture 1.35/1.21 Mbps; SOCKS 2.04/1.93 Mbps.
  `curl.exe` to the same test server, alternating, bytes/s:

  | Test | Capture | SOCKS listener |
  |---|---|---|
  | One 3 MB download | 240,391 / 235,108 | 237,113 / 238,856 |
  | 6 parallel 700 KB downloads | 239,567 / 236,522 | 243,304 / 242,398 |
  | One 1.5 MB upload | 233,692 / 233,680 | 233,672 / 220,285 |

- Run 4 (2026-11-17, browser and Sluice logs): browser: capture 1.06/1.23 Mbps; SOCKS 1.92/1.96 Mbps. The speed test
  picked **a different server** in each mode (server A by hostname through SOCKS, server B by bare IP when captured), and
  when captured the browser's connections went to bare IPs (its own Secure DNS, §10). curl, 2 MB, both servers: full
  tunnel rate through both paths (one first visit to server B waited 5 s for a slow name lookup on the SSH server,
  FEATURES #8).
- Run 5 (2026-11-17, headless Chrome, same server A, Secure DNS off, back to back), bytes/s:

  | Test | Capture | SOCKS listener |
  |---|---|---|
  | 1 × 3 MB | 206,583 (first byte 1.9 s) | 224,660 (first byte 1.2 s) |
  | 4 × 1 MB | 198,582 | 201,146 |
  | 6 × 700 KB | 149,422 | 139,844 |

  (6 × 700 KB: both modes had 6–8 s first bytes with the tunnel full.)

**Why:**
1. **Tunnel restarts during a test** (runs 1 and 2): once Sluice's health check restarted a busy tunnel (§9, fixed);
   once the SSH connection itself dropped and the tunnel server's name didn't resolve for ~50 s (network side).
2. **The browser tests weren't like for like** (runs 3 and 4): the speed test picks its server by its own ping and
   picked different servers in each mode; when captured, the browser resolved names itself (§10); one server's first
   visits hit the SSH server's slow name lookups.
3. **Per-packet path, measured.** Over 105,008 packets Sluice holds a packet 0.04 ms on average (max 16 ms); a
   new-connection decision takes 0.38 ms on average (max 20 ms); the owner always came from the socket event, never the
   TCP table. WinDivert's own delivery delay is 7 ms on average, 7–18 ms on average (66 ms max) in bursts of 3,000–4,000
   packets/s, mostly sing-box's own tunnel traffic, which the filter then still caught (no longer, see below). Not limiting at ~2 Mbps; may matter
   on faster tunnels.
4. Minor: app↔relay traffic is hairpinned through the adapter path, not true loopback. With UDP mode `bypass` on a
   network where direct UDP is blocked, Chrome's QUIC attempts fail before it falls back to TCP.

Proxifier redirects TCP in the kernel at connect time with a WFP callout driver (ALE connect-redirect layer), so data
packets never reach user mode and the driver supplies the PID.

**Done** (`perf/capture-throughput`, merged 2026-11-17): the busy-tunnel health check fix (§9); WinDivert queue at its maximum
(16384 packets / 32 MB); new-flow decisions on 4 worker threads (`PacketReflector.DeferDecisions`, `CompleteDeferred`);
wait up to 3 ms for the sniffed owner before the table scan; log strings built only when needed; timing counters
(`LatencyStats`).

**How to reproduce:** headless Chrome with a throwaway profile and its built-in DNS client and DoH off:
`--headless --disable-web-security --user-data-dir=<temp dir>`, plus `--proxy-server=socks5://127.0.0.1:1080` for the
SOCKS run. Open a page that times parallel `fetch()` downloads from a speed-test server's `/download` endpoint. Run
captured and through SOCKS back to back, same server; compare with `curl` and `curl --socks5-hostname 127.0.0.1:1080`.

**Narrowed capture filter** (`perf/narrow-capture-filter`, 2026-11-17): before it, every outbound TCP packet on the PC
went through Sluice. Counted with a passive sniff handle during a 10 MB download (~1.2 MB/s): 54% relay→app data
(MTU-sized), 27% app ACKs, **19% the tunnel helper's own packets to the SSH server**; through the SOCKS listener 97% of
what Sluice handled was the helper's. Now only packets Sluice may rewrite are captured
([STATUS.md](STATUS.md#how-capture-works)): captured download 10,600 packets handled (all rewritten), SOCKS download
282. WinDivert queue wait during downloads 0.5–0.8 ms on average (max 6.7 ms), was 7–18 ms in bursts. Speeds (bytes/s,
same server, back to back): curl 6 × 700 KB capture 1.25 M / 1.58 M vs SOCKS 1.10 M / 1.36 M; headless Chrome
4 × 1 MB 1.51 M vs 1.48 M and 6 × 700 KB 1.53 M vs 1.46 M (averages of 3); single 5 MB, 5 rounds: curl 1.42 M vs
1.24 M, Chrome 1.33 M vs 1.49 M. Equal within the tunnel's noise. Plain-IP redirects (followers' handle): 5 MB at
1.9 MB/s, and a 10 MB download completed intact while the filter was reopened 4 times.
100 MB (10 × 10 MB on one connection; the test server ends any response after ~20 s), each build started fresh, back
to back: before: capture 1.51 / 1.46 MB/s, SOCKS 1.55 / 1.19, Sluice CPU per 100 MB 11.9–12.7 s captured; after:
capture 1.82 / 1.51, SOCKS 1.48 / 1.56, CPU 2.9 s captured and 1.1–1.3 s through SOCKS. Not yet explained: the same
build used 7.5–9.1 s per 100 MB after running 38 minutes (with `--log-file`, so at Debug level). Checked afterwards:
not reproduced at 17–25 minutes, after 500 extra connections, after browser runs, or with the window visible (230–515 ms
per 10 MB captured, 31–266 ms through SOCKS); the high readings fell in one 12-minute window and hit both builds and
SOCKS mode too, so they came from outside Sluice's state. The "Relay (5 s)" log line (reads, read size, CPU per MB;
[STATUS.md](STATUS.md#how-capture-works)) now shows the cause if it happens again.

**Speed-test pitfall:** run the two modes within seconds of each other on the same server, and make sure every test
browser has exited (a runner whose cleanup failed left headless Chrome instances running and spread the runs over
minutes, which looked like a 2–3× gap).

**Later** (possible improvements, for faster tunnels):
- Batch with `WinDivertRecvEx`/`SendEx`.
- App↔relay packets are MTU-sized (adapter path, no LSO on Wi-Fi): routing the fake range through loopback could cut
  their number ~45× (needs a route while capture runs; experiment).
- **Own WFP connect-redirect driver** (like Proxifier): no per-packet work at all, but needs driver signing and reverses
  the 2026-11-10 WinDivert decision. Not needed for now; the owner's decision.

**Fixed when**, with no tunnel restart during the tests, on the same tunnel and the same server:
- `curl` downloads (single and parallel) and uploads through capture are within ~10% of the SOCKS listener. **Passes**
  (runs 3, 4).
- A Chromium browser's downloads through capture are within ~10% of the SOCKS listener. **Passes** (run 5).
- The owner's browser speed test on the same hand-picked server, both modes. **Pending.**

## 9. Health check restarts a busy tunnel — fixed

**Seen:** during a speed test Sluice restarted a working tunnel, resetting every connection through it.

**Why:** the periodic check opens a new connection through the tunnel within `TunnelBackends.ProbeTimeout` (6 s, 3 s per
test target). Through a saturated SSH tunnel opening a new channel takes longer, so two checks failed in a row.

**Fix:** a failed check is ignored while the tunnel delivered data within `TunnelBackends.BusyGrace` (10 s); the relay
calls `TunnelBackends.NoteData` as data arrives. Live test: `BusyTunnel_FailedCheck_IsNotRestarted`.

## 10. Browsers' own Secure DNS bypasses Sluice's DNS — owner's decision

**Seen:** when captured, almost all of a Chromium-based browser's connections reach Sluice as bare IPs; through the
SOCKS listener the same browser sends hostnames.

**Why** (most likely): without a proxy, the browser resolves names itself with DNS over HTTPS ("Secure DNS"), so
Sluice's fake DNS never sees them. Hostname rules then don't match that browser's traffic, and its name lookups go
through the tunnel as ordinary HTTPS.

**Options** (the owner's choice, not decided): document that browsers' Secure DNS should be turned off while capturing;
or have Sluice block or redirect well-known DoH endpoints so browsers fall back to the system resolver.

## 11. DNS settings changed while capture runs are ignored until capture restarts — fixed

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

## 12. IPv6 destinations fail through a tunnel whose server has no IPv6 — medium

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

## 13. Building on Linux needs `EnableWindowsTargeting` — high (for contributors)

**Seen** ([issue #1](https://github.com/ZaidX88/sluice/issues/1)): on Linux, `dotnet build src/Sluice.Desktop -f net10.0`,
`dotnet build src/Sluice.Cli` and `dotnet test tests/Sluice.Tests` fail at restore with NETSDK1100.

**Why:** `-f net10.0` limits the build, not the restore. `Sluice.Desktop` targets `net10.0-windows10.0.19041.0;net10.0`,
and restore also restores the Windows target and its reference to `Sluice.Platform.Windows`.

**Workaround:** add `-p:EnableWindowsTargeting=true` to each command (documented in [linux/BUILD.md](linux/BUILD.md)).

**Fix:** set `<EnableWindowsTargeting>true</EnableWindowsTargeting>` in a shared `Directory.Build.props` (it only allows
restoring Windows targets; it doesn't change what gets built). Also add `out/` to `.gitignore`: the docs build into
`out/` and it shows up as untracked ([issue #6](https://github.com/ZaidX88/sluice/issues/6)).

**Fixed when:** the commands in `AGENTS.md`, `CONTRIBUTING.md` and `linux/BUILD.md` work as written on Linux.

## 14. No log file unless "file log" is turned on — low (UI)

**Seen** ([issue #7](https://github.com/ZaidX88/sluice/issues/7)): `AppSettings.FileLogEnabled` defaults to `false`, so a
new install writes no log. After a problem there is nothing to attach to a report. On Linux it matters more: the
`setcap` app is not dumpable, so the log is the only outside view of what capture decided.

**Options:** turn the file log on by default at Verbose (daily files are small; capture writes a timing line every 5 s
at Verbose, so check the daily size first), or always keep the last few thousand lines in a rolling file.

**Fixed when:** a fresh install has a log of the last session to attach to a report.
