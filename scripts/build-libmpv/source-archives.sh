#!/usr/bin/env bash
# AlbumWall - the source the audio engine was built from, as archives to attach to
# the same release as the library.
#
#   scripts/build-libmpv/source-archives.sh <output directory>
#
# WHY. mpv is GPL and FFmpeg, libplacebo and FriBidi are LGPL, and those licenses
# give whoever receives the library the right to the exact source it was built
# from. That right is triggered by handing someone the BINARY - it has nothing to
# do with whether the source was changed, and ours is not. Pointing at upstream's
# tags is what most people do, but the one who distributes is the one responsible
# and upstream can move a tag or disappear; the licenses accept, in so many words,
# source offered from the same place as the binary. So: one archive per project,
# beside the DLL and the .so files on the libmpv-* release.
#
# WHAT IS IN THEM. Exactly what inner.sh clones: the same repositories, the same
# pins (read out of inner.sh, not repeated here), libplacebo with its submodules
# because the recipe clones it --recursive and the others without. The recipe
# itself - build.sh, inner.sh, this file - goes in an archive of its own, because
# "the scripts used to control compilation" are part of the source too.
#
# HOW. `git archive`, never a tar of a checked-out tree: it reads the objects, so
# a symlink stays a symlink whatever the platform made of it on checkout. BUT IT
# IS NOT IMMUNE TO THE PLATFORM, which the first version of this comment claimed.
# `git archive` runs file contents through the same end-of-line conversion a
# checkout does, so on a Windows box with core.autocrlf=true - Hambench - every
# text file came out CRLF: each of the seven archives differed from upstream in
# thousands of files, and looked fine. It was caught by comparing the license file
# inside each archive with the copy tools/fetch-licenses.py had fetched from the
# same tag by another road. Hence two things below: every `git archive` runs with
# autocrlf off, and that comparison is now part of this script, so a machine whose
# git rewrites the source fails here instead of publishing it. gzip runs with -n,
# so the same tag gives the same bytes on any machine on any day.

set -euo pipefail

OUT="${1:?usage: source-archives.sh <output directory>}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LICENSES="$HERE/../../licenses"

# Bytes as committed, whatever this machine's git is configured to do to text.
archive() { git -c core.autocrlf=false -c core.eol=lf -C "$1" archive --format=tar "${@:2}"; }
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

pin() { sed -n "s/^$1=\([^[:space:]#]*\).*/\1/p" "$HERE/inner.sh" | head -1; }

# name, repository, pin, recursive?, then the check: a file inside the archive and
# the copy of it in licenses/, fetched from the same tag over HTTPS. They must be
# byte for byte the same.
PROJECTS=(
    "mpv        https://github.com/mpv-player/mpv     $(pin MPV)         no   LICENSE.GPL       mpv-LICENSE.GPL.txt"
    "ffmpeg     https://github.com/FFmpeg/FFmpeg      $(pin FFMPEG)      no   COPYING.LGPLv2.1  ffmpeg-COPYING.LGPLv2.1.txt"
    "libplacebo https://github.com/haasn/libplacebo   $(pin LIBPLACEBO)  yes  LICENSE           libplacebo-LICENSE.txt"
    "libass     https://github.com/libass/libass      $(pin LIBASS)      no   COPYING           libass-COPYING.txt"
    "freetype   https://github.com/freetype/freetype  $(pin FREETYPE)    no   docs/FTL.TXT      freetype-FTL.TXT"
    "fribidi    https://github.com/fribidi/fribidi    $(pin FRIBIDI)     no   COPYING           fribidi-COPYING.txt"
    "harfbuzz   https://github.com/harfbuzz/harfbuzz  $(pin HARFBUZZ)    no   COPYING           harfbuzz-COPYING.txt"
    "mbedtls    https://github.com/Mbed-TLS/mbedtls    $(pin MBEDTLS)     yes  LICENSE           mbedtls-LICENSE.txt"
)

for line in "${PROJECTS[@]}"; do
    read -r name url tag recursive inside fetched <<<"$line"
    [ -n "$tag" ] || { echo "no pin for $name in inner.sh" >&2; exit 1; }

    echo "== $name $tag"
    src="$WORK/$name"
    extra=()
    [ "$recursive" = yes ] && extra=(--recursive --shallow-submodules)
    git -c advice.detachedHead=false clone -q --depth 1 --branch "$tag" "${extra[@]}" "$url" "$src"

    prefix="$name-$tag"
    tar="$WORK/$prefix.tar"
    archive "$src" --prefix="$prefix/" HEAD > "$tar"

    if [ "$recursive" = yes ]; then
        # Each submodule is a repository of its own; archive it under the path it
        # occupies in its parent and join the tars.
        git -C "$src" submodule foreach --recursive --quiet 'echo "$displaypath"' | while read -r sub; do
            [ -n "$sub" ] || continue
            echo "   + $sub"
            archive "$src/$sub" --prefix="$prefix/$sub/" HEAD > "$WORK/sub.tar"
            tar --concatenate --file="$tar" "$WORK/sub.tar"
        done
    fi

    gzip -9 -n -c "$tar" > "$OUT/$prefix.tar.gz"
    rm -rf "$src" "$tar"

    # Is what went in what upstream ships? See HOW, above.
    if [ -f "$LICENSES/$fetched" ]; then
        if tar -xzOf "$OUT/$prefix.tar.gz" "$prefix/$inside" | cmp -s - "$LICENSES/$fetched"; then
            echo "   verified: $inside is byte for byte licenses/$fetched"
        else
            echo "   NOT FAITHFUL: $inside in the archive differs from licenses/$fetched" >&2
            echo "   (line endings? check this machine's core.autocrlf)" >&2
            exit 1
        fi
    else
        echo "   not verified: licenses/$fetched is missing - run tools/fetch-licenses.py first" >&2
        exit 1
    fi
done

# The recipe. From the repository's own objects when there is a commit to take it
# from, so it is the committed recipe and not whatever is lying in the folder.
echo "== the recipe"
repo="$(git -C "$HERE" rev-parse --show-toplevel)"
archive "$repo" --prefix="albumwall-build-libmpv/" HEAD:scripts/build-libmpv | gzip -9 -n > "$OUT/albumwall-build-libmpv.tar.gz"

( cd "$OUT" && sha256sum ./*.tar.gz | sed 's| \./| |' > SHA256SUMS-sources )
echo
ls -l "$OUT"
