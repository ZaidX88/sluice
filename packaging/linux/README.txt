Sluice for Linux (x86-64)
=========================

Sluice sends each app's connections where you choose: directly, blocked, or through a
tunnel (SSH, SOCKS, HTTP(S), VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC).
Everything it needs is in this folder; .NET does not have to be installed.

Install
-------
    tar -xzf sluice-linux-x64.tar.gz
    cd sluice-linux-x64
    sudo ./install.sh

Then start "Sluice" from the applications menu, or run:  sluice

The installer copies Sluice to /opt/sluice, adds it to the menu, and gives it the
rights to capture traffic (setcap), so you do not need to run it as root.

You also need (the installer tells you if something is missing):
  - nftables                      for capturing other apps' traffic
  - X11 libraries and fontconfig  for the window (present on normal desktops; Wayland
                                  desktops run it through XWayland)

Try without installing
----------------------
    ./app/Sluice.Desktop          the app (capture needs the installer's rights, or sudo)
    ./app/Sluice.Cli --help       command line, for servers

Using it
--------
1. Tunnels: add your server (or Import a share link / subscription), then Check it.
2. Rules: choose which apps or sites go through which tunnel; the rest go Direct.
3. Press the play button at the bottom to start capturing.

Apps can also use Sluice without capture: point them at the SOCKS5 server shown at the
bottom right (127.0.0.1:<port>).

Good to know
------------
- UDP through tunnels is not available on Linux yet: UDP of apps whose rule says
  "tunnel" is blocked, so browsers fall back from QUIC to normal HTTPS. Everything
  else works.
- If Sluice ever crashes while capturing and DNS stops working, run:
      sudo nft delete table inet sluice
- The tray icon needs a desktop with a system tray (KDE, XFCE, Cinnamon...). On GNOME,
  install the "AppIndicator and KStatusNotifierItem Support" extension.
- Settings and profiles are stored in ~/.config/Sluice

Uninstall
---------
    sudo /opt/sluice/uninstall.sh

Licences: sing-box (included as the "sing-box" helper) is GPL-3.0, see sing-box-LICENSE.txt.
