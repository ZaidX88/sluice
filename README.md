# Sluice

A rule-based tunnelling client for **Windows and Linux**. Sluice captures other applications' connections and decides,
per application, host and port, whether each one goes **direct**, gets **blocked**, or flows through a **tunnel** (SSH,
SOCKS, HTTP(S), VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC) or a chain of tunnels. Apps need no proxy settings.

- **Capture without app support:** WinDivert on Windows, nftables + NFQUEUE on Linux. Apps can also use Sluice's local
  SOCKS5 server instead.
- **Hostnames reach the tunnel:** a built-in fake-DNS server keeps names intact, so rules like `*.example.com` work and
  DNS doesn't leak.
- **Every common tunnel type:** SSH (direct, HTTP-payload and TLS/SNI modes), SOCKS4/5, HTTP(S), and through the sing-box
  helper Shadowsocks (incl. 2022), VMess, VLESS (REALITY/Vision), Trojan, Hysteria2 and TUIC over TCP, WebSocket, gRPC or
  HTTP transports.
- **Share links and subscriptions** (`vless://`, `vmess://`, `ss://`, `trojan://`, `hysteria2://`, `tuic://`, `ssh://`…).
- **Tunnel health:** checks, automatic reconnect, idle stop, notifications.
- **Profiles** are `.sluice` files (JSON), with optional password encryption.
- **Desktop app** (light/dark, tray) and a **command line** for headless machines.

Status per platform, including what isn't done yet: [docs/STATUS.md](docs/STATUS.md). How one codebase serves both
Windows and Linux, and how every part works: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Getting started

- **Use it:** [docs/QUICKSTART.md](docs/QUICKSTART.md)
- **Linux:** install a release with [docs/linux/INSTALL.md](docs/linux/INSTALL.md) (`sudo ./install.sh` adds .NET,
  nftables and the rest; packaging scripts in `packaging/linux/`); build from source with [docs/linux/BUILD.md](docs/linux/BUILD.md).
- **Windows:** build and run from source with [docs/windows/BUILD.md](docs/windows/BUILD.md) (a release package is planned).

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone <this repository> && cd sluice
dotnet build src/Sluice.Desktop -f net10.0-windows10.0.19041.0   # Windows app
dotnet build src/Sluice.Desktop -f net10.0                       # Linux app
dotnet test  Sluice.Windows.slnf                                # Windows: shared + Windows tests
dotnet test  Sluice.Linux.slnf                                  # Linux: shared + Linux tests
```

The first build downloads the two runtime helpers, pinned and checked against their SHA-256: **WinDivert 2.2.2-A**
(Windows capture driver) and **sing-box 1.14.4** (encrypted tunnels). Details and the offline switch are in
`build/Dependencies.targets`.

## Layout

```
src/Sluice.Core              engine: rules, tunnels, relay, DNS, profiles (shared, no UI)
src/Sluice.Platform.Windows  Windows-specific code (notifications, WinDivert capture)
src/Sluice.Platform.Linux    Linux-specific code (nftables/NFQUEUE capture, /proc lookups, notifications)
src/Sluice.Desktop           the desktop app (Avalonia), Windows and Linux
src/Sluice.Cli               command line
src/Sluice.App               the old WPF app (being retired)
tests/Sluice.Tests*          xUnit tests: shared, .Linux, .Windows, .Support (helpers)
build/                       build-time dependency fetching
scripts/                     live-test helpers, sing-box download scripts
docs/                        user guides, status, plans, contributing
AGENTS.md, CLAUDE.md         rules for AI coding agents
```

## Contributing and continuing the work

Work is split into areas (Linux, Windows, UI) so people can work in parallel through forks and pull requests:
[docs/CONTRIBUTING.md](docs/CONTRIBUTING.md). AI coding agents follow [AGENTS.md](AGENTS.md). The plan of record is in [docs/REQUIRED.md](docs/REQUIRED.md), each area's `FIXES.md` and `FEATURES.md` ([docs/README.md](docs/README.md)) and the GitHub issues. Security problems are reported privately:
[.github/SECURITY.md](.github/SECURITY.md).

## Third-party software

Not stored in this repository; downloaded by the build or the Linux installer:
- [WinDivert](https://github.com/basil00/WinDivert), LGPL-3.0 / GPL-2.0;
- [sing-box](https://github.com/SagerNet/sing-box), GPL-3.0, which runs as a separate helper process.

NuGet packages (Avalonia, SSH.NET, …) come under their own licences. Every bundled component, its version, licence and
source: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Licence

Sluice is free software under the [GNU General Public License, version 3](LICENSE) (GPL-3.0-only). You may use,
change and share it; versions you distribute must stay under the same licence, with their source.
