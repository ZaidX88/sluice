# Required work

Items that must be done (not optional ideas), roughly in order. Known bugs are in each area's `FIXES.md` ([README.md](README.md)); the
**high** ones also belong before a public release.

## 1. Move Windows-only code out of Core

The agreed structure is **one branch, separate modules**: shared code in `Sluice.Core`, OS code in
`Sluice.Platform.Windows` / `Sluice.Platform.Linux`, and every new OS difference behind `IPlatformServices` (or a narrower
interface it hands out). The WinDivert capture (#53), the job object and the DNS-cache flush (#56) have moved.
Still in Core:

| File | Move to / replace with |
|---|---|
| `Platform/ProcessLookup.cs` (iphlpapi/kernel32 code) | `WindowsProcessLookup : IProcessLookup`; Core keeps the interface and `Backend` (#54). |
| `Profiles/Dpapi.cs` (used by `ProxifierPasswordCodec`) | an `ISecretProtector` in Core. Linux: a clear "not supported" for "account"-mode (DPAPI) passwords (#55). |
| WinDivert entries in `build/Dependencies.targets` | import from the Windows module instead of Core (#58). |

`Sluice.Cli` is multi-targeted like the desktop app and takes its capture from the platform module (done in #53).

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

**Done** (#64, DECISIONS.md 2026-11-22): `scripts/package-windows.ps1` publishes `Sluice.Desktop` + `Sluice.Cli`
self-contained with WinDivert and sing-box, and `packaging/windows/Sluice.iss` builds the Inno Setup installer
`Sluice-Setup-<version>-x64.exe`, which adds the Start Menu shortcut and the firewall rules. Guide:
[windows/INSTALL.md](windows/INSTALL.md). Remaining:

- **Code signing** (#65, SignPath Foundation, for 1.0.1): v1.0.0 ships unsigned, so SmartScreen warns on first run
  ([windows/INSTALL.md → Install](windows/INSTALL.md#3-install)); signing also reduces antivirus complaints about the
  WinDivert driver.
- **A WiX MSI** after 1.0 (#186).

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
- The installer's online paths (package-manager install, Microsoft's .NET installer, the GitHub download) on Ubuntu,
  Fedora and Arch. Done (#71).

## 6. Continuous integration

GitHub Actions (#51): build both desktop targets; run the tests on Windows and Linux (Linux also runs
`Socket_owner_is_found_through_proc`, skipped on Windows); include the live sing-box tests (the build fetches the pinned
sing-box) (#72, after #41 and #33). Optionally run `scripts/linux/capture-livetest.sh` in a privileged Linux job (#73).

- **Release packages from tags:** done (#74). `.github/workflows/release.yml` builds the packages from a tag `vX.Y.Z`
  and drafts the GitHub Release ([CONTRIBUTING.md → Releases](CONTRIBUTING.md#releases)).
