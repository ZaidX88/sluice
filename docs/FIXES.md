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
- The relay listens on `[::]:random` (reachable from the LAN; unknown clients are closed). Bind 127.0.0.1 and ::1 instead,
  like the DNS server.
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
build used 7.5–9.1 s per 100 MB after running 38 minutes (with `--log-file`, so at Debug level); worth checking whether
something grows with uptime.

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
