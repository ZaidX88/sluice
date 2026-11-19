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
  traffic now matches the SOCKS listener on the same server (docs/windows/FIXES.md §3, docs/core/FIXES.md §3).
- sing-box configs moved out of the temp folder (2026-11-17): emptying the temp folder while Sluice ran made every
  later tunnel start fail ("Could not find a part of the path …"). They now go to a per-user app folder that is
  recreated before each start (`HelperConfigFolder`).
- Narrowed Windows capture filter (2026-11-17): only packets Sluice may rewrite reach user mode (`CaptureFilter`):
  new SYNs, the relay's packets, the fake-address range, and a reopened "followers" handle for redirected real
  addresses. Direct traffic and the tunnel helpers' own connections stay in the kernel (docs/windows/FIXES.md §3).
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
- Linux builds without extra flags (2026-11-18, issues #1, #6): `Directory.Build.props` sets `EnableWindowsTargeting`;
  `out/` is ignored by git.
- Issue dependencies enforced (2026-11-18): the "Blocked issues" check fails a PR while an issue it fixes is blocked by
  an open issue; a branch rule on `main` requires it.
- Docs-first workflow (2026-11-18): docs pull request → issue → code pull request for every fix, feature or required
  item; check an issue for open blockers before starting it (`docs/CONTRIBUTING.md`, `AGENTS.md`).
- Issue-then-PR workflow and docs by area (2026-11-19): an issue (templates in `.github/ISSUE_TEMPLATE/`), then one pull
  request with code, tests and docs; `FIXES.md` / `FEATURES.md` split per area (`docs/core|linux|windows|ui/`); every
  area may update the shared docs for its own changes. New issues are triaged (and split) by the owner, claimed by
  comment and assigned by the owner before work starts; labels, a project board and a 14-day unclaim rule.
- Linux capture: connections that share a source port are no longer reset or mixed up (2026-11-19, issue #38,
  contributor): flow records keyed by source and destination, found through conntrack.
- Contributors in any area (2026-11-19): the area comes from the claimed issue; one issue at a time; Core on `help wanted`
  only; the "Area" check (`.github/workflows/area-check.yml`) fails a pull request that spans areas; `.github/CODEOWNERS`.
- Coordination and syncing rules (2026-11-19): every split parent has a Coordination section the sub-issues follow;
  work starts from the latest `main` and is updated onto it before each push. Review follow-ups of PR #75 recorded as
  docs/linux/FIXES.md §3.
- Shared IPv6 fallback tracker (2026-11-19, #29): `RoutingEngine.Ipv6Fallback` marks a tunnel or chain route that
  can't reach IPv6 destinations (2 IPv6 addresses failing before they opened, while IPv4 works through it) for
  10 minutes; the Linux (#30) and Windows (#31) captures will refuse IPv6 through it. `ProxyException` keeps the
  SOCKS5 reply code.
- Linux capture: a connection's owner from the kernel's socket diagnostics instead of parsing `/proc/net/tcp` for each
  new connection (2026-11-19, issue #82, contributor): 60 connections at once are decided in ~10 ms instead of ~200 ms.
- Linux refuses IPv6 through a marked route (2026-11-19, #30): new IPv6 SYNs for a tunnel or chain that
  `RoutingEngine.Ipv6Fallback` marked get a TCP reset from the nft chain `refuse`, so apps fall back to IPv4 at once.
- Log retention (2026-11-19, #39): `FileLog` keeps the last 7 days of `Sluice-yyyy-MM-dd.log` files (`RetainDays`;
  0 keeps everything), so the desktop app can turn the file log on by default (#40).
- Linux capture decides new connections on 4 worker threads instead of the NFQUEUE thread (2026-11-19, issue #83,
  contributor): a program Sluice hasn't seen yet no longer holds up other programs' new connections.
- Windows refuses IPv6 through a marked route (2026-11-19, #31): new IPv6 SYNs for a tunnel or chain that
  `RoutingEngine.Ipv6Fallback` marked are answered with an injected TCP reset, so apps fall back to IPv4 at once;
  with #29 and #30 this fixes #4.
- One version for every build (2026-11-19, #91): `<Version>` in `Directory.Build.props` (`1.0.0-dev`), shown as
  `1.0.0-dev+abc1234` by `Sluice.Cli --version`, the CLI's first line and the first line of every log file.
- File dialogs with capture rights on Linux (2026-11-19, issue #25, contributor): the in-process GTK dialog instead of
  the portal, which refuses a `setcap` process; the profile menu closes before its dialog opens. While capture runs,
  the dialog still needs two Linux fixes (group IDs #28, ambient capability).
- Licence (2026-11-19, #92): Sluice is GPL-3.0-only (`LICENSE`); `THIRD-PARTY-NOTICES.md` lists sing-box, WinDivert,
  the NuGet libraries and the font with their versions, licences and sources.
- Packaging on `main` (2026-11-19, #93): packaging scripts are code on `main` (Linux: `packaging/linux/`,
  `scripts/package-linux.sh`; Windows: `packaging/windows/`, `scripts/package-windows.ps1`), releases are built from
  tags `vX.Y.Z`; `release/linux` and `release/windows` are retired. The files move in a follow-up pull request.
- Linux packaging moved onto `main` (2026-11-19, #93): `packaging/linux/` (`install.sh`, `uninstall.sh`,
  `sluice.desktop`, `README.txt`) and `scripts/package-linux.sh`, unchanged from `release/linux`.
- Ambient capability lowered after `nft` (2026-11-19, issue #99, contributor): `NftRules` no longer leaves
  `CAP_NET_ADMIN` raised on the thread that started capture, which broke GTK's file dialog (part of #9).
- Platform interfaces for the Core split fixed (2026-11-19, #52): `PlatformServices.Use/Current`, `IPlatformServices`
  `ProcessLookup`, `TieChildProcess`, `FlushDnsCache`, `SecretProtector`; `ISecretProtector`; `UdpFlows` with
  `AppDatagram` / `IDatagramSender` (docs/architecture/platforms.md §6).
- Core side of the platform interfaces (2026-11-19, #104): `PlatformServices`, the new `IPlatformServices` members and
  `ISecretProtector` in Core, called by the desktop app, the CLI, `SingBoxSession` and the `.ppx` password codec; no
  behaviour change until the platform parts (#54–#57) fill them in.
- Capture rights and autostart in the platform interface (2026-11-19, #68): `IPlatformServices.CanGrantCaptureRights`,
  `TryGrantCaptureRightsAsync`, `AutostartSupported`, `Autostart` as default members, for Linux (#69) and the UI (#70).
- Capture's saved group ID set too (2026-11-19, issue #28, contributor): `LinuxCapture` switches the real, effective
  and saved group, so GTK's file dialog starts while capture runs (part of #9).
- Fake DNS answers after capture stops (2026-11-19, #43): not reproduced; every stop path flushes the Windows DNS cache
  and now logs it; real answers within 0.11 s of each stop (docs/windows/FIXES.md §2).
- sing-box helpers end with Sluice on Linux (2026-11-19, #57): `LinuxPlatform.TieChildProcess` ends them on exit
  signals, and the next start ends those of a Sluice killed with SIGKILL; `FlushDnsCache` and `ProcessLookup` on
  `LinuxPlatform`.
- WSL and virtual machines work with capture on (2026-11-19, #42): their DNS questions to Windows' DNS proxy get
  real answers from Sluice for the names it fakes; the proxy's replies and DHCP aren't blocked by the UDP mode.
  Not tunneled; WSL with DNS tunneling not covered (docs/windows/FIXES.md §1).
- Start Menu shortcut and app ID only in the Windows module (2026-11-20, #62): confirmed; the shortcut's
  mis-encoded tooltip fixed, and older shortcuts rewritten at start (docs/windows/FIXES.md §4).
- File log on by default at Normal level (2026-11-20, #40): a "Log detail" choice, and the folder, the 7 days kept and
  a privacy note in Settings; on Linux the settings folder no longer falls back to the current folder on a fresh account.
- Fake-DNS state always in the settings folder (2026-11-20, #115): on a fresh Linux account it went to the current
  folder (docs/core/FIXES.md §8).
- Security policy (2026-11-20): `.github/SECURITY.md`; security problems are reported through GitHub's private
  vulnerability reporting, linked from the README and CONTRIBUTING.
