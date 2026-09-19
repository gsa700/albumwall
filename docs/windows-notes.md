# Windows notes — what happened on Hambench

Answers to `windows-bringup.md`, written from the machine it could not see.
Hambench is Windows 11 Pro, two 3840x2160 displays at 150% scaling, .NET SDK
10.0.302, Windows PowerShell 5.1 only (no PowerShell 7).

## 2026-09-19 — first run

**It came up first try.** Build clean (one CS0618 on `Bitmap.Save` in the
snapshot code), window on the primary display, wall rendered, covers loaded,
2.7 s to scan 54 albums / 718 tracks from a local `~\Music`.

What the bring-up list asked about:

- **Empty state: not seen.** The Music folder here was not empty, so the first
  run went straight to a wall. Still untested on Windows.
- **Window chrome: correct.** `[wall] window buttons left=[] right=[Minimize,Maximize,Close]`,
  square, on the right.
- **DPI: fine.** `scaling=1.5` reported for both screens, `renderScaling=1.5`,
  window opened at 1444x1080 at 304,304. Text and covers are sensibly sized.
- **Fonts: fine.** Inter renders; Consolas is present for the mono bits.
- **Panel opens**, in place, tinted, track list in two columns.
- **Playback starts** through the fetched libmpv: the transport appears and the
  position advances. That was a test run at volume 0, so nobody has yet HEARD
  it, and **gapless across a track boundary has not been listened to** — still
  the open hard requirement. The volume slider is likewise unverified by ear.

**One thing that disagrees with the bring-up doc.** It says rounded corners and
window transparency are deliberately Linux-only, but the log here says
`transparency=Transparent corner=12` and `decorations=None extended=True`. So
either the platform gate is not firing on Windows or the doc is ahead of the
code. The self-rendered snapshot cannot show what the real window edge looks
like; someone has to look at the corners on the actual screen.

### `get-libmpv.ps1` did not run as written (fixed, a009521)

Two failures, neither visible from Fedora: `?.` is PowerShell 7 syntax and 5.1
refuses to parse the whole file; and SourceForge serves `Invoke-WebRequest` its
HTML countdown page instead of the archive unless the user agent does not look
like a browser. Details in that commit.

### Things that are different here and worth knowing

- **The trigger files work as documented**, including `trace`. Everything below
  was verified through them, without touching the window.
- **A GUI exe detaches from the shell.** `dotnet run` holds the console;
  launching `AlbumWall.App.exe` directly returns at once and stdout goes
  nowhere unless redirected (`& .\AlbumWall.App.exe | Out-File run.log`).
- **Windows lets a playing file's folder be deleted.** mpv opens with delete
  sharing, so the track plays on from a folder that is gone.
- **NTFS reports late.** Reading a freshly written file makes NTFS flush its
  timestamp, which the library watcher sees as a change — so every real change
  is followed by one extra scan that finds nothing. Harmless (the result is
  dropped), but it is a whole second scan on a large library.

### Scan times, full library (1,034 albums, 15,531 tracks, 122 GB, local disk)

- **Cold, straight after copying it in: 160 s.** Every file is a first read and
  goes through Defender on the way. Behind a bare "scanning…" this was
  reported as a hang, which is what the progress display is for.
- **Warm: 4.5 s, and 2.9 s for an immediate rescan.** So the cost is the disk
  and the virus scanner, not TagLib, and an index would buy little here once
  the OS cache is warm. A NAS-backed root is where it would matter.
- The extra NTFS-flush scan described above was NOT seen after the real first
  scan, only in the small test library. Not understood; not chased.

## Test runs use a scratch config

`ALBUMWALL_CONFIG_DIR` now moves `settings.json` elsewhere. Anything launched
to try a change should set it, together with `ALBUMWALL_SNAP`, and seed the
directory with a `settings.json` whose `LibraryPath` points at a scratch
library — a second instance on the real settings fights the first over window
geometry and whichever closes last wins.

A scratch library does not need real music: a 844-byte silent WAV tagged with
TagLib (`AlbumArtists`, `Album`, `Title`, `Track`) per album is enough for the
wall, and 60 of them is enough to scroll.
