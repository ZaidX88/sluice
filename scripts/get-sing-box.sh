#!/bin/bash
# Downloads the pinned sing-box release for Linux (used by Sluice's encrypted tunnels and fast SSH client)
# into lib/sing-box-linux-amd64 and verifies it against the SHA-256 GitHub publishes. sing-box is GPL-3.0.
set -euo pipefail
VERSION=1.14.4
SHA256=699a1b510aa4396b2176d32d1181920399ebf1e267835da6f3b598d0f7c24968   # sing-box-1.14.4-linux-amd64.tar.gz
ASSET=sing-box-$VERSION-linux-amd64.tar.gz
DEST=$(cd "$(dirname "$0")/.." && pwd)/lib/sing-box-linux-amd64
mkdir -p "$DEST"
[ -f "$DEST/$ASSET" ] || curl -fL --retry 5 -C - -o "$DEST/$ASSET" "https://github.com/SagerNet/sing-box/releases/download/v$VERSION/$ASSET"
echo "$SHA256  $DEST/$ASSET" | sha256sum -c -
