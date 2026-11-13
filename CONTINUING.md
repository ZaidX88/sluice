# Continuing the work

Sluice was built by its owner working with an AI coding agent (Claude Code). Everything needed to continue, by hand or
with any agent, is in this repository. This page explains how.

## 1. Set up

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and git. For Linux work also a Linux machine or
   WSL2 (Windows 11 has WSLg for the UI).
2. Clone and build; the first build downloads WinDivert and sing-box (pinned, checksum-verified):

   ```bash
   dotnet build src/Sluice.Desktop -f net10.0-windows10.0.19041.0    # or -f net10.0 for Linux
   dotnet test tests/Sluice.Tests
   ```

3. Read, in this order: [README.md](README.md) → [docs/STATUS.md](docs/STATUS.md) → [docs/REQUIRED.md](docs/REQUIRED.md)
   → [docs/FIXES.md](docs/FIXES.md) → [docs/FEATURES.md](docs/FEATURES.md). Settled decisions are in
   [claude/DECISIONS.md](claude/DECISIONS.md).

## 2. Continue with an AI agent

**Claude Code:** open the repository folder and start a session. [CLAUDE.md](CLAUDE.md) is loaded automatically and
pulls in the notes from [claude/memory/](claude/memory/): the project, the working rules, the environment pitfalls and the
current status. To let Claude Code keep updating those notes as its own memory, copy them into its memory folder (see
[claude/README.md](claude/README.md)).

**Other agents** (Codex, Cursor, Copilot, Aider…): point them at `CLAUDE.md` first; most read `AGENTS.md`-style files,
and `CLAUDE.md` is plain Markdown. A good first message:

> You are continuing an existing project. Read CLAUDE.md, the files it references in claude/memory/, claude/DECISIONS.md
> and docs/STATUS.md. Then take the first open item in docs/REQUIRED.md (or: <the item you want>). Work on a new branch,
> follow claude/memory/working-rules.md, verify with the tests and checks in docs/FEATURES.md, and update docs/ and
> claude/ when you finish. Don't commit or push until I say so.

Tips:
- **Give one item at a time** from `docs/REQUIRED.md`, `FIXES.md` or `FEATURES.md`. Each entry says what "done" means, so
  you can hold the agent to it.
- **Ask for proof:** test output, `--screenshot` images for UI changes, a live capture test for network changes. The notes
  list past cases where unit tests passed but the real thing didn't.
- **Decisions stay yours:** the rules tell agents to ask before downloads, irreversible steps and scope changes.
- **Keep the notes alive:** after each piece of work, have the agent update the matching doc and `claude/memory/status.md`.
  The next session, yours or someone else's, starts from them.

## 3. Continue by hand

Same reading list. Conventions: one branch per change (`feature/…`, `fix/…`), merged into `main` with `--no-ff`; shared
code in `Sluice.Core`, OS code in the platform modules; outbounds are "tunnels"; UI text in plain sentence case, the same
design on both OSes. Update `docs/` when you change behaviour.

## 4. What not to commit

No secrets, real server addresses, personal names or machine-specific paths: in code, docs or the `claude/` notes, all of
which are public. Third-party binaries come from the build (`.deps/`), never from git. Release artefacts and recordings
stay outside the repository (`.gitignore` covers `promo/`, `release-linux/`, `.deps/`, `lib/`).
