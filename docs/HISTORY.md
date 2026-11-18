# History

What was built, in order (each line is a branch merged into `main`; `git log --first-parent main` shows them).

**2026-11-10: foundations**
- Core engine: `.ppx` profiles, rule engine, SOCKS4/4a/5 and HTTPS clients, chains, relay, connection tracking, local SOCKS5
  listener, process detection, CLI.
- Windows capture with WinDivert (packet reflection to a local relay).
- DNS through tunnels: fake IPs from 198.18.0.0/15, hostname rules for captured traffic, loop prevention.
- Proxifier-compatible profile password encryption.
- Tunnel checker, statistics per app/target/rule, daily log file, clean shutdown.
- First desktop app (WPF), monochrome design.
- Proxies renamed to tunnels; `.sluice` profile format.
- SSH tunnels (password/key, host-key pinning, payload and TLS/SNI modes).
- sing-box helper: Shadowsocks, VMess, VLESS (+REALITY/Vision), Trojan, Hysteria2, TUIC.
- Share links and subscriptions.
- UDP through tunnels (Windows).
- Happy Eyeballs for IPv6/NAT64 networks; firewall detection banner; session status.

**2026-11-11: speed and lifecycle**
- SSH at full speed through sing-box's SSH client (no head-of-line blocking).
- Tunnel lifecycle: on/off switch, idle stop, health checks, retries, notifications.
- Notifications titled "Sluice" with its icon (Start Menu shortcut with an app ID).

**2026-11-13: Linux**
- Dead tunnels detected in seconds; restart on network change.
- Avalonia UI for Windows and Linux; platform modules; Linux capture with nftables + NFQUEUE.
- Fixes found by running on Linux (check panel after a fast failure).
- Linux package with `install.sh` and capture without root (setcap, ambient capability for `nft`).
- Fixes from testing 79 real public servers and every protocol end to end on Linux: SNI for CDN-fronted links, Xray-only
  uTLS fingerprints, private folder for sing-box configs.

**2026-11-14: release and publishing prep**
- nft ruleset forced to LF (a CRLF checkout broke capture on a real Linux PC).
- Linux release made app-only; `install.sh` installs .NET 10 and the other dependencies.
- WinDivert and sing-box fetched at build time instead of committed.
- Documentation reorganised; agent notes in `claude/`; history cleaned for publishing.
- Stale fake addresses fixed: the fake-DNS pool is saved between runs (`fake-dns.json`), and a fake address Sluice
  doesn't know is reset at once instead of being sent into a tunnel.
- Architecture reference: `docs/ARCHITECTURE.md` (connection and DNS flow) with one page per part in
  `docs/architecture/`, read from the code.
- Windows capture speed (2026-11-17): connection decisions moved off the packet thread to 4 workers, WinDivert queue
  at its maximum, timing counters; the health check no longer restarts a tunnel that is carrying data. Captured
  traffic now matches the SOCKS listener on the same server (docs/FIXES.md §8, §9).
- sing-box configs moved out of the temp folder (2026-11-17): emptying the temp folder while Sluice ran made every
  later tunnel start fail ("Could not find a part of the path …"). They now go to a per-user app folder that is
  recreated before each start (`HelperConfigFolder`).
- Narrowed Windows capture filter (2026-11-17): only packets Sluice may rewrite reach user mode (`CaptureFilter`):
  new SYNs, the relay's packets, the fake-address range, and a reopened "followers" handle for redirected real
  addresses. Direct traffic and the tunnel helpers' own connections stay in the kernel (docs/FIXES.md §8).
- Relay counters (2026-11-17): reads per direction, read size and Sluice's CPU per MB, logged every 5 s at Verbose
  ("Relay (5 s)"), to explain CPU spikes (`RelayStats`).

**2026-11-18: published**
- Repository published on GitHub. Work split into areas (Linux, Windows, UI, Core) for parallel contributors
  (`AGENTS.md`, `docs/CONTRIBUTING.md`, `docs/linux/WORK.md`); release packaging moved to `release/linux` and
  `release/windows`, agent notes to `agent-notes`. A real server address in a test was replaced by a documentation
  address throughout the history.
- DNS settings changed during capture take effect at once (2026-11-18, issues #5, #12): the policy follows the setting
  values (`DnsPolicyCache`) instead of the profile object, and the system DNS cache is flushed on a change. Linux part by
  a contributor (PR #13).
- Linux capture relay bound to 127.0.0.1 and ::1 instead of every interface (2026-11-18, PR #14, contributor).
- Tests split by platform (2026-11-18, issue #18): shared tests run on both OSes; `tests/Sluice.Tests.Linux` and
  `tests/Sluice.Tests.Windows` belong to their areas; each area runs `Sluice.Linux.slnf` or `Sluice.Windows.slnf`.
- Issue dependencies enforced (2026-11-18): the "Blocked issues" check fails a PR while an issue it fixes is blocked by
  an open issue; a branch rule on `main` requires it.
