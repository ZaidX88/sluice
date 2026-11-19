# Sluice: rules for AI coding agents

Sluice is a rule-based tunnelling client for Windows and Linux (C# / .NET 10, Avalonia UI, WinDivert on Windows,
nftables + NFQUEUE on Linux, sing-box as a helper process). People work on it in parallel, one **area** each, through
forks and pull requests. These rules keep their changes from colliding. Read all of them before changing anything.

## 1. Work in one area only

Every task belongs to exactly one area: the `area:` label of the issue the person you work for has claimed. Contributors
may work in **any** area, but on **one claimed issue at a time**; the area comes from the issue, not from the person. If
the area isn't clear, **ask before editing anything**. You may change only the files of that area, plus the shared docs
(below):

| Area | You may change | Owner of everything else |
|---|---|---|
| **Linux** | `src/Sluice.Platform.Linux/**`, `scripts/linux/**`, `packaging/linux/**`, `scripts/package-linux.sh`, `docs/linux/**`, `tests/Sluice.Tests.Linux/**` | repository owner |
| **Windows** | `src/Sluice.Platform.Windows/**`, `docs/windows/**`, the Windows-only files still in Core (`src/Sluice.Core/Interception/Interceptor.cs`, `WinDivertNative.cs`, `UdpNat.cs`, `CaptureFilter.cs`, `src/Sluice.Core/Platform/ProcessLookup.cs`, `src/Sluice.Core/Profiles/Dpapi.cs`), `scripts/*.ps1`, `packaging/windows/**`, `tests/Sluice.Tests.Windows/**` | repository owner |
| **UI** | `src/Sluice.Desktop/**` (and the legacy `src/Sluice.App/**`), `docs/ui/**`, `docs/architecture/desktop.md` | repository owner |
| **Core** (owner; contributors only on `help wanted` issues) | everything else: `src/Sluice.Core/**` (shared engine), `src/Sluice.Cli/**`, the shared tests `tests/Sluice.Tests/**` and `tests/Sluice.Tests.Support/**`, `build/**`,
`Sluice.slnx`, `Sluice.*.slnf`, `docs/core/**`, `.github/**`, `README.md`, `AGENTS.md`, `CLAUDE.md`, `.gitignore`, `.gitattributes` | — |
| **Shared docs** (every area) | `docs/*.md` (`STATUS.md`, `REQUIRED.md`, `ARCHITECTURE.md`, `HISTORY.md`, `QUICKSTART.md`, …) and `docs/architecture/**`, for what your change affects | — |

- **Strict.** If the task needs a change outside your area, even one line in shared code or the UI, **stop**. Don't make
  it. Tell the person what is needed, where and why, so the area's owner can make it (a GitHub issue is the usual way).
  Then finish what you can inside your area.
- **One area per pull request**, checked automatically (the "Area" check, `.github/workflows/area-check.yml`). A Linux
  task changes Linux files only: no UI, no `Sluice.Core`, no Windows changes; the same for every area. A change found
  for another area goes in an issue.
- **Only claim what can be tested:** Windows issues need a Windows machine, Linux issues a Linux one (a live run is
  required); UI issues at least one OS, with screenshots in light and dark.
- **Core is the owner's** unless its issue is labelled `help wanted`; then a contributor may claim it like any other.
  The rules (`AGENTS.md`, `docs/CONTRIBUTING.md`) and `docs/DECISIONS.md` stay the owner's.
- **A problem that needs changes in several areas** gets one parent issue and one sub-issue per area
  (`docs/CONTRIBUTING.md` → Issues). Offer the person a draft: the parent with the evidence and a "Parts" list
  (`Core: …`, `Linux: …`), saying which part must land first and what interface the other parts will use. Only the
  owner splits it into sub-issues, when triaging. Then fix only your area's sub-issue; its pull request says `Fixes #<sub-issue>`. Ask the
  person before creating, commenting on or closing issues. Don't start a sub-issue that is **blocked by** an open issue
  without asking: its pull request can't be merged until the blocker is closed (the required "Blocked issues" check).
- **Docs:** every area has its own `docs/<area>/FIXES.md` (known bugs) and `FEATURES.md` (ideas, performance work);
  Core's are in `docs/core/`. The shared docs (`docs/*.md`, `docs/architecture/**`) may be changed by every area in the
  pull request whose change they describe: `STATUS.md`, `REQUIRED.md`, the architecture pages, a `HISTORY.md` line.
  Changes to the rules (`docs/CONTRIBUTING.md`, `AGENTS.md`) and to settled decisions (`docs/DECISIONS.md`) need the
  owner's agreement first.
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

**Order for every fix, feature or required item** (`docs/CONTRIBUTING.md` → From idea to merge): an **issue**, the
owner's **triage** (and split, if it needs several areas), the person **claims** it, then **one pull request** with
the code, the tests and the doc updates, saying `Fixes #<issue>`. Don't write code for an item that has no issue:
offer the person a draft of the issue (the template's fields) and ask before creating it.

**Work only on an issue that is labelled `ready` or `in progress` and assigned to the person you work for.** Never on
a `triage` issue or a parent issue. To claim a `ready` issue, the person comments "I'll take this" and the owner
assigns them; draft that comment and ask before posting it. Questions about the plan or the split go in the issue (the
parent, for a split), not in the pull request. An assigned issue left without activity for 14 days is unassigned and
goes back to `ready`.
Only a **decision** gets a docs pull request before the code: changing `docs/DECISIONS.md`, a new interface several
areas will use, or an architecture change. Trivial changes (typos, wording) need no issue.

**Before any work, update the local copy with the latest `main` of the owner's repository**
(`github.com/ZaidX88/sluice`) and start the branch from it. Check `git remote -v`: in a fork that remote is usually
`upstream` (the fork itself is `origin`, and its `main` may be old); in the owner's own clone it is `origin`. Then
`git fetch <that remote>`, `git switch main && git merge --ff-only <that remote>/main`, `git switch -c <area>/<topic>`.
Before opening the pull request and before every later push, update the branch onto that `main` again, resolve
conflicts on your side and rerun the tests.

**For a sub-issue, follow the parent's Coordination section exactly:** the order, the interface between the parts
(names, signatures, formats), and which files your part may touch. Don't design your own interface or change another
part's files. If the Coordination section doesn't fit what you find, stop and tell the person; it is raised on the
parent and the owner updates the section before anyone codes around it.

**Before starting any issue, check that it isn't blocked:**

```bash
gh api repos/ZaidX88/sluice/issues/<N>/dependencies/blocked_by --jq '.[] | select(.state=="open") | "#\(.number) \(.title)"'
```

Any output means it is blocked by an open issue: stop and tell the person which one; don't start without their
agreement. Also check it is in your area and assigned to the person (`gh issue view <N> --json assignees,labels`).

1. Read `docs/CONTRIBUTING.md` (workflow, areas, testing), then `docs/STATUS.md`, then the issue you were given and the
   doc entry it links (`docs/REQUIRED.md`, or your area's `FIXES.md` / `FEATURES.md`). Linux: also `docs/linux/WORK.md`. Settled decisions are in
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
The commands work as written on Linux (`Directory.Build.props` allows restoring the Windows target there); the Linux
test failures to expect are listed in `docs/CONTRIBUTING.md`.

## 4. Conventions

- Shared logic in `Sluice.Core`, OS-specific code in `Sluice.Platform.Windows` / `Sluice.Platform.Linux` behind
  `IPlatformServices`; the UI is one Avalonia app for both OSes with the same design.
- Outbound connections are **tunnels**, never "proxies", in UI text and code.
- `*.sh` and anything handed to Linux tools must have LF line endings (`.gitattributes` handles `*.sh`).
- `main` holds code, tests and docs (plus repository checks in `.github/workflows/`). Packaging scripts are code and
  live on `main` too (`packaging/**`, `scripts/package-*`); built packages are never committed, CI builds them from a
  release tag. Agent notes live on `agent-notes`, which is never merged into `main`.
