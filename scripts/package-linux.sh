#!/bin/bash
# Builds the shareable Linux package sluice-linux-x64.tar.gz: the app only (it uses the system's .NET 10 runtime),
# the launcher (packaging/linux/launcher, the only file that gets the capture rights) and install.sh, which installs
# the missing dependencies, downloads sing-box and grants the capture rights. Run on Linux or WSL; needs a C compiler.
#
#   scripts/package-linux.sh [--deb] <publish dir> <output dir>
#
# --deb also builds sluice_<version>_amd64.deb for Ubuntu 24.04 and later (#67) from the same files, with dpkg-deb:
# the same /opt/sluice layout, sing-box bundled (the pinned, SHA-256-checked release: $SINGBOX_TGZ if set, else the
# build's .deps/ copy, else downloaded), and packaging/linux/deb/ for the control file and maintainer scripts.
#
# <publish dir>: framework-dependent publish of Sluice.Desktop (-f net10.0) and Sluice.Cli into ONE folder:
#   dotnet publish src/Sluice.Desktop -f net10.0 -r linux-x64 --self-contained false -c Release -p:SluiceFetchDependencies=false -o out/app
#   dotnet publish src/Sluice.Cli            -r linux-x64 --self-contained false -c Release -p:SluiceFetchDependencies=false -o out/app
set -euo pipefail
DEB=0
if [ "${1:-}" = --deb ]; then DEB=1; shift; fi
PUB=$(realpath "$1"); OUT=$(realpath -m "$2")
REPO=$(cd "$(dirname "$0")/.." && pwd)
NAME=sluice-linux-x64
WORK=$(mktemp -d /tmp/sluice-package.XXXXXX)
trap 'case "$WORK" in /tmp/sluice-package.*) rm -rf -- "$WORK";; esac' EXIT
STAGE=$WORK/$NAME
mkdir -p "$STAGE/app" "$OUT"

[ -f "$PUB/Sluice.Desktop" ] && [ -f "$PUB/Sluice.Cli" ] || { echo "Sluice.Desktop / Sluice.Cli not found in $PUB" >&2; exit 1; }
if [ -f "$PUB/libcoreclr.so" ]; then echo "$PUB is a self-contained publish; use --self-contained false" >&2; exit 1; fi

cp -a "$PUB/." "$STAGE/app/"
# Windows-only files that Sluice.Core copies to every output, sing-box (install.sh downloads it), debug symbols.
rm -f "$STAGE/app/WinDivert.dll" "$STAGE/app/WinDivert64.sys" "$STAGE/app/sing-box.exe" "$STAGE/app/sing-box-LICENSE.txt" "$STAGE"/app/*.pdb
# The launcher, installed as /opt/sluice/sluice: starts Sluice.Desktop (or Sluice.Cli as "sluice-cli") with the rights.
# Its tests (folder checks, environment filter) run first.
"${CC:-cc}" -O2 -Wall -Wextra -Werror -o "$WORK/launcher-test" "$REPO/packaging/linux/launcher/launcher-test.c"
"$WORK/launcher-test" > "$WORK/launcher-test.log" || { cat "$WORK/launcher-test.log" >&2; echo "launcher tests failed" >&2; exit 1; }
"${CC:-cc}" -O2 -Wall -Wextra -Werror -D_FORTIFY_SOURCE=3 -fstack-protector-strong -fPIE -pie -Wl,-z,relro,-z,now \
    -o "$STAGE/app/sluice" "$REPO/packaging/linux/launcher/sluice-launcher.c"

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
PY

sed -i 's/\r$//' "$STAGE"/*.sh "$STAGE"/*.txt "$STAGE"/*.desktop
# Permissions matter: the installed launcher gets capabilities and refuses to run if anything next to it is writable
# by group/others (files copied from a Windows drive arrive as 777).
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE" -type f -exec chmod 644 {} +
chmod 755 "$STAGE"/*.sh "$STAGE/app/sluice" "$STAGE/app/Sluice.Desktop" "$STAGE/app/Sluice.Cli"

tar -czf "$OUT/$NAME.tar.gz" --owner=0 --group=0 -C "$WORK" "$NAME"
(cd "$OUT" && sha256sum "$NAME.tar.gz" > "$NAME.tar.gz.sha256")
echo "$(du -h "$OUT/$NAME.tar.gz" | cut -f1)  $OUT/$NAME.tar.gz  ($(ls "$STAGE/app" | wc -l) app files)"
cat "$OUT/$NAME.tar.gz.sha256"

[ "$DEB" -eq 1 ] || exit 0

# ---------------------------------------------------------------- .deb (#67)

command -v dpkg-deb >/dev/null || { echo "--deb needs dpkg-deb (Debian/Ubuntu: package dpkg)" >&2; exit 1; }
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$REPO/Directory.Build.props" | head -n1)
[ -n "$VERSION" ] || { echo "no <Version> in Directory.Build.props" >&2; exit 1; }
DEBVER=$(printf '%s' "$VERSION" | tr '-' '~')   # 1.0.0-rc.1 -> 1.0.0~rc.1, which sorts before 1.0.0
# sing-box: the version and SHA-256 install.sh pins, so both packages ship the same one.
SB_VERSION=$(sed -n 's/^SINGBOX_VERSION=//p' "$REPO/packaging/linux/install.sh")
SB_SHA256=$(sed -n 's/^SINGBOX_SHA256=\([0-9a-f]*\).*/\1/p' "$REPO/packaging/linux/install.sh")
SB_NAME=sing-box-$SB_VERSION-linux-amd64
SB_DIR=$WORK/singbox
mkdir -p "$SB_DIR"
if [ -n "${SINGBOX_TGZ:-}" ] || [ ! -f "$REPO/.deps/$SB_NAME/sing-box" ]; then
    TGZ=${SINGBOX_TGZ:-$SB_DIR/$SB_NAME.tar.gz}
    [ -n "${SINGBOX_TGZ:-}" ] || curl -fsSL --retry 3 -o "$TGZ" \
        "https://github.com/SagerNet/sing-box/releases/download/v$SB_VERSION/$SB_NAME.tar.gz"
    echo "$SB_SHA256  $TGZ" | sha256sum -c - >/dev/null || { echo "sing-box SHA-256 mismatch: $TGZ" >&2; exit 1; }
    tar -xzf "$TGZ" -C "$SB_DIR"
    SB_SRC=$SB_DIR/$SB_NAME
else
    SB_SRC=$REPO/.deps/$SB_NAME   # downloaded and SHA-256-checked by the build (build/Dependencies.targets)
fi

D=$WORK/deb
mkdir -p "$D/DEBIAN" "$D/opt/sluice" "$D/usr/bin" "$D/usr/share/applications" \
         "$D/usr/share/icons/hicolor/256x256/apps" "$D/usr/share/doc/sluice"
cp -a "$STAGE/app/." "$D/opt/sluice/"
install -m 755 "$SB_SRC/sing-box" "$D/opt/sluice/sing-box"
install -m 644 "$SB_SRC/LICENSE" "$D/opt/sluice/sing-box-LICENSE.txt"
# Both commands go through the launcher; it starts Sluice.Cli when called "sluice-cli".
ln -s /opt/sluice/sluice "$D/usr/bin/sluice"
ln -s /opt/sluice/sluice "$D/usr/bin/sluice-cli"
install -m 644 "$STAGE/sluice.desktop" "$D/usr/share/applications/sluice.desktop"
install -m 644 "$STAGE/sluice.png" "$D/usr/share/icons/hicolor/256x256/apps/sluice.png"
install -m 644 "$REPO/packaging/linux/deb/copyright" "$REPO/THIRD-PARTY-NOTICES.md" "$D/usr/share/doc/sluice/"
for s in preinst postinst prerm postrm; do install -m 755 "$REPO/packaging/linux/deb/$s" "$D/DEBIAN/$s"; done
# The launcher needs the C library version it was built against (what dpkg-shlibdeps would find).
GLIBC=$(objdump -T "$D/opt/sluice/sluice" | grep -o 'GLIBC_[0-9.]*' | sed 's/GLIBC_//' | sort -V | tail -n1)
sed -e "s/@VERSION@/$DEBVER/" -e "s/@INSTALLED_SIZE@/$(du -sk --exclude=DEBIAN "$D" | cut -f1)/" \
    -e "s/@LIBC@/libc6 (>= $GLIBC)/" -e "s/@SINGBOX_VERSION@/$SB_VERSION/" \
    "$REPO/packaging/linux/deb/control" > "$D/DEBIAN/control"
sed -i 's/\r$//' "$D"/DEBIAN/* "$D/usr/share/doc/sluice/copyright"
find "$D" -type d -exec chmod 755 {} +

# The file name keeps the release version (GitHub renames "~" in release files; UpdateCheck looks for this name).
DEBFILE=sluice_${VERSION}_amd64.deb
dpkg-deb --root-owner-group -Zxz --build "$D" "$OUT/$DEBFILE" >/dev/null
(cd "$OUT" && sha256sum "$DEBFILE" > "$DEBFILE.sha256")
echo "$(du -h "$OUT/$DEBFILE" | cut -f1)  $OUT/$DEBFILE"
cat "$OUT/$DEBFILE.sha256"
