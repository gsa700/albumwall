#!/usr/bin/env bash
# AlbumWall - publish the audio engine: one libmpv-* pre-release, from native/.
#
#   scripts/release-libmpv.sh libmpv-0.41.0-4            a DRAFT pre-release, to check first
#   scripts/release-libmpv.sh libmpv-0.41.0-4 --publish  published at once
#
# WHAT GOES UP. The three libraries as they are in native/<rid>/ right now (built
# by scripts/build-libmpv/build.sh: x64 and win-x64 here, arm64 natively on a
# Pi), each zipped with its libmpv-build-info.txt as libmpv-<tag-version>-<rid>.zip;
# SHA256SUMS over those; the source they were built from, made by
# build-libmpv/source-archives.sh, with SHA256SUMS-sources. That last part is not
# optional: handing out the binary is what obliges us to hand out the source.
#
# A PRE-RELEASE ON PURPOSE. The app's updater reads /releases/latest, which skips
# pre-releases, so an engine release is never mistaken for an app version.
#
# BEFORE RUNNING IT. tools/gapless-check.cs must have passed on every library in
# the set (with GAPLESS_AF, now that the player filters), and on Windows ON
# WINDOWS: a cross-compiled DLL measured on Linux has been measured on a
# different file. The release notes say what was measured; they are written by
# hand, not generated (--notes-file). Then scripts/LIBMPV_RELEASE names the tag,
# and the app picks it up at its next release.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/.." && pwd)
REPO=gsa700/albumwall

TAG=${1:?usage: release-libmpv.sh libmpv-X.Y.Z-N [--publish] [--notes-file FILE]}
shift
PUBLISH=0; NOTES=""
while [ $# -gt 0 ]; do
    case "$1" in
        --publish) PUBLISH=1 ;;
        --notes-file) NOTES=${2:?--notes-file needs a file}; shift ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done

die() { echo "release-libmpv: $*" >&2; exit 1; }

[[ $TAG =~ ^libmpv-[0-9]+\.[0-9]+\.[0-9]+-[0-9]+$ ]] || die "tag must look like libmpv-0.41.0-4, not $TAG"
command -v gh >/dev/null || die "the GitHub CLI (gh) is needed"
gh release view "$TAG" -R "$REPO" >/dev/null 2>&1 && die "$TAG is already released on $REPO"
[ -n "$NOTES" ] && [ ! -f "$NOTES" ] && die "no such notes file: $NOTES"

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
VERSION=${TAG#libmpv-}

# Every library in the set, present, and carrying astats: the app's level meters
# need it, and since libmpv-0.41.0-4 a build without it is a build from the wrong
# recipe (the app would play, but with no meters).
for rid in linux-x64 linux-arm64 win-x64; do
    dir="$ROOT/native/$rid"
    case $rid in win-*) lib=libmpv-2.dll ;; *) lib=libmpv.so.2 ;; esac
    [ -f "$dir/$lib" ] || die "missing $dir/$lib"
    [ -f "$dir/libmpv-build-info.txt" ] || die "missing $dir/libmpv-build-info.txt"
    grep -aq 'Show time domain statistics about audio frames' "$dir/$lib" \
        || die "$rid: $lib has no astats filter (built from an old recipe?)"
    python3 - "$WORK/libmpv-$VERSION-$rid.zip" "$dir/$lib" "$dir/libmpv-build-info.txt" <<'PY'
import sys, zipfile, os
out, *files = sys.argv[1:]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for f in files:
        info = zipfile.ZipInfo.from_file(f, os.path.basename(f))
        info.external_attr = (0o755 if f.endswith((".so.2", ".dll")) else 0o644) << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        with open(f, "rb") as src: z.writestr(info, src.read())
PY
    echo "  $rid: $(head -1 "$dir/libmpv-build-info.txt")"
done
(cd "$WORK" && sha256sum libmpv-*.zip > SHA256SUMS)

echo "source archives (this takes a while: it clones every pinned project)"
mkdir -p "$WORK/src"
"$ROOT/scripts/build-libmpv/source-archives.sh" "$WORK/src"
SOURCES=("$WORK"/src/*)

ARGS=(--repo "$REPO" --prerelease --title "$TAG (audio-only libmpv, all three platforms)")
[ -n "$NOTES" ] && ARGS+=(--notes-file "$NOTES") || ARGS+=(--notes "Audio-only libmpv builds for AlbumWall. Notes to be written before publishing.")
[ $PUBLISH -eq 1 ] || ARGS+=(--draft)
gh release create "$TAG" "${ARGS[@]}" "$WORK"/libmpv-*.zip "$WORK/SHA256SUMS" "${SOURCES[@]}"
echo
[ $PUBLISH -eq 1 ] && echo "published: https://github.com/$REPO/releases/tag/$TAG" \
                   || echo "DRAFT created; check it${NOTES:+ (notes from $NOTES)}${NOTES:- and write the notes}, then publish from the page or with: gh release edit $TAG -R $REPO --draft=false"
