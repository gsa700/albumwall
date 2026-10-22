# What's inside AlbumWall

AlbumWall is free software under the GNU General Public License, version 3 or
later. A release is a single file, and that file carries other people's work
inside it: parts of the audio engine's decoders, the toolkit the window is
drawn with, the tag reader, a font. This page says what each one is, what it does here, the license
it comes under, and where its source is.

Nothing listed here has been modified, but for the decoders under Hymie, which
are ported. Each is the upstream project's own code at the version given.

## Hymie, AlbumWall's own audio engine

Everything you hear goes through AlbumWall's own engine, Hymie, with its own
decoders written in C#: FLAC, MP3, AAC, ALAC and WAV, and an output for each
platform (CoreAudio on the Mac, WASAPI on Windows, PulseAudio or PipeWire on
Linux, through the system's own libraries). Until 0.7.0 it was libmpv, with
FFmpeg inside. Some decoders are ported from someone else's work, and so are
the exception to "nothing here has been modified": each is the same decoder,
rewritten function for function in another language.

- **minimp3** (commit `ea99364`), by lieff — the MP3 decoder's core: Layer III,
  ported to C# with its tables copied value for value.
  License: CC0 1.0 Universal (dedicated to the public domain).
  Source: https://github.com/lieff/minimp3

- **FAAD2** (commit `521d105`) — the AAC decoder's core: its AAC-LC path, ported
  to C# with its tables copied value for value. Code from FAAD2 is copyright
  (c) Nero AG, www.nero.com.
  License: GNU GPL, version 2 or later.
  Source: https://github.com/knik0/faad2

- **Apple Lossless (ALAC)** (commit `c38887c`), by Apple — the ALAC decoder's
  core: its adaptive Golomb decoder, dynamic predictor, stereo unmix and
  channel elements, ported to C# (src/AlbumWall.Codecs/Alac.cs, which says
  what was changed). Copyright (c) 2011 Apple Inc.
  License: Apache License 2.0.
  Source: https://github.com/macosforge/alac

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

- **FreeType** — font rendering, inside Skia's Linux build.
  License: the FreeType License (FTL). Portions of this software are copyright
  © The FreeType Project (www.freetype.org). All rights reserved.
  Source: https://github.com/freetype/freetype

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

## The license texts

Each license named above is in the `licenses/` folder of AlbumWall's source,
exactly as its project ships it — fetched from the project's own repository by
`tools/fetch-licenses.py`, never typed or edited, and listed with its SHA-256 in
`licenses/README.md`. They are inside the program as well: in the app, they
follow this page, each behind its own fold. SQLite is in the public domain and
ships no license file.
