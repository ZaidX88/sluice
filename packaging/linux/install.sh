#!/bin/sh
# Installs Sluice from this folder and everything it needs.
#
#   sudo ./install.sh                 install (or update)
#   sudo ./install.sh --check         only report what is missing; change nothing
#
# What it does:
#   1. installs missing system packages with your package manager (apt, dnf, pacman or zypper):
#      the .NET 10 runtime, nftables, libcap (setcap), X11 / font libraries, notify-send
#      (.NET falls back to Microsoft's official installer where the distribution has no package)
#   2. downloads sing-box (encrypted tunnels) from GitHub and verifies its checksum; offline, put
#      sing-box-1.14.4-linux-amd64.tar.gz next to this script and it is used instead
#   3. copies Sluice to /opt/sluice, adds "sluice" / "sluice-cli" commands and a menu entry
#   4. grants the capture rights (setcap), so Sluice does not need to run as root
# Undo with: sudo /opt/sluice/uninstall.sh
set -eu

DEST=/opt/sluice
DOTNET_CHANNEL=10.0
SINGBOX_VERSION=1.14.4
SINGBOX_SHA256=699a1b510aa4396b2176d32d1181920399ebf1e267835da6f3b598d0f7c24968   # linux-amd64
SINGBOX_ASSET="sing-box-$SINGBOX_VERSION-linux-amd64.tar.gz"
SINGBOX_URL="https://github.com/SagerNet/sing-box/releases/download/v$SINGBOX_VERSION/$SINGBOX_ASSET"
CAPS="cap_net_admin,cap_setgid,cap_sys_ptrace,cap_dac_read_search+ep"

CHECK_ONLY=0
[ "${1:-}" = "--check" ] && CHECK_ONLY=1

say()  { printf '%s\n' "$*"; }
step() { printf '\n== %s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

if [ "$(id -u)" -ne 0 ] && [ "$CHECK_ONLY" -eq 0 ]; then
    die "run this with sudo:  sudo ./install.sh"
fi
case "$(uname -m)" in
    x86_64|amd64) ;;
    *) die "this package is for 64-bit Intel/AMD (x86_64); this computer is $(uname -m)" ;;
esac

HERE=$(cd "$(dirname "$0")" && pwd)
[ -f "$HERE/app/Sluice.Desktop" ] || die "app/Sluice.Desktop not found next to install.sh; run it from the unpacked Sluice folder"

# ---------------------------------------------------------------- package manager

PM=""
if   command -v apt-get >/dev/null 2>&1; then PM=apt
elif command -v dnf     >/dev/null 2>&1; then PM=dnf
elif command -v yum     >/dev/null 2>&1; then PM=yum
elif command -v pacman  >/dev/null 2>&1; then PM=pacman
elif command -v zypper  >/dev/null 2>&1; then PM=zypper
fi

APT_UPDATED=0
pkg_install() {   # names of packages for $PM
    [ $# -eq 0 ] && return 0
    say "Installing: $*"
    case "$PM" in
        apt)    if [ "$APT_UPDATED" -eq 0 ]; then DEBIAN_FRONTEND=noninteractive apt-get update -q || true; APT_UPDATED=1; fi
                DEBIAN_FRONTEND=noninteractive apt-get install -y -q "$@" ;;
        dnf)    dnf install -y -q "$@" ;;
        yum)    yum install -y -q "$@" ;;
        pacman) pacman -S --needed --noconfirm "$@" ;;
        zypper) zypper --non-interactive install --no-recommends "$@" ;;
        *)      return 1 ;;
    esac
}

# Package that provides each requirement, per package manager.
pkg_name() {   # requirement
    case "$PM:$1" in
        apt:nft)        echo nftables ;;      dnf:nft|yum:nft|pacman:nft|zypper:nft) echo nftables ;;
        apt:setcap)     echo libcap2-bin ;;   dnf:setcap|yum:setcap|pacman:setcap)  echo libcap ;;
        zypper:setcap)  echo libcap-progs ;;
        apt:libX11)     echo libx11-6 ;;      dnf:libX11|yum:libX11) echo libX11 ;;  pacman:libX11) echo libx11 ;;  zypper:libX11) echo libX11-6 ;;
        apt:libICE)     echo libice6 ;;       dnf:libICE|yum:libICE) echo libICE ;;  pacman:libICE) echo libice ;;  zypper:libICE) echo libICE6 ;;
        apt:libSM)      echo libsm6 ;;        dnf:libSM|yum:libSM)   echo libSM ;;   pacman:libSM)  echo libsm ;;   zypper:libSM)  echo libSM6 ;;
        apt:fontconfig) echo libfontconfig1 ;; *:fontconfig) echo fontconfig ;;
        apt:notify)     echo libnotify-bin ;; dnf:notify|yum:notify|pacman:notify) echo libnotify ;; zypper:notify) echo libnotify-tools ;;
        apt:curl|dnf:curl|yum:curl|pacman:curl|zypper:curl) echo curl ;;
        apt:certs|dnf:certs|yum:certs|pacman:certs|zypper:certs) echo ca-certificates ;;
        apt:dotnet|dnf:dotnet|yum:dotnet) echo dotnet-runtime-$DOTNET_CHANNEL ;;
        pacman:dotnet)  echo dotnet-runtime ;;
        *) echo "" ;;
    esac
}

has_lib() { ldconfig -p 2>/dev/null | grep -q "$1"; }

# ---------------------------------------------------------------- 1. system packages

step "Checking system packages${PM:+ ($PM)}"
missing=""
command -v nft    >/dev/null 2>&1 || missing="$missing nft"
command -v setcap >/dev/null 2>&1 || [ -x /usr/sbin/setcap ] || [ -x /sbin/setcap ] || missing="$missing setcap"
has_lib libX11.so.6       || missing="$missing libX11"
has_lib libICE.so.6       || missing="$missing libICE"
has_lib libSM.so.6        || missing="$missing libSM"
has_lib libfontconfig.so.1 || missing="$missing fontconfig"
command -v notify-send >/dev/null 2>&1 || missing="$missing notify"
command -v curl   >/dev/null 2>&1 || missing="$missing curl"
[ -d /etc/ssl/certs ] || [ -d /etc/pki/tls/certs ] || missing="$missing certs"

if [ -z "$missing" ]; then
    say "All present."
elif [ "$CHECK_ONLY" -eq 1 ]; then
    say "Missing:$missing"
elif [ -z "$PM" ]; then
    warn "no supported package manager found (apt, dnf, yum, pacman, zypper). Install these yourself:$missing"
else
    pkgs=""
    for r in $missing; do p=$(pkg_name "$r"); [ -n "$p" ] && pkgs="$pkgs $p"; done
    # shellcheck disable=SC2086
    pkg_install $pkgs || warn "some packages failed to install:$pkgs"
fi

# ---------------------------------------------------------------- .NET 10 runtime

# Where an app like Sluice looks for .NET: /etc/dotnet/install_location*, then /usr/share/dotnet.
dotnet_root_with_runtime() {
    for root in "$(cat /etc/dotnet/install_location_x64 2>/dev/null)" "$(cat /etc/dotnet/install_location 2>/dev/null)" \
                /usr/share/dotnet /usr/lib/dotnet /usr/lib64/dotnet /opt/dotnet \
                "$(dirname "$(readlink -f "$(command -v dotnet 2>/dev/null || echo /nonexistent)")")"; do
        [ -n "$root" ] && ls -d "$root"/shared/Microsoft.NETCore.App/$DOTNET_CHANNEL.* >/dev/null 2>&1 && { echo "$root"; return 0; }
    done
    return 1
}

step "Checking the .NET $DOTNET_CHANNEL runtime"
if root=$(dotnet_root_with_runtime); then
    say "Found in $root"
elif [ "$CHECK_ONLY" -eq 1 ]; then
    say "Missing: .NET $DOTNET_CHANNEL runtime"
else
    p=$(pkg_name dotnet)
    if [ -n "$p" ]; then pkg_install "$p" || say "No $p package here; using Microsoft's installer instead."; fi
    if ! root=$(dotnet_root_with_runtime); then
        say "Installing .NET $DOTNET_CHANNEL with Microsoft's official script (dot.net/v1/dotnet-install.sh)"
        tmp=$(mktemp -d)
        curl -fsSL --retry 3 https://dot.net/v1/dotnet-install.sh -o "$tmp/dotnet-install.sh" \
            || die "could not download the .NET installer; check the internet connection and run install.sh again"
        bash "$tmp/dotnet-install.sh" --channel "$DOTNET_CHANNEL" --runtime dotnet --install-dir /usr/share/dotnet \
            || die "the .NET installer failed"
        rm -rf "$tmp"
        ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
        root=$(dotnet_root_with_runtime) || die ".NET $DOTNET_CHANNEL is still not found after installing"
    fi
    say ".NET $DOTNET_CHANNEL runtime: $root"
fi
# Sluice runs with capabilities, so it ignores DOTNET_ROOT: tell it where .NET is if that is not a default place.
if [ "$CHECK_ONLY" -eq 0 ] && [ -n "${root:-}" ] && [ "$root" != /usr/share/dotnet ] && \
   [ "$(cat /etc/dotnet/install_location_x64 2>/dev/null)" != "$root" ] && [ "$(cat /etc/dotnet/install_location 2>/dev/null)" != "$root" ]; then
    mkdir -p /etc/dotnet
    echo "$root" > /etc/dotnet/install_location_x64
    say "Registered $root in /etc/dotnet/install_location_x64"
fi

# ---------------------------------------------------------------- 2. sing-box

step "Getting sing-box $SINGBOX_VERSION"
SINGBOX_TGZ=""
if [ "$CHECK_ONLY" -eq 1 ]; then
    if [ -x "$DEST/sing-box" ] && "$DEST/sing-box" version 2>/dev/null | grep -q "$SINGBOX_VERSION"; then say "Installed."; else say "Not installed yet (will be downloaded)."; fi
else
    work=$(mktemp -d)
    if [ -f "$HERE/$SINGBOX_ASSET" ]; then
        SINGBOX_TGZ="$HERE/$SINGBOX_ASSET"; say "Using $SINGBOX_ASSET from this folder"
    elif [ -x "$DEST/sing-box" ] && "$DEST/sing-box" version 2>/dev/null | grep -q "$SINGBOX_VERSION"; then
        cp "$DEST/sing-box" "$work/sing-box.keep"; say "Already installed; keeping it"
    elif curl -fsSL --retry 3 --connect-timeout 20 -o "$work/$SINGBOX_ASSET" "$SINGBOX_URL"; then
        SINGBOX_TGZ="$work/$SINGBOX_ASSET"
    else
        warn "could not download sing-box from GitHub. Sluice still works with SSH, SOCKS and HTTP tunnels;"
        warn "for VLESS/VMess/Trojan/Shadowsocks/Hysteria2/TUIC, download $SINGBOX_URL"
        warn "elsewhere, put it next to install.sh and run install.sh again."
    fi
    if [ -n "$SINGBOX_TGZ" ]; then
        echo "$SINGBOX_SHA256  $SINGBOX_TGZ" | sha256sum -c - >/dev/null 2>&1 || die "sing-box checksum mismatch: $SINGBOX_TGZ is not the official release"
        tar -xzf "$SINGBOX_TGZ" -C "$work" && cp "$work"/sing-box-*/sing-box "$work/sing-box.keep" && cp "$work"/sing-box-*/LICENSE "$work/sing-box-LICENSE.txt"
        say "sing-box verified (SHA-256)"
    fi
fi

# ---------------------------------------------------------------- 3. install Sluice

if [ "$CHECK_ONLY" -eq 1 ]; then
    say; say "Check finished; nothing was changed."; exit 0
fi

step "Installing Sluice to $DEST"
rm -rf "$DEST"
mkdir -p "$DEST"
cp -a "$HERE/app/." "$DEST/"
cp "$HERE/uninstall.sh" "$HERE/README.txt" "$DEST/"
[ -f "$work/sing-box.keep" ] && cp "$work/sing-box.keep" "$DEST/sing-box"
[ -f "$work/sing-box-LICENSE.txt" ] && cp "$work/sing-box-LICENSE.txt" "$DEST/"
rm -rf "$work"
# The binaries get capabilities below: everything they load must be owned by root and not writable by others.
chown -R root:root "$DEST"
find "$DEST" -type d -exec chmod 755 {} +
find "$DEST" -type f -exec chmod 644 {} +
chmod 755 "$DEST/Sluice.Desktop" "$DEST/Sluice.Cli" "$DEST/uninstall.sh"
[ -f "$DEST/sing-box" ] && chmod 755 "$DEST/sing-box"

ln -sf "$DEST/Sluice.Desktop" /usr/local/bin/sluice
ln -sf "$DEST/Sluice.Cli" /usr/local/bin/sluice-cli
mkdir -p /usr/share/icons/hicolor/256x256/apps /usr/share/applications
install -m 644 "$HERE/sluice.png" /usr/share/icons/hicolor/256x256/apps/sluice.png
install -m 644 "$HERE/sluice.desktop" /usr/share/applications/sluice.desktop
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q /usr/share/icons/hicolor 2>/dev/null || true
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications 2>/dev/null || true

# ---------------------------------------------------------------- 4. capture rights

#   cap_net_admin        nftables rules and the NFQUEUE netlink socket
#   cap_setgid           Sluice's private network group (its own traffic is never captured)
#   cap_sys_ptrace,
#   cap_dac_read_search  see which app (of any user) owns a connection, for rules by application
step "Granting capture rights"
SETCAP=$(command -v setcap || ls /usr/sbin/setcap /sbin/setcap 2>/dev/null | head -1 || true)
if [ -n "$SETCAP" ]; then
    "$SETCAP" "$CAPS" "$DEST/Sluice.Desktop"
    "$SETCAP" "$CAPS" "$DEST/Sluice.Cli"
    say "Done (setcap)."
else
    warn "setcap is not available; capture will need: sudo sluice"
fi

# ---------------------------------------------------------------- final check

step "Checking the installation"
if "$DEST/Sluice.Cli" --help >/dev/null 2>&1; then say "Sluice starts."; else warn "Sluice did not start: run $DEST/Sluice.Cli --help to see why"; fi
command -v nft >/dev/null 2>&1 || warn "nftables is missing: capturing traffic will not work until it is installed"
[ -f "$DEST/sing-box" ] || warn "sing-box is missing: only SSH, SOCKS and HTTP tunnels work"

say
say "Done. Start Sluice from the applications menu, or run: sluice"
