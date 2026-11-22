# Changelog

What changed for people who use Sluice, version by version. The developer log (decisions, investigations, internal
fixes) is [docs/HISTORY.md](docs/HISTORY.md).

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). Each pull request with a change users would notice adds one line under
**Unreleased**, in one of the groups **Added**, **Changed**, **Fixed**, **Removed** or **Security**, with its issue
number ([docs/CONTRIBUTING.md](docs/CONTRIBUTING.md#from-idea-to-merge)). At a release the owner turns **Unreleased**
into the version's section, and that section, with the install links, becomes the GitHub Release notes.

## [Unreleased]

### Added
- Linux: UDP goes through tunnels that carry it (SOCKS5 and the sing-box protocols), so QUIC, calls and games work
  through them instead of being blocked. Apps with a Direct rule can also use UDP to names looked up through the
  tunnel (#60).
- Windows: apps with a Direct rule can use UDP (QUIC, NTP, games) to names looked up through the tunnel; before, Sluice
  dropped it and the app had to fall back to TCP (#53).
- The version is shown at the bottom of Settings → General, with a Copy button for bug reports, and the Log page
  starts with the same version line as the log files (#96).
- Sluice tells you when a new version is out: a notice with a Download link for your system, checked once a day on
  GitHub. It never downloads or installs anything itself; turn it off in Settings → General (#198).

### Changed
- Linux: Sluice's windows have their own title bar and border in the app's theme, like on Windows, instead of the
  desktop's (#84).
- Name resolution, connection and UDP settings now belong to Sluice on this PC, not to each profile, so opening
  another profile no longer changes them. They start from the profile you last used (#148).
- A shorter Settings page: General, Privacy and Tunnels hold what most people change; the rest is under Advanced,
  closed until you open it. The SOCKS5 port applies without an Apply button (#149).
- Profiles live in Sluice's own folder, and the profile menu lists them: a click switches at once, and New, Save as,
  Rename, Delete and Import manage them, with no file dialogs. Your last profile is copied there the first time; the
  original file stays where it was (#134).
- Password protection moved from Settings to the profile menu, since it belongs to each profile: one switch for a
  master password, and on Windows the option to tie passwords to your Windows account. Plain text is no longer offered;
  a profile saved that way is saved with basic encryption from now on (#150).
- Website names are looked up through the tunnel by default, so a new install doesn't leak DNS. Settings and
  profiles you already have keep their choice, and "On this PC" is still in Settings → Privacy (#167).

### Fixed
- Linux: with UDP set to "Let it go direct", apps' UDP (NTP, QUIC, games) to names looked up through the tunnel reaches
  them again, sent directly by name; before, it went nowhere. Other UDP is still left alone in that mode (#209).
- Linux: Settings → Tunnels shows and saves "Through the tunnel" for UDP again, instead of showing it as "Block it"
  and leaving it out of the list, now that Linux tunnels UDP (#200).
- Linux: on Arch, when pacman can't install the packages Sluice needs (an old or never-synced package database),
  install.sh stops with what to run (`sudo pacman -Syu`) instead of carrying on without them (#140).
- Linux: install.sh no longer ends with "Sluice starts." when the desktop app's libraries are missing; it names
  them in a warning at the end (#141).
- The search box in the top bar no longer shows a white border and dark fill when clicked, is a little bigger, and
  gives up the caret when you click elsewhere or press Esc (which also clears the search) (#135).
- Selected text in text fields is readable again: it was filled with the text's own colour, so it showed as a solid
  block (#143).
- Text buttons such as Clear, Disconnect all and Reset statistics, and the small icon buttons, no longer look
  disabled: they use the normal text colour and show a fill on hover (#136).
- Pressing a text or icon button shows it again: it fades while held, instead of looking the same as a hover (#160).
- Linux: notifications show Sluice's icon instead of a generic VPN icon when Sluice was installed with install.sh
  (#178).

<!-- Add a line under one of these headings; create the heading if it isn't here yet:
### Added
### Changed
### Fixed
### Removed
### Security
-->

## [1.0.0] - not released yet

The first release. Sluice captures other applications' connections and decides, per application, host and port,
whether each one goes direct, gets blocked, or goes through a tunnel or a chain of tunnels. Apps need no proxy
settings.

### Added

- **Capture on Windows and Linux** without any support from the apps: WinDivert on Windows, nftables and NFQUEUE on
  Linux. Apps can also use Sluice's local SOCKS5 server instead.
- **Rules** by application, host (with wildcards such as `*.example.com`), IP range and port, sending each connection
  direct, blocking it, or sending it through a tunnel or a chain of tunnels.
- **Tunnels:** SSH (direct, HTTP-payload and TLS/SNI modes), SOCKS4/5 and HTTP(S), and through the sing-box helper
  Shadowsocks (including 2022), VMess, VLESS (including REALITY and Vision), Trojan, Hysteria2 and TUIC, over TCP,
  WebSocket, gRPC, HTTP or HTTPUpgrade transports.
- **Share links and subscriptions:** `vless://`, `vmess://`, `ss://`, `trojan://`, `hysteria2://`, `tuic://`,
  `ssh://` and others.
- **Hostnames reach the tunnel:** a built-in fake-DNS server keeps names intact for the tunnel, so name rules work and
  DNS doesn't leak. IPv6 is refused through tunnels that can't reach it, so apps fall back to IPv4 (#30, #31).
- **Tunnel health:** checks, automatic reconnect, restart after a network change, idle stop, and notifications.
- **Profiles:** Sluice's own `.sluice` files (JSON, with optional password encryption).
- **Desktop app** for Windows and Linux: light and dark themes, a tray icon, a statistics page, and a banner that says
  what capture needs and offers to get it (#70).
- **Command line** (`sluice-cli` on Linux, `Sluice.Cli` on Windows) for machines without a desktop.
- **A log file** on by default, with the last 7 days kept and a choice of detail in Settings (#40, #39).
- **Linux:** a package with `install.sh`, which installs what Sluice needs (.NET, nftables and the rest). It gives the
  capture rights to a small launcher, so Sluice captures without running as root. A copy run without the installer can
  get the same rights from the app, and Sluice can start at login (#69).
- **Windows:** an installer (`Sluice-Setup-<version>-x64.exe`) with everything Sluice needs, .NET included, so it
  installs offline: Start Menu shortcut, firewall rules, and a clean uninstall (#64). Run Sluice as administrator for
  capture.

### Known limitations

<!-- At release time: keep only what is still true, and add anything else still open. -->
- **No UDP through tunnels on Linux** (QUIC, games, calls): tunneled apps' UDP is blocked, so they fall back to TCP
  where they can (#47). Windows tunnels UDP.
- **x86-64 only**, on both Windows and Linux.
- **Not supported:** Xray-only features that sing-box doesn't implement, namely the `xhttp` transport and VLESS
  post-quantum encryption (`mlkem768x25519plus`). Sluice shows an error for such links.
- **Linux tray:** needs a StatusNotifier host (on GNOME, the AppIndicator extension). Without one, closing the window
  quits Sluice instead of hiding it (#45).
- **Linux capture rights belong to the launcher:** start Sluice as `sluice`, `sluice-cli` or from the menu. Started
  directly as `Sluice.Desktop`, it can't capture.
- **Linux file dialogs:** with capture rights, Sluice uses Avalonia's own file dialog instead of the desktop's (#126).
- **Windows:** capture needs Sluice to run as administrator. <!-- The package isn't signed yet (#65), if still true. -->
