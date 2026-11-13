# claude/

Notes that let an AI coding agent (Claude Code or any other) continue Sluice where the last session stopped. Most of the
project was built by the owner working with Claude; these files carry over what isn't obvious from the code.

| File | What it holds |
|---|---|
| [memory/MEMORY.md](memory/MEMORY.md) | Index of the memory notes below (the format Claude Code uses for its auto-memory) |
| [memory/project.md](memory/project.md) | What Sluice is, the stack, terminology, the module structure |
| [memory/working-rules.md](memory/working-rules.md) | How the owner wants work done: commits, downloads, testing, safety |
| [memory/environment.md](memory/environment.md) | Development-environment pitfalls that cost time before |
| [memory/status.md](memory/status.md) | Where the work stands and what comes next |
| [DECISIONS.md](DECISIONS.md) | Decisions with dates and reasons, so they aren't re-litigated |
| [HISTORY.md](HISTORY.md) | What was built, in order |

The plan of record is in `docs/`: [STATUS.md](../docs/STATUS.md), [REQUIRED.md](../docs/REQUIRED.md),
[FIXES.md](../docs/FIXES.md), [FEATURES.md](../docs/FEATURES.md).

## Using these with Claude Code

`CLAUDE.md` at the repository root is loaded automatically and imports the working rules and project notes. To also give
Claude Code these notes as its *auto-memory* (the notes it updates itself), copy them into its memory folder for this
project:

```bash
# Linux / macOS (the folder name is the project's path with / replaced by -)
mkdir -p ~/.claude/projects/<project-path-slug>/memory && cp claude/memory/*.md ~/.claude/projects/<project-path-slug>/memory/
```

```powershell
# Windows: e.g. C:\src\sluice becomes C--src-sluice
Copy-Item claude\memory\*.md "$env:USERPROFILE\.claude\projects\<project-path-slug>\memory\"
```

Other agents: read `CLAUDE.md`, then these files, then `docs/`.

## Keeping them current

When a session finishes something or learns something non-obvious, update the matching file here and the plan in `docs/`
in the same change. Never put secrets, server addresses, personal names or local paths in these files; they are public.
