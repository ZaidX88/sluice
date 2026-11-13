# Sluice — status and next steps

A handoff document: where the project stands, how it is put together, and what to do next, grouped as
**fixes**, **unfinished platform work**, **features** and **engineering**. Each item names the files to touch
and what "done" means. Read this, `README.md` and `ANALYSIS.md` (the Proxifier analysis the project started
from) before starting.

Last updated: 2026-11-13 (main at `b53ea44`), including findings from recording the real app for the promo video
(sections 2.5–2.9 and 6).

---

## 1. Where things stand

Sluice is a Proxifier-style app: it captures other applications' connections and routes each one by rules,
either Direct, Blocked, or through a **tunnel** (SSH, SOCKS, HTTP(S), VLESS, VMess, Trojan, Shadowsocks,
Hysteria2, TUIC). Stack: C# / .NET 10, Avalonia UI, WinDivert on Windows, nftables + NFQUEUE on Linux,
sing-box as a helper process for the encrypted protocols.

| Area | State |
|---|---|
| Routing engine, rules, chains, `.ppx` import/export, `.sluice` profiles, password encryption | done |
| Tunnels: SSH (sing-box client with SSH.NET fallback, payload/TLS/SNI modes), SOCKS4/5, HTTP(S), sing-box protocols | done |
| Share links, subscriptions | done |
| UDP through tunnels | done on Windows; **not yet on Linux** (UDP of tunneled apps is blocked there) |
| Tunnel lifecycle: on/off switch, idle stop, health checks, retries/give-up, network-change restart, notifications | done |
| Fake DNS (198.18.0.0/15) so hostnames reach tunnels | done (Windows and Linux) |
| Windows capture (WinDivert) | done |
| Linux capture (nftables + NFQUEUE) | done and verified live in WSL Ubuntu 24.04; UDP tunneling missing |
| Avalonia desktop app `Sluice.Desktop` (Windows + Linux) | done, matches the WPF design; verified on Windows and on Linux under WSLg |
| Old WPF app `Sluice.App` | still in the repo; to be removed (see 3.4) |
| Platform split into modules | started: notifications + capture + process lookup go through `IPlatformServices`; WinDivert code still lives in Core (see 3.1) |
| Promo video | first cut done: `promo/sluice-promo.mp4` (52.7 s, 1080p, silent); `promo/` is **not in git** yet (see 6) |

### Projects

| Project | Target | Role |
|---|---|---|
| `src/Sluice.Core` | net10.0 | Everything shared: engine, tunnels, DNS, profiles, inbound listeners, interfaces (`Platform/IPlatformServices.cs`, `Interception/ITrafficCapture.cs`, `Platform/ProcessLookup.cs` with `IProcessLookup`). **Still contains Windows-only code** (WinDivert interceptor, iphlpapi lookups, DPAPI, Job object). |
| `src/Sluice.Platform.Windows` | net10.0-windows10.0.19041.0 | Toast notifications (Start Menu shortcut with AUMID "Sluice"), creates the WinDivert `Interceptor`. |
| `src/Sluice.Platform.Linux` | net10.0 | notify-send notifications, `/proc` process lookup, nftables/NFQUEUE capture (`Capture/`), loopback DNS server. |
| `src/Sluice.Desktop` | net10.0-windows…; net10.0 | Avalonia app. `#if WINDOWS` in `Services/Platform.cs` picks the platform module. |
| `src/Sluice.Cli` | net10.0 | Headless engine; `--intercept` uses WinDivert on Windows, `LinuxPlatform` on Linux. |
| `src/Sluice.App` | net10.0-windows | Legacy WPF app (to be deleted). |
| `tests/Sluice.Tests` | net10.0 | xUnit; references Core and the Linux module. |

### How Linux capture works (so it can be extended)

`src/Sluice.Platform.Linux/Capture/`:

1. `NftRules` installs table `inet sluice`.
   - Chain `queue_new` (filter/output, priority mangle) sends the first SYN of each new outbound TCP connection to NFQUEUE 1473 with `bypass`, so traffic passes if Sluice is gone. It also queues new UDP flows when the UDP mode isn't Bypass.
   - Chain `to_sluice` (nat/output, priority dstnat) redirects packets carrying mark `0x5C1CE` to the relay port, and UDP/53 to the DNS port.
   - Both chains first skip `meta skgid <Sluice GID>` and loopback.
   - Chain names must not be nft keywords: `redirect` broke the ruleset once.
2. `NfQueue` is a hand-written netlink NFQUEUE client (no libnetfilter_queue). It works on raw `AF_NETLINK` sockets through `Native/Libc.cs`, binds the queue with fail-open, and sends verdicts with an optional mark.
3. `LinuxCapture.OnPacket` handles each queued packet.
   - TCP: it finds the PID (`LinuxProcessLookup`, /proc), then `RoutingEngine.Classify`. To redirect, it sets the mark and stores the original destination keyed by the app's endpoint (REDIRECT keeps the source).
   - UDP: it drops or passes the flow, caching each verdict for 30 s.
4. Sluice's own sockets are skipped by group: at start, the process calls `setresgid(-1, GID, -1)` with GID 377294 (or the next free one). Helpers it starts afterwards inherit that GID.
5. `LinuxDnsServer` handles DNS.
   - It listens on **127.0.0.1:P and [::1]:P**. Replies must leave from the redirect address or conntrack won't translate them back. A wildcard socket was a real bug.
   - Faked names are answered with `DnsRedirector.TryAnswerQuery`; other queries are forwarded to the real upstreams from `/run/systemd/resolve/resolv.conf` (falling back to `/etc/resolv.conf`), skipping loopback stubs.
6. Rights: root, or `cap_net_admin,cap_setgid` (plus `cap_sys_ptrace,cap_dac_read_search` to see other users' processes) set on the binary.

---

## 2. Fixes (known bugs)

### 2.1 Windows: WSL / VMs get fake DNS answers they cannot use — **high**
**Symptom:** inside WSL2 (and likely Hyper-V VMs or Docker), names resolve to 198.18.x.x while Sluice capture is on in Windows, and connections to them time out. Seen as `dot.net → 198.18.0.57` with `curl` hanging. Direct IPs work.

**Why:** WSL sends DNS to the Windows host's DNS proxy, whose upstream query is captured and faked by `Interceptor` (`DnsRedirector.TryAnswer` doesn't know the asker). WSL's TCP to the fake address is then *forwarded* through the Hyper-V NAT. That forwarded flow apparently never reaches the relay: no owning PID, and `PacketReflector` reflects only local flows.

**To do:**
1. Confirm how the forwarded SYN appears to WinDivert: layer, `Outbound`, interface, source address before or after NAT. Use `--log debug` and `DiagnosticPackets` in `Sluice.Cli`.
2. Fix it one of two ways:
   - **(a)** don't fake DNS queries made by the host DNS proxy on behalf of VMs. Detect them by source port or process (svchost hosting the "SharedAccess"/HNS DNS proxy).
   - **(b)** make forwarded flows to fake addresses reach the relay.
   - (a) is simpler and safer.
3. Add an option "Capture WSL / virtual machines" if (b) turns out to be feasible later.

**Done when:** with Windows capture on, `curl https://dot.net` works inside WSL.

### 2.2 Tests that fail because of the developer machine
- `HappyEyeballsTests.DeadFirstAddress_DoesNotDelayTheWorkingOne` fails whenever a Sluice capture is running on the machine (the relay accepts every connection). Make the test detect this (e.g. skip if 127.0.0.1:dead-port accepts) instead of failing.
- `Sluice.Cli.exe` has no Windows Firewall allow rule on Private networks, so CLI capture tests fail there. The desktop app can add its own rule (banner + "Allow" button, `EngineHost.AllowThroughFirewall`). Give the CLI the same (`--allow-firewall`) or document it.
- `scripts/linux/test-socks5.py` (the stand-in tunnel of the Linux live test) accepts connections but **relays no data on Windows**. It is fine for its Linux job. On Windows, use the CLI as a stand-in SOCKS5 tunnel instead: `Sluice.Cli --socks 127.0.0.1:<port>`.
- Any test or demo that makes real connections misbehaves while a Sluice capture is running on the same machine. Fake-DNS answers and tunnel routing apply to the test's own helper processes too. Pause capture (and see 2.6) before such runs.

### 2.3 Avalonia UI gaps (small)
- **Dark title bar on Windows** (the WPF app used DWM `DWMWA_USE_IMMERSIVE_DARK_MODE`). Do it in `Sluice.Platform.Windows` behind a new `IPlatformServices` method, or via Avalonia's `TransparencyLevelHint`/`ExtendClientAreaToDecorationsHint`.
- **SSH client selector** in `Views/TunnelDialog.axaml`. Setting `sshclient` = `auto | singbox | sshnet` is stored in tunnel settings but can only be edited in the profile file.
- **Screenshot mode** (`Services/ScreenshotMode.cs`) renders at 1× because `RenderTargetBitmap` mis-scales DataGrid content at >100% DPI. This affects screenshots only. Override with `SLUICE_SHOT_SCALE`.
- **Linux tray icon:** Avalonia uses StatusNotifierItem. GNOME needs the AppIndicator extension, otherwise there's no tray, and closing the window must then not hide the app. Detect a missing tray and quit on close instead.

### 2.4 Linux capture limitations (by design for now, should be lifted)
- **UDP through tunnels** (see 3.2). Today a UDP flow whose rule says "tunnel" is **dropped**, so browsers fall back from QUIC to TCP, and fake addresses over UDP are always dropped.
- IPv6 extension headers aren't parsed in `PacketInfo.TryParse`, so such first packets pass uncaptured.
- **DNS when no upstream can be found.** If there is no non-loopback upstream DNS (e.g. dnsmasq without systemd-resolved), DNS isn't captured and a log line says so. A fix needs the original destination of redirected UDP (TPROXY + `IP_RECVORIGDSTADDR`), which comes naturally with 3.2.
- `LinuxProcessLookup` scans `/proc/*/fd` on a miss, newest processes first, with a recent-owner list. That is fast enough in tests (20 parallel connections in ~0.15 s). On machines with tens of thousands of fds, consider `NETLINK_SOCK_DIAG` + an inode→pid cache refreshed in the background, or eBPF later.
- The relay listens on `[::]:random`, which is reachable from the LAN. Lookups fail and close for unknown clients, but binding to loopback would be tidier. REDIRECT targets 127.0.0.1 / ::1, so bind two loopback sockets like the DNS server does.
- **Running the GUI as root on Linux** breaks notifications (root has no session bus) and Wayland may refuse root apps. Recommended path: `setcap` on the binary (hint shown in the UI). Long-term: a small privileged helper/daemon (see 4.10).

### 2.5 Stale fake addresses after Sluice restarts — **high**
**Seen live (Windows, real SSH tunnel):** after Sluice restarted, the log filled with lines like
`claude.exe - 198.18.0.49:443 error via SSH <server>: SSH tunnel <server> did not answer within 20 s` and
`sing-box <server>: ERROR … open connection to 198.18.0.43:443 … ssh: rejected: connect failed (Connection timed out)`.

**Why:**
- The new process starts with an empty `FakeIpPool`, but apps still hold fake addresses from the previous run. Chromium/Electron apps (Claude, Vivaldi, Edge) keep their **own** DNS caches, which flushing the OS cache doesn't clear.
- `Interceptor.Decide` (`Interception/Interceptor.cs` ~line 195) logs "Connection to unknown fake address" but then classifies the flow normally and sends the **raw 198.18.x.x address** through the tunnel. The tunnel server can't reach it, so each connection hangs about 20 s.
- Those failures also make the health monitor think the tunnel died ("did not answer within 20 s; checking the tunnel now"), so a healthy tunnel gets probed and reported as not responding.

**To do:**
1. **Persist the fake pool:** save host↔address pairs (with last use) to `%APPDATA%\Sluice\fake-dns.json`, or `~/.config/Sluice` on Linux, and reload it on start. Old cached answers then still map to the right host. `LinuxCapture` has the same pool and needs the same treatment.
2. **Fail fast for unknown fakes:** never send a 198.18.0.0/15 destination to a tunnel or direct. Reset the connection at once so the app retries and re-resolves. With WinDivert, reflect the flow and close it in the relay. On Linux, redirect it and close it.
3. **Keep health checks honest:** don't count such failures toward tunnel health.

**Done when:** you restart Sluice with Chrome and Claude open, existing tabs reconnect within a few seconds, and no `198.18.` address appears in "via SSH/…" log lines.

### 2.6 Fake DNS answers outliving capture — investigate
**Seen once** with the old WPF build (`Sluice.App`, started the day before):
- The user reported capture as paused.
- `ipconfig /flushdns` followed by `Resolve-DnsName www.google.com` still returned `198.18.0.25`.
- Programs connecting to those addresses got nowhere.

Either "pause" did not stop interception in that build, or something else kept answering DNS.

**To do:** in the Avalonia app, check every way to stop capture: the capture button, the tray "Capture traffic" item, and quitting. After each, `Resolve-DnsName` must return a real address within a second. `Interceptor.DisposeAsync` already flushes the OS cache when fakes were handed out; confirm it runs on every stop path, and add a log line. If the UI ever offers a "pause" separate from "stop", it must stop fake DNS too.

### 2.7 Layout at narrower window sizes
**Seen in the live recording**, at a window about 1040×650 DIP (1302×814 px at 125% scaling):
- **Tunnels page:** with the check panel open, the **Status** column disappears and Session is cut ("Connect…").
- **Statistics page:** the "Share of traffic" header is cut ("Share of traf").

**To do:**
- Let the check panel shrink, or move it below the list under a width threshold.
- Hide the Address and Session columns before Status.
- Shorten the header to "Share".
- Add a narrow-window pass to screenshot mode, e.g. `--screenshot DIR --size 1040x650`, to keep this checked.

### 2.8 sing-box output logged at the wrong level
`SingBoxSession.cs` (~line 107) logs every helper line at the normal level, so sing-box `ERROR[…]` lines appear as **INFO** in the Log page. They are also long and repetitive (connection IDs, `outbound/ssh[out]`).

**To do:**
- Parse sing-box's level prefix (`ERROR`, `WARN`, `INFO`, `DEBUG`) and map it to `LogLevel`.
- Show raw helper lines only at Verbose.
- At Normal, write one short line per distinct error, e.g. "SSH tunnel X: the server refused a connection to host:port (timed out)", and collapse repeats.

### 2.9 Screenshot and recording notes
- `RenderTargetBitmap` mis-scales DataGrid content above 100% DPI (2.3). Live recordings avoid this: `promo/capture-window.ps1` uses `PrintWindow` with `PW_RENDERFULLCONTENT` and works on the Avalonia window even when covered.
- `PrintWindow` sizes are wrong unless the capturing process is DPI-aware (`SetProcessDPIAware`); the script does that.

---

## 3. Unfinished platform work (Linux port and module split)

The agreed structure: **one branch, separate modules.** Shared code goes in `Sluice.Core`, OS-specific code in `Sluice.Platform.Windows` / `Sluice.Platform.Linux`, and any new OS difference goes behind `IPlatformServices` (or a narrower interface it hands out).

### 3.1 Move Windows-only code out of Core
Currently in Core, to move into `Sluice.Platform.Windows`:

| File | What |
|---|---|
| `Interception/WinDivertNative.cs`, `Interceptor.cs`, `PacketReflector.cs`, `UdpNat.cs` (WinDivert-injection parts) | WinDivert capture. `PacketReflector`, `DnsRedirector`, `TcpPacket`, `UdpPacket` are pure packet logic and may stay in Core. |
| `Platform/ProcessLookup.cs` (the iphlpapi/kernel32 code) | becomes `WindowsProcessLookup : IProcessLookup`; Core keeps only the interface + `Backend` |
| `Profiles/Dpapi.cs` | used by `ProxifierPasswordCodec` (Proxifier's DPAPI-protected passwords). Needs an `ISecretProtector` in Core. On Linux: unsupported for DPAPI blobs from `.ppx` (show a clear error), and use libsecret / a key file for Sluice's own needs. |
| `Tunnels/SingBoxSession.cs` Job object | becomes `IPlatformServices.TieChildProcess(Process)`. Windows: Job object. Linux: `prctl(PR_SET_PDEATHSIG)` is awkward from .NET, so use a watchdog that kills helpers on exit plus `setsid` handling. |
| `Interceptor.FlushOsDnsCache` (dnsapi) | already platform-specific; Linux uses `resolvectl flush-caches`. |
| WinDivert `.dll/.sys` copy in `Sluice.Core.csproj` | move to the Windows module's csproj. |

The CLI should then multi-target like the desktop app (`net10.0-windows…;net10.0`) and get its capture from the platform module instead of the `OperatingSystem.IsLinux()` branch in `Program.cs`.
**Done when:** Core has no `DllImport` of Windows libraries, both targets build, all tests pass.

### 3.2 UDP through tunnels on Linux (TPROXY)
The design that fits the current capture:
- In `queue_new`, keep queuing new UDP flows. For flows to tunnel, set a second mark (e.g. `0x5C1CF`).
- Add chain `mangle/prerouting`: `meta mark 0x5C1CF tproxy to :UDPPORT`.
- Locally generated packets need rerouting through `lo`: `ip rule add fwmark 0x5C1CF lookup 100` and `ip route add local default dev lo table 100`, plus the IPv6 equivalents. A `type route hook output` chain sets the mark so the reroute happens.
- Add a UDP socket with `IP_TRANSPARENT` and `IP_RECVORIGDSTADDR` (and `IPV6_TRANSPARENT` / `IPV6_RECVORIGDSTADDR`). Receive gives the original destination. Replies are sent from a transparent socket bound to that original address so the app sees the real server.
- Reuse the Windows `UdpNat` flow logic (per-flow tunnel association via `RoutingEngine.OpenUdpAsync`) by splitting its WinDivert I/O from its flow table.
- Clean up the `ip rule` / route on stop, like the nft table.
- This also solves the missing-upstream DNS case: TPROXY DNS gets the original server.

**Done when:** `UdpMode.Tunnel` carries a QUIC request (`curl --http3`) and a DNS query through a SOCKS5 tunnel in the live test.

### 3.3 Linux packaging and integration
**Done (2026-11-13):** a standalone x64 package, `sluice-linux-x64.tar.gz` (~73 MB, no .NET needed).
- **Build:**
  1. `dotnet publish` `Sluice.Desktop -f net10.0` and `Sluice.Cli` with `-r linux-x64 --self-contained` into one folder.
  2. `scripts/get-sing-box.sh` fetches sing-box (SHA-256 checked).
  3. `scripts/package-linux.sh <publish dir> <sing-box tar.gz> <out dir>`, run on Linux/WSL, assembles the package.
- **Contents:** `app/` (the app, `Sluice.Cli`, `sing-box`), `install.sh`, `uninstall.sh`, `sluice.desktop`, `sluice.png`, `README.txt`.
- **`install.sh`:**
  - installs to `/opt/sluice` (root-owned, nothing group/other-writable);
  - adds the `sluice` / `sluice-cli` links, menu entry and icon;
  - runs `setcap cap_net_admin,cap_setgid,cap_sys_ptrace,cap_dac_read_search+ep` on both binaries.
- **Verified** in WSL Ubuntu 24.04: fresh install, UI renders with no system .NET, capture works as a **normal user** (no root).
- **Two setcap pitfalls, now handled in code:**
  - File capabilities are not inherited by `nft`. `NftRules.Run` raises `CAP_NET_ADMIN` as an ambient capability first (`Libc.RaiseAmbient`).
  - The kernel strips ambient capabilities when a program starts with effective group ≠ real group, so `LinuxCapture` sets **both** to Sluice's network group (`setresgid(gid, gid, -1)`).
- `TunnelBackends.SingBoxPath` is `sing-box` on Linux and `sing-box.exe` on Windows.

Still to do:
- **ARM64 package** (`-r linux-arm64` + sing-box linux-arm64).
- **`.deb` / AppImage** wrapping the same layout (post-install = `install.sh` steps). Not Flatpak or Snap: a sandbox can't use nftables.
- **Package downloads behind a slow tunnel.** NuGet restores of the runtime pack may stall. The workaround used: download the `.nupkg` with resumable `curl -C -` into a folder and pass `-p:RestoreAdditionalProjectSources=<folder>`.
- **"Grant capture rights" button** (pkexec + setcap) for people who run the app without the installer; `CanRestartElevated` is still false.
- **Autostart entry** (`~/.config/autostart`) as a setting.

### 3.4 Retire the WPF app
Before deleting `src/Sluice.App`, check these Avalonia features by hand on Windows:
- tray (show, hide, exit, capture toggle);
- single instance (a second launch shows the window);
- "Restart as administrator" with `--intercept`;
- firewall banner and Allow button;
- tunnel notifications (title "Sluice" + icon);
- open, save and recent profile, master password;
- import links and subscriptions, update subscription;
- profile export to `.ppx`;
- window close goes to the tray with the hint toast;
- `--profile`, `--log-file`, `--theme`, `--screenshot`.

Then remove `Sluice.App` from `Sluice.slnx`, delete it, and update `README.md`: it still describes the WPF app in places. Also move the Start Menu shortcut / AUMID code fully into the Windows module.

---

## 4. Features (not built yet)

Ordered roughly by value. Numbers match the earlier suggestion list where applicable.

1. **SSH client selector** in the tunnel dialog (see 2.3).
2. **UDP for the local SOCKS5 listener**: `Socks5Inbound` has no UDP ASSOCIATE, so apps pointed at 127.0.0.1:1080 get no UDP.
3. **Chains editor page.** Chains work in the engine and import from `.ppx`, but nothing in the UI creates or edits them.
4. **Kill switch.** When every tunnel a rule uses is down (`TunnelBackends` states), block that rule's traffic instead of failing per connection, with "allow LAN" as an exception.
5. **Faster failover.** Redundancy chains should skip tunnels the health monitor knows are down.
6. **Restore after sleep:** reconnect tunnels and flush stale fake-DNS entries on resume, not only on network change.
7. **Service / daemon mode.** Windows service; on Linux, a systemd unit running the engine with capabilities. The UI becomes a client over a local pipe or socket. This fixes start-at-login with rights on both OSes and the root-GUI problems on Linux.
8. **Local DNS option per tunnel.** Resolve on the PC and send the server an IP, for servers with slow DNS (seen with one SSH server: ~5 s on first visit). Opt-in, because DNS then leaves the tunnel.
9. **Rules from domain lists** (geosite / plain text import).
10. **Per-app quick toggle** from the Connections page (right-click: always direct / via tunnel X), which creates the rule.
11. **Speed and latency test for all tunnels**, sortable; auto-pick the fastest for load-balancing chains.
12. **Scheduled subscription updates** (e.g. every 12 h) with a notification summary.
13. **Traffic history** saved to disk (per day, per app) with charts on the Statistics page.
14. **Tray quick switching** (profile, tunnel) and a global hotkey for capture.
15. **DNS over HTTPS** for Sluice's own resolver.
16. **Capture WSL / VMs on Windows** (see 2.1(b)).

## 5. Performance

- **Windows capture is single-threaded.** The WinDivert loop measured ~16–35 MB/s locally versus ~400 MB/s through the SOCKS listener. Use several worker threads with `WinDivertRecvEx` batching.
- **Process lookup on the packet thread** (Windows and Linux) holds up other packets. Move it to a worker and give verdicts asynchronously. NFQUEUE allows out-of-order verdicts; WinDivert would need re-injection from the worker.
- **UDP "through tunnels" mode on Windows** sends every UDP packet through Sluice. Exclude LAN, multicast and always-direct ports in the WinDivert filter.

## 6. Engineering

- **CI** (GitHub Actions): build both Desktop targets; run tests on Windows and on Linux (Linux runs `Socket_owner_is_found_through_proc`, which is skipped on Windows); add the sing-box live tests with the pinned checksum. Optionally run `scripts/linux/capture-livetest.sh` in a privileged Linux job.
- **Installers:** MSIX or Inno Setup for Windows (firewall rule, Start Menu shortcut, sing-box, service); `.deb` / AppImage for Linux (setcap, desktop file).
- **Diagnostics bundle:** one button that zips logs, settings and the profile with secrets removed.
- **README refresh** for Avalonia, Linux and the module layout.

### Promo video and its tooling (`promo/`, not in git yet)
The first cut, `promo/sluice-promo.mp4` (52.7 s, 1920×1080, 30 fps, silent, 14 scenes), mixes two sources:
- **Live recordings** of the real app capturing real traffic: Connections, Tunnels with a passing check, Statistics, Log.
- **Screenshot-mode images:** Rules, the dialogs, Settings, dark theme.

Files:

| File | Purpose |
|---|---|
| `promo/render.py` | Scene list + ffmpeg pipeline: text on the left, screenshot on the right with shadow and a slow push-in, title/end cards with the logo, crossfades. `python promo/render.py` renders everything; `python promo/render.py 05` re-renders one scene and re-joins. |
| `promo/capture-window.ps1` | Records the running `Sluice.Desktop` window every N seconds via `PrintWindow`, without clicking or typing. |
| `promo/blur_live.py` | Blurs the private parts of chosen live frames (tunnel server, SSH user, profile name; pixel boxes per frame) and crops the Windows title bar into `promo/shots/live/`. Blur in RGB with a downscale/upscale; `gblur`/`boxblur` on YUV left green fringes. |
| `promo/PROMPT.md` | A step-by-step prompt for another model to make a video from screenshot mode only. |
| `promo/shots/`, `promo/work/`, `promo/build*/`, `promo/live/` | Generated. **`promo/live/` holds unblurred recordings with the real server name and username: never commit it.** |

To do:
- **Commit the scripts** (`render.py`, `blur_live.py`, `capture-window.ps1`, `PROMPT.md`) under `scripts/promo/`, and add the generated folders and the `.mp4` to `.gitignore`.
- **Promo traffic in screenshot mode (lost, to redo):** a variant of `Services/ScreenshotMode.cs` was built in a temporary worktree that has since been deleted. It sent real app traffic through the SOCKS listener:
  - headless Edge with `--proxy-server=socks5://…` and its own `--user-data-dir`, routed by the "Browsers" rule;
  - `curl --limit-rate … --socks5-hostname …` downloads under a new "Downloads" rule (`curl.exe` → Local tunnel);
  - a blocked tracker request;
  - a stand-in SOCKS5 tunnel on 6002 (`Sluice.Cli --socks 127.0.0.1:6002`) so the Local tunnel check passes.

  Add it as an option, e.g. `--screenshot DIR --demo-traffic`, so promo screenshots are reproducible without a real profile.
- Before publishing: add music. Then re-check scenes 2, 9 and 10: they show real app names and some sites a browser visited (blurred only where they named the tunnel server).

---

## 7. How to build, test and verify

```bash
# build
dotnet build src/Sluice.Desktop -f net10.0-windows10.0.19041.0   # Windows app
dotnet build src/Sluice.Desktop -f net10.0                       # Linux app (builds on Windows too)
dotnet build src/Sluice.Cli

# tests (one known machine-dependent failure, see 2.2)
dotnet test tests/Sluice.Tests --filter "FullyQualifiedName!~Live"

# UI screenshots of every page and dialog, off-screen (compare light/dark, Windows/Linux)
Sluice.Desktop --screenshot <dir> [--theme dark]
```

- **Visual check of the Linux UI:** WSL2 with WSLg (Windows 11) runs `dotnet Sluice.Desktop.dll` (net10.0 build) in a window. Avalonia needs libX11, libICE, libSM, fontconfig (present in Ubuntu 24.04).
- **Linux capture live test:** see the header of `scripts/linux/capture-livetest.sh`. It needs root, nftables, python3, curl, wget, bc and the .NET 10 runtime. On WSL, turn off a Windows Sluice capture first, or pass an upstream DNS argument (see 2.1).
- **Restores through a slow tunnel:** if Sluice itself is capturing `dotnet.exe` through a slow tunnel, NuGet's parallel downloads of large packages (SkiaSharp/HarfBuzz natives) time out and restart forever. Add a Direct rule for `dotnet.exe`, pause capture, or run `dotnet restore --disable-parallel`.
- **Recording the real app** (for docs or promo):
  - Run `powershell -ExecutionPolicy Bypass -File promo\capture-window.ps1 -Seconds 180 -Every 2` while someone clicks through the pages.
  - Frames land in `promo/live/`. Identify pages by cropping the title area of each frame; contact sheets built with ffmpeg `tile` can drop frames and mislabel them.
  - Blur private details with `promo/blur_live.py` before using any frame.
- **Rendering the promo:** `python promo/render.py` (needs ffmpeg with drawtext/zoompan/xfade, e.g. the gyan.dev essentials build). Then extract one frame per scene with `ffmpeg -ss <t> -i promo/sluice-promo.mp4 -frames:v 1` and look at each.

## 8. Conventions

- Terminology: outbounds are **tunnels**, never "proxies", in UI and code. "Proxy" appears only in `.ppx` XML and protocol messages. The native profile format is `.sluice` (JSON); `.ppx` is import/export.
- One feature or fix per branch (`feature/…`, `fix/…`, `perf/…`), merged into `main` with `--no-ff`.
- UI text: plain, sentence case, no jargon where avoidable; errors say what to do next.
- Keep the visual design (monochrome, JetBrains Mono, pill buttons, cards) identical across Windows and Linux; verify with `--screenshot` in both themes after UI changes.
