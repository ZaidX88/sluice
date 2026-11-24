# Installing Sluice on Linux

Sluice sends each app's connections where you choose: **directly**, **blocked**, or **through a tunnel**
(SSH, SOCKS, HTTP(S), VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC).

There are two packages:

| File | For |
|---|---|
| `sluice_<version>_amd64.deb` (+ `.sha256`) | **Ubuntu 24.04 LTS or later**: see [the .deb](#ubuntu-2404-or-later-the-deb) below |
| `sluice-linux-x64.tar.gz` (+ `.sha256`) | every other distribution, **Debian** and **Ubuntu 22.04 or older** included: the rest of this guide |

The tar.gz holds only Sluice itself (about 13 MB); its installer adds whatever your system is missing.

The short version (install, add a tunnel, add rules, start) is [../QUICKSTART.md](../QUICKSTART.md); building the
package yourself is in [BUILD.md](BUILD.md).

---

## Ubuntu 24.04 or later: the .deb

```bash
sudo apt install ./sluice_<version>_amd64.deb
```

Or open the file in **App Center**. apt installs what Sluice needs with it from Ubuntu's own archive: the .NET 10
runtime (`dotnet-runtime-10.0`), nftables, libcap, the X11 / font libraries and notify-send. sing-box 1.14.4 is in the
package, so nothing is downloaded from GitHub. The package puts Sluice in `/opt/sluice`, adds the `sluice` and
`sluice-cli` commands and the menu entry, and gives the launcher `/opt/sluice/sluice` the capture rights, as
`install.sh` does. A copy installed earlier with `install.sh` is replaced. Then start Sluice as in
[4. Start Sluice](#4-start-sluice).

Use `apt install ./…`, with the `./`, not `dpkg -i`: dpkg doesn't install the .NET runtime, and leaves Sluice
unconfigured, without capture rights, until `sudo apt -f install`.

**Lowest release: Ubuntu 24.04 LTS**, the first whose archive has `dotnet-runtime-10.0`. On Ubuntu 22.04 apt refuses
with "Depends: dotnet-runtime-10.0 but it is not installable". There, and on **Debian** (which has no
`dotnet-runtime-10.0` in its archive), use the tar.gz with `install.sh` below.

Update by installing a newer `.deb` the same way. Uninstall with:

```bash
sudo apt remove sluice     # or: sudo apt purge sluice
```

A running Sluice is closed first, so it takes its capture rules down itself. This removes the app, sing-box, the
commands, the menu entry and the capture rights. Your profiles in
`~/.config/Sluice` are kept. The .NET runtime and the other packages apt added stay until `sudo apt autoremove`.

The rest of this guide is the tar.gz.

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

It ends with `Done.`, after checking that the command-line version starts and that the desktop app's libraries are
there. Any problem is printed as a `WARNING:` line saying what to do.

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

**"sluice: refusing to run: only root may be able to change: …"**
Sluice holds the capture rights only if nothing it loads can be changed by other users: its own folder
(`/opt/sluice`) and the .NET folder (named in `/etc/dotnet/install_location*`, otherwise `/usr/share/dotnet`), with
every folder above them. The message names what failed. Run `sudo ./install.sh` again: it installs .NET into a
root-only folder if needed.

**"Capturing traffic needs root, or these capabilities…"**
The capture rights are missing, usually because Sluice was started from the unpacked folder or as
`/opt/sluice/Sluice.Desktop` instead of through the `sluice` command or the menu entry, or `setcap` wasn't available
during install. Install `libcap2-bin` (Debian/Ubuntu) or `libcap`, then
run `sudo ./install.sh` again. As a one-off alternative, run `sudo sluice`; notifications may not appear then.

**Arch: "pacman couldn't install the packages Sluice needs"**
pacman can only install against an up-to-date system: with no synced package database, or one newer than the installed
packages, it reports "target not found" or conflicting files. The installer stops there without changing anything,
rather than upgrading your whole system. Run `sudo pacman -Syu`, then `sudo ./install.sh` again.

**"the desktop app won't start until these libraries are installed: …"**
The package manager couldn't install the X11 and font libraries the window needs (see the `WARNING:` lines above it);
`sluice-cli` still works. Install the packages for the window from the table in [3. Install](#3-install), or fix the
package manager and run `sudo ./install.sh` again.

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
UDP goes through tunnels that can carry it: SOCKS5 and the sing-box protocols (Shadowsocks, VMess, VLESS, Trojan,
Hysteria2, TUIC). SSH and HTTP tunnels can't, so UDP from apps sent through them is blocked; browsers then fall back
from QUIC (HTTP/3) to normal HTTPS automatically. Voice and video calls and games that need UDP should go through a
tunnel that carries it, or get a **Direct** rule. If the log says "UDP can't be taken through tunnels", the kernel lacks
tproxy support (`nft_tproxy`), and UDP of tunneled apps is blocked as well.

**The window doesn't open on a Wayland desktop**
Make sure XWayland is installed (`xwayland` / `xorg-x11-server-Xwayland`), then run `sluice` from a terminal to see
any error.

**In a Docker container: "sluice-cli: Operation not permitted"**
The launcher has capabilities that Docker's default set doesn't include, and the kernel won't start it without them,
even for `--help`. Start the container with
`--cap-add SYS_PTRACE --cap-add DAC_READ_SEARCH --cap-add NET_ADMIN`; all three are needed for Sluice to start, and
`NET_ADMIN` also lets it capture.

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
