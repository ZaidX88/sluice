---
name: environment
description: Development-environment pitfalls that cost time on Sluice and the workarounds that worked
metadata:
  type: project
---

The original development machine (Windows 11 + WSL2 Ubuntu 24.04 with WSLg) has a network where **direct traffic is
blocked and everything goes through the owner's own running Sluice capture** (an SSH tunnel, ~200–400 KB/s, no UDP).
Consequences worth knowing on any similar setup:

- **A running Sluice capture affects your own builds and tests:** fake DNS and routing apply to test helpers too.
  `HappyEyeballsTests.DeadFirstAddress…` fails while a capture runs (environmental). Don't ask the owner to pause capture
  for long; work around it.
- **NuGet restores of large packages stall** through a slow tunnel (parallel downloads time out and restart). Download the
  `.nupkg` with a resumable `curl -C -` loop into a folder and pass `-p:RestoreAdditionalProjectSources=<folder>`, or use
  `--disable-parallel`.
- **No outbound UDP**: real Hysteria2/TUIC servers can't be tested there; use local sing-box servers (see docs/linux/BUILD.md).
- **WSL's own traffic goes out directly**, so WSL has no internet on that network; only loopback/local tests work there.
  While Windows capture is on, WSL also gets fake DNS answers (docs/FIXES.md §2). Give a test process its own resolver
  with `unshare -m` + a bind-mounted `resolv.conf` (see `scripts/linux/capture-livetest.sh`).
- **The owner's running Sluice locks its own build output folder**: build to a separate output folder (`-o …`).
- **CRLF:** git on Windows checks `.cs` files out with CRLF; C# raw strings keep those line endings. Anything sent to Linux
  tools must be normalised to LF (done for the nft ruleset); `*.sh` are LF via `.gitattributes`.
- **Shell quoting:** writing C#/Python through shell heredocs turned `\n` and `\b` into real characters several times.
  Write scripts to files, or use an editor tool for exact text.
- **Calling WSL from Git Bash:** `MSYS_NO_PATHCONV=1 wsl.exe -d Ubuntu-24.04 -u root -- bash /mnt/c/.../script.sh` and pipe
  through `tr -d '\0'`.

**Why:** each of these once looked like a Sluice bug and wasn't.
**How to apply:** before blaming the code for a network or build failure, check whether a capture is running.
