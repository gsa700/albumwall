# What's inside AlbumWall

AlbumWall is free software under the GNU General Public License, version 3 or
later. A release is a single file, and that file carries other people's work
inside it: the audio engine, the toolkit the window is drawn with, the tag
reader, a font. This page says what each one is, what it does here, the license
it comes under, and where its source is.

Nothing listed here has been modified. Each is the upstream project's own code
at the version given, built with the options this app needs.

## The audio engine

Everything you hear goes through one library, **libmpv**, which AlbumWall builds
itself from the seven projects below so that the same engine ships on every
platform. It is an audio-only build: no video decoders, no command-line player.
The recipe is `scripts/build-libmpv/` in AlbumWall's source, and it is part of
the source of the library in the sense the licenses mean. The exact source
archives belong beside each `libmpv-*` release; see the end of this page for
where that stands.

A development build on Linux may be using your distribution's libmpv instead of
this one; the versions below are the ones a release carries.

- **mpv 0.41.0** — the player library itself: the queue, gapless playback,
  ReplayGain, the audio outputs.
  License: GNU GPL, version 2 or later.
  Source: https://github.com/mpv-player/mpv (tag `v0.41.0`)

- **FFmpeg 9.0.2** — reads the files and decodes them: FLAC, MP3, AAC, ALAC,
  Vorbis, Opus and PCM, and nothing else. Built without `--enable-gpl` and
  without `--enable-nonfree`.
  License: GNU LGPL, version 2.1 or later.
  Source: https://github.com/FFmpeg/FFmpeg (tag `n9.0.2`)

- **libplacebo 7.360.1** — a rendering library mpv will not build without. No
  picture is ever drawn with it here. It carries small third-party components
  of its own, listed in its repository.
  License: GNU LGPL, version 2.1 or later.
  Source: https://github.com/haasn/libplacebo (tag `v7.360.1`)

- **libass 0.17.5** — subtitle rendering, likewise required by mpv and unused.
  License: ISC.
  Source: https://github.com/libass/libass (tag `0.17.5`)

- **FreeType 2.14.3** — font rendering, required by libass.
  License: the FreeType License (FTL). Portions of this software are copyright
  © The FreeType Project (www.freetype.org). All rights reserved.
  Source: https://github.com/freetype/freetype (tag `VER-2-14-3`)

- **FriBidi 1.0.16** — right-to-left text layout, required by libass.
  License: GNU LGPL, version 2.1 or later.
  Source: https://github.com/fribidi/fribidi (tag `v1.0.16`)

- **HarfBuzz 14.4.0** — text shaping, required by libass.
  License: MIT ("Old MIT").
  Source: https://github.com/harfbuzz/harfbuzz (tag `14.4.0`)

## The application

- **.NET 10 runtime** — a release is self-contained, so the runtime is inside
  it.
  License: MIT.
  Source: https://github.com/dotnet/runtime

- **Avalonia 12.1.2** — the user-interface toolkit: every window, control and
  animation. Includes the Fluent theme.
  License: MIT.
  Source: https://github.com/AvaloniaUI/Avalonia

- **SkiaSharp 3.119.4 and Skia** — what Avalonia draws with.
  License: MIT (SkiaSharp); BSD 3-Clause (Skia).
  Source: https://github.com/mono/SkiaSharp and https://skia.googlesource.com/skia

- **HarfBuzzSharp 8.3.1.3** — text shaping for the interface, with its own copy
  of HarfBuzz.
  License: MIT.
  Source: https://github.com/mono/SkiaSharp

- **MicroCom 0.11.6** — how Avalonia talks to Windows.
  License: MIT.
  Source: https://github.com/kekekeks/MicroCom

- **TagLib# 2.3.0** — reads the tags and the embedded cover art out of your
  music files.
  License: GNU LGPL, version 2.1.
  Source: https://github.com/mono/taglib-sharp

- **Microsoft.Data.Sqlite 10.0.12, SQLitePCLRaw 2.1.12 and SQLite** — the
  library index (Windows only): what was read from each file, so that it need
  not be opened again.
  License: MIT (Microsoft.Data.Sqlite); Apache 2.0 (SQLitePCLRaw); SQLite itself
  is in the public domain.
  Source: https://github.com/dotnet/efcore, https://github.com/ericsink/SQLitePCL.raw
  and https://sqlite.org

- **Tmds.DBus.Protocol 0.94.1** — on Linux, how the desktop's media keys and
  "now playing" display reach the app.
  License: MIT.
  Source: https://github.com/tmds/Tmds.DBus

- **Inter** — the typeface the whole interface is set in, by Rasmus Andersson.
  License: SIL Open Font License 1.1.
  Source: https://github.com/rsms/inter

## The audio engine's source

The licenses of the audio engine give you the right to the exact source it was
built from. It is attached to the same `libmpv-*` release as the library itself:
one archive per project, taken from the tag named above, together with the
recipe in `scripts/build-libmpv/` that turns them into this library.

## The license texts

Each license named above is in the `licenses/` folder of AlbumWall's source,
exactly as its project ships it — fetched from the project's own repository by
`tools/fetch-licenses.py`, never typed or edited, and listed with its SHA-256 in
`licenses/README.md`. They are inside the program as well: in the app, they
follow this page, each behind its own fold. SQLite is in the public domain and
ships no license file.
