---
name: working-rules
description: How the owner wants work done on Sluice — commits only when asked, ask before downloads, never drive their desktop, verify visually and live
metadata:
  type: feedback
---

Rules the owner set or confirmed (2026-11-10 … 2026-11-14):

- **Commit and merge only when the owner says so** (the phrase used was "Commit and merge"). Work on a branch per feature
  or fix (`feature/…`, `fix/…`, `perf/…`, `docs/…`, `chore/…`), then commit, check out `main`, `git merge --no-ff`.
  Sometimes the owner wants a branch kept separate instead of merged: follow the latest instruction. Never push unless asked.
- **Ask before any download** (new NuGet packages, runtimes, tools, binaries); say what and roughly how big. One approval
  covers that download only. (The build's own pinned dependency fetch is part of the project and fine.)
- **Never simulate mouse clicks or keyboard input on the owner's desktop.** An automated click once hit their video
  player. Opening a window for them to look at is fine; driving it is not.
- **Don't change firewall rules, system settings or the owner's running Sluice yourself.** Offer the command or a button.
  Ask before stopping their processes: their internet may depend on a running Sluice.
- **Don't modify the owner's settings or profile as a side effect of tests.** Use scratch copies and scratch output folders.
- **Verify, don't assume.** UI work: render `--screenshot` in light and dark and compare. Capture/network work: a live test,
  not only unit tests. A Linux package: install it fresh and start capture as a normal user before handing it out (a
  package once passed protocol checks while its capture was broken by CRLF line endings).
- When asked only for suggestions ("don't implement anything"), give a categorised list and change no code.
- Decisions that are the owner's (scope, what to keep, what to publish) get asked, briefly, with a recommendation.

**Why:** these came from explicit instructions or from mistakes the owner saw.
**How to apply:** check them before any outward or irreversible action. Related: [[environment]], [[project]].
