---
name: project
description: What Sluice is, its stack, terminology and module structure
metadata:
  type: project
---

**Sluice** is a Proxifier-like, rule-based tunnelling client for Windows and Linux (the working folder was once called
`rea-proxifier`; the project was renamed on 2026-11-10). It captures other apps' connections and routes each by rules:
Direct, Block, or through a tunnel / chain of tunnels.

Stack and structure:
- C# / .NET 10. UI: **Avalonia 12** (`src/Sluice.Desktop`), ported on 2026-11-13 from the original WPF app
  (`src/Sluice.App`, legacy, to be deleted) with an identical monochrome design (JetBrains Mono, pill buttons, cards).
- Capture: **WinDivert** on Windows (not a custom WFP driver: avoids driver signing), **nftables + NFQUEUE** on Linux.
- Encrypted tunnels run through **sing-box** as a helper process (one per tunnel, local SOCKS5 inbound); SSH uses
  sing-box's SSH client with SSH.NET as fallback.
- **One branch, separate modules:** shared code in `Sluice.Core`; OS code in `Sluice.Platform.Windows` /
  `Sluice.Platform.Linux` behind `IPlatformServices`. Core still holds some Windows-only code (see docs/REQUIRED.md §1).
- Third-party binaries (WinDivert, sing-box) are **not committed**: `build/Dependencies.targets` fetches pinned,
  SHA-256-checked releases on the first build; the Linux `install.sh` downloads sing-box at install time.

Terminology (owner's request): outbounds are **tunnels**, never "proxies", in UI and code (`Tunnel`, `TunnelType`,
`RuleActionType.Tunnel`, `TunnelsViewModel`). "Proxy" only appears in `.ppx` XML and SOCKS/HTTP protocol messages.
Native profile format: `.sluice` (JSON); Proxifier 4 `.ppx` is import/export only and stays compatible.

**How to apply:** UI-free logic goes in Core; UI in Sluice.Desktop; anything OS-specific in a platform module. See
[[status]] for what's next and [[working-rules]] for how to work.
