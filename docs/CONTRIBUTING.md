# Contributing

Sluice is developed by several people in parallel, often with an AI coding agent. The code is split into **areas**
(Linux, Windows, UI, Core); every issue and every pull request belongs to exactly one, and areas don't overlap, so pull
requests don't conflict. Contributors may work in any area, one claimed issue at a time. The rules for agents are in [../AGENTS.md](../AGENTS.md)
(`CLAUDE.md` loads the same file); this page is the same thing for people, with the workflow.

## Areas

| Area | Files | Typical work |
|---|---|---|
| **Linux** | `src/Sluice.Platform.Linux/`, `scripts/linux/`, `docs/linux/`, `tests/Sluice.Tests.Linux/` | capture (nftables, NFQUEUE), DNS, process lookup, notifications, testing on real distributions: [linux/WORK.md](linux/WORK.md) |
| **Windows** | `src/Sluice.Platform.Windows/`, `docs/windows/`, the Windows-only files still in Core (listed in `AGENTS.md`), `scripts/*.ps1`, `tests/Sluice.Tests.Windows/` | WinDivert capture, Windows integration |
| **UI** | `src/Sluice.Desktop/` (legacy `src/Sluice.App/`), `docs/ui/`, `docs/architecture/desktop.md` | the Avalonia app on both OSes |
| **Core** (owner; `help wanted` issues: anyone) | everything else (`src/Sluice.Core/` shared engine, `src/Sluice.Cli/`, shared tests `tests/Sluice.Tests/` and `tests/Sluice.Tests.Support/`, `docs/core/`, `.github/`, build files) | the repository owner |
| **Shared docs** | `docs/*.md` (`STATUS.md`, `REQUIRED.md`, `ARCHITECTURE.md`, `HISTORY.md`, …) and `docs/architecture/` | every area, in the pull request whose change they describe |

**Who works where:**
- **The area belongs to the issue, not to the person.** Claim any `ready` issue; its `area:` label decides which files
  your pull request may change. Work that needs several areas is a parent with one sub-issue per area ([Issues](#issues)).
- **One claimed issue at a time** (two at most while one waits for review).
- **Only claim what you can test:** Windows issues need a Windows machine and Linux issues a Linux one, because a live
  run is required; UI issues need at least one OS, with screenshots in light and dark.
- **Core is the owner's**, except issues labelled `help wanted`, which anyone may claim. The rules (`AGENTS.md`, this
  page) and [DECISIONS.md](DECISIONS.md) stay the owner's.
- **One area per pull request.** The **Area** check (`.github/workflows/area-check.yml`) fails a pull request whose files
  span more than one area (shared docs don't count). A change for another area, even a small one, goes in an issue.
  `.github/CODEOWNERS` asks the owner to review changes to Core, `.github/` and the rules.
- When triaging, the owner doesn't mark two issues `ready` at the same time if they change the same files; one waits
  for the other (marked "blocked by").

### Docs

| Docs | What | Who changes them |
|---|---|---|
| `docs/<area>/FIXES.md` | the area's known bugs and limits: what was seen, why, what to do, when it counts as fixed | that area |
| `docs/<area>/FEATURES.md` | the area's ideas not built yet, and its performance work | that area |
| `docs/linux/WORK.md`, `docs/<area>/BUILD.md` | how to work on, build and test the area | that area |
| `docs/REQUIRED.md` | must-do work for everyone, in order | every area |
| `docs/STATUS.md`, `ARCHITECTURE.md`, `architecture/`, `HISTORY.md`, `QUICKSTART.md` | how Sluice stands and works, what was built | every area |
| `docs/CONTRIBUTING.md`, `docs/DECISIONS.md`, `AGENTS.md` | the rules and settled decisions | every area, **with the owner's agreement first** |

`<area>` is `core`, `linux`, `windows` or `ui`. Every area keeps the shared docs true for its own changes: a pull
request that changes how something works updates `STATUS.md` or the architecture page in the same pull request.

## From idea to merge

Every fix, feature or required item: **an issue, triaged by the owner, then one pull request.**

1. **Open the issue** with the template (**New issue** → Bug, Feature or Required work), so it says what was seen or
   wanted, why, what to do, and when it counts as done. Title starts with the area. If it needs several areas, propose
   the split under a "Parts" heading; don't create sub-issues yourself. The template adds the **`triage`** label.
2. **The owner triages it**, with a comment: confirms the area and that it is worth doing, and if it needs several
   areas, **splits it** and writes the parent's **Coordination** section ([Issues](#issues) step 3): the issue becomes the parent, the owner creates one sub-issue per area and marks which parts
   are blocked by which ([Issues](#issues)). Then the owner replaces `triage` with **`ready`** on each issue that can
   be worked on. A parent issue never gets `ready`; only its sub-issues do.
3. **Claim it.** Comment "I'll take this" on a `ready` issue in your area. The owner assigns you (in this repository
   contributors can't assign themselves) and the label becomes **`in progress`**. Don't start before you are assigned.
4. **Work on it** after the checks in [Before you start an issue](#before-you-start-an-issue). Opening a **draft pull
   request** early shows the work is under way.
5. **Pull request.** One per issue (or sub-issue), with the code, the tests **and** the doc updates together:
   - `Fixes #<issue>` in the description, so the issue closes when it is merged;
   - your area's `FIXES.md` / `FEATURES.md`: add an entry when the cause, evidence or reproduction is worth keeping
     after the fix, or mark the existing entry **fixed** (with the issue and PR numbers) or remove it;
   - the shared docs the change affects (`STATUS.md`, `REQUIRED.md`, an architecture page) and one line in
     [HISTORY.md](HISTORY.md).

**A docs pull request comes first only for a decision**, so it is agreed before anyone builds on it: changing a settled
decision ([DECISIONS.md](DECISIONS.md)), a new interface several areas will use (for example a Core class that the
Linux and Windows parts will call), or an architecture change. It goes in the parent issue's "Parts" as the first part;
the code pull requests come after it is merged.

**No issue needed** for trivial changes: typos, doc wording, a broken link. Open the pull request directly.

**Questions go in the issue, not the pull request.** The plan, the split and the interface between parts are
discussed on the issue (the parent, for a split) before code starts. The pull request is for reviewing the code.

### Labels and the board

| Label | Meaning | Set by |
|---|---|---|
| `triage` | new, not looked at yet; nobody works on it | the issue templates |
| `ready` | triaged and free to claim | the owner |
| `in progress` | assigned; someone is working on it | the owner, when assigning |
| `bug`, `enhancement` | kind of issue | the issue templates |
| `area: core`, `area: linux`, `area: windows`, `area: ui` | the area that does the work | the owner, when triaging |

The **Sluice** project board (the repository's **Projects** tab) shows every issue and pull request in columns
**Triage → Ready → In progress → In review → Done**. The labels are the source of truth; the board is the overview
and follows them by itself (`.github/workflows/project-sync.yml`): a label moves the card to its column, a pull request
moves to In review (and the issues it fixes with it; a draft stays In progress), and a closed issue or merged pull
request moves to Done. New issues and pull requests are added to the board.

**Unclaim rule:** an assigned issue with no activity (comments, commits, pull request updates) for **14 days** goes
back to `ready` and is unassigned, so someone else can take it. A daily check (`.github/workflows/unclaim.yml`) posts a
reminder after 10 days without activity and unassigns after 14. Activity is a comment, the assignment itself, or an
update to an open pull request that fixes the issue. Say in the issue if you need longer.

### Before you start an issue

Check that it is free to work on, every time, before you write any code:

- **Not blocked.** Open the issue → **Relationships**: if it is **blocked by** an issue that is still open, don't
  start it; wait for the blocker or ask the owner. From the command line:

  ```bash
  gh api repos/ZaidX88/sluice/issues/<N>/dependencies/blocked_by --jq '.[] | select(.state=="open") | "#\(.number) \(.title)"'
  ```

  Empty output means nothing blocks it. A pull request for a blocked issue can't be merged anyway (the required
  **Blocked issues** check, see [Issues](#issues) step 3), so work started early is likely to wait or need redoing.
- **Yours.** It's in your area, labelled `ready` or `in progress`, and **assigned to you** ([From idea to
  merge](#from-idea-to-merge) step 3). Never work on a `triage` issue or on a parent issue.
- **Its parent's Coordination.** For a sub-issue, read the parent's Coordination section: what lands first, the
  interface to use, which files your part may touch ([Issues](#issues) step 3).
- **Up to date.** Before any work, bring your copy up to date with the owner's `main` and start your branch from it:

  ```bash
  git fetch upstream
  git switch main && git merge --ff-only upstream/main && git push origin main
  git switch -c linux/<topic>
  ```

  Before you open the pull request, and before every later push to it, update the branch again
  (`git fetch upstream && git rebase upstream/main`, or `git merge upstream/main`), resolve any conflict on your side,
  and rerun your area's tests. A pull request built on an old `main` is how two finished parts end up conflicting.

## Issues

Every problem or request is a GitHub issue ([From idea to merge](#from-idea-to-merge)). Use the template; the title
starts with the area (`[Linux] …`, `[Windows] …`, `[UI] …`, `[Core] …`). Describe what you saw, how to reproduce it,
the evidence (commands, output, log lines; no real server addresses), and what "fixed" would look like.

**One area:** one issue, fixed by one pull request from that area's owner.

**Several areas** (for example a bug with a Linux part and a Core part): one **parent issue** and one **sub-issue per
area**, so each part has its own owner and pull request and nobody edits another area's files.

1. **The finder opens the issue**: the problem once, the evidence, and a proposed split under a "Parts" heading
   (`Core: …`, `Linux: …`). It becomes the parent. No title prefix, or name both areas.
2. **The owner decides the split** when triaging, says so in a comment, and creates the sub-issues, one per area, each
   with its own area prefix and labelled `ready` (or left without it while blocked). The owner knows the shared design:
   a split can often keep a platform part free of Core changes (for example by adding a shared helper the platform code
   just calls). Only the owner creates sub-issues.
3. **Write the coordination down in the parent**, before any sub-issue is marked `ready`. The owner adds a
   **Coordination** section to the parent issue; it is the contract every part follows, so two parts can't each solve
   the problem their own way:
   - **Order:** which part lands first and which wait for it.
   - **Interface:** the exact names and signatures between the parts (class, method, event, setting, file format),
     what each returns or raises, and who calls whom. A new interface several areas use is agreed in a docs pull
     request first ([From idea to merge](#from-idea-to-merge)).
   - **Who changes what:** the files or classes each part touches, so no two parts edit the same thing.
   - **What stays as it is:** behaviour or files no part may change.

   Each sub-issue links to the parent's Coordination section and is implemented exactly as written there. If it doesn't
   fit once you start, **stop and say so on the parent**; the owner updates the section (and tells the other parts)
   before anyone codes around it. A pull request that departs from the Coordination section isn't merged.
   Usually the shared part (Core) lands first; the parts that use it start from it once it is merged ([Before you start
   an issue](#before-you-start-an-issue): up to date), or, if they start earlier against the agreed interface, update
   onto it before their pull request. A part that has to wait is marked on GitHub: open its issue → **Relationships → Mark as blocked by** → the issue it waits for.
   **This is enforced at merge time:** a pull request that fixes a blocked issue gets a failing **Blocked issues**
   check, and `main` requires that check, so it can be reviewed but not merged until the blocker is closed. The check
   re-runs by itself when an issue is closed or reopened, and when the pull request's description changes. If you link
   the issue only in the PR's sidebar, re-run it from **Actions → Blocked issues → Run workflow**
   (`.github/workflows/blocked-issues.yml`).
4. **One sub-issue, one pull request, one owner.** Contributors claim a sub-issue like any other issue (comment, then
   the owner assigns). The pull request says `Fixes #<sub-issue>`, which closes that sub-issue when it is merged.
5. **Close the parent by hand** when every sub-issue is closed (it doesn't close by itself), with a short note on what
   changed overall. The owner then updates the shared docs if no pull request did.

Don't split a problem that only one area has to change. Example of a split: #11 (DNS settings ignored during capture)
with #5 for the Windows part and #12 for the Linux part.

## Workflow

1. **Fork** the repository on GitHub and clone your fork.
2. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and git. Build once; the first build downloads
   WinDivert and sing-box (pinned, SHA-256 checked) into `.deps/`.
3. Add the owner's repository as `upstream` (`git remote add upstream <owner's repository>`). **Before any work**,
   update your `main` from it and branch from there; update the branch again before opening the pull request and
   before every later push ([Before you start an issue](#before-you-start-an-issue)).
4. Pick an issue that is free to work on ([Before you start an issue](#before-you-start-an-issue)); a new fix or
   feature gets its issue first ([From idea to merge](#from-idea-to-merge)).
5. One branch per task: `linux/<topic>`, `windows/<topic>`, `ui/<topic>`.
6. Before opening a pull request:
   - your area's tests pass: `dotnet test Sluice.Linux.slnf` on Linux or `dotnet test Sluice.Windows.slnf` on
     Windows (see the known environment-only failures below);
   - you checked the change for real (live capture run, or `--screenshot` images in light and dark for UI);
   - you updated the docs the change affects (your area's `FIXES.md` / `FEATURES.md`, shared docs, a `HISTORY.md`
     line).
7. Open the pull request against the owner's `main`. Title starts with the area (`[Linux] Bind the relay to loopback`).
   Description: what changed and why, the issue it fixes (`Fixes #N`, so the issue closes on merge), how you tested it with the commands and their output, and anything that needs a change
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

Known environment-only failures ([core/FIXES.md §2](core/FIXES.md#2-tests-that-depend-on-the-developer-machine)):
- `HappyEyeballsTests.DeadFirstAddress_DoesNotDelayTheWorkingOne` fails while a Sluice capture runs on the same machine.
- Live sing-box tests are timing-sensitive under load; if one fails in a full run, rerun it alone
  (`--filter FullyQualifiedName~LiveSingBoxTests`).
- **On Linux**, `RelayTests.SlowApp_DoesNotBlockUpstream_OnlyWithElasticBuffer` fails until
  [issue #3](https://github.com/ZaidX88/sluice/issues/3) is fixed. Every Linux test must still pass. List the
  failures you saw in the pull request.

### More test tools

- **Live tests** start real sing-box servers on 127.0.0.1 for every protocol (TCP and UDP) and connect through Sluice;
  they need the sing-box binary the build fetches. REALITY additionally needs `SLUICE_TEST_INTERNET=1`. Without them:
  `dotnet test Sluice.Windows.slnf --filter "FullyQualifiedName!~Live"`.
- **UI check:** `Sluice.Desktop --screenshot <dir> [--theme dark]` renders every page and dialog off-screen. After UI
  changes, compare both themes on Windows and Linux.
- **Real servers:** `Sluice.Cli check "<share link>"` runs the three-step tunnel check. Public lists of free servers are
  useful for compatibility testing (expect many dead entries; failures need a second look before blaming Sluice).
- **Linux capture:** `scripts/linux/capture-livetest.sh` (root, WSL works) checks routing decisions, DNS and clean-up;
  see [linux/BUILD.md](linux/BUILD.md). Always test capture from an *installed* package before handing one out.
- **Environment pitfalls:**
  - while a Sluice capture runs on the same machine, tests that make real connections are affected (fake DNS, routing);
  - NuGet restores of large packages through a slow tunnel time out and restart forever: route `dotnet.exe` Direct, run
    `dotnet restore --disable-parallel`, or download the `.nupkg` with a resumable `curl -C -` and pass
    `-p:RestoreAdditionalProjectSources=<folder>`.
- The first build downloads WinDivert and sing-box into `.deps/` (pinned, SHA-256 checked; `build/Dependencies.targets`);
  `-p:SluiceFetchDependencies=false` skips that.

### Live testing with a real tunnel

The tests use local servers only. To check capture and tunnels for real you need a **working tunnel of your own**: an SSH
server, a SOCKS5 proxy, or a VLESS/Trojan/… server you are allowed to use. Its address and credentials **never go into
the repository**:
- keep profiles and share links outside the repository, or in `local/` (ignored by git);
- tests that need one read it from environment variables (e.g. `SLUICE_LIVE_SSH_PORT`, `SLUICE_LIVE_SSH_USER`,
  `SLUICE_LIVE_SSH_KEY`) and skip when they're unset;
- in pull requests and docs, describe the setup ("SSH tunnel, ~2 MB/s") without the server's name or address.

## Rules for every change

- **Licence:** Sluice is under GPL-3.0 ([LICENSE](../LICENSE)). By opening a pull request you agree that your
  contribution is under the same licence (inbound = outbound). A new bundled component or NuGet package is added to
  [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) in the same pull request.
- **No private data:** no real server addresses, credentials, profiles, share links, personal names or machine paths in
  code, tests, docs or commit messages. Use `example.com`, `ssh.example`, `alice`, and `192.0.2.x`, `198.51.100.x`,
  `203.0.113.x`, `2001:db8::/32`. (`198.18.0.0/15` is Sluice's fake-address range, not a placeholder.)
- **Settled decisions** ([DECISIONS.md](DECISIONS.md)) change only with the owner's agreement.
- Outbound connections are **tunnels**, never "proxies", in UI text and code.
- Shell scripts and anything given to Linux tools use LF line endings.
- No third-party binaries in git: the build fetches them.

## Releases

- **One version for every build**, in one place: `<Version>` in `Directory.Build.props` (now `1.0.0-dev`). .NET puts
  it into every assembly with the commit it was built from; `SluiceVersion` (`src/Sluice.Core/SluiceVersion.cs`) shows
  it as `1.0.0-rc.1+abc1234`. `Sluice.Cli --version` prints it, and every log file starts with
  `Sluice <version> on <system>`, so a bug report says which build it came from.
- **Bumping it** is the owner's job, in its own small pull request: `1.0.0-dev` while working towards a release,
  `1.0.0-rc.1`, `-rc.2` … for release candidates, `1.0.0` for the release, then the next `-dev`. Never change it in a
  feature pull request.
- **A release tag** `vX.Y.Z` (or `vX.Y.Z-rc.N`) must match `<Version>`; release CI (#74) checks that and builds the
  packages, whose file names take the version (`sluice-1.0.0-linux-x64.tar.gz`).

## Branches in the owner's repository

| Branch | Holds | Merged into `main`? |
|---|---|---|
| `main` | code, tests, docs | — |
| `release/linux`, `release/windows` | release packaging (installer scripts, package layout, version bumps) on top of `main` | never |
| `agent-notes` | the owner's notes for AI agents | never |
