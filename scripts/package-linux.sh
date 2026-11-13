#!/bin/bash
# Builds the shareable Linux package sluice-linux-x64.tar.gz. Run on Linux or WSL.
#
#   scripts/package-linux.sh <publish dir> <sing-box-<ver>-linux-amd64.tar.gz> <output dir>
#
# <publish dir>: self-contained publish of Sluice.Desktop (-f net10.0) and Sluice.Cli into ONE folder:
#   dotnet publish src/Sluice.Desktop -f net10.0 -r linux-x64 --self-contained -c Release -o out/app
#   dotnet publish src/Sluice.Cli            -r linux-x64 --self-contained -c Release -o out/app
# The sing-box archive comes from scripts/get-sing-box.sh (checksum-verified).
set -euo pipefail
PUB=$(realpath "$1"); SINGBOX=$(realpath "$2"); OUT=$(realpath -m "$3")
REPO=$(cd "$(dirname "$0")/.." && pwd)
NAME=sluice-linux-x64
WORK=$(mktemp -d /tmp/sluice-package.XXXXXX)
trap 'case "$WORK" in /tmp/sluice-package.*) rm -rf -- "$WORK";; esac' EXIT
STAGE=$WORK/$NAME
mkdir -p "$STAGE/app" "$OUT"

cp -a "$PUB/." "$STAGE/app/"
# Windows-only files that Sluice.Core copies to every output, and debug symbols.
rm -f "$STAGE/app/WinDivert.dll" "$STAGE/app/WinDivert64.sys" "$STAGE/app/sing-box.exe" "$STAGE"/app/*.pdb

# sing-box helper (encrypted tunnels and the fast SSH client)
tar -xzf "$SINGBOX" -C "$STAGE" --wildcards '*/sing-box' '*/LICENSE'
mv "$STAGE"/sing-box-*/sing-box "$STAGE/app/sing-box"
mv "$STAGE"/sing-box-*/LICENSE "$STAGE/app/sing-box-LICENSE.txt"
rmdir "$STAGE"/sing-box-*

cp "$REPO/packaging/linux/install.sh" "$REPO/packaging/linux/uninstall.sh" \
   "$REPO/packaging/linux/sluice.desktop" "$REPO/packaging/linux/README.txt" "$STAGE/"
# Icon: the 256 px PNG stored inside Sluice.ico
python3 - "$REPO/src/Sluice.Desktop/Assets/Sluice.ico" "$STAGE/sluice.png" <<'PY'
import struct, sys
d = open(sys.argv[1], "rb").read()
n = struct.unpack_from("<H", d, 4)[0]
best = None
for i in range(n):
    w, h, _, _, _, _, size, off = struct.unpack_from("<BBBBHHII", d, 6 + 16 * i)
    w = w or 256
    if d[off:off + 8] == b"\x89PNG\r\n\x1a\n" and (best is None or w > best[0]):
        best = (w, d[off:off + size])
if best is None:
    sys.exit("no PNG image in the .ico")
open(sys.argv[2], "wb").write(best[1])
print(f"icon {best[0]}px")
PY

sed -i 's/\r$//' "$STAGE"/*.sh "$STAGE"/*.txt "$STAGE"/*.desktop
# Permissions matter: the installed binaries get capabilities, so nothing in the package may be writable by
# group/others (files copied from a Windows drive arrive as 777).
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE" -type f -exec chmod 644 {} +
chmod 755 "$STAGE"/*.sh "$STAGE/app/Sluice.Desktop" "$STAGE/app/Sluice.Cli" "$STAGE/app/sing-box" "$STAGE/app/createdump"

tar -czf "$OUT/$NAME.tar.gz" --owner=0 --group=0 -C "$WORK" "$NAME"
(cd "$OUT" && sha256sum "$NAME.tar.gz" > "$NAME.tar.gz.sha256")
echo "$(du -h "$OUT/$NAME.tar.gz" | cut -f1)  $OUT/$NAME.tar.gz"
cat "$OUT/$NAME.tar.gz.sha256"
