# Known bugs

Each entry: what was seen, why, what to do, and when it counts as fixed. Priorities: **high** = breaks normal use.

## 1. Stale fake addresses after Sluice restarts — high

**Seen** (Windows, real SSH tunnel): after Sluice restarted, the log filled with lines like
`app.exe - 198.18.0.49:443 error via SSH <server>: SSH tunnel <server> did not answer within 20 s`.

**Why:**
- A new process starts with an empty `FakeIpPool`, but apps still hold fake addresses from the previous run. Chromium and
  Electron apps keep their **own** DNS caches, which flushing the OS cache doesn't clear.
- `Interceptor.Decide` (`src/Sluice.Core/Interception/Interceptor.cs`) logs "Connection to unknown fake address" but then
  sends the **raw 198.18.x.x address** through the tunnel. The server can't reach it, so each connection hangs ~20 s.
- Those failures make the health monitor think the tunnel died, so a healthy tunnel is reported as not responding.

**To do:**
1. **Persist the fake pool:** save host↔address pairs (with last use) to `%APPDATA%\Sluice\fake-dns.json` /
   `~/.config/Sluice/fake-dns.json` and reload on start. `LinuxCapture` has the same pool.
2. **Fail fast for unknown fakes:** never send a 198.18.0.0/15 destination to a tunnel or direct; reset the connection so
   the app re-resolves (Windows: reflect and close in the relay; Linux: redirect and close).
3. **Keep health checks honest:** don't count such failures toward tunnel health.

**Fixed when:** restarting Sluice with Chrome open, existing tabs reconnect within seconds and no `198.18.` address
appears in "via SSH/…" log lines.

## 2. Windows: WSL / VMs get fake DNS answers they can't use — high

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

## 3. Fake DNS answers outliving capture — investigate

**Seen once** with the old WPF build: capture reported as paused, yet `Resolve-DnsName www.google.com` still returned
`198.18.0.25` after `ipconfig /flushdns`.

**To do:** in the Avalonia app, stop capture every way (capture button, tray "Capture traffic", quit). After each,
`Resolve-DnsName` must return a real address within a second. Confirm `Interceptor.DisposeAsync` (which flushes the OS
cache when fakes were handed out) runs on every stop path; add a log line.

## 4. Layout at narrower window sizes

**Seen** at about 1040×650 DIP: on **Tunnels** with the check panel open, the Status column disappears and Session is cut;
on **Statistics** the "Share of traffic" header is cut.

**To do:** let the check panel shrink or move below the list under a width threshold; hide Address and Session before
Status; shorten the header to "Share"; add `--screenshot DIR --size 1040x650` to keep it checked.

## 5. sing-box output logged at the wrong level

`SingBoxSession.cs` logs every helper line at Normal, so sing-box `ERROR[…]` lines show as INFO and are long and
repetitive. **To do:** parse sing-box's level prefix and map it to `LogLevel`; raw helper lines only at Verbose; at Normal,
one short line per distinct error, repeats collapsed.

## 6. Avalonia UI gaps (small)

- **Dark title bar on Windows** (the WPF app used DWM `DWMWA_USE_IMMERSIVE_DARK_MODE`): add it via `IPlatformServices`.
- **Screenshot mode** renders at 1× because `RenderTargetBitmap` mis-scales DataGrid content above 100% DPI (screenshots
  only; `SLUICE_SHOT_SCALE` overrides). For real-window captures use `PrintWindow` with `PW_RENDERFULLCONTENT` from a
  DPI-aware process.
- **Linux tray:** Avalonia uses StatusNotifierItem; GNOME needs the AppIndicator extension. Without a tray, closing the
  window hides the app with no way back except relaunching. Detect a missing tray and quit on close instead.

## 7. Linux capture limitations

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

## 8. Tests that depend on the developer machine

- `HappyEyeballsTests.DeadFirstAddress_DoesNotDelayTheWorkingOne` fails whenever a Sluice capture is running on the same
  machine (the relay accepts every connection). Make the test detect that and skip.
- `Sluice.Cli.exe` has no Windows Firewall allow rule on Private networks, so CLI capture tests fail there. Give the CLI
  `--allow-firewall` like the app's banner (`EngineHost.AllowThroughFirewall`), or document it.
- `scripts/linux/test-socks5.py` relays no data on Windows (fine for its Linux job). On Windows use
  `Sluice.Cli --socks 127.0.0.1:<port>` as the stand-in tunnel.
- Any test making real connections misbehaves while a capture runs on the same machine (fake DNS and routing apply to the
  test's helpers too).
