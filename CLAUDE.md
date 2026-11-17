# Sluice: instructions for AI agents

Sluice is a rule-based tunnelling client for Windows and Linux (C# / .NET 10, Avalonia, WinDivert, nftables + NFQUEUE,
sing-box helper). Read these before working:

@claude/memory/project.md
@claude/memory/working-rules.md
@claude/memory/environment.md
@claude/memory/status.md

Then, as needed: `claude/DECISIONS.md` (settled decisions), `docs/ARCHITECTURE.md` (how everything works: connection
and DNS flow, then one page per part in `docs/architecture/`), `docs/STATUS.md` (what works, capture internals),
`docs/REQUIRED.md`, `docs/FIXES.md`, `docs/FEATURES.md` (the plan), `docs/linux/BUILD.md` and `docs/windows/BUILD.md`
(building, testing).

## Essentials

- Build: `dotnet build src/Sluice.Desktop -f net10.0-windows10.0.19041.0` (Windows) or `-f net10.0` (Linux). The first
  build downloads WinDivert and sing-box into `.deps/` (`build/Dependencies.targets`).
- Test: `dotnet test tests/Sluice.Tests` (includes live tests against local sing-box servers).
- UI check: `Sluice.Desktop --screenshot <dir> [--theme dark]`, compare light and dark.
- Branch per change; commit and merge only when the owner asks; never push unless asked.
- Shared code in `Sluice.Core`, OS-specific code in `Sluice.Platform.Windows` / `Sluice.Platform.Linux`.
- Outbounds are "tunnels", never "proxies".
- When you finish or learn something non-obvious, update `docs/` and `claude/` in the same change. These files are
  public: no secrets, server addresses, personal names or local paths.
