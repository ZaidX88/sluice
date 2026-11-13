---
name: status
description: Where Sluice stands as of 2026-11-14 and what comes next; details live in docs/
metadata:
  type: project
---

As of 2026-11-14: routing engine, all tunnel types, share links/subscriptions, UDP through tunnels (Windows only), tunnel
lifecycle and notifications, the Avalonia app for Windows and Linux, Linux capture (nftables + NFQUEUE, works without root
via setcap), and an app-only Linux release package whose `install.sh` installs the dependencies. Verified against 79 real
public servers (Windows) and end to end for every protocol on Linux.

Plan of record, in order of reading: `docs/STATUS.md` → `docs/REQUIRED.md` → `docs/FIXES.md` → `docs/FEATURES.md`.

Next, as the owner last discussed:
1. Publishing the repository on GitHub with releases for Linux and Windows (the owner has plans for `scripts/` and
   `packaging/`; discuss before changing them).
2. High-priority bugs: stale fake addresses after a restart; WSL/VM DNS on Windows (docs/FIXES.md §1–2).
3. UDP through tunnels on Linux (TPROXY), moving Windows-only code out of Core, retiring the WPF app (docs/REQUIRED.md).

Earlier improvement ideas the owner asked for but hasn't chosen yet are in docs/FEATURES.md.

**How to apply:** start from docs/REQUIRED.md; keep docs/ and these notes updated when items are done. See
[[working-rules]].
