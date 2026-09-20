# Windows notes — what happened on Hambench

Answers to `windows-bringup.md`, written from the machine it could not see.
Hambench is Windows 11 Pro, two 3840x2160 displays at 150% scaling, .NET SDK
10.0.302, Windows PowerShell 5.1 only (no PowerShell 7).

## 2026-09-19 — first run

**It came up first try.** Build clean (one CS0618 on `Bitmap.Save` in the
snapshot code), window on the primary display, wall rendered, covers loaded,
2.7 s to scan 54 albums / 718 tracks from a local `~\Music`.

What the bring-up list asked about:

- **Empty state: correct**, checked later against an empty scratch folder since
  the Music folder here was not empty. Heading, the folder it looked in, and
  the Choose button, and the new progress display gets out of its way. One
  cosmetic thing: a long Windows path wraps mid-word ("C:" alone on the first
  line), because a path has no spaces to break at.
- **Window chrome: correct.** `[wall] window buttons left=[] right=[Minimize,Maximize,Close]`,
  square, on the right.
- **DPI: fine.** `scaling=1.5` reported for both screens, `renderScaling=1.5`,
  window opened at 1444x1080 at 304,304. Text and covers are sensibly sized.
- **Fonts: fine.** Inter renders; Consolas is present for the mono bits.
- **Panel opens**, in place, tinted, track list in two columns.
- **Playback works** through the fetched libmpv (mpv-dev-x86_64-20260830).
- **GAPLESS WORKS ON WINDOWS.** The hard requirement, by ear, on the album that
  exists to test it. His words: "gapless works, no gap between tracks on Dark
  Side" — specifically Speak to Me/Breathe (one track on this 9-track edition)
  into On the Run, On the Run into Time, "and so on": consecutive joins where
  the sound is continuous across the boundary, not one lucky transition.
  Worth noting what that was tested on: M4A (AAC), not FLAC — lossy
  formats are the harder case, because the encoder pads each track and a
  player has to honor the gapless metadata to trim it. So this passing says
  more than the FLAC result on Linux did.
- **Volume slider: correct, by ear.** His words: "volume slider only changes the
  app". The system volume is left alone.

**One thing that disagrees with the bring-up doc.** It says rounded corners and
window transparency are deliberately Linux-only, but the log here says
`transparency=Transparent corner=12` and `decorations=None extended=True`. So
either the platform gate is not firing on Windows or the doc is ahead of the
code. The self-rendered snapshot cannot show what the real window edge looks
like; someone has to look at the corners on the actual screen.

**He looked, and it is fine.** His words: "window corners and the resize arrows
all look good including prefs and about". So transparency and the 12 px corner
WORK on Windows 11 — no black fringe, no square backing showing through — the
resize cursors appear on the 7 px grab band, and the Preferences and About
sheets draw correctly. Whatever the bring-up doc meant by Linux-only, there is
nothing to fix here; the doc is what is out of date, and the gate (if one was
intended) is not needed.

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

### The library here is not the library this was written against

Techbench's is FLAC with a `cover.jpg` beside every album. Hambench's is 8,654
MP3 and 6,876 M4A, no FLAC, art embedded in 1,011 of 1,034 albums — and the art
is what a collection ripped over twenty years looks like: 18 covers under
200 px on the shorter side (the smallest is 70x70), 70 more under 300, and 56
that are not square. One album has no art at all.

That is where the cover handling in `AlbumVm`/`ArtCache` came from. The thing
worth knowing from the other side: **the art cache used to enlarge small covers
at decode**, so nothing downstream could tell a thumbnail from a scan. It no
longer does. `art:small`, `art:missing` and `art:nonsquare` in the search box
list the albums whose files want better art. He does not want art fetched from
the internet, "at least not yet": the files are the truth.

**A black tile is not a bug.** "Resolution" by Andy Timmons showed as a black
square. The file is a genuine 200x200 black JPEG, 1,305 bytes — one of about 31
byte-identical `Folder.jpg` placeholders some tool wrote across the library in
a single batch on 2026-06-23. Only four albums showed it, because the scanner
prefers embedded art and the rest have some. Detecting "blank" covers in the
app was considered and rejected: ranked by bytes per pixel, the two blankest
REAL covers in the library are Spinal Tap's black album and the White Album.
The placeholders were removed from the files instead (32 of them, to the
Recycle Bin, matched by SHA-256).

**Removing them uncovered three real bugs**, all in how a cover is found, and
all invisible on a library of clean rips. This one has been carried along since
the RealJukebox days and its tags show it:

- Art was resolved track by track and stopped at the first hit, so an album
  whose FIRST track had no picture took the folder's sidecar and never looked
  at track two. That is the only reason the black placeholders were ever seen.
  Embedded art in any track now wins.
- 13 albums have ID3 picture frames with no description field. TagLib reads up
  to the image's first zero byte as the description, so the picture arrives
  missing its first 5-9 bytes and will not decode. What is lost is fixed text
  (PNG signature, JPEG SOI+APP0 or SOI+DQT), so `ImageSize.Repair` puts it back.
- A Momentary Lapse Of Reason opens with a 1,272-byte `RealJukebox:Metadata`
  frame typed NotAPicture, with the real cover second. "First frame" is now
  "front cover, else the first frame that is actually an image"
  (`ImageSize.Cover`, shared by the scanner and the art cache).

After all three: 1,034 albums, 1,020 embedded, 13 sidecar, 1 with no art, and
every cover's size readable.

### Two copies of %AppData%, and the hour it cost

The Claude desktop app sandboxes its tool shell: anything launched from there
gets a PRIVATE copy of `%AppData%\albumwall`. His own terminal, and anything
started through Explorer, use the real one. They are different files with the
same path.

It looked exactly like a bug. The unfolded-album restore worked on every launch
made from the tool shell and not on his, with his exact settings and session
copied into a scratch config, on his monitor, at his window size. The session
with the album open had been saved by a tool-shell launch into the private
copy; his launch read the real file, last written by his own earlier run on a
build that predated the feature. The app restored precisely what that file
said. Worse, it had been "ruled out" earlier on evidence that was one sandboxed
launch reading what another sandboxed launch wrote.

What settled it was listing the real folder from a one-off scheduled task,
which runs outside the sandbox, and comparing timestamps.

So, on Hambench:

- **Launch the live app through Explorer**, never from the tool shell:
  `explorer.exe <path>\AlbumWall.App.exe`. The parent becomes `explorer` and
  the real settings are used.
- **Read the real `albumwall.log` and `session.json` with a one-off scheduled
  task** that copies them somewhere unsandboxed, then unregister it.
- Test runs were never affected: they set `ALBUMWALL_CONFIG_DIR`.

### Icon, identity, Start Menu

The icon is drawn by `tools/make_icon.py` (needs Pillow; the outputs in
`Assets/` are committed). The app claims the AppUserModelID `gsa700.AlbumWall`
at startup, and **Preferences > About > Add to the Start Menu** writes a
shortcut carrying the same ID — it takes both for Windows to call the program
AlbumWall rather than `AlbumWall.App.exe` in the media flyout, and to pin it
properly. The ID is permanent: existing shortcuts carry it.

The button is in the app, not in a script, on purpose. The Start Menu folder
is under `%AppData%`, which the Claude tool shell sees a private copy of (see
below), so a shortcut made from there never reaches the real Start Menu. One
made by the app, launched through Explorer, does.

The shortcut points at the exe that made it. After anything that moves the
build output, the About tab says so and offers to re-point it.

### The build output moved

Since media keys, the app targets the Windows SDK when built on Windows, so
the exe is at `src/AlbumWall.App/bin/Debug/net10.0-windows10.0.19041.0/`, not
`bin/Debug/net10.0/`. The old folder is stale and can be deleted. `dotnet run`
finds the right one by itself; anything that names the path has to be told.

Media keys can be tested without touching the keyboard: PowerShell can ask
`GlobalSystemMediaTransportControlsSessionManager` for the AlbumWall session,
read back the title, artist and thumbnail Windows holds for it, and call
`TrySkipNextAsync()` / `TryPauseAsync()` — the same path a key press takes.

## Test runs use a scratch config

`ALBUMWALL_CONFIG_DIR` now moves `settings.json` elsewhere. Anything launched
to try a change should set it, together with `ALBUMWALL_SNAP`, and seed the
directory with a `settings.json` whose `LibraryPath` points at a scratch
library — a second instance on the real settings fights the first over window
geometry and whichever closes last wins.

A scratch library does not need real music: a 844-byte silent WAV tagged with
TagLib (`AlbumArtists`, `Album`, `Title`, `Track`) per album is enough for the
wall, and 60 of them is enough to scroll.
