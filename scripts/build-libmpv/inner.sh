#!/usr/bin/env bash
# Runs INSIDE the build container. See build.sh, which is the one to run.
#
# Builds an audio-only libmpv with everything it needs folded in statically, so
# the result depends on nothing but glibc and the three audio client libraries.
#
# WHY THE CHAIN IS THIS LONG FOR AN AUDIO PLAYER: mpv requires libass and
# libplacebo at build time unconditionally — there is no option to leave them
# out, even with video disabled — and libass in turn needs freetype, fribidi and
# harfbuzz. None of it is ever exercised here. It is built as small as each
# project allows and linked in, because the alternative is 338 shared libraries.
set -euo pipefail

# ---- Pinned. Change these deliberately, together, and re-test gapless. --------
# mpv matches what Fedora 44 ships, which is what gapless was verified against.
# ffmpeg stays on the 8.x line: mpv 0.41 predates ffmpeg 9 and its API removals.
MPV=v0.41.0
FFMPEG=n8.1.2
LIBPLACEBO=v7.360.1
LIBASS=0.17.5
FREETYPE=VER-2-14-3
FRIBIDI=v1.0.16
HARFBUZZ=14.4.0

PREFIX=/opt/min
JOBS=$(nproc)
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
export CFLAGS="-O2 -fPIC"
export CXXFLAGS="-O2 -fPIC"

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
    build-essential git ca-certificates pkg-config python3 python3-pip \
    python3-jinja2 ninja-build nasm autoconf automake libtool gperf \
    libasound2-dev libpulse-dev libpipewire-0.3-dev >/dev/null

# Debian 12's meson is 1.0; mpv wants 1.3 or newer.
pip3 install -q --break-system-packages 'meson>=1.4'

fetch() { # name url tag [--recursive]
    git clone -q --depth 1 --branch "$3" ${4:-} "$2" "/src/$1"
    echo "== $1 @ $3"
}
mkdir -p /src "$PREFIX"

# auto_features=disabled is what keeps every one of these minimal: anything a
# project would switch on because it found a library is switched off instead,
# and only what is named below comes back.
MESON_COMMON="--prefix=$PREFIX --libdir=lib --buildtype=release
              -Ddefault_library=static -Db_staticpic=true -Dauto_features=disabled"

fetch freetype https://github.com/freetype/freetype "$FREETYPE"
meson setup /src/freetype/b /src/freetype $MESON_COMMON -Dtests=disabled >/dev/null
ninja -C /src/freetype/b install >/dev/null

fetch fribidi https://github.com/fribidi/fribidi "$FRIBIDI"
meson setup /src/fribidi/b /src/fribidi $MESON_COMMON -Ddocs=false -Dbin=false -Dtests=false >/dev/null
ninja -C /src/fribidi/b install >/dev/null

fetch harfbuzz https://github.com/harfbuzz/harfbuzz "$HARFBUZZ"
meson setup /src/harfbuzz/b /src/harfbuzz $MESON_COMMON \
    -Dtests=disabled -Ddocs=disabled -Dutilities=disabled >/dev/null
ninja -C /src/harfbuzz/b install >/dev/null

fetch libass https://github.com/libass/libass "$LIBASS"
( cd /src/libass && ./autogen.sh >/dev/null 2>&1 &&
  ./configure --prefix="$PREFIX" --enable-static --disable-shared --with-pic \
      --disable-fontconfig --disable-require-system-font-provider >/dev/null &&
  make -j"$JOBS" install >/dev/null )

fetch libplacebo https://github.com/haasn/libplacebo "$LIBPLACEBO" --recursive
meson setup /src/libplacebo/b /src/libplacebo $MESON_COMMON -Ddemos=false -Dtests=false >/dev/null
ninja -C /src/libplacebo/b install >/dev/null

# ffmpeg: start from nothing and add back exactly what the app's scanner
# accepts — .flac .mp3 .m4a .ogg .opus .wav — and nothing else. mpv insists on
# all six libraries being present, so swscale and avfilter stay, empty.
#
# http/tcp are here for the day the app streams from Navidrome over the LAN.
# https is NOT: it needs a TLS library, and that is a dependency to take on
# when there is something to use it.
fetch ffmpeg https://github.com/FFmpeg/FFmpeg "$FFMPEG"
( cd /src/ffmpeg && ./configure --prefix="$PREFIX" \
    --enable-pic --enable-static --disable-shared \
    --disable-everything --disable-autodetect --disable-programs --disable-doc \
    --disable-avdevice --disable-debug \
    --enable-decoder=flac,mp3,mp3float,aac,aac_latm,alac,vorbis,opus \
    --enable-decoder=pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be \
    --enable-decoder=pcm_f32le,pcm_f64le,pcm_u8 \
    --enable-demuxer=flac,mp3,mov,ogg,wav,aac \
    --enable-parser=flac,mpegaudio,aac,aac_latm,vorbis,opus \
    --enable-protocol=file,http,tcp \
    --enable-filter=aresample,aformat,anull,atrim >/dev/null &&
  make -j"$JOBS" install >/dev/null )
echo "== ffmpeg configured audio-only"

# mpv itself: the library, no player, no video output of any kind, and the
# three audio outputs a Linux desktop might be using. These three are the only
# things the result links against dynamically, on purpose — they must match
# the daemon on the machine it runs on, so they can never be bundled.
#
# gl is named explicitly because it is the ONE feature in mpv 0.41 that
# defaults to "enabled" rather than "auto", so auto_features does not reach it.
fetch mpv https://github.com/mpv-player/mpv "$MPV"
meson setup /src/mpv/b /src/mpv --prefix="$PREFIX" --libdir=lib --buildtype=release \
    -Dauto_features=disabled -Ddefault_library=shared \
    -Dlibmpv=true -Dcplayer=false -Dbuild-date=false -Dgl=disabled \
    -Dalsa=enabled -Dpulse=enabled -Dpipewire=enabled \
    -Dc_link_args="-static-libgcc -static-libstdc++" \
    -Dcpp_link_args="-static-libgcc -static-libstdc++"
ninja -C /src/mpv/b

# -type f: meson keeps its object files in a DIRECTORY named libmpv.so.2.x.y.p,
# which sorts first and is not what strip wants to be handed.
LIB=$(find /src/mpv/b -maxdepth 1 -type f -name 'libmpv.so.2.*' | head -1)
strip --strip-unneeded "$LIB"
cp "$LIB" /out/libmpv.so.2

{
    echo "libmpv for AlbumWall — audio only, built $(date -u +%F)"
    echo
    echo "mpv $MPV, ffmpeg $FFMPEG, libplacebo $LIBPLACEBO, libass $LIBASS,"
    echo "freetype $FREETYPE, fribidi $FRIBIDI, harfbuzz $HARFBUZZ"
    echo
    echo "Sources: the tags above, from each project's own repository. The exact"
    echo "recipe is scripts/build-libmpv/inner.sh in the AlbumWall repository."
    echo
    echo "Links dynamically against:"
    ldd /out/libmpv.so.2 | awk '{print "  " $1}' | sort
} > /out/libmpv-build-info.txt

echo "== done: $(du -h /out/libmpv.so.2 | cut -f1)"
