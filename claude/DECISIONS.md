# Decisions

Decisions made with the owner, newest last, with the reason. Change one only with the owner's agreement.

| Date | Decision | Why |
|---|---|---|
| 2026-11-10 | C# / .NET 10; **WinDivert** for Windows capture, not a custom WFP driver | No driver signing or WDK needed |
| 2026-11-10 | Keep **Proxifier 4 `.ppx`** import/export byte-compatible (incl. password encryption) | Easy migration from Proxifier |
| 2026-11-10 | Outbounds are called **tunnels**, never "proxies" (UI and code); native format `.sluice` (JSON) | Owner's terminology, like HTTP Injector |
| 2026-11-10 | Tunnel roadmap: native SSH (payload, TLS+SNI) → sing-box sidecar for SS/VMess/VLESS/REALITY/Trojan/Hysteria2/TUIC → share links/subscriptions → UDP | Covers what users of such apps expect |
| 2026-11-10 | sing-box as a **separate helper process** (GPL-3.0), one per tunnel with a local SOCKS5 inbound | Mature protocol implementations without linking GPL code |
| 2026-11-11 | SSH through **sing-box's SSH client** by default (per-connection flow control); SSH.NET as fallback, selectable per tunnel (`sshclient`) | SSH.NET suffered head-of-line blocking (one slow connection stalled others) |
| 2026-11-11 | Tunnel lifecycle: on/off switch, idle stop, health checks, retries then give up, notifications on connect/disconnect | Owner's request |
| 2026-11-13 | Ship on **Linux** too: UI ported to **Avalonia 12**, design kept identical | Cross-platform with minimal code changes |
| 2026-11-13 | **One branch, separate modules**: `Sluice.Core` shared; `Sluice.Platform.Windows` / `.Linux` behind `IPlatformServices` | Owner's choice over separate branches |
| 2026-11-13 | Linux capture with **nftables + NFQUEUE** (per-connection decision on the first SYN, like Windows); Sluice's own traffic excluded by a private group ID; fail-open queue | Same model as Windows; direct traffic never touched |
| 2026-11-13 | Linux capture **without root** via `setcap` (installer), not a root GUI | Root GUIs lose notifications and Wayland may refuse them |
| 2026-11-13 | Linux UDP through tunnels deferred (TPROXY); until then UDP of tunneled apps is blocked | Browsers fall back from QUIC to TCP, nothing leaks |
| 2026-11-14 | Linux release is **app-only** (framework-dependent); `install.sh` installs .NET 10, nftables, libcap, libraries | Small package; owner's choice over a 73 MB self-contained one |
| 2026-11-14 | sing-box for the Linux release is **downloaded by `install.sh`** (pinned, SHA-256 checked), with an offline fallback (archive next to the installer) | Owner's choice (smallest package) |
| 2026-11-14 | **No third-party binaries in git**: the build fetches WinDivert and sing-box (pinned, SHA-256 checked) into `.deps/` | Owner's request: like Maven dependencies |
| 2026-11-14 | Public history cleaned: no reverse-engineering notes, no promo material, no release artefacts, no real server or user names | Preparing to publish on GitHub |
| 2026-11-14 | Docs split by purpose (`docs/STATUS`, `REQUIRED`, `FIXES`, `FEATURES`, `QUICKSTART`, `linux/`, `windows/`); agent notes in `claude/` | Owner's request, so anyone can continue |
