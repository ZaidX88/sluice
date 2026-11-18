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
| DNS settings changed during capture are ignored until restart (`LinuxCapture.CurrentPolicy` caches by profile reference) | [FIXES §11](../FIXES.md#11-dns-settings-changed-while-capture-runs-are-ignored-until-capture-restarts--medium) | Linux copy of issue #5; Linux work |
| Relay listens on `[::]`; bind 127.0.0.1 and ::1 instead | [FIXES §6](../FIXES.md#6-linux-capture-limitations) | small, good first task |
| IPv6 extension headers not parsed; such first packets pass uncaptured | FIXES §6 | `PacketInfo.TryParse` |
| DNS not captured without a non-loopback upstream (dnsmasq without systemd-resolved) | FIXES §6 | test on a distribution without systemd-resolved |
| `/proc/*/fd` scan cost on busy machines | FIXES §6 | `NETLINK_SOCK_DIAG` + inode cache |
| Process lookup on the packet thread | [FEATURES](../FEATURES.md) | worker + out-of-order verdicts |
| UDP through tunnels (TPROXY) | [REQUIRED §2](../REQUIRED.md#2-udp-through-tunnels-on-linux) | the design reuses `UdpNat`, which is in Core: **ask the owner** to split its WinDivert part out first; the nft/TPROXY/socket side is Linux work |
| Installer paths on Ubuntu, Fedora, Arch; ARM64; `.deb` / AppImage | [REQUIRED §5](../REQUIRED.md#5-linux-release-remaining) | packaging lives on the owner's `release/linux` branch; ask for it before starting |

UI problems found on Linux (layout, fonts, tray, notifications shown by the app) belong to the UI area: report them in an
issue with a screenshot instead of changing `src/Sluice.Desktop/`.

## Results

Add one entry per machine tested.

| Date | Distribution / desktop | Tests | Capture live test | Real tunnel | Notes |
|---|---|---|---|---|---|
| | | | | | |
