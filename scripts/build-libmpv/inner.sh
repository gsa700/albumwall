#!/usr/bin/env bash
# Runs INSIDE the build container. See build.sh, which is the one to run.
#
#   inner.sh linux     native build      -> /out/libmpv.so.2
#   inner.sh win64     mingw cross-build -> /out/libmpv-2.dll
#
# Builds an audio-only libmpv with everything it needs folded in statically. On
# Linux the result depends on nothing but glibc, zlib and the three audio client
# libraries; on Windows, on nothing but DLLs that are part of Windows.
#
# ONE RECIPE FOR EVERY PLATFORM is the point: the same pins, the same decoder
# list, the same switches, so the player inside the app is the same player
# everywhere. Windows is cross-compiled from here because that is how mpv's own
# Windows builds are made, and because a second recipe in a second environment
# is a second thing to keep in step by hand.
#
# WHY THE CHAIN IS THIS LONG FOR AN AUDIO PLAYER: mpv requires libass and
# libplacebo at build time unconditionally — there is no option to leave them
# out, even with video disabled — and libass in turn needs freetype, fribidi and
# harfbuzz. None of it is ever exercised here. It is built as small as each
# project allows and linked in, because the alternative is 338 shared libraries
# on Linux and a 100 MB DLL full of video codecs on Windows.
set -euo pipefail

TARGET=${1:-linux}

# ---- Pinned. Change these deliberately, together, and re-test gapless. --------
# mpv matches what Fedora 44 ships, which is what gapless was verified against.
#
# ffmpeg MUST be 9.x. On the 8.x line mov.c reads the iTunSMPB tag, takes the
# priming count from it and IGNORES the end padding, so every AAC track plays
# its encoder filler: the burp at the track change that Hambench heard on
# 2026-09-20. ffmpeg da04251772a3 ("avformat/mov: export information about the
# last actual sample in a stream", 2026-06-16) sets first/last_discard_sample
# from the tag's remainder and fixes it. It is in 9.0 and was never backported
# -- not in n8.1.2, not in n8.2-dev. tools/gapless-check.cs is the test; do not
# move this pin back to 8.x without running it.
MPV=v0.41.0
FFMPEG=n9.0.2
LIBPLACEBO=v7.360.1
LIBASS=0.17.5
FREETYPE=VER-2-14-3
FRIBIDI=v1.0.16
HARFBUZZ=14.4.0

# ---- The decoder list: exactly what the app's scanner accepts, nothing else. --
# .flac .mp3 .m4a .ogg .opus .wav. AAC is the one entry with a live patent
# question (see docs/roadmap.md, "AAC stays in the build"); dropping
# aac,aac_latm here is the conservative lever.
#
# http/tcp are here for the day the app streams from Navidrome over the LAN.
# https is NOT: it needs a TLS library, and that is a dependency to take on
# when there is something to use it.
FFMPEG_FEATURES=(
    --enable-decoder=flac,mp3,mp3float,aac,aac_latm,alac,vorbis,opus
    --enable-decoder=pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be
    --enable-decoder=pcm_f32le,pcm_f64le,pcm_u8
    --enable-demuxer=flac,mp3,mov,ogg,wav,aac
    --enable-parser=flac,mpegaudio,aac,aac_latm,vorbis,opus
    --enable-protocol=file,http,tcp
    --enable-filter=aresample,aformat,anull,atrim
    # astats: the level meters (compact view / front panel VU needles). It
    # only MEASURES, passing every sample through untouched; the kiosk still
    # has to prove that bit-exact before it goes on the stereo.
    --enable-filter=astats
)

PREFIX=/opt/min
JOBS=$(nproc)

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
    build-essential git ca-certificates pkg-config python3 python3-pip \
    python3-jinja2 ninja-build nasm autoconf automake libtool gperf >/dev/null

# Debian 12's meson is 1.0; mpv wants 1.3 or newer.
pip3 install -q --break-system-packages 'meson>=1.4'

MESON_CROSS=()
case "$TARGET" in
linux)
    apt-get install -y -qq --no-install-recommends \
        libasound2-dev libpulse-dev libpipewire-0.3-dev >/dev/null
    export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
    export CFLAGS="-O2 -fPIC" CXXFLAGS="-O2 -fPIC"
    ;;
win64)
    # The posix-threads flavor of the toolchain: its libstdc++ is complete,
    # and the final link pulls the runtime in statically, so the DLL never asks
    # for libwinpthread-1.dll or libgcc at run time.
    apt-get install -y -qq --no-install-recommends mingw-w64 >/dev/null
    T=x86_64-w64-mingw32
    export CC=$T-gcc-posix CXX=$T-g++-posix
    export CFLAGS="-O2" CXXFLAGS="-O2"
    # dlltool is listed below because libplacebo looks it up by its bare name,
    # and Debian's mingw only installs the prefixed one.
    #
    # LIBDIR, not PATH: the cross build must never see the container's own
    # Linux .pc files and quietly link against the wrong platform.
    export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig"
    cat > /tmp/cross.txt <<EOF
[binaries]
c = '$T-gcc-posix'
cpp = '$T-g++-posix'
ar = '$T-ar'
strip = '$T-strip'
windres = '$T-windres'
dlltool = '$T-dlltool'
pkg-config = 'pkg-config'

[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
EOF
    MESON_CROSS=(--cross-file /tmp/cross.txt)
    ;;
*)
    echo "unknown target: $TARGET" >&2; exit 1 ;;
esac

# Quiet when it works, loud when it does not. Every step's output goes to a
# log, and a failing step prints the end of its own log before stopping —
# meson and ninja report errors on STDOUT, so simply discarding stdout (as this
# script first did) turns every failure into a bare "exit 1".
mkdir -p /tmp/logs
run() { # label command...
    local label=$1; shift
    if ! "$@" > "/tmp/logs/$label.log" 2>&1; then
        echo "!! $label failed — last 40 lines:" >&2
        tail -40 "/tmp/logs/$label.log" >&2
        exit 1
    fi
}

fetch() { # name url tag [--recursive]
    git clone -q --depth 1 --branch "$3" ${4:-} "$2" "/src/$1" 2>/dev/null
    echo "== $1 @ $3"
}
mkdir -p /src "$PREFIX"

# auto_features=disabled is what keeps every one of these minimal: anything a
# project would switch on because it found a library is switched off instead,
# and only what is named below comes back.
MESON_COMMON=(--prefix="$PREFIX" --libdir=lib --buildtype=release
              -Ddefault_library=static -Db_staticpic=true -Dauto_features=disabled
              "${MESON_CROSS[@]}")

fetch freetype https://github.com/freetype/freetype "$FREETYPE"
run freetype-setup meson setup /src/freetype/b /src/freetype "${MESON_COMMON[@]}" -Dtests=disabled
run freetype-build ninja -C /src/freetype/b install

fetch fribidi https://github.com/fribidi/fribidi "$FRIBIDI"
run fribidi-setup meson setup /src/fribidi/b /src/fribidi "${MESON_COMMON[@]}" -Ddocs=false -Dbin=false -Dtests=false
run fribidi-build ninja -C /src/fribidi/b install

fetch harfbuzz https://github.com/harfbuzz/harfbuzz "$HARFBUZZ"
run harfbuzz-setup meson setup /src/harfbuzz/b /src/harfbuzz "${MESON_COMMON[@]}" \
    -Dtests=disabled -Ddocs=disabled -Dutilities=disabled
run harfbuzz-build ninja -C /src/harfbuzz/b install

fetch libass https://github.com/libass/libass "$LIBASS"
LIBASS_ARGS=(--prefix="$PREFIX" --enable-static --disable-shared --with-pic
             --disable-fontconfig --disable-require-system-font-provider)
[ "$TARGET" = win64 ] && LIBASS_ARGS+=(--host=x86_64-w64-mingw32 --disable-directwrite)
cd /src/libass
run libass-autogen ./autogen.sh
run libass-configure ./configure "${LIBASS_ARGS[@]}"
run libass-build make -j"$JOBS" install
cd /

fetch libplacebo https://github.com/haasn/libplacebo "$LIBPLACEBO" --recursive
run libplacebo-setup meson setup /src/libplacebo/b /src/libplacebo "${MESON_COMMON[@]}" -Ddemos=false -Dtests=false
run libplacebo-build ninja -C /src/libplacebo/b install

# libplacebo has one C++ file (number formatting, via std::to_chars), and mpv
# links as a C program, so nothing pulls the C++ runtime in. It has to be named
# AFTER libplacebo on the link line — static libraries resolve left to right —
# which rules out mpv's link args, because meson puts those first. libplacebo's
# own pkg-config entry is the one place that lands in the right order.
#
# BY FULL PATH TO THE STATIC ARCHIVE, not as -lstdc++. Given the short form,
# meson resolves it to the shared library: on Windows the DLL came out
# depending on libstdc++-6.dll, which no clean Windows machine has.
#
# ON LINUX THE SAME HOLE WAS INVISIBLE FOR A DAY. A shared library may be
# linked with symbols left undefined, so the build succeeded, and the app
# played music through it — because every .NET process already has libstdc++
# loaded, and the missing symbols were resolved by accident from there. Loaded
# into a clean process on a Pi it failed at once. A library that only works
# inside a host that happens to bring its dependencies is not self-contained;
# hence the -z defs and the clean-process load test further down.
if [ "$TARGET" = win64 ]; then
    STDCXX="$($CXX -print-file-name=libstdc++.a) $($CXX -print-file-name=libwinpthread.a)"
else
    STDCXX=$(g++ -print-file-name=libstdc++.a)
fi
sed -i "/^Libs:/ s|\$| $STDCXX|" "$PREFIX/lib/pkgconfig/libplacebo.pc"

# ffmpeg: start from nothing and add back the list at the top. mpv insists on
# all six libraries being present, so swscale and avfilter stay, empty.
fetch ffmpeg https://github.com/FFmpeg/FFmpeg "$FFMPEG"
FFMPEG_ARGS=(--prefix="$PREFIX" --enable-static --disable-shared
             --disable-everything --disable-autodetect --disable-programs --disable-doc
             --disable-avdevice --disable-debug "${FFMPEG_FEATURES[@]}")
if [ "$TARGET" = win64 ]; then
    # Windows threads, not pthreads, so ffmpeg itself never reaches for
    # winpthread either.
    FFMPEG_ARGS+=(--arch=x86_64 --target-os=mingw32 --enable-cross-compile
                  --cross-prefix=x86_64-w64-mingw32- --cc="$CC" --cxx="$CXX"
                  --pkg-config=pkg-config --enable-w32threads --disable-pthreads)
else
    FFMPEG_ARGS+=(--enable-pic)
fi
cd /src/ffmpeg
run ffmpeg-configure ./configure "${FFMPEG_ARGS[@]}"
run ffmpeg-build make -j"$JOBS" install
cd /
echo "== ffmpeg configured audio-only"

# mpv itself: the library, no player, no video output of any kind.
#
# gl is named explicitly because it is the ONE feature in mpv 0.41 that
# defaults to "enabled" rather than "auto", so auto_features does not reach it.
fetch mpv https://github.com/mpv-player/mpv "$MPV"
MPV_ARGS=(--prefix="$PREFIX" --libdir=lib --buildtype=release
          -Dauto_features=disabled -Ddefault_library=shared
          -Dlibmpv=true -Dcplayer=false -Dbuild-date=false -Dgl=disabled
          "${MESON_CROSS[@]}")
if [ "$TARGET" = win64 ]; then
    # WASAPI is the one output. -static folds the compiler runtime in; the
    # Windows system DLLs are import libraries and stay dynamic regardless.
    MPV_ARGS+=(-Dwasapi=enabled -Dwin32-threads=enabled
               -Dc_link_args=-static -Dcpp_link_args=-static)
else
    # The three audio outputs a Linux desktop might be using. These are the
    # only things the result links against dynamically, on purpose — they must
    # match the daemon on the machine it runs on, so they can never be bundled.
    #
    # -z defs makes an undefined symbol a LINK ERROR instead of something that
    # surfaces on someone else's machine.
    MPV_ARGS+=(-Dalsa=enabled -Dpulse=enabled -Dpipewire=enabled
               "-Dc_link_args=-static-libgcc -Wl,-z,defs"
               "-Dcpp_link_args=-static-libgcc -Wl,-z,defs")
fi
run mpv-setup meson setup /src/mpv/b /src/mpv "${MPV_ARGS[@]}"
run mpv-build ninja -C /src/mpv/b

if [ "$TARGET" = win64 ]; then
    LIB=$(find /src/mpv/b -maxdepth 1 -type f -name 'libmpv-2.dll' | head -1)
    x86_64-w64-mingw32-strip --strip-unneeded "$LIB"
    cp "$LIB" /out/libmpv-2.dll
    OUTFILE=/out/libmpv-2.dll
    DEPS=$(x86_64-w64-mingw32-objdump -p "$OUTFILE" | awk '/DLL Name:/ {print "  " $3}' | sort -f)

    # A DLL that imports the compiler's own runtime loads on the machine that
    # built it and nowhere else. That is invisible from here unless it is
    # checked, so it is checked: the build fails rather than shipping it.
    if echo "$DEPS" | grep -qiE 'libstdc\+\+|libgcc|libwinpthread'; then
        echo "!! the DLL imports the mingw runtime and would not load on a clean Windows:" >&2
        echo "$DEPS" | grep -iE 'libstdc\+\+|libgcc|libwinpthread' >&2
        exit 1
    fi
else
    # -type f: meson keeps its object files in a DIRECTORY named
    # libmpv.so.2.x.y.p, which sorts first and is not what strip wants.
    LIB=$(find /src/mpv/b -maxdepth 1 -type f -name 'libmpv.so.2.*' | head -1)
    strip --strip-unneeded "$LIB"
    cp "$LIB" /out/libmpv.so.2
    OUTFILE=/out/libmpv.so.2
    DEPS=$(readelf -d "$OUTFILE" | sed -nE 's/.*NEEDED.*\[(.*)\]/  \1/p' | sort)

    # The test the first version of this script lacked: load the library into a
    # process that has NOTHING else in it, binding every symbol now. python3 is
    # already here for meson. If this fails, the build fails.
    python3 -c "import ctypes, os; ctypes.CDLL('$OUTFILE', mode=os.RTLD_NOW)" \
        || { echo "!! the library does not load into a clean process" >&2; exit 1; }
    if echo "$DEPS" | grep -q 'libstdc++'; then
        echo "!! the library depends on the host's libstdc++" >&2; exit 1
    fi
fi

{
    echo "libmpv for AlbumWall — audio only, $TARGET, built $(date -u +%F)"
    echo
    echo "mpv $MPV, ffmpeg $FFMPEG, libplacebo $LIBPLACEBO, libass $LIBASS,"
    echo "freetype $FREETYPE, fribidi $FRIBIDI, harfbuzz $HARFBUZZ"
    echo
    echo "Sources: the tags above, from each project's own repository. The exact"
    echo "recipe is scripts/build-libmpv/inner.sh in the AlbumWall repository."
    echo
    echo "Links dynamically against (direct dependencies only):"
    echo "$DEPS"
} > /out/libmpv-build-info.txt

echo "== done: $(du -h "$OUTFILE" | cut -f1)"
