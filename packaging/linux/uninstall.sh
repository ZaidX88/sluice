#!/bin/sh
# Removes Sluice installed by install.sh. Your profiles and settings in ~/.config/Sluice are kept.
#
#   sudo /opt/sluice/uninstall.sh
set -e

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this with sudo: sudo $0" >&2
    exit 1
fi

# A capture left behind by a crash would keep redirecting DNS: remove Sluice's nftables table if present.
command -v nft >/dev/null 2>&1 && nft delete table inet sluice 2>/dev/null || true

rm -f /usr/local/bin/sluice /usr/local/bin/sluice-cli
rm -f /usr/share/applications/sluice.desktop /usr/share/icons/hicolor/256x256/apps/sluice.png
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications 2>/dev/null || true
rm -rf /opt/sluice

echo "Sluice removed. Your profiles in ~/.config/Sluice were kept."
