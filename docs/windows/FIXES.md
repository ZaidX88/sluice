# Known bugs: Windows

Bugs and limits whose fix belongs to the **Windows** area ([CONTRIBUTING.md → Areas](../CONTRIBUTING.md#areas)). The
other areas: [Core](../core/FIXES.md), [Linux](../linux/FIXES.md), [UI](../ui/FIXES.md).

Each entry: what was seen, why, what to do, and when it counts as fixed, with the GitHub issue that tracks it.
Priorities: **high** = breaks normal use. An entry is kept after the fix, marked **fixed**, when its cause or evidence
is worth keeping.

## 1. Windows: WSL / VMs get fake DNS answers they can't use — fixed (NAT mode)

**Seen:** inside WSL2 (likely also Hyper-V VMs and Docker), names resolve to 198.18.x.x while capture is on in Windows,
and connections to them time out (`dot.net → 198.18.0.57`). Direct IPs work. Later, with UDP of tunneled apps blocked,
WSL got no DNS answers at all.

**Why:**
1. WSL (NAT mode) sends DNS to Windows' DNS proxy (the "SharedAccess" service) on the host's virtual adapter. The proxy
   asks the Windows DNS client, so the question reaches Sluice like any local app's and gets a fake address. Sluice
   can't tell them apart there: the proxy doesn't use its own ports for it.
2. WSL's connections are forwarded by Windows' NAT below anything WinDivert sees: a probe on the network and the
   forward layer saw none of them, to a fake address or a real one. So they can't reach the relay, and a fake address
   leads nowhere. WSL can't be tunneled this way.
3. The proxy's replies to WSL (from port 53) and its DHCP replies (67/68) counted as svchost's own UDP, so the UDP mode
   blocked them under a rule that tunnels everything.

**Fixed** (#42; WSL works with capture on, **not tunneled**, as without Sluice):
- WSL's DNS questions are caught as they arrive at the host (`Interceptor.VmDnsFilter`). Names Sluice would fake are
  answered there with real addresses from Sluice's own resolver (TTL 30 s); the proxy's own names (`*.mshome.net`,
  single-label names, `*.internal`, `.lan`, `.home`, `.localdomain`) and everything else go on to the proxy unchanged.
  Only when SharedAccess owns port 53.
- UDP from port 53 is never captured (a DNS server's reply), and SharedAccess's own UDP ports are left alone.
- Local apps are unchanged: the Windows DNS cache still holds their fake answers, and their tunnels work.

**Live run** (WSL 2.6, Ubuntu 24.04, NAT mode, capture on, name resolution through tunnels on): `dig` through the proxy
gave real addresses for `dot.net`, `www.example.com` and `github.com`, the PC's `*.mshome.net` name from the proxy, and
NXDOMAIN for a name that doesn't exist; `curl https://dot.net` 301, `https://www.example.com` 200,
`https://github.com` 200. On Windows, `dot.net` still resolved to its fake address and `curl` went through the tunnel.

**Not covered:** WSL with **DNS tunneling** (`dnsTunneling`, the default on Windows 11 22H2 and later): its questions
reach Windows without passing a network adapter, so they still get fake answers. Workaround: in WSL,
`/etc/wsl.conf` → `[network] generateResolvConf = false`, and a public DNS server in `/etc/resolv.conf`; or
`dnsTunneling=false` in `.wslconfig`. Mirrored networking isn't tested.

## 2. Fake DNS answers outliving capture — fixed (not reproduced)

**Seen once** with the old WPF build: capture reported as paused, yet `Resolve-DnsName www.google.com` still returned
`198.18.0.25` after `ipconfig /flushdns`. A flush can't leave a fake answer behind, so capture was most likely still
running in that build.

**Checked** (2026-11-19, #43) in the Avalonia app: every stop path (the capture button, the tray's "Capture traffic",
Exit, and the restart after a UDP-mode change) goes through `EngineHost.StopInterceptionAsync` →
`Interceptor.DisposeAsync`, which flushes the Windows DNS cache when fake addresses were handed out. Fake answers also
carry a 5 s TTL, so even a crash leaves them in the cache for 5 s at most. Each flush is now logged:
`Flushed the Windows DNS cache (capture started | capture stopped | name resolution settings changed)`.

**Live run** (name resolution through tunnels on; `Resolve-DnsName` polled every 200 ms for three names): after each
stop the names returned real addresses 0.09–0.11 s after the "capture stopped" flush: capture button 0.09 s, tray
"Capture traffic" 0.11 s, Exit 0.11 s. Turning name resolution on while capturing turned them fake 1.2 s after its
flush.

## 3. Windows capture seemed slower than the SOCKS listener — fixed

**Status:** the main cause, Sluice's health check restarting a busy tunnel ([core FIXES
§3](../core/FIXES.md#3-health-check-restarts-a-busy-tunnel--fixed)), is fixed (merged 2026-11-17). Sluice's per-packet
path is measured and no longer limits throughput at this tunnel's speed. With the same server and the browser's DNS
going through Sluice, Chromium and curl get the same speed captured as through the SOCKS listener. The owner's remaining
browser speed-test gap is explained by the test setup (run 4). Final confirmation: the owner runs the browser speed test
on the same hand-picked server in both modes.

**Seen** (same SSH tunnel, ~1.9 Mbps):
- Run 1, browser speed test: SOCKS listener ~1.97/1.99 Mbps (loaded ping 2.2 s); capture 0.86/1.08 Mbps (loaded ping
  7.1 s); `ssh -D` and Proxifier ~2.0/2.0 Mbps.
- Run 2 (2026-11-16, first build from `perf/capture-throughput`): capture 1.43/1.15 Mbps; SOCKS 2.04/1.77 Mbps. The
  tunnel was torn down twice during the capture test (cause 1).
- Run 3 (2026-11-17, with the core FIXES §3 fix; no tunnel restarts): browser: capture 1.35/1.21 Mbps; SOCKS 2.04/1.93
  Mbps. `curl.exe` to the same test server, alternating, bytes/s:

  | Test | Capture | SOCKS listener |
  |---|---|---|
  | One 3 MB download | 240,391 / 235,108 | 237,113 / 238,856 |
  | 6 parallel 700 KB downloads | 239,567 / 236,522 | 243,304 / 242,398 |
  | One 1.5 MB upload | 233,692 / 233,680 | 233,672 / 220,285 |

- Run 4 (2026-11-17, browser and Sluice logs): browser: capture 1.06/1.23 Mbps; SOCKS 1.92/1.96 Mbps. The speed test
  picked **a different server** in each mode (server A by hostname through SOCKS, server B by bare IP when captured),
  and when captured the browser's connections went to bare IPs (its own Secure DNS, [core FIXES
  §4](../core/FIXES.md#4-browsers-own-secure-dns-bypasses-sluices-dns--owners-decision)). curl, 2 MB, both servers: full
  tunnel rate through both paths (one first visit to server B waited 5 s for a slow name lookup on the SSH server, [core
  FEATURES #6](../core/FEATURES.md)).
- Run 5 (2026-11-17, headless Chrome, same server A, Secure DNS off, back to back), bytes/s:

  | Test | Capture | SOCKS listener |
  |---|---|---|
  | 1 × 3 MB | 206,583 (first byte 1.9 s) | 224,660 (first byte 1.2 s) |
  | 4 × 1 MB | 198,582 | 201,146 |
  | 6 × 700 KB | 149,422 | 139,844 |

  (6 × 700 KB: both modes had 6–8 s first bytes with the tunnel full.)

**Why:**
1. **Tunnel restarts during a test** (runs 1 and 2): once Sluice's health check restarted a busy tunnel ([core FIXES
   §3](../core/FIXES.md#3-health-check-restarts-a-busy-tunnel--fixed), fixed); once the SSH connection itself dropped
   and the tunnel server's name didn't resolve for ~50 s (network side).
2. **The browser tests weren't like for like** (runs 3 and 4): the speed test picks its server by its own ping and
   picked different servers in each mode; when captured, the browser resolved names itself ([core FIXES
   §4](../core/FIXES.md#4-browsers-own-secure-dns-bypasses-sluices-dns--owners-decision)); one server's first visits hit
   the SSH server's slow name lookups.
3. **Per-packet path, measured.** Over 105,008 packets Sluice holds a packet 0.04 ms on average (max 16 ms); a
   new-connection decision takes 0.38 ms on average (max 20 ms); the owner always came from the socket event, never the
   TCP table. WinDivert's own delivery delay is 7 ms on average, 7–18 ms on average (66 ms max) in bursts of 3,000–4,000
   packets/s, mostly sing-box's own tunnel traffic, which the filter then still caught (no longer, see below). Not
   limiting at ~2 Mbps; may matter on faster tunnels.
4. Minor: app↔relay traffic is hairpinned through the adapter path, not true loopback. With UDP mode `bypass` on a
   network where direct UDP is blocked, Chrome's QUIC attempts fail before it falls back to TCP.

Proxifier redirects TCP in the kernel at connect time with a WFP callout driver (ALE connect-redirect layer), so data
packets never reach user mode and the driver supplies the PID.

**Done** (`perf/capture-throughput`, merged 2026-11-17): the busy-tunnel health check fix ([core FIXES
§3](../core/FIXES.md#3-health-check-restarts-a-busy-tunnel--fixed)); WinDivert queue at its maximum (16384 packets / 32
MB); new-flow decisions on 4 worker threads (`PacketReflector.DeferDecisions`, `CompleteDeferred`); wait up to 3 ms for
the sniffed owner before the table scan; log strings built only when needed; timing counters (`LatencyStats`).

**How to reproduce:** headless Chrome with a throwaway profile and its built-in DNS client and DoH off:
`--headless --disable-web-security --user-data-dir=<temp dir>`, plus `--proxy-server=socks5://127.0.0.1:1080` for the
SOCKS run. Open a page that times parallel `fetch()` downloads from a speed-test server's `/download` endpoint. Run
captured and through SOCKS back to back, same server; compare with `curl` and `curl --socks5-hostname 127.0.0.1:1080`.

**Narrowed capture filter** (`perf/narrow-capture-filter`, 2026-11-17): before it, every outbound TCP packet on the PC
went through Sluice. Counted with a passive sniff handle during a 10 MB download (~1.2 MB/s): 54% relay→app data
(MTU-sized), 27% app ACKs, **19% the tunnel helper's own packets to the SSH server**; through the SOCKS listener 97% of
what Sluice handled was the helper's. Now only packets Sluice may rewrite are captured
([STATUS.md](../STATUS.md#how-capture-works)): captured download 10,600 packets handled (all rewritten), SOCKS download
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
[STATUS.md](../STATUS.md#how-capture-works)) now shows the cause if it happens again.

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

## 4. Garbled Start Menu shortcut tooltip — fixed

**Seen** (#62): the shortcut Sluice writes for its notifications showed "Sluice â€” route applications through tunnels"
as its tooltip. The em dash in `StartMenuShortcut.cs` had been saved mis-encoded.

**Fixed:** the description is written with an escaped dash (`StartMenuShortcut.Description`), and a working shortcut
with an older description is rewritten to the same target at start. The shortcut and app-ID code is only in
`Sluice.Platform.Windows`; the desktop app calls `OnAppStart()` and `Notify()`.
