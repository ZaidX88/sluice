#!/bin/bash
# Prints the GitHub Release notes for a version (#74): its section of CHANGELOG.md, without the heading and without
# HTML comments, then where to get and how to install the packages.
#
#   scripts/release-notes.sh <version> <prerelease: true|false> [CHANGELOG.md]
#
# A release (1.0.0) needs a "## [1.0.0] - <date>" section: one still marked "not released yet" stops it, so the owner
# dates the section before tagging. A release candidate (1.0.0-rc.1) uses the [1.0.0] section if there is one,
# otherwise [Unreleased].
set -euo pipefail
VERSION=$1; PRERELEASE=$2; CHANGELOG=${3:-CHANGELOG.md}
REPO=${GITHUB_REPOSITORY:-ZaidX88/sluice}
BASE=${VERSION%%-*}

section() {   # the lines after "## [$1]…" up to the next "## [" heading
    awk -v h="## [$1]" 'index($0, h) == 1 { on = 1; next } /^## \[/ { if (on) exit } on' "$CHANGELOG"
}
heading=$(grep -m1 -F "## [$BASE]" "$CHANGELOG" || true)
if [ -n "$heading" ]; then
    if [ "$PRERELEASE" != true ] && printf '%s' "$heading" | grep -qi 'not released yet'; then
        echo "CHANGELOG.md: \"$heading\" is still marked not released; give it the release date before tagging v$VERSION" >&2
        exit 1
    fi
    body=$(section "$BASE")
elif [ "$PRERELEASE" = true ]; then
    body=$(section Unreleased)
else
    echo "CHANGELOG.md has no \"## [$BASE]\" section for v$VERSION" >&2
    exit 1
fi

# Drop HTML comments (single- or multi-line), then leading and trailing blank lines.
body=$(printf '%s\n' "$body" | perl -0pe 's/<!--.*?-->//gs' | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}')
[ -n "$body" ] || { echo "The CHANGELOG.md section for v$VERSION is empty" >&2; exit 1; }

printf '%s\n\n' "$body"
cat <<EOF
## Download and install

| System | File | Guide |
|---|---|---|
| Windows 10 / 11, x64 | \`Sluice-Setup-$VERSION-x64.exe\` | [docs/windows/INSTALL.md](https://github.com/$REPO/blob/v$VERSION/docs/windows/INSTALL.md) |
| Linux, x86-64 | \`sluice-$VERSION-linux-x64.tar.gz\` | [docs/linux/INSTALL.md](https://github.com/$REPO/blob/v$VERSION/docs/linux/INSTALL.md) |

Each file has a \`.sha256\` next to it to check the download.
EOF
