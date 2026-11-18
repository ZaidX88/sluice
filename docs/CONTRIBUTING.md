# Contributing

Sluice is developed by several people in parallel, each on one **area** (Linux, Windows, UI), often with an AI coding
agent. Areas don't overlap, so pull requests don't conflict. The rules for agents are in [../AGENTS.md](../AGENTS.md)
(`CLAUDE.md` loads the same file); this page is the same thing for people, with the workflow.

## Areas

| Area | Files | Typical work |
|---|---|---|
| **Linux** | `src/Sluice.Platform.Linux/`, `scripts/linux/`, `docs/linux/`, `tests/Sluice.Tests.Linux/` | capture (nftables, NFQUEUE), DNS, process lookup, notifications, testing on real distributions: [linux/WORK.md](linux/WORK.md) |
| **Windows** | `src/Sluice.Platform.Windows/`, `docs/windows/`, the Windows-only files still in Core (listed in `AGENTS.md`), `scripts/*.ps1`, `tests/Sluice.Tests.Windows/` | WinDivert capture, Windows integration |
| **UI** | `src/Sluice.Desktop/` (legacy `src/Sluice.App/`), `docs/architecture/desktop.md` | the Avalonia app on both OSes |
| **Core** | everything else (`src/Sluice.Core/` shared engine, `src/Sluice.Cli/`, shared tests `tests/Sluice.Tests/` and `tests/Sluice.Tests.Support/`, shared docs, build files) | the repository owner |

A change outside your area, even a small one, is not made in your pull request: open an issue that says what is needed
and why, and the area's owner makes it (see [Issues](#issues) for problems that need changes in several areas). Shared
docs (`docs/STATUS.md`, `FIXES.md`, `REQUIRED.md`, `docs/architecture/`) are the owner's: put your findings in your
area's docs and in the pull request, and the owner carries them over.

## Issues

Every problem or request is a GitHub issue. Title starts with the area (`[Linux] …`, `[Windows] …`, `[UI] …`, `[Core] …`).
Describe what you saw, how to reproduce it, the evidence (commands, output, log lines; no real server addresses), and
what "fixed" would look like.

**One area:** one issue, fixed by one pull request from that area's owner.

**Several areas** (for example a bug with a Linux part and a Core part): one **parent issue** and one **sub-issue per
area**, so each part has its own owner and pull request and nobody edits another area's files.

1. **The finder opens the parent issue**: the problem once, the evidence, and a proposed split under a "Parts" heading
   (`Core: …`, `Linux: …`). No title prefix, or name both areas.
2. **The owner decides the split** and creates the sub-issues, one per area, each with its own area prefix. The owner
   knows the shared design: a split can often keep a platform part free of Core changes (for example by adding a shared
   helper the platform code just calls). If you can create and link sub-issues yourself, do so; the owner may still
   change the split.
3. **Write down the order.** Usually the shared part (Core) lands first. Its sub-issue names the interface the others
   will use (class, method, event), so they can plan or start before it is merged. A part that has to wait is marked
   on GitHub: open its issue → **Relationships → Mark as blocked by** → the issue it waits for.
   **This is enforced at merge time:** a pull request that fixes a blocked issue gets a failing **Blocked issues**
   check, and `main` requires that check, so it can be reviewed but not merged until the blocker is closed. The check
   re-runs by itself when an issue is closed or reopened, and when the pull request's description changes. If you link
   the issue only in the PR's sidebar, re-run it from **Actions → Blocked issues → Run workflow**
   (`.github/workflows/blocked-issues.yml`).
4. **One sub-issue, one pull request, one owner.** The pull request says `Fixes #<sub-issue>`, which closes that
   sub-issue when it is merged. Each sub-issue is assigned to its owner.
5. **Close the parent by hand** when every sub-issue is closed (it doesn't close by itself), with a short note on what
   changed overall. The owner then updates the shared docs if no pull request did.

Don't split a problem that only one area has to change. Example of a split: #11 (DNS settings ignored during capture)
with #5 for the Windows part and #12 for the Linux part.

## Workflow

1. **Fork** the repository on GitHub and clone your fork.
2. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and git. Build once; the first build downloads
   WinDivert and sing-box (pinned, SHA-256 checked) into `.deps/`.
3. Keep your fork's `main` up to date with the owner's (`git remote add upstream <owner's repository>`,
   `git pull upstream main`).
4. One branch per task: `linux/<topic>`, `windows/<topic>`, `ui/<topic>`.
5. Before opening a pull request:
   - your area's tests pass: `dotnet test Sluice.Linux.slnf` on Linux or `dotnet test Sluice.Windows.slnf` on
     Windows (see the known environment-only failures below);
   - you checked the change for real (live capture run, or `--screenshot` images in light and dark for UI);
   - you updated your area's docs.
6. Open the pull request against the owner's `main`. Title starts with the area (`[Linux] Bind the relay to loopback`).
   Description: what changed and why, the issue it fixes (`Fixes #N`, so the issue closes on merge) or the doc item it
   addresses (e.g. "FIXES §6"), how you tested it with the commands and their output, and anything that needs a change
   in another area (with the issue you opened for it).

## Building and testing

```bash
dotnet build src/Sluice.Desktop -f net10.0                       # Linux app (builds on Windows too)
dotnet build src/Sluice.Desktop -f net10.0-windows10.0.19041.0   # Windows app
dotnet build src/Sluice.Cli
dotnet test  Sluice.Linux.slnf                                   # on Linux: shared + Linux tests
dotnet test  Sluice.Windows.slnf                                 # on Windows: shared + Windows tests
```

### Tests

| Project | Owner | Runs on | Holds |
|---|---|---|---|
| `tests/Sluice.Tests` | owner | Windows and Linux | everything that doesn't depend on the OS (engine, rules, DNS, share links, tunnels, live sing-box tests) |
| `tests/Sluice.Tests.Linux` | Linux area | Linux | `Sluice.Platform.Linux` and the Linux versions of OS-dependent tests |
| `tests/Sluice.Tests.Windows` | Windows area | Windows only | WinDivert capture, DPAPI, the Windows versions of OS-dependent tests |
| `tests/Sluice.Tests.Support` | owner | (library) | shared helpers: loopback test servers, `[WindowsFact]` / `[LinuxFact]` |

- Each area runs its filter on its own OS: `Sluice.Linux.slnf` = shared + Linux, `Sluice.Windows.slnf` = shared +
  Windows. The shared tests are not copied; they run in both filters.
- **New tests go in your area's project.** A behaviour that differs per OS (application paths, process lookup, system
  calls) gets one test in each platform project, written by that area. For example, application rules are tested with
  `C:\…\app.exe` in the Windows project and with `/usr/bin/app` in the Linux one.
- A test that only works on one OS but has to sit in a project that also runs elsewhere uses `[WindowsFact]` /
  `[LinuxFact]` (and the `Theory` versions), which skip themselves on the other OS.
- Shared tests are the owner's. If one fails on your OS, open an issue rather than changing or skipping it.

Platform guides: [linux/BUILD.md](linux/BUILD.md), [windows/BUILD.md](windows/BUILD.md). How everything works:
[ARCHITECTURE.md](ARCHITECTURE.md).

Known environment-only failures ([FIXES.md §7](FIXES.md#7-tests-that-depend-on-the-developer-machine)):
- `HappyEyeballsTests.DeadFirstAddress_DoesNotDelayTheWorkingOne` fails while a Sluice capture runs on the same machine.
- Live sing-box tests are timing-sensitive under load; if one fails in a full run, rerun it alone
  (`--filter FullyQualifiedName~LiveSingBoxTests`).
- **On Linux**, `RelayTests.SlowApp_DoesNotBlockUpstream_OnlyWithElasticBuffer` fails until
  [issue #3](https://github.com/ZaidX88/sluice/issues/3) is fixed. Every Linux test must still pass. List the
  failures you saw in the pull request.

### Live testing with a real tunnel

The tests use local servers only. To check capture and tunnels for real you need a **working tunnel of your own**: an SSH
server, a SOCKS5 proxy, or a VLESS/Trojan/… server you are allowed to use. Its address and credentials **never go into
the repository**:
- keep profiles and share links outside the repository, or in `local/` (ignored by git);
- tests that need one read it from environment variables (e.g. `SLUICE_LIVE_SSH_PORT`, `SLUICE_LIVE_SSH_USER`,
  `SLUICE_LIVE_SSH_KEY`) and skip when they're unset;
- in pull requests and docs, describe the setup ("SSH tunnel, ~2 MB/s") without the server's name or address.

## Rules for every change

- **No private data:** no real server addresses, credentials, profiles, share links, personal names or machine paths in
  code, tests, docs or commit messages. Use `example.com`, `ssh.example`, `alice`, and `192.0.2.x`, `198.51.100.x`,
  `203.0.113.x`, `2001:db8::/32`. (`198.18.0.0/15` is Sluice's fake-address range, not a placeholder.)
- **Settled decisions** ([DECISIONS.md](DECISIONS.md)) change only with the owner's agreement.
- Outbound connections are **tunnels**, never "proxies", in UI text and code.
- Shell scripts and anything given to Linux tools use LF line endings.
- No third-party binaries in git: the build fetches them.

## Branches in the owner's repository

| Branch | Holds | Merged into `main`? |
|---|---|---|
| `main` | code, tests, docs | — |
| `release/linux`, `release/windows` | release packaging (installer scripts, package layout, version bumps) on top of `main` | never |
| `agent-notes` | the owner's notes for AI agents | never |
