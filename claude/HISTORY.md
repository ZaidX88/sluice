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
