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
1. **Windows capture speed (2026-11-17):** docs/FIXES.md §8. Fixed (`perf/capture-throughput`, merged): the
   main cause was the health check restarting a busy tunnel (§9). Chromium and curl now match the SOCKS listener
   through capture on the same server. Pending: the owner's own browser speed test on one hand-picked server in both
   modes. Secure DNS handling (§10) and the WFP driver are the owner's decisions.
2. Publishing the repository on GitHub with releases for Linux and Windows (the owner has plans for `scripts/` and
   `packaging/`; discuss before changing them).
3. Bug fixing, from docs/FIXES.md (the owner's choice, 2026-11-14). Done: stale fake addresses after a restart. Next
   high one: WSL/VM DNS on Windows (§1).
4. UDP through tunnels on Linux (TPROXY), moving Windows-only code out of Core, retiring the WPF app (docs/REQUIRED.md).

Earlier improvement ideas the owner asked for but hasn't chosen yet are in docs/FEATURES.md.

**How to apply:** start from docs/REQUIRED.md; keep docs/ and these notes updated when items are done. See
[[working-rules]].
