# Sluice: rules for AI coding agents

Sluice is a rule-based tunnelling client for Windows and Linux (C# / .NET 10, Avalonia UI, WinDivert on Windows,
nftables + NFQUEUE on Linux, sing-box as a helper process). People work on it in parallel, one **area** each, through
forks and pull requests. These rules keep their changes from colliding. Read all of them before changing anything.

## 1. Work in one area only

Every task belongs to exactly one area. The person you work for names it at the start ("Area: Linux"). If they didn't,
**ask before editing anything**. You may change only the files of that area:

| Area | You may change | Owner of everything else |
|---|---|---|
| **Linux** | `src/Sluice.Platform.Linux/**`, `scripts/linux/**`, `docs/linux/**`, `tests/Sluice.Tests.Linux/**` | repository owner |
| **Windows** | `src/Sluice.Platform.Windows/**`, `docs/windows/**`, the Windows-only files still in Core (`src/Sluice.Core/Interception/Interceptor.cs`, `WinDivertNative.cs`, `UdpNat.cs`, `CaptureFilter.cs`, `src/Sluice.Core/Platform/ProcessLookup.cs`, `src/Sluice.Core/Profiles/Dpapi.cs`), `scripts/*.ps1`, `tests/Sluice.Tests.Windows/**` | repository owner |
| **UI** | `src/Sluice.Desktop/**` (and the legacy `src/Sluice.App/**`), `docs/architecture/desktop.md` | repository owner |
| **Core** (owner only) | everything else: `src/Sluice.Core/**` (shared engine), `src/Sluice.Cli/**`, the shared tests `tests/Sluice.Tests/**` and `tests/Sluice.Tests.Support/**`, `build/**`,
`Sluice.slnx`, `Sluice.*.slnf`, shared docs (`docs/*.md`, `docs/architecture/**`), `README.md`, `AGENTS.md`, `CLAUDE.md`, `.gitignore`, `.gitattributes` | — |

- **Strict.** If the task needs a change outside your area, even one line in shared code or the UI, **stop**. Don't make
  it. Tell the person what is needed, where and why, so the area's owner can make it (a GitHub issue is the usual way).
  Then finish what you can inside your area.
- **Linux work is Linux code only**: no UI changes, no `Sluice.Core` changes, no Windows changes. A UI change found
  during Linux work goes to whoever works on the UI area.
- **A problem that needs changes in several areas** gets one parent issue and one sub-issue per area
  (`docs/CONTRIBUTING.md` → Issues). Offer the person a draft: the parent with the evidence and a "Parts" list
  (`Core: …`, `Linux: …`), saying which part must land first and what interface the other parts will use. The owner
  decides the final split. Then fix only your area's sub-issue; its pull request says `Fixes #<sub-issue>`. Ask the
  person before creating, commenting on or closing issues. Don't start a sub-issue that is **blocked by** an open issue
  without asking: its pull request can't be merged until the blocker is closed (the required "Blocked issues" check).
- Shared docs belong to the owner. Record your findings and progress in your area's docs (Linux: `docs/linux/`) and in
  the pull request description; the owner carries them into `docs/STATUS.md`, `FIXES.md` and `REQUIRED.md`.
- Don't reformat, rename or "tidy" files you weren't asked to change.

## 2. No private data in the repository

The repository is public. Never commit:
- real server addresses, hostnames, ports, user names, passwords, keys, share links or subscription URLs;
- tunnel profiles (`*.sluice`, `*.ppx`) of real servers, logs, screenshots that show them;
- personal names, e-mail addresses, machine-specific paths (`C:\Users\…`, `/home/<name>/…`).

In code, tests and docs use placeholders: `example.com`, `ssh.example`, `alice`, and documentation addresses
`192.0.2.x`, `198.51.100.x`, `203.0.113.x`, `2001:db8::/32`. Note that `198.18.0.0/15` is not a placeholder here: it is
Sluice's fake-address range.

**Live testing needs a real tunnel.** Unit tests and the live tests use local servers only, but checking capture and
tunnels for real needs a working tunnel of your own (an SSH server, SOCKS5 proxy, VLESS/Trojan server…). Keep its profile
and credentials **outside the repository** (or in `local/`, which git ignores), pass them by environment variable or file
path, and report results in the pull request without the address.

## 3. How to work

1. Read `docs/CONTRIBUTING.md` (workflow, areas, testing), then `docs/STATUS.md`, then the item you were given in
   `docs/REQUIRED.md`, `docs/FIXES.md` or `docs/FEATURES.md`. Linux: also `docs/linux/WORK.md`. Settled decisions are in
   `docs/DECISIONS.md`; don't reverse one without the owner's agreement. How everything works: `docs/ARCHITECTURE.md`.
2. One branch per task in your fork, named for the area: `linux/<topic>`, `windows/<topic>`, `ui/<topic>`.
3. Build and run **your area's tests** (commands below): Linux runs `Sluice.Linux.slnf` on Linux, Windows runs
   `Sluice.Windows.slnf` on Windows; both include the shared tests. Every test must pass before a pull request, except
   the documented environment-only failures in `docs/CONTRIBUTING.md`. **New tests go in your area's test project**
   (Linux: `tests/Sluice.Tests.Linux`, Windows: `tests/Sluice.Tests.Windows`). A test that needs the other OS's version
   too (different paths, processes, system calls) gets one in each platform project, written by that area. Shared tests
   are the owner's: if one fails on your OS, report it in an issue instead of changing it.
4. **Verify for real, not only with unit tests.** Network and capture changes need a live run; UI changes need
   `--screenshot` images in light and dark. Paste the evidence (commands and output) into the pull request.
5. Ask the person before downloading anything new (packages, tools, runtimes), before commits, pushes and pull requests,
   and before anything that changes the machine's network or firewall settings.
6. Pull requests go to `main` of the owner's repository: one area per pull request, with the area in the title
   (`[Linux] …`).

```bash
dotnet build src/Sluice.Desktop -f net10.0                       # Linux app (also builds on Windows)
dotnet build src/Sluice.Desktop -f net10.0-windows10.0.19041.0   # Windows app
dotnet test  Sluice.Linux.slnf                                   # Linux area, on Linux: shared + Linux tests
dotnet test  Sluice.Windows.slnf                                 # Windows area, on Windows: shared + Windows tests
```

The UI area runs both on the OSes it can (at least the one it develops on).

The first build downloads WinDivert and sing-box (pinned, SHA-256 checked) into `.deps/` (`build/Dependencies.targets`).
On Linux, add `-p:EnableWindowsTargeting=true` to the `dotnet build` commands until issue #1 is fixed
(`Sluice.Linux.slnf` doesn't need it); the Linux test failures to expect are listed in `docs/CONTRIBUTING.md`.

## 4. Conventions

- Shared logic in `Sluice.Core`, OS-specific code in `Sluice.Platform.Windows` / `Sluice.Platform.Linux` behind
  `IPlatformServices`; the UI is one Avalonia app for both OSes with the same design.
- Outbound connections are **tunnels**, never "proxies", in UI text and code.
- `*.sh` and anything handed to Linux tools must have LF line endings (`.gitattributes` handles `*.sh`).
- `main` holds code, tests and docs only (plus repository checks in `.github/workflows/`). Release packaging lives on `release/linux` and `release/windows`, agent notes on
  `agent-notes`; none of them is merged into `main`.
