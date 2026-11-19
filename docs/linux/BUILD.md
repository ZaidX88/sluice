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
```

Package layout and installer: `packaging/linux/` (`install.sh`, `uninstall.sh`, `sluice.desktop`, `README.txt`).
`install.sh`:
- installs missing packages with apt/dnf/yum/pacman/zypper: .NET 10 runtime, nftables, libcap, X11/ICE/SM, fontconfig,
  notify-send; falls back to Microsoft's `dotnet-install.sh` (into `/usr/share/dotnet`) where there's no .NET 10 package;
- writes `/etc/dotnet/install_location_x64` when .NET is somewhere non-default, because the setcap'd app ignores
  `DOTNET_ROOT`;
- downloads sing-box 1.14.4 (SHA-256 checked) or uses the archive placed next to it;
- installs to `/opt/sluice` (root-owned, nothing writable by group/others: the binaries get capabilities) and runs
  `setcap cap_net_admin,cap_setgid,cap_sys_ptrace,cap_dac_read_search+ep` on both binaries;
- `--check` only reports what's missing.

### Pitfalls already hit

- **Line endings.** Scripts and the nft ruleset must be LF. `.gitattributes` keeps `*.sh` LF; the ruleset is normalised in
  code. A CRLF ruleset broke capture on a real PC once.
- **Capabilities.** Files copied from a Windows drive arrive as 777; the package and installer reset permissions. File
  capabilities are not inherited by child processes, so Sluice passes `CAP_NET_ADMIN` to `nft` as an ambient capability,
  and keeps real and effective group equal (otherwise the kernel drops ambient capabilities on exec).
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
