# Required work

Items that must be done (not optional ideas), roughly in order. Known bugs are in each area's `FIXES.md` ([README.md](README.md)); the
**high** ones also belong before a public release.

## 1. Move Windows-only code out of Core

The agreed structure is **one branch, separate modules**: shared code in `Sluice.Core`, OS code in
`Sluice.Platform.Windows` / `Sluice.Platform.Linux`, and every new OS difference behind `IPlatformServices` (or a narrower
interface it hands out). Still in Core:

| File | Move to / replace with |
|---|---|
| `Interception/WinDivertNative.cs`, `Interceptor.cs`, WinDivert I/O in `UdpNat.cs` | `Sluice.Platform.Windows`. `PacketReflector`, `DnsRedirector`, `TcpPacket`, `UdpPacket` are pure packet logic and can stay. |
| `Platform/ProcessLookup.cs` (iphlpapi/kernel32 code) | `WindowsProcessLookup : IProcessLookup`; Core keeps the interface and `Backend`. |
| `Profiles/Dpapi.cs` (used by `ProxifierPasswordCodec`) | an `ISecretProtector` in Core. Linux: a clear "not supported" for "account"-mode (DPAPI) passwords. |
| Job object in `Tunnels/SingBoxSession.cs` | `IPlatformServices.TieChildProcess(Process)`. Windows: Job object. Linux: done (#57): SIGTERM to the helpers on exit, and the next start ends those of a Sluice killed with SIGKILL. |
| `Interceptor.FlushOsDnsCache` (dnsapi) | platform method; Linux uses `resolvectl flush-caches`. |
| WinDivert entries in `build/Dependencies.targets` | import from the Windows module instead of Core. |

Then make `Sluice.Cli` multi-target like the desktop app and take its capture from the platform module instead of the
`OperatingSystem.IsLinux()` branch in `Program.cs`.

The interfaces every part uses (`PlatformServices`, the new `IPlatformServices` members, `ISecretProtector`,
`UdpFlows` / `IDatagramSender`) are fixed in [architecture/platforms.md §6](architecture/platforms.md#6-interfaces-for-the-core-split-decided-in-52).

**Done when:** Core has no `DllImport` of Windows libraries, both targets build, all tests pass.

## 2. UDP through tunnels on Linux

**Done** (#60, on Core's `UdpFlows` from #59): a UDP flow Sluice takes (through a tunnel, or Direct to a placeholder
address) gets a mark with its NFQUEUE verdict, kept in conntrack for its later packets; a policy routing rule sends
marked packets back in through `lo`, where nftables' `tproxy` hands them to a transparent socket that reads the original
destination and passes them to `UdpFlows` ([architecture/capture.md](architecture/capture.md#linux-linuxcapture)).

## 3. Retire the WPF app

**Done** (#48): the Avalonia app was checked against the WPF app by hand on Windows (#61; its gaps are issues of
their own), and `src/Sluice.App` was deleted (#63).

## 4. Windows release package

There is no Windows package yet (Linux has one: [linux/INSTALL.md](linux/INSTALL.md)). Needed (DECISIONS.md, 2026-11-22):
a self-contained build of `Sluice.Desktop` + `Sluice.Cli` with WinDivert and sing-box, an Inno Setup installer that adds
the Start Menu shortcut and the firewall rule (#64; a WiX MSI after 1.0, #186), and a guide in `docs/windows/`. Code signing avoids SmartScreen
warnings and antivirus complaints about the WinDivert driver.

## 5. Linux release, remaining

- ~~**ARM64 package**~~ **not planned for now** (2026-11-19): there is no plan to release for ARM64 for the time being;
  v1.0.0 ships for x86-64 only. If it comes back: `-r linux-arm64`, install.sh downloads sing-box linux-arm64, and
  check every architecture-specific constant in `Native/Libc.cs` (e.g. `O_DIRECTORY` differs on arm64). Issue #66 was
  closed as not planned.
- **`.deb` for Ubuntu** with the same layout (post-install = `install.sh` steps). Done (#67):
  `scripts/package-linux.sh --deb` builds `sluice_<version>_amd64.deb` for Ubuntu 24.04 LTS and later, with sing-box
  bundled; Debian and older Ubuntu use the tar.gz. **AppImage:** not for 1.0 (#144, after 1.0). Not Flatpak/Snap: a
  sandbox can't use nftables.
- **"Grant capture rights" button** (pkexec + setcap) for people who run the app without the installer. Done: the
  banner button (#70) and the Linux part (#69: `setcap` on the launcher next to the running copy, only when root alone
  can change that folder and every folder above it).
- **Autostart entry** (`~/.config/autostart`) as a setting. Done: the switch in Settings (#70) and the Linux part
  (#69: an XDG autostart entry that starts the launcher).
- The installer's online paths (package-manager install, Microsoft's .NET installer, the GitHub download) have not been
  run on a machine with internet yet; test them on Ubuntu, Fedora and Arch.

## 6. Continuous integration

GitHub Actions: build both desktop targets; run the tests on Windows and Linux (Linux also runs
`Socket_owner_is_found_through_proc`, skipped on Windows); include the live sing-box tests (the build fetches the pinned
sing-box). Optionally run `scripts/linux/capture-livetest.sh` in a privileged Linux job. Publish release packages from tags.
