#!/bin/sh
# Installs Sluice from this folder into /opt/sluice and gives it the rights to capture traffic.
#
#   sudo ./install.sh
#
# What it does:
#   - copies the app to /opt/sluice (replacing an older copy)
#   - adds "sluice" (the app) and "sluice-cli" to /usr/local/bin
#   - adds Sluice to the applications menu, with its icon
#   - grants the capture capabilities with setcap, so Sluice can capture without running as root
# Undo with: sudo /opt/sluice/uninstall.sh
set -e

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this with sudo: sudo ./install.sh" >&2
    exit 1
fi

HERE=$(cd "$(dirname "$0")" && pwd)
DEST=/opt/sluice

if [ ! -x "$HERE/app/Sluice.Desktop" ]; then
    echo "app/Sluice.Desktop not found next to install.sh. Run it from the unpacked Sluice folder." >&2
    exit 1
fi

echo "Installing Sluice to $DEST"
rm -rf "$DEST"
mkdir -p "$DEST"
cp -a "$HERE/app/." "$DEST/"
cp "$HERE/uninstall.sh" "$HERE/README.txt" "$DEST/"
# The binaries get capabilities below: everything they load must be owned by root and not writable by others.
chown -R root:root "$DEST"
find "$DEST" -type d -exec chmod 755 {} +
find "$DEST" -type f -exec chmod 644 {} +
chmod 755 "$DEST/Sluice.Desktop" "$DEST/Sluice.Cli" "$DEST/uninstall.sh"
[ -f "$DEST/createdump" ] && chmod 755 "$DEST/createdump"
[ -f "$DEST/sing-box" ] && chmod 755 "$DEST/sing-box"

ln -sf "$DEST/Sluice.Desktop" /usr/local/bin/sluice
ln -sf "$DEST/Sluice.Cli" /usr/local/bin/sluice-cli

mkdir -p /usr/share/icons/hicolor/256x256/apps /usr/share/applications
install -m 644 "$HERE/sluice.png" /usr/share/icons/hicolor/256x256/apps/sluice.png
install -m 644 "$HERE/sluice.desktop" /usr/share/applications/sluice.desktop
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q /usr/share/icons/hicolor 2>/dev/null || true
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications 2>/dev/null || true

# Capture rights without root:
#   cap_net_admin       nftables rules and the NFQUEUE netlink socket
#   cap_setgid          Sluice's private network group (its own traffic is never captured)
#   cap_sys_ptrace,
#   cap_dac_read_search see which app (of any user) owns a connection, for rules by application
CAPS="cap_net_admin,cap_setgid,cap_sys_ptrace,cap_dac_read_search+ep"
if command -v setcap >/dev/null 2>&1; then
    setcap "$CAPS" "$DEST/Sluice.Desktop"
    setcap "$CAPS" "$DEST/Sluice.Cli"
    echo "Capture rights granted (setcap)."
else
    echo "WARNING: setcap not found (package libcap2-bin / libcap). Capture will need: sudo sluice" >&2
fi

missing=""
command -v nft >/dev/null 2>&1 || missing="$missing nftables"
if command -v ldconfig >/dev/null 2>&1; then
    for lib in libX11.so.6 libICE.so.6 libSM.so.6 libfontconfig.so.1; do
        ldconfig -p | grep -q "$lib" || missing="$missing $lib"
    done
fi
if [ -n "$missing" ]; then
    echo
    echo "Also install:$missing"
    echo "  Debian/Ubuntu: sudo apt install nftables libx11-6 libice6 libsm6 libfontconfig1"
    echo "  Fedora:        sudo dnf install nftables libX11 libICE libSM fontconfig"
    echo "  Arch:          sudo pacman -S nftables libx11 libice libsm fontconfig"
fi

echo
echo "Done. Start Sluice from the applications menu, or run: sluice"
