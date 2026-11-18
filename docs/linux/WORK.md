# Linux work

What the **Linux area** needs, for whoever works on it on a real Linux machine (see
[../CONTRIBUTING.md](../CONTRIBUTING.md) for the workflow and [../../AGENTS.md](../../AGENTS.md) for the rules). Linux
work changes Linux files only: `src/Sluice.Platform.Linux/`, `scripts/linux/`, `docs/linux/`,
`tests/Sluice.Tests/Linux*Tests.cs`. Anything else needed goes in an issue for the owner.

## Where it stands

Linux capture works (nftables + NFQUEUE, fake DNS, process lookup through `/proc`, capture without root via `setcap`) and
every tunnel protocol was checked end to end, but almost all of that was on **WSL2**, not on a real installed
distribution. How it works: [../STATUS.md](../STATUS.md#how-capture-works) and
[../architecture/capture.md](../architecture/capture.md).

## 1. First: verify on a real machine

Report the results in a pull request that adds them to the "Results" section below.

1. Build and run the tests ([BUILD.md](BUILD.md)): `dotnet test tests/Sluice.Tests`. All should pass;
   `Socket_owner_is_found_through_proc` runs only on Linux.
2. Run the capture live test (needs root; it uses a local test SOCKS5 server, no internet tunnel):

   ```bash
   dotnet build src/Sluice.Cli -o out/cli
   dotnet run scripts/linux/make-test-profile.cs -- local/linux-test.sluice
   sudo scripts/linux/capture-livetest.sh out/cli local/linux-test.sluice
   ```

3. Run the desktop app (`dotnet run --project src/Sluice.Desktop -f net10.0`) with **your own real tunnel** (SSH server,
   SOCKS5, VLESS…; keep its profile in `local/`, never commit it). Turn capture on and check: browsing works through the
   tunnel, the Connections page lists apps with the right rule, a Direct rule really goes direct, a Block rule blocks,
   DNS names show in the log (not bare IPs), and stopping capture leaves no nft table (`sudo nft list tables`).
4. Note your distribution, version, desktop (X11 or Wayland), resolver (systemd-resolved or other) and anything that
   failed, with the log lines.

## 2. Then: the open Linux items

Take one at a time; each says what "done" means in the linked doc.

| Item | Where | Notes |
|---|---|---|
| IPv6 extension headers not parsed; such first packets pass uncaptured | FIXES §6 | `PacketInfo.TryParse` |
| DNS not captured without a non-loopback upstream (dnsmasq without systemd-resolved) | FIXES §6 | test on a distribution without systemd-resolved |
| `/proc/*/fd` scan cost on busy machines | FIXES §6 | `NETLINK_SOCK_DIAG` + inode cache |
| Process lookup on the packet thread | [FEATURES](../FEATURES.md) | worker + out-of-order verdicts |
| UDP through tunnels (TPROXY) | [REQUIRED §2](../REQUIRED.md#2-udp-through-tunnels-on-linux) | the design reuses `UdpNat`, which is in Core: **ask the owner** to split its WinDivert part out first; the nft/TPROXY/socket side is Linux work |
| Installer paths on Ubuntu, Fedora, Arch; ARM64; `.deb` / AppImage | [REQUIRED §5](../REQUIRED.md#5-linux-release-remaining) | packaging lives on the owner's `release/linux` branch; ask for it before starting |

**IPv6 through a tunnel without IPv6** (FIXES §12, issue #4), Linux part fixed on 2026-11-18: the relay accepts the app's
connection before the tunnel opens the destination, so an app whose IPv6 attempt "connected" never tried IPv4 and failed
over and over (seen with Claude desktop, the `claude` CLI and ChatGPT/Codex on a network with IPv6). Now one failed IPv6
connection through a tunnel marks it for 10 minutes (`Capture/Ipv6Fallback.cs`); new IPv6 connections meant for it are
refused at the SYN (nft chain `refuse`, TCP reset), and apps use IPv4 at once. To test without an IPv6 network, add a
dummy interface with a `2001:db8::` address and a route for the site's IPv6 prefix, then remove it
(`sudo ip link del <name>`).

UI problems found on Linux (layout, fonts, tray, notifications shown by the app) belong to the UI area: report them in an
issue with a screenshot instead of changing `src/Sluice.Desktop/`.

## Results

Add one entry per machine tested.

| Date | Distribution / desktop | Tests | Capture live test | Real tunnel | Notes |
|---|---|---|---|---|---|
| 2026-11-18 | Ubuntu 26.04.1 LTS, kernel 7.0, GNOME on Wayland, systemd-resolved, nftables 1.1.6, .NET SDK 10.0.115 | 263 / 276 pass, all Linux tests pass; 13 failures in shared tests (5 need DPAPI, 5 use `C:\` application paths, 1 starts `powershell.exe`, `EngineTests.ApplicationRule_MatchesThisTestProcess` finds PID 0, `RelayTests.SlowApp_DoesNotBlockUpstream_OnlyWithElasticBuffer` with the 8 MB buffer fails every run, also alone) | ✅ all checks: curl through SOCKS by IP and by name, wget blocked, python direct, fake IP 198.18.x, forwarded DNS reply from 8.8.8.8, 20 parallel curls in 0.41 s, table gone after stop; no errors in the log. Background `ssh` (by name, via fake IP) and IPv6 connections were captured and handled correctly too | ✅ SSH tunnel (sing-box), desktop app installed with `setcap` in a root-owned folder, run as a normal user: tunnel rule (public address differs from direct), Direct and Block rules all correct; with "Resolve hostnames through the tunnel" on, names get 198.18.x addresses and the log shows host names; stopping capture leaves no nft table. **Bug:** changing a DNS setting while capture runs has no effect until capture restarts (`LinuxCapture.CurrentPolicy` caches by profile reference; the Settings page edits the profile in place). Fixed on 2026-11-18: the policy now follows setting values, changes apply within a second without restarting capture, and systemd-resolved's cache is flushed. IPv6 destinations fail through an SSH server without IPv6 ("ssh: rejected: connect failed"), no fallback to IPv4. A rule `claude` does not match `claude-desktop` | Building needs `-p:EnableWindowsTargeting=true` (restore also restores the Windows target). The live-test script was not executable in git (fixed). The desktop app writes no log file unless "file log" is turned on. One capture session counted 1,841 redirected connections with only ~130 logged; unexplained |
