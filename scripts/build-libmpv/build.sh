#!/usr/bin/env bash
# Builds the audio-only libmpv that ships inside the Linux releases.
#
#   scripts/build-libmpv/build.sh            native, for this machine
#   scripts/build-libmpv/build.sh win-x64    the Windows DLL, cross-compiled
#
# The result lands in native/linux-<arch>/libmpv.so.2, where the project picks
# it up and the single-file publish folds it in — the same arrangement as the
# Windows DLL, so both platforms ship one file with its player inside.
#
# WHY BUILD OUR OWN. The distribution's libmpv is 3 MB and pulls in 338 shared
# libraries, 259 MB of them: Blu-ray, CD paranoia, Vulkan, VapourSynth, Lua, a
# windowing stack. This app plays audio. Bundling that tree is how AppImages end
# up with no sound — the PipeWire and Pulse client libraries in it have to match
# the host's daemon. This build contains decoders for the formats the scanner
# accepts and nothing else, and links dynamically only against glibc and the
# audio client libraries, which are exactly the ones that must come from the
# host.
#
# It runs in a container so the result does not depend on the machine that
# built it. Debian 12 sets the floor: glibc 2.36, which covers Fedora, Debian
# 12 / Raspberry Pi OS bookworm and later, and Ubuntu 24.04. (Debian 11 would
# reach further back, but its PipeWire is too old for mpv's PipeWire output.)
#
# Needs podman (or docker: CONTAINER=docker). Run it on the architecture you
# want: x86_64 here, natively on a Pi for arm64 — the recipe is the same.
#
# This is a RARE build — only when the pinned versions in inner.sh change. The
# output is published as release assets and fetched by whoever cuts an app
# release; like the Windows DLL, it is never committed.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)

# What to build. With no argument: a native build for this machine. "win-x64"
# cross-compiles the Windows DLL from here — the same recipe with a mingw
# toolchain, which is also how mpv's own Windows builds are made.
case "${1:-native}" in
    native)
        case "$(uname -m)" in
            x86_64)  RID=linux-x64 ;;
            aarch64) RID=linux-arm64 ;;
            *) echo "unsupported architecture: $(uname -m)" >&2; exit 1 ;;
        esac
        TARGET=linux; IMAGE=debian:12 ;;
    win-x64)
        # Debian 13, not 12. The old-release choice exists to keep the LINUX
        # build's glibc floor low, which means nothing to a Windows DLL — and
        # Debian 12's mingw-w64 headers are too old for mpv 0.41, which uses
        # CREATE_WAITABLE_TIMER_HIGH_RESOLUTION and stops compiling without it.
        RID=win-x64; TARGET=win64; IMAGE=debian:13 ;;
    *)
        echo "usage: build.sh [native|win-x64]" >&2; exit 1 ;;
esac

OUT="$ROOT/native/$RID"
mkdir -p "$OUT"

"${CONTAINER:-podman}" run --rm \
    -v "$HERE:/work:ro,Z" \
    -v "$OUT:/out:Z" \
    "docker.io/library/$IMAGE" \
    bash /work/inner.sh "$TARGET"

echo
echo "Built into: $OUT"
cat "$OUT/libmpv-build-info.txt"
