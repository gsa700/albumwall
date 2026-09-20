# AlbumWall

A player for a music library you own, built around seeing the whole collection
at once.

The library is a wall of cover art. Clicking a record unfolds it in place —
the wall never goes away, the album opens inside it, tinted by its own sleeve —
and the sleeve is the play button. It is meant to feel like flipping through a
bin of CDs looking for something to listen to, which is the part most players
get wrong.

Early, and built for one person's library first. It works.

## Running it

Needs the **.NET 10 SDK** and **libmpv**. Everything else comes from NuGet.

**Linux**

```sh
sudo dnf install mpv-libs          # Fedora
sudo apt install libmpv2           # Debian/Ubuntu

dotnet run --project src/AlbumWall.App
```

**Windows**

```powershell
.\scripts\get-libmpv.ps1           # once: fetches libmpv-2.dll (needs `gh auth login` while this repository is private)
dotnet run --project src\AlbumWall.App
```

libmpv is not committed to this repository. It is a large binary from another
GPL project, and vendoring it would mean shipping their source alongside ours;
the script fetches our own audio-only build from this repository's releases
instead (`-Upstream` fetches the official full build from SourceForge). The project copies it next to
the executable when you build.

## Installing

A release is one file. Run it from wherever you unzipped it and it offers to
install itself — per user, no administrator rights: into
`%LocalAppData%\Programs\AlbumWall` on Windows (listed in Installed apps, with a
Start Menu shortcut), or `~/.local/share/albumwall` on Linux (in your
applications menu, plus an `albumwall` command). `--install` and `--uninstall`
do the same from a terminal; add `--quiet` for scripts. Uninstalling keeps your
settings unless you say otherwise, and never touches your music.

To run it from a folder or a USB stick and leave no trace on the machine, put
an empty file named `portable.txt` beside it.

## Your music

It scans your **Music** folder — `~/Music`, or `%USERPROFILE%\Music` — and
everything under it. If your records live somewhere else, open the menu in the
top right and set the folder in Preferences.

Reads FLAC, MP3, M4A, Ogg, Opus and WAV. Album art comes from a `cover.jpg`
beside the tracks, or from the tags if there is no file.

Albums are grouped by **album artist and album title**, and the year is taken
from a `YEAR - Album` folder name where there is one, so a box set does not
scatter itself across a decade.

## What it does

- A virtualized wall of cover art that scrolls to any size of library
- Albums open in place, centered, colored from their own artwork
- Gapless playback, native sample rate, ReplayGain (album or track)
- Media keys, and the GNOME panel and lock screen controls, over MPRIS
- Live search, and a way back to whatever is playing
- The palette is derived from your own covers — the ground takes the average
  hue of the collection, and the controls in the top bar tune how light it sits

Not built yet: tag editing, an A–Z rail, anything resembling a library database.

## License

GPL v3 or later — see [LICENSE](LICENSE).

Audio is [libmpv](https://mpv.io) (GPL v2 or later), tags are
[TagLib#](https://github.com/mono/taglib-sharp) (LGPL), the interface is
[Avalonia](https://avaloniaui.net) (MIT).
