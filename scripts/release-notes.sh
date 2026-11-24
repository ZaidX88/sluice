#!/bin/bash
# Prints the GitHub Release notes for a version (#74): its section of CHANGELOG.md, without the heading and without
# HTML comments, then where to get and how to install the packages.
#
#   scripts/release-notes.sh <version> [prerelease: true|false] [CHANGELOG.md]
#
# The prerelease flag defaults to true for a version with a suffix (1.0.0-rc.1), false otherwise.
#
# A release (1.0.0) needs a "## [1.0.0] - <date>" section and an empty [Unreleased]: a section still marked "not
# released yet", or changes still under [Unreleased], stop it, so the owner moves them into the dated section before
# tagging. A release candidate (1.0.0-rc.1) gets everything the release will have: [Unreleased] joined with a
# [1.0.0] section that isn't released yet (one list per group, Added, Changed, ...), or [Unreleased] alone (#214).
set -euo pipefail
VERSION=$1; CHANGELOG=${3:-CHANGELOG.md}
BASE=${VERSION%%-*}
if [ -n "${2:-}" ]; then PRERELEASE=$2; elif [ "$BASE" != "$VERSION" ]; then PRERELEASE=true; else PRERELEASE=false; fi
REPO=${GITHUB_REPOSITORY:-ZaidX88/sluice}

section() {   # the lines after "## [$1]…" up to the next "## [" heading, without HTML comments
    awk -v h="## [$1]" 'index($0, h) == 1 { on = 1; next } /^## \[/ { if (on) exit } on' "$CHANGELOG" |
        perl -0pe 's/<!--.*?-->//gs'
}

# Joins sections: the intro texts, then each "### group" once, with the items of every section in turn. Groups follow
# Keep a Changelog's order; others (Known limitations) come last.
join_sections() {
    perl -e '
        my @order = qw(Added Changed Deprecated Removed Fixed Security);
        my (@intro, %parts, @seen);
        sub trim { my $t = shift; $t =~ s/\A\s*\n//; $t =~ s/\s+\z//; return $t }
        for my $text (@ARGV) {
            my ($cur, %buf) = ("");
            for (split /\n/, $text, -1) {
                if (/^### (.+?)\s*$/) { $cur = $1; push @seen, $cur unless grep { $_ eq $cur } @seen; next }
                $buf{$cur} .= "$_\n";
            }
            for my $g (keys %buf) {
                my $t = trim($buf{$g});
                next unless length $t;
                if ($g eq "") { push @intro, $t } else { push @{ $parts{$g} }, $t }
            }
        }
        my %known = map { $_ => 1 } @order;
        my @out = @intro;
        for my $g ((grep { $parts{$_} } @order), (grep { !$known{$_} && $parts{$_} } @seen)) {
            push @out, "### $g\n\n" . join("\n", @{ $parts{$g} });
        }
        print join("\n\n", @out), "\n";
    ' "$@"
}

unreleased=$(section Unreleased)
heading=$(grep -m1 -F "## [$BASE]" "$CHANGELOG" || true)
if [ "$PRERELEASE" != true ]; then
    if [ -z "$heading" ]; then
        echo "CHANGELOG.md has no \"## [$BASE]\" section for v$VERSION" >&2
        exit 1
    fi
    if printf '%s' "$heading" | grep -qi 'not released yet'; then
        echo "CHANGELOG.md: \"$heading\" is still marked not released; give it the release date before tagging v$VERSION" >&2
        exit 1
    fi
    if printf '%s' "$unreleased" | grep -q '[^[:space:]]'; then
        echo "CHANGELOG.md: [Unreleased] still has changes; move them into \"$heading\" before tagging v$VERSION" >&2
        exit 1
    fi
    body=$(join_sections "$(section "$BASE")")
elif [ -n "$heading" ] && ! printf '%s' "$heading" | grep -qi 'not released yet'; then
    body=$(join_sections "$(section "$BASE")")
elif [ -n "$heading" ]; then
    body=$(join_sections "$(section "$BASE")" "$unreleased")
else
    body=$(join_sections "$unreleased")
fi
[ -n "$body" ] || { echo "The CHANGELOG.md section for v$VERSION is empty" >&2; exit 1; }

printf '%s\n\n' "$body"
cat <<EOF
## Download and install

| System | File | Guide |
|---|---|---|
| Windows 10 / 11, x64 | \`Sluice-Setup-$VERSION-x64.exe\` | [docs/windows/INSTALL.md](https://github.com/$REPO/blob/v$VERSION/docs/windows/INSTALL.md) |
| Ubuntu 24.04 LTS or later, x86-64 | \`sluice_${VERSION}_amd64.deb\` | [docs/linux/INSTALL.md](https://github.com/$REPO/blob/v$VERSION/docs/linux/INSTALL.md#ubuntu-2404-or-later-the-deb) |
| Other Linux, x86-64 (Debian, Ubuntu 22.04 or older, Fedora, Arch, …) | \`sluice-$VERSION-linux-x64.tar.gz\` | [docs/linux/INSTALL.md](https://github.com/$REPO/blob/v$VERSION/docs/linux/INSTALL.md) |

On Ubuntu, install the .deb with \`sudo apt install ./sluice_${VERSION}_amd64.deb\` (with the \`./\`, not \`dpkg -i\`),
so apt brings in the .NET runtime too. Each file has a \`.sha256\` next to it to check the download.
EOF
