Sluice for Linux (x86-64)
=========================

Sluice sends each app's connections where you choose: directly, blocked, or through a
tunnel (SSH, SOCKS, HTTP(S), VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC).

Install
-------
    tar -xzf sluice-linux-x64.tar.gz
    cd sluice-linux-x64
    sudo ./install.sh

install.sh needs an internet connection the first time. It:
  - installs what is missing with your package manager (apt, dnf, pacman or zypper):
    the .NET 10 runtime, nftables, libcap, X11 / font libraries, notify-send
    (.NET comes from Microsoft's official installer if your distribution has no package)
  - downloads sing-box 1.14.4 from GitHub (encrypted tunnels) and checks its SHA-256
  - copies Sluice to /opt/sluice and adds it to the applications menu
  - gives Sluice the rights to capture traffic, so you do not need to run it as root

See what is missing without changing anything:   ./install.sh --check

No access to GitHub? Download sing-box-1.14.4-linux-amd64.tar.gz from
https://github.com/SagerNet/sing-box/releases/tag/v1.14.4 another way, put it next to
install.sh, and run install.sh again. Without sing-box, SSH, SOCKS and HTTP tunnels still work.

Then start "Sluice" from the applications menu, or run:  sluice

Using it
--------
1. Tunnels: add your server (or Import a share link / subscription), then Check it.
2. Rules: choose which apps or sites go through which tunnel; the rest go Direct.
   App names have no .exe on Linux: firefox, chrome, curl, or a full path.
3. Press the play button at the bottom to start capturing.

Apps can also use Sluice without capture: point them at the SOCKS5 server shown at the
bottom right (127.0.0.1:<port>).

Good to know
------------
- UDP goes through SOCKS5 and sing-box tunnels. SSH and HTTP tunnels can't carry it, so
  UDP of apps sent through them is blocked, and browsers fall back from QUIC to HTTPS.
- If Sluice ever crashes while capturing and DNS stops working, run:
      sudo nft delete table inet sluice
- The tray icon needs a desktop with a system tray (KDE, XFCE, Cinnamon...). On GNOME,
  install the "AppIndicator and KStatusNotifierItem Support" extension.
- Settings and profiles are stored in ~/.config/Sluice

Uninstall
---------
    sudo /opt/sluice/uninstall.sh
(The packages install.sh added, such as .NET and nftables, stay installed.)

Licences: sing-box is GPL-3.0; install.sh puts its licence in /opt/sluice/sing-box-LICENSE.txt.
