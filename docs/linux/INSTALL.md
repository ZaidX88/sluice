# Installing Sluice on Linux

Sluice sends each app's connections where you choose: **directly**, **blocked**, or **through a tunnel**
(SSH, SOCKS, HTTP(S), VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC).

The package holds only Sluice itself (about 13 MB). The installer adds whatever your system is missing.

| File | What it is |
|---|---|
| `sluice-linux-x64.tar.gz` | the release package |
| `sluice-linux-x64.tar.gz.sha256` | its checksum, to check the download |

The short version (install, add a tunnel, add rules, start) is [../QUICKSTART.md](../QUICKSTART.md); building the
package yourself is in [BUILD.md](BUILD.md).

---

## 1. Requirements

- **A 64-bit Intel/AMD PC** (x86-64). ARM computers (Raspberry Pi, ARM laptops) are not supported by this package, and
  there is no plan to release an ARM64 build for now.
- **A desktop with X11 or Wayland.** On Wayland, Sluice runs through XWayland, which is installed by default on
  GNOME, KDE and most other desktops.
- **A distribution with apt, dnf, pacman or zypper** (Debian, Ubuntu, Mint, Pop!_OS, Fedora, RHEL-likes, Arch,
  Manjaro, openSUSE). On others, install the dependencies listed in step 3 yourself.
- **Internet access while installing**, for the packages and for sing-box. See "Offline or GitHub blocked" below.

## 2. Check the download (optional)

In the folder holding both files, run:

```bash
sha256sum -c sluice-linux-x64.tar.gz.sha256
```

It should print `sluice-linux-x64.tar.gz: OK`.

## 3. Install

```bash
tar -xzf sluice-linux-x64.tar.gz
cd sluice-linux-x64
sudo ./install.sh
```

The installer:

1. **installs missing dependencies** with your package manager, skipping anything already there:

   | Needed for | Debian/Ubuntu | Fedora | Arch | openSUSE |
   |---|---|---|---|---|
   | running Sluice | `dotnet-runtime-10.0` | `dotnet-runtime-10.0` | `dotnet-runtime` | *(Microsoft's installer)* |
   | capturing traffic | `nftables` | `nftables` | `nftables` | `nftables` |
   | capture without root | `libcap2-bin` | `libcap` | `libcap` | `libcap-progs` |
   | the window | `libx11-6 libice6 libsm6 libfontconfig1` | `libX11 libICE libSM fontconfig` | `libx11 libice libsm fontconfig` | `libX11-6 libICE6 libSM6 fontconfig` |
   | notifications | `libnotify-bin` | `libnotify` | `libnotify` | `libnotify-tools` |

   If your distribution has no .NET 10 package, it uses Microsoft's official installer (`dot.net/v1/dotnet-install.sh`)
   and puts .NET in `/usr/share/dotnet`.
2. **downloads sing-box 1.14.4** from GitHub, the helper for VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC and fast
   SSH, and checks its SHA-256 against the official release;
3. copies Sluice to **`/opt/sluice`** (replacing an older copy) and adds the commands **`sluice`** and **`sluice-cli`**;
4. adds **Sluice** to your applications menu, with its icon;
5. grants Sluice's launcher (`/opt/sluice/sluice`, which the `sluice` command and the menu entry start) the rights to
   capture traffic (`setcap`), so **you don't need to run it as root**.

It ends with `Done.`, after checking that Sluice starts. Any problem is printed as a `WARNING:` line saying what to do.

To see what's missing **without changing anything**:

```bash
./install.sh --check
```

### Offline or GitHub blocked

If sing-box can't be downloaded, the installer still installs Sluice. SSH, SOCKS and HTTP tunnels work; the other
protocols need sing-box. Get `sing-box-1.14.4-linux-amd64.tar.gz` from
<https://github.com/SagerNet/sing-box/releases/tag/v1.14.4> another way, put it **next to `install.sh`**, and run
`sudo ./install.sh` again. The installer checks the file's SHA-256, so a modified copy is refused.

After installing, you can delete the unpacked `sluice-linux-x64` folder and the `.tar.gz`.

## 4. Start Sluice

Open **Sluice** from the applications menu, or run:

```bash
sluice
```

Then:

1. **Tunnels**: add your server with **Add tunnel**, or paste share links / a subscription URL with **Import**.
   Select it and press **Check** to test it.
2. **Rules**: choose which apps or websites use which tunnel. Anything without a rule goes **Direct**.
   On Linux, rules by application use the program name, for example `firefox` or `curl` (no `.exe`).
3. Press the **play button** at the bottom to start capturing. Press it again to stop.

Apps can also use Sluice **without capture**. Point them at the SOCKS5 server shown at the bottom right
(`127.0.0.1:<port>`), for example in the browser's proxy settings.

Your settings and profiles are stored in `~/.config/Sluice`.

## 5. Command line (servers, no desktop)

`sluice-cli` runs the same engine without a window:

```bash
sluice-cli --help
sluice-cli myprofile.sluice --intercept            # capture all traffic using the profile's rules
sluice-cli myprofile.sluice --socks 127.0.0.1:1080 # only a SOCKS5 server, no capture
sluice-cli check "vless://…"                       # test a tunnel or share link
sluice-cli import myprofile.sluice --subscription https://example.com/sub
```

The built-in help still mentions WinDivert and Administrator for `--intercept`. On Linux, capture uses nftables
and works for any user once `install.sh` has run.

## 6. Uninstall

```bash
sudo /opt/sluice/uninstall.sh
```

This removes the app, sing-box, the commands and the menu entry. Your profiles in `~/.config/Sluice` are kept; delete
that folder too if you want them gone. Packages the installer added (.NET, nftables…) stay installed, since other
programs may use them.

---

## Troubleshooting

**"Capturing traffic needs root, or these capabilities…"**
The capture rights are missing, usually because Sluice was started from the unpacked folder or as
`/opt/sluice/Sluice.Desktop` instead of through the `sluice` command or the menu entry, or `setcap` wasn't available
during install. Install `libcap2-bin` (Debian/Ubuntu) or `libcap`, then
run `sudo ./install.sh` again. As a one-off alternative, run `sudo sluice`; notifications may not appear then.

**"nftables is not installed"**
Run `sudo ./install.sh` again with internet access; it installs nftables. Or install the `nftables` package yourself.

**The internet or DNS stops working after Sluice crashed while capturing**
Sluice normally removes its rules when it stops. If it crashed, remove them by hand:

```bash
sudo nft delete table inet sluice
```

**No tray icon**
Sluice's tray icon needs a desktop with a system tray. On KDE, XFCE and Cinnamon it just works. On GNOME, install the
*AppIndicator and KStatusNotifierItem Support* extension. Without a tray, closing the window keeps Sluice running in the
background; start `sluice` again to bring the window back.

**A website loads slowly or a call/game doesn't connect while its app goes through a tunnel**
UDP through tunnels isn't available on Linux yet, so UDP from apps whose rule says "tunnel" is blocked. Browsers then
fall back from QUIC (HTTP/3) to normal HTTPS automatically. Voice and video calls and games that need UDP should get a
**Direct** rule.

**The window doesn't open on a Wayland desktop**
Make sure XWayland is installed (`xwayland` / `xorg-x11-server-Xwayland`), then run `sluice` from a terminal to see
any error.

**Running Sluice without installing it**
With the .NET 10 runtime installed, `./app/Sluice.Desktop` (the app) and `./app/Sluice.Cli` (the command line) run
straight from the unpacked folder. Capture then needs `sudo`, because only `install.sh` grants the rights, and
encrypted tunnels need a `sing-box` binary next to them.

**"You must install .NET to run this application"**
The .NET 10 runtime is missing or not found. Run `sudo ./install.sh` again; it installs .NET and registers where it
is. If you installed .NET yourself in an unusual folder, write that folder into `/etc/dotnet/install_location_x64`.

---

## What's in the package

```
sluice-linux-x64/
  install.sh, uninstall.sh   installer (adds the dependencies and sing-box) / uninstaller
  README.txt                 short version of this guide
  sluice.desktop, sluice.png menu entry and icon
  app/
    Sluice.Desktop           the app
    Sluice.Cli               the command line
    *.dll, *.so              Sluice's own libraries (uses the system's .NET 10 runtime)
```
