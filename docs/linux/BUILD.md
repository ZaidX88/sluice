# Linux: building, packaging and testing

For installing a release, see [INSTALL.md](INSTALL.md). Shared build and test notes: [../CONTRIBUTING.md](../CONTRIBUTING.md#building-and-testing).

## Build and run from source

Needs the .NET 10 SDK and the libraries the UI uses (X11, ICE, SM, fontconfig: present on normal desktops). The first
build downloads the Linux sing-box into `.deps/`.

```bash
dotnet build src/Sluice.Desktop -f net10.0
dotnet run --project src/Sluice.Desktop -f net10.0
dotnet run --project src/Sluice.Cli -- --help
```

These work as written on Linux: `Directory.Build.props` sets `EnableWindowsTargeting`, because restore also restores
the Windows target of `Sluice.Desktop` ([core FIXES §7](../core/FIXES.md#7-building-on-linux-needed-enablewindowstargeting--fixed)).
Build output under `out/` is ignored by git.

Capture needs root, or the capabilities the installer grants (see below). For a quick test: `sudo dotnet <path>/Sluice.Cli.dll <profile> --intercept`.

The Linux app also builds on Windows (`-f net10.0`). It can be checked on Windows 11 with WSL2 + WSLg: copy the build
output into WSL and run `dotnet Sluice.Desktop.dll`; it opens as a normal window.

## Release package

The packaging files (`scripts/package-linux.sh`, `packaging/linux/`) are on `main` and belong to the Linux area
(decision of 2026-11-19, #93). Release packages are built from a release tag (`docs/CONTRIBUTING.md` → Releases) and
are never committed.

`sluice-linux-x64.tar.gz` holds only the app (framework-dependent, ~13 MB); `install.sh` installs everything else.

```bash
dotnet publish src/Sluice.Desktop -f net10.0 -r linux-x64 --self-contained false -c Release -p:SluiceFetchDependencies=false -o out/app
dotnet publish src/Sluice.Cli             -r linux-x64 --self-contained false -c Release -p:SluiceFetchDependencies=false -o out/app
scripts/package-linux.sh out/app out/dist          # run on Linux or WSL (sets permissions, extracts the icon)
scripts/package-linux.sh --deb out/app out/dist    # the same, plus sluice_<version>_amd64.deb (needs dpkg-deb)
```

`--deb` (#67) also builds `sluice_<version>_amd64.deb` (and its `.sha256`) for Ubuntu 24.04 LTS and later, from the
same files, with `dpkg-deb --root-owner-group`. `<version>` is `<Version>` from `Directory.Build.props`; the package's
own version turns `-` into `~` (`1.0.0-rc.1` → `1.0.0~rc.1`, which apt sorts before `1.0.0`), the file name keeps it.
The package holds:
- `/opt/sluice`: the same files as the tar.gz's `app/` (launcher included), plus **sing-box** and its licence, the
  version and SHA-256 that `install.sh` pins. It comes from `$SINGBOX_TGZ` (the release archive) if set, else from
  the build's `.deps/` copy (checked by `build/Dependencies.targets`), else it is downloaded; archives are
  SHA-256-checked;
- `/usr/bin/sluice` and `/usr/bin/sluice-cli` (links to the launcher), the menu entry and the icon at the same paths as
  `install.sh` (`/usr/share/applications/sluice.desktop`, `/usr/share/icons/hicolor/256x256/apps/sluice.png`), and
  `/usr/share/doc/sluice/` (`copyright`, `THIRD-PARTY-NOTICES.md`).

`Depends:` Ubuntu's `dotnet-runtime-10.0`, `nftables`, `libcap2-bin`, `libx11-6`, `libice6`, `libsm6`,
`libfontconfig1`, `libnotify-bin`, and `libc6` at least the version the launcher was built against (from `objdump`).
The control file and maintainer scripts are in `packaging/linux/deb/`:
- `preinst`: removes a copy installed by `install.sh` (`/opt/sluice` with `uninstall.sh`, its `/usr/local/bin` links,
  menu entry and icon), so the two don't mix;
- `postinst`: if `/etc/dotnet` doesn't lead to a .NET 10 runtime, writes `/usr/lib/dotnet` into
  `install_location_x64` (keeping the old file in `/var/lib/sluice`); keeps `/etc/dotnet` root's; `setcap` on the
  launcher only. Ubuntu's `dotnet-host` already names `/usr/lib/dotnet` there, so usually nothing changes;
- `prerm` (remove): deletes the `inet sluice` nftables table and the launcher's capabilities;
- `postrm` (remove/purge): restores `install_location_x64` if `postinst` changed it and nobody changed it since.

Package layout and installer: `packaging/linux/` (`install.sh`, `uninstall.sh`, `sluice.desktop`, `README.txt`, and the
launcher's source in `launcher/`). `package-linux.sh` compiles the launcher with the system C compiler (`cc`, or `$CC`)
into `app/sluice`, after building and running its tests (`launcher/launcher-test.c`).
`install.sh`:
- installs missing packages with apt/dnf/yum/pacman/zypper: .NET 10 runtime, nftables, libcap, X11/ICE/SM, fontconfig,
  notify-send; falls back to Microsoft's `dotnet-install.sh` (into `/usr/share/dotnet`) where there's no .NET 10 package;
- uses only a .NET folder that root alone can change (it, the folders above it, everything in it, and links only to
  files inside it, as the launcher requires); a runtime anywhere
  else (for example a `~/.dotnet` found through `sudo`'s `PATH`) is skipped, and .NET is installed instead;
- makes `/etc/dotnet/install_location*` lead to that folder (writes `install_location_x64` when they don't), because
  the launcher doesn't pass `DOTNET_ROOT` on, and keeps `/etc/dotnet` and those files root's;
- downloads sing-box 1.14.4 (SHA-256 checked) or uses the archive placed next to it;
- installs to `/opt/sluice` (root-owned, nothing writable by group/others: the launcher refuses to run otherwise) and
  runs `setcap cap_net_admin,cap_setgid,cap_sys_ptrace,cap_dac_read_search+ep` on the launcher `/opt/sluice/sluice`
  only; `sluice`, `sluice-cli` and the menu entry start Sluice through it (see "The launcher" below);
- `--check` only reports what's missing.

### The launcher

`/opt/sluice/sluice` (`packaging/linux/launcher/sluice-launcher.c`) is the only file with capabilities. It starts
`Sluice.Desktop`, or `Sluice.Cli` when called `sluice-cli`, from its own folder, and passes the four capabilities on as
ambient capabilities; the .NET apps carry none. It passes on only a fixed list of environment variables (the session,
the user's folders, language; language and time-zone values that are paths are dropped), resets `PATH`, turns off
.NET's diagnostics (`DOTNET_EnableDiagnostics=0`), and refuses to run unless root alone can change its folder and the
.NET folder the app will load, each with the folders above it and everything in it, plus `/etc/dotnet` and its
`install_location` files. It finds the .NET folder as the app does: `install_location_x64`, then `install_location`,
otherwise `/usr/share/dotnet`; links inside it (distribution packages have them) must lead to root-only files inside
it. Sluice clears the passed-on
capabilities at start-up and on the starting thread before it runs any other program (`Libc.ClearPassedOn`; for
sing-box through `PrepareHelperStart`), and doesn't start the program if that fails; `nft` alone gets `CAP_NET_ADMIN`
(`NftRules`).

### Pitfalls already hit

- **Line endings.** Scripts and the nft ruleset must be LF. `.gitattributes` keeps `*.sh` LF; the ruleset is normalised in
  code. A CRLF ruleset broke capture on a real PC once.
- **Capabilities.** Files copied from a Windows drive arrive as 777; the package and installer reset permissions. File
  capabilities are not inherited by child processes, so Sluice passes `CAP_NET_ADMIN` to `nft` as an ambient capability,
  and keeps real and effective group equal (otherwise the kernel drops ambient capabilities on exec). The ambient
  capability must be lowered again right after `nft` starts: left raised, every later thread and program got it, and
  GTK's sandboxed image loader (`bwrap`) refused to run, which aborted the file dialog (#99).
- **Shared `/tmp`.** sing-box configs hold secrets: they go to `$XDG_RUNTIME_DIR/sluice` (or
  `~/.local/state/sluice/helpers`), owner-only 0700, never `/tmp`.

## Testing on Linux

- **Tests:** `dotnet test Sluice.Linux.slnf` runs the shared and the Linux tests.
  `Socket_owner_is_found_through_proc` runs only on Linux. One shared relay test fails on Linux for now (issue #3).
  New Linux tests go in `tests/Sluice.Tests.Linux` ([CONTRIBUTING.md → Tests](../CONTRIBUTING.md#tests)).
- **Capture live test** (root; WSL works):

  ```bash
  dotnet build src/Sluice.Cli -o out/cli
  dotnet run scripts/linux/make-test-profile.cs -- local/linux-test.sluice
  sudo scripts/linux/capture-livetest.sh out/cli local/linux-test.sluice [upstream-dns]
  ```

  Expected: curl goes through the test SOCKS5 server (including `example.com:80` by name), wget is blocked, python goes
  direct, `getent hosts` returns 198.18.x.x, and the nft table is gone after stop. The optional upstream DNS is given to
  Sluice alone through a private mount namespace (useful when the machine's resolver is unusable).
- **Protocol end-to-end without internet:** start sing-box *servers* for each protocol on 127.0.0.1 (the live tests in
  `tests/Sluice.Tests/SingBoxTests.cs` show the configs) and run `Sluice.Cli check "<share link>" --target 127.0.0.1:<web port>`
  against a local `python3 -m http.server`. This covers Hysteria2 and TUIC over UDP too.
- **Real tunnel:** the tests above use local servers only. To check real-world capture, use a tunnel of your own and keep
  its profile in `local/` (ignored by git); never commit server addresses or credentials. Checklist:
  [WORK.md](WORK.md).
- **Before handing out a package:** install it fresh, then start capture **as a normal user** and confirm routing decisions
  in the log, besides the protocol checks.
