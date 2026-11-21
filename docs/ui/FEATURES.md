# Features: UI

Ideas not built yet for the **UI** area (roughly by value) and its performance work. The other areas:
[Core](../core/FEATURES.md), [Linux](../linux/FEATURES.md), [Windows](../windows/FEATURES.md). Work that also needs
another area says so; it gets a parent issue with one sub-issue per area ([CONTRIBUTING.md →
Issues](../CONTRIBUTING.md#issues)).

## Features

1. **SSH client selector** in the tunnel dialog (`src/Sluice.Desktop/Views/TunnelDialog.axaml`). The setting
   `sshclient` = `auto | singbox | sshnet` exists but can only be edited in the profile file.
2. **Chains editor page.** Chains work in the engine, but the UI can't create or edit them.
3. **Per-app quick toggle** on the Connections page (right-click: always direct / via tunnel X) that creates the rule.
4. **Traffic history** (storing it is Core work) saved to disk (per day, per app) with charts on the Statistics page.
5. **Tray quick switching** (profile, tunnel) and a global hotkey for capture.
6. **Demo traffic for screenshot mode** (`--screenshot DIR --demo-traffic`): real traffic through the SOCKS listener
   (headless browser with its own profile, rate-limited `curl` downloads, a blocked tracker, a stand-in SOCKS5 tunnel
   via `Sluice.Cli --socks 127.0.0.1:6002`) so screenshots look alive without a real profile.
7. **Diagnostics bundle:** one button that zips logs, settings and the profile with secrets removed.
