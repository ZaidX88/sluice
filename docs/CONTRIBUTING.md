# Contributing

Sluice is developed by several people in parallel, each on one **area** (Linux, Windows, UI), often with an AI coding
agent. Areas don't overlap, so pull requests don't conflict. The rules for agents are in [../AGENTS.md](../AGENTS.md)
(`CLAUDE.md` loads the same file); this page is the same thing for people, with the workflow.

## Areas

| Area | Files | Typical work |
|---|---|---|
| **Linux** | `src/Sluice.Platform.Linux/`, `scripts/linux/`, `docs/linux/`, `tests/Sluice.Tests/Linux*Tests.cs` | capture (nftables, NFQUEUE), DNS, process lookup, notifications, testing on real distributions: [linux/WORK.md](linux/WORK.md) |
| **Windows** | `src/Sluice.Platform.Windows/`, `docs/windows/`, the Windows-only files still in Core (listed in `AGENTS.md`), `scripts/*.ps1`, their tests | WinDivert capture, Windows integration |
| **UI** | `src/Sluice.Desktop/` (legacy `src/Sluice.App/`), `docs/architecture/desktop.md` | the Avalonia app on both OSes |
| **Core** | everything else (`src/Sluice.Core/` shared engine, `src/Sluice.Cli/`, shared tests and docs, build files) | the repository owner |

A change outside your area, even a small one, is not made in your pull request: open an issue that says what is needed
and why, and the area's owner makes it. Shared docs (`docs/STATUS.md`, `FIXES.md`, `REQUIRED.md`, `docs/architecture/`)
are the owner's: put your findings in your area's docs and in the pull request, and the owner carries them over.

## Workflow

1. **Fork** the repository on GitHub and clone your fork.
2. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and git. Build once; the first build downloads
   WinDivert and sing-box (pinned, SHA-256 checked) into `.deps/`.
3. Keep your fork's `main` up to date with the owner's (`git remote add upstream <owner's repository>`,
   `git pull upstream main`).
4. One branch per task: `linux/<topic>`, `windows/<topic>`, `ui/<topic>`.
5. Before opening a pull request:
   - `dotnet test tests/Sluice.Tests` passes (see the known environment-only failures below);
   - you checked the change for real (live capture run, or `--screenshot` images in light and dark for UI);
   - you updated your area's docs.
6. Open the pull request against the owner's `main`. Title starts with the area (`[Linux] Bind the relay to loopback`).
   Description: what changed and why, the doc item it addresses (e.g. "FIXES §6"), how you tested it with the commands
   and their output, and anything that needs a change in another area.

## Building and testing

```bash
dotnet build src/Sluice.Desktop -f net10.0                       # Linux app (builds on Windows too)
dotnet build src/Sluice.Desktop -f net10.0-windows10.0.19041.0   # Windows app
dotnet build src/Sluice.Cli
dotnet test  tests/Sluice.Tests                                  # unit tests + live tests on local sing-box servers
```

Platform guides: [linux/BUILD.md](linux/BUILD.md), [windows/BUILD.md](windows/BUILD.md). How everything works:
[ARCHITECTURE.md](ARCHITECTURE.md).

Known environment-only failures ([FIXES.md §7](FIXES.md#7-tests-that-depend-on-the-developer-machine)):
- `HappyEyeballsTests.DeadFirstAddress_DoesNotDelayTheWorkingOne` fails while a Sluice capture runs on the same machine.
- Live sing-box tests are timing-sensitive under load; if one fails in a full run, rerun it alone
  (`--filter FullyQualifiedName~LiveSingBoxTests`).

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
