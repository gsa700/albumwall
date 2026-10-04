#!/usr/bin/env bash
# AlbumWall - cut an app release: all three platforms, one GitHub release.
#
#   scripts/release.sh 0.2.0                     a DRAFT release, to look at first
#   scripts/release.sh 0.2.0 --notes-file N.md   with the notes written by hand
#   scripts/release.sh 0.2.0 --publish           published at once (users are offered it)
#   scripts/release.sh 0.2.0 --dry-run           build and zip only: no tag, no release,
#                                                version/tree checks only warn, and the
#                                                engine comes from native/ if its release
#                                                is not out yet. Leaves the zips in ./dist.
#
# WHAT A RELEASE IS, because the updater (Install/UpdateService.cs) reads exactly
# this and nothing else: a normal, non-pre-release GitHub release tagged vX.Y.Z,
# carrying AlbumWall-<rid>.zip for linux-x64, linux-arm64 and win-x64 - each zip
# holding the single-file program - and SHA256SUMS listing every zip. The updater
# reads /releases/latest, refuses a download SHA256SUMS does not vouch for, and
# ignores drafts and pre-releases entirely. So a DRAFT is safe to make, look at
# and delete; publishing it is the moment every installed copy is offered it.
#
# THE AUDIO ENGINE IS FETCHED, NOT TAKEN FROM WHATEVER IS IN native/. The release
# named in scripts/LIBMPV_RELEASE is downloaded, checked against its SHA256SUMS
# and put in native/<rid> before anything is built, so what ships is what was
# measured and published, whatever a developer's native/ folder happens to hold.
# (It does replace that folder's contents; they are the same files a release
# carries, which is the point.)
#
# REFUSES TO RUN unless: the tree is clean, HEAD is what origin/master has (a
# release is built from pushed code), <Version> in the csproj equals the version
# asked for (bump it in a commit of its own first), and neither the tag nor the
# release exists yet.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/.." && pwd)
REPO=gsa700/albumwall
CSPROJ="$ROOT/src/AlbumWall.App/AlbumWall.App.csproj"
RIDS=(linux-x64 linux-arm64 win-x64)

VERSION=${1:?usage: release.sh X.Y.Z [--notes-file FILE] [--publish]}
shift
PUBLISH=0; NOTES=""; DRY=0
while [ $# -gt 0 ]; do
    case "$1" in
        --publish) PUBLISH=1 ;;
        --dry-run) DRY=1 ;;
        --notes-file) NOTES=${2:?--notes-file needs a file}; shift ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done

die() { echo "release: $*" >&2; exit 1; }
# A precondition that stops a real release and only warns in a dry run.
must() { if [ $DRY -eq 1 ]; then echo "release (dry run, would stop here): $*" >&2; else die "$*"; fi; }
say() { echo "== $*"; }

# ---- preconditions ----------------------------------------------------------
[[ $VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || die "version must look like 0.2.0, not $VERSION"
[[ $VERSION == *-* ]] && die "$VERSION has a pre-release suffix, and the updater never sees pre-releases; use a plain version"
TAG="v$VERSION"
command -v gh >/dev/null || die "the GitHub CLI (gh) is needed"
# Python 3, by whichever name is a real one. Linux and macOS call it python3.
# The python.org installer for Windows provides only python (and py), and there
# `python3` is the Microsoft Store's stand-in, which prints "Python was not
# found" and fails - so the name existing is not enough: it has to run. Found
# cutting 0.6.0 from Hambench, where Python 3.13 is installed as `python`.
PY=""
for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 \
       && "$candidate" -c 'import sys; sys.exit(sys.version_info[0] != 3)' >/dev/null 2>&1; then
        PY=$candidate; break
    fi
done
[ -n "$PY" ] || die "Python 3 is needed (python3, or python on Windows)"
command -v dotnet >/dev/null || die "dotnet is not on PATH (DOTNET_ROOT=\$HOME/.dotnet PATH=\$HOME/.dotnet:\$PATH)"
[ -n "$NOTES" ] && [ ! -f "$NOTES" ] && die "no such notes file: $NOTES"

cd "$ROOT"
[ -z "$(git status --porcelain)" ] || must "the working tree is not clean"
git fetch -q origin
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/master)" ] || must "HEAD is not origin/master: push first, release from what is pushed"
have=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$CSPROJ" | head -1)
[ "$have" = "$VERSION" ] || must "the csproj says <Version>$have</Version>; bump it to $VERSION in a commit first"
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null && must "tag $TAG already exists here"
git ls-remote --exit-code --tags origin "refs/tags/$TAG" >/dev/null 2>&1 && must "tag $TAG already exists on origin"
gh release view "$TAG" -R "$REPO" >/dev/null 2>&1 && must "release $TAG already exists"

# PRIVACY (see CLAUDE.md). The repository is public; a release is the moment its
# state is announced to everyone. Every author and committer in the history must be
# the GitHub no-reply address, and no tracked file may carry a private LAN address
# (four-part 10.x, 192.168.x, 172.16-31.x; a Windows SDK version like 10.0.19041.0
# is not one). Both slipped once, on the day it went public.
NOREPLY="298062495+gsa700@users.noreply.github.com"
bad=$(git log HEAD --format='%ae%n%ce' | sort -u | grep -vx "$NOREPLY" || true)
[ -z "$bad" ] || die "commits by an address other than the no-reply one: $bad (rewrite them before releasing; CLAUDE.md)"
PRIVATE='\b(10(\.[0-9]{1,3}){3}|192\.168(\.[0-9]{1,3}){2}|172\.(1[6-9]|2[0-9]|3[01])(\.[0-9]{1,3}){2})\b'
if git grep -qE "$PRIVATE" -- .; then
    git grep -nE "$PRIVATE" -- . | head -5 >&2
    die "a tracked file holds a private network address (above); use the machine's name instead"
fi

LIBMPV=$(tr -d '[:space:]' < "$HERE/LIBMPV_RELEASE")
[ -n "$LIBMPV" ] || die "scripts/LIBMPV_RELEASE is empty"

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# ---- the audio engine, by tag -----------------------------------------------
say "audio engine: $LIBMPV"
mkdir -p "$WORK/libmpv"
if ! gh release download "$LIBMPV" -R "$REPO" -D "$WORK/libmpv" -p "$LIBMPV-*.zip" -p SHA256SUMS 2>/dev/null; then
    must "could not download $LIBMPV from $REPO"
    echo "   (dry run: using native/ as it is)"
    for rid in "${RIDS[@]}"; do echo "   native/$rid = $(head -1 "$ROOT/native/$rid/libmpv-build-info.txt" 2>/dev/null || echo MISSING)"; done
else
(cd "$WORK/libmpv" && sha256sum -c --quiet SHA256SUMS) || die "$LIBMPV does not match its SHA256SUMS"
for rid in "${RIDS[@]}"; do
    zip="$WORK/libmpv/$LIBMPV-$rid.zip"
    [ -f "$zip" ] || die "$LIBMPV has no $rid build"
    rm -rf "$ROOT/native/$rid"; mkdir -p "$ROOT/native/$rid"
    "$PY" -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$zip" "$ROOT/native/$rid"
    chmod 755 "$ROOT"/native/"$rid"/libmpv* 2>/dev/null || true
    printf '%s\n' "$LIBMPV" > "$ROOT/native/$rid/libmpv-source.txt"     # the stamp the build checks
    echo "   native/$rid <- $(head -1 "$ROOT/native/$rid/libmpv-build-info.txt")"
done
fi

# ---- build ------------------------------------------------------------------
for rid in "${RIDS[@]}"; do
    say "publish $rid"
    out="$WORK/pub/$rid"
    dotnet publish "$ROOT/src/AlbumWall.App" -c Release -r "$rid" -o "$out" -v quiet -nologo \
        | grep -E ' error |warning CS' | grep -v -E 'IL3000|CS0618|CS0649|CS0414' || true
    case $rid in win-*) exe=AlbumWall.exe; magic=4d5a ;; *) exe=AlbumWall; magic=7f454c46 ;; esac
    [ -f "$out/$exe" ] || die "$rid: no $exe after publish"
    head -c 4 "$out/$exe" | od -An -tx1 | tr -d ' \n' | grep -q "^$magic" || die "$rid: $exe is not a $rid executable"
    [ "$(stat -c %s "$out/$exe")" -gt 50000000 ] || die "$rid: $exe is too small to be the single-file build"
    "$PY" - "$WORK/AlbumWall-$rid.zip" "$out/$exe" <<'PY'
import sys, zipfile, os
out, exe = sys.argv[1:]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    info = zipfile.ZipInfo.from_file(exe, os.path.basename(exe))
    info.external_attr = 0o755 << 16          # executable when unpacked on Linux
    info.compress_type = zipfile.ZIP_DEFLATED
    with open(exe, "rb") as f: z.writestr(info, f.read())
PY
    echo "   AlbumWall-$rid.zip  $(du -h "$WORK/AlbumWall-$rid.zip" | cut -f1)"
done
(cd "$WORK" && sha256sum AlbumWall-*.zip > SHA256SUMS)
cat "$WORK/SHA256SUMS"

if [ $DRY -eq 1 ]; then
    mkdir -p "$ROOT/dist"; rm -f "$ROOT"/dist/AlbumWall-*.zip "$ROOT/dist/SHA256SUMS"
    cp "$WORK"/AlbumWall-*.zip "$WORK/SHA256SUMS" "$ROOT/dist/"
    echo; echo "DRY RUN: nothing tagged, nothing released. The zips are in dist/."
    exit 0
fi

# ---- tag and release --------------------------------------------------------
say "tag $TAG"
git tag -a "$TAG" -m "AlbumWall $VERSION"
git push -q origin "$TAG"

if [ -z "$NOTES" ]; then
    NOTES="$WORK/notes.md"
    prev=$(git describe --tags --abbrev=0 --match 'v[0-9]*' "$TAG^" 2>/dev/null || true)
    {
        echo "AlbumWall $VERSION. Audio engine: $LIBMPV."
        echo
        echo "Download the zip for your system, unpack it and run the program: it offers to install itself."
        echo "An installed copy updates itself from the About tab in Preferences."
        echo
        echo "Changes${prev:+ since $prev}:"
        git log --no-merges --format='- %s' ${prev:+"$prev..$TAG"} | grep -v -E '^- (Roadmap|gapless-check)' | head -60
    } > "$NOTES"
fi

ARGS=(--repo "$REPO" --title "AlbumWall $VERSION" --notes-file "$NOTES" --verify-tag)
[ $PUBLISH -eq 1 ] || ARGS+=(--draft)
gh release create "$TAG" "${ARGS[@]}" "$WORK"/AlbumWall-*.zip "$WORK/SHA256SUMS"
echo
if [ $PUBLISH -eq 1 ]; then
    echo "PUBLISHED: every installed copy will be offered $VERSION."
else
    echo "DRAFT created. Nobody is offered it until it is published:"
    echo "   gh release edit $TAG -R $REPO --draft=false"
fi
