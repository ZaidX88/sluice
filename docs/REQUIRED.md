# Required work

Items that must be done (not optional ideas), roughly in order. Known bugs are in each area's `FIXES.md` ([README.md](README.md)); the
**high** one (WSL/VM DNS, [windows/FIXES.md §1](windows/FIXES.md#1-windows-wsl--vms-get-fake-dns-answers-they-cant-use--high)) also belongs before a public release.

## 1. Move Windows-only code out of Core

The agreed structure is **one branch, separate modules**: shared code in `Sluice.Core`, OS code in
`Sluice.Platform.Windows` / `Sluice.Platform.Linux`, and every new OS difference behind `IPlatformServices` (or a narrower
interface it hands out). Still in Core:

| File | Move to / replace with |
|---|---|
| `Interception/WinDivertNative.cs`, `Interceptor.cs`, WinDivert I/O in `UdpNat.cs` | `Sluice.Platform.Windows`. `PacketReflector`, `DnsRedirector`, `TcpPacket`, `UdpPacket` are pure packet logic and can stay. |
| `Platform/ProcessLookup.cs` (iphlpapi/kernel32 code) | `WindowsProcessLookup : IProcessLookup`; Core keeps the interface and `Backend`. |
| `Profiles/Dpapi.cs` (used by `ProxifierPasswordCodec`) | an `ISecretProtector` in Core. Linux: a clear "not supported" for DPAPI blobs from `.ppx`. |
| Job object in `Tunnels/SingBoxSession.cs` | `IPlatformServices.TieChildProcess(Process)`. Windows: Job object. Linux: watchdog that kills helpers on exit. |
| `Interceptor.FlushOsDnsCache` (dnsapi) | platform method; Linux uses `resolvectl flush-caches`. |
| WinDivert entries in `build/Dependencies.targets` | import from the Windows module instead of Core. |

Then make `Sluice.Cli` multi-target like the desktop app and take its capture from the platform module instead of the
`OperatingSystem.IsLinux()` branch in `Program.cs`.

**Done when:** Core has no `DllImport` of Windows libraries, both targets build, all tests pass.

## 2. UDP through tunnels on Linux

Today a UDP flow whose rule says "tunnel" is dropped (browsers fall back from QUIC to TCP). Design that fits the current
capture (TPROXY):

- In `queue_new`, mark UDP flows to tunnel with a second mark (e.g. `0x5C1CF`).
- Chain `mangle/prerouting`: `meta mark 0x5C1CF tproxy to :UDPPORT`; locally generated packets need rerouting through
  `lo`: `ip rule add fwmark 0x5C1CF lookup 100` + `ip route add local default dev lo table 100` (and IPv6), with a
  `type route hook output` chain setting the mark.
- A UDP socket with `IP_TRANSPARENT` + `IP_RECVORIGDSTADDR` (and IPv6 equivalents) receives the original destination;
  replies go out from a transparent socket bound to that address.
- Reuse `UdpNat`'s flow logic (per-flow association via `RoutingEngine.OpenUdpAsync`) by splitting off its WinDivert I/O.
- Remove the `ip rule` / route on stop, like the nft table. This also fixes DNS capture when only a loopback resolver exists.

**Done when:** `UdpMode.Tunnel` carries `curl --http3` and a DNS query through a SOCKS5 tunnel in the Linux live test.

## 3. Retire the WPF app

Check by hand on Windows that the Avalonia app does everything `src/Sluice.App` did:
- tray (show, hide, exit, capture toggle); single instance (a second launch shows the window);
- "Restart as administrator" with `--intercept`; firewall banner and Allow button;
- tunnel notifications (title "Sluice" + icon);
- open / save / recent profile, master password; import links and subscriptions; export to `.ppx`;
- closing the window goes to the tray with the hint toast;
- `--profile`, `--log-file`, `--theme`, `--screenshot`.

Then remove `Sluice.App` from `Sluice.slnx` and delete it, and move the Start Menu shortcut / AUMID code fully into the
Windows module.

## 4. Windows release package

There is no Windows package yet (Linux has one: [linux/INSTALL.md](linux/INSTALL.md)). Needed: a self-contained or
framework-dependent build of `Sluice.Desktop` + `Sluice.Cli` with WinDivert and sing-box, an installer (MSIX or Inno Setup)
that adds the Start Menu shortcut and the firewall rule, and a guide in `docs/windows/`. Code signing avoids SmartScreen
warnings and antivirus complaints about the WinDivert driver.

## 5. Linux release, remaining

- **ARM64 package** (`-r linux-arm64`; install.sh downloads sing-box linux-arm64).
- **`.deb` / AppImage** with the same layout (post-install = `install.sh` steps). Not Flatpak/Snap: a sandbox can't use
  nftables.
- **"Grant capture rights" button** (pkexec + setcap) for people who run the app without the installer.
- **Autostart entry** (`~/.config/autostart`) as a setting.
- The installer's online paths (package-manager install, Microsoft's .NET installer, the GitHub download) have not been
  run on a machine with internet yet; test them on Ubuntu, Fedora and Arch.

## 6. Continuous integration

GitHub Actions: build both desktop targets; run the tests on Windows and Linux (Linux also runs
`Socket_owner_is_found_through_proc`, skipped on Windows); include the live sing-box tests (the build fetches the pinned
sing-box). Optionally run `scripts/linux/capture-livetest.sh` in a privileged Linux job. Publish release packages from tags.
