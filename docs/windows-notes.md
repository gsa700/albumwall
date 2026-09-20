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
- **"Warm" does not last the night, so the conclusion above was wrong.** First
  launch on 2026-09-20, same library, machine not rebooted since the 14th:
  **158 s** again, against 4.2 s at 01:01 that morning. During it AlbumWall
  used 16 s of CPU in total, the NVMe sat at 4% busy, and Defender (MsMpEng)
  burned one full core in lockstep with the file opens. So the cost is one
  virus scan per file opened, it comes back whenever Defender's and the OS's
  caches have been turned over, and in daily use that means the first launch
  of the day — reported as "it's taking forever to scan". Listing the folder
  costs nothing; opening 15,531 music files is what costs.
- **It is not even per-day: it is per-program.** Two hours after that launch, a
  different exe (the check tool below) scanning the same files took **191 s**,
  and 4 s on its second pass. Whatever Defender remembers, it did not carry
  over from AlbumWall having just read every one of them.
- **So the index (spec §6) is built: `LibraryIndex`, `index.db` beside the
  settings.** A file whose path, size and modified time it recognizes is not
  opened. Same library, second launch: **166 ms, 0 files opened** (71 ms of
  that is loading 15,531 rows), against 6.2 s with everything warm and 158 s
  cold. 3.9 MB on disk. `tools/index-check.cs` scans four ways and requires
  them to agree; on the real library they do.

### The index: what it trusts, and what that can miss (for Techbench to weigh)

Spec §6 rule 1 says never to detect changes by mtime alone and to keep a
`tag_hash`. **The index keeps path, size and modified time and NO tag hash**,
on purpose: a hash can only be checked by opening the file, and opening the
file is the entire cost on Windows. What stands in for it:

- **While the app is open, the watcher names every music or art file it sees
  an event for, and the index forgets that file** (`Touch`) whatever its size
  and time say afterwards. Tested live: a same-length title edit with the
  modified time put back was picked up — "1 files opened". The watcher still
  decides nothing about the album list; this only ever makes a scan MORE
  honest, and a dropped event leaves that file judged by size and time.
- **Rescan is the honest scan (rule 2):** every file opened, nothing in the
  index believed, the index rebuilt from the result. Startup, a new folder and
  the watcher trust the index; only Rescan does not. On Windows that is
  minutes, which is why nothing automatic may ask for it — and why it **left
  the main menu**, where it sat one slip below Preferences from the days when
  it cost four seconds. It is now "Read everything again" on the Library tab
  of Preferences, under a paragraph that says what it does, how many files,
  why it is slow, and when you would want it. His call: "move the rescan from
  the hamburger menu into the prefs proper with a warning about what is
  actually going to happen". The button becomes **Stop** while it runs;
  stopping leaves the wall as it was and keeps what had been re-read. The tab
  also shows a progress bar for ANY scan that lasts more than 0.6 s, since
  Preferences is usually covering the main window's own. (`rescan` and
  `rescan stop` are the trigger commands.)
- **Deleting `index.db` is boring (rule 5).** So is a corrupt one or one from
  another `Version`: discarded, rebuilt by the next scan. Uninstall removes it
  whatever was chosen about settings. Nothing the user makes lives in it.
- **THE GAP THAT IS LEFT: an edit that preserves size AND modified time, made
  while the app is CLOSED** — `metaflac --preserve-modtime` into existing
  padding is exactly that — is not seen until Rescan. `index-check.cs … edit`
  shows it ("trusting scan … sees the edit: False"). That is the very failure
  §6 was written about, softened to "until Rescan" rather than "forever", and
  it matters more on Techbench, where that flag is in daily use, than here.
  What would close it is the inode change time (`st_ctime` / NTFS ChangeTime),
  which no tool can put back; .NET exposes neither. Options for Techbench:
  P/Invoke `statx` on Linux and add ctime to the key there, or have the app
  not trust the index on Linux at all, where opening files is cheap and only a
  NAS-backed root would miss it. ~~Not decided here — it can't be tested here.~~

  **DECIDED ON TECHBENCH 2026-09-20, his call: the index is not trusted on
  Linux.** `MainWindow.TrustsIndex` is `OperatingSystem.IsWindows()`. The
  measurement that settled it, same app, same code, each machine's own library:

  |            | every file opened | index trusted |
  |---|---|---|
  | Linux, 3,337 files   | ~190 ms      | ~56 ms  |
  | Windows, 15,531 files| ~158,000 ms  | ~166 ms |

  The index is the difference between usable and unusable on Windows and worth
  about an eighth of a second on Linux, where it would be paid for in the one
  currency that library cannot spare. **Nothing about Windows changes.** `statx`
  is held in reserve for a NAS-backed root, which is the only case where the
  Linux saving would be large enough to earn a hand-rolled P/Invoke; growth
  alone does not trigger it, since five times that library is still about a
  second. Proved end to end on a `--preserve-modtime` edit with the file's size
  and mtime identical afterwards: trusting, the second launch opened 0 files and
  still showed the old title; not trusting, it opened all 3 and showed the new
  one.
- **A trap for whoever next changes the scanner:** an indexed file is never
  re-read, so a new fallback rule or `ImageSize.Repair` shape reaches no file
  already indexed until `LibraryIndex.Version` is bumped. Both files say so at
  the spot; `index-check.cs` is the check.
- Found by the first test of `Touch`: the scanner's paths come through
  `DirectoryInfo`, which expands 8.3 names, and the watcher's do not — so a
  root spelled `C:\Users\DAVIDE~1\…` touched nothing. `Touch` now normalizes,
  and paths compare case-insensitively on Windows.
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

## 2026-09-20 — v0.1.0-test3 on Hambench: everything works except gapless

Downloaded `AlbumWall-win-x64.zip` from the pre-release, SHA-256 matching
`SHA256SUMS`, unzipped to `%USERPROFILE%/AlbumWall-test`, launched through
Explorer. Read from OUTSIDE the tool shell's sandbox (scheduled task):

- **The single-file exe starts**, and the audio-only DLL self-extracts and
  loads: the process has `libmpv-2.dll` at **9.3 MB** from
  `%LocalAppData%/Temp/.net/AlbumWall/<hash>/` (the upstream one was 114.8 MB).
  Note `[mpv] library:` is not logged on Windows, so the log alone cannot show
  this; the process's module list does.
- **The installer's Windows half works**, first time. Loose copy at 00:46:52
  logged `mode is Loose`, he accepted the offer, `install: ok — reg import exit
  0; start menu ok; desktop shortcut created` five seconds later, relaunched
  from `%LocalAppData%/Programs/AlbumWall/AlbumWall.exe` at 00:46:58 with
  `startup: ok`. His word for it: "fb" (fine business).
- **The Installed-apps entry is REAL** — read back from the real HKCU
  uninstall key by a process outside the sandbox: AlbumWall 0.1.0, publisher
  "David Erickson (AB0R)", icon, location, both uninstall strings. The one
  `reg import` did what the family's rule promised; no PCA overlay ate it.
- Start Menu and Desktop shortcuts created. Session restore and media keys
  fine. **MP3 and M4A both play.** The new icon "looks good".
- NOT yet tested: uninstall (both forms), the taskbar pin grouping, the About
  tab in installed mode.

### GAPLESS FAILS with our libmpv on AAC — measured, and the cause is known

His report: "gapless has a barely audible burp right after the track change".
The upstream DLL was seamless on the same files a day earlier.

Measured with `tools/gapless-check.cs` (new — see its header), which decodes
tracks back to back through a given libmpv into a WAV at full speed and counts
samples. Dark Side, On the Run -> Time -> The Great Gig in the Sky:

| libmpv | versions | samples over what the files contain |
|---|---|---|
| upstream (SourceForge) | mpv 0.41.0-1012 git, ffmpeg N-126314 git | **+6** (0.1 ms, inaudible) |
| ours, libmpv-0.41.0-2 | mpv 0.41.0, ffmpeg n8.1.2 | **+1,026** |

1,020 = 676 + 336 + 8, which are EXACTLY the three files' end padding as their
`iTunSMPB` tags state it. Cross-correlating the two decodes: track 1 is
sample-identical and aligned; track 2 starts 676 samples late in ours. So our
build trims each track's encoder delay (2,112) and **plays its end padding**:
15 ms of encoder filler before every track change. That is the burp.

- **AAC only.** An MP3 pair decodes sample-identical through both.
- **It is the versions, not the trimmed feature list.** 381 of 400 sampled
  M4A files here carry gapless info ONLY as `iTunSMPB`, with no MP4 edit list
  (normal for iTunes rips; 14 had an edit list, 5 neither). ffmpeg n8.1.2's mov
  demuxer takes the priming count from that tag and ignores the remainder.
  Something between there and git master honors it. Candidates, unverified:
  ffmpeg `bfcf9fcb37` "avformat: factor out iTunSMPB parsing" (2026-08-21,
  touches mov.c), which the upstream build postdates by nine days — or a change
  on the mpv side. Only a rebuild can say which.
- **Why Linux did not catch it:** the library there is FLAC, which has no
  padding and is gapless in anything that does not stop between files. It was
  never a test of trimming. Test with iTunes AAC.

**What the Fedora side needs to do:** move the recipe's pins forward (ffmpeg
first) and run `dotnet run tools/gapless-check.cs -- <the new .so or .dll>
<two or three iTunes .m4a tracks>` until it says PASS. It needs no ears, no
Windows and half a second. Until then test builds are not gapless on AAC, and
the development build with the upstream DLL in `native/win-x64` still is —
so `get-libmpv.ps1` stays pointed at SourceForge for now.

#### FIXED THE SAME DAY on Fedora: the pin is ffmpeg 9

`FFMPEG=n9.0.2`. The guess above was close but named the wrong commit. The one
that matters is **`da04251772a3`, "avformat/mov: export information about the
last actual sample in a stream" (2026-06-16)**: it is what sets
`first_discard_sample` / `last_discard_sample` from the iTunSMPB *remainder*,
and its diff updates exactly the FATE refs you would expect —
`gaplessinfo-itunes1/2` and `gaplessenc-itunes-to-ipod-aac`. `bfcf9fcb37`, the
commit these notes guessed at, is a later refactor that only moves the parsing
into a helper `mp3dec` can share; it is not in 9.0.x at all.

**The fix is in 9.0 and was NEVER backported** — not in `n8.1.1`, `n8.1.2`,
`n8.2-dev` or `release/8.1`, all four checked. Side by side, n8.1.2 sets
`sc->start_pad = priming` and does nothing whatever with `remainder`; 9.0 adds
the discard-sample block. So there was no way to stay on the 8.x line short of
carrying a patch.

**The recipe's warning that "mpv 0.41 predates ffmpeg 9 and its API removals"
was wrong** — or at least not true of an audio-only build. mpv `v0.41.0`
compiles clean against `n9.0.2`; its meson only ever states a MINIMUM libav*
version and mpv has landed no "support ffmpeg 9" commit since 0.41.0. Only the
pin moved. Same dependency list, 8.3 MB -> 8.5 MB.

Measured on Techbench with the same three Dark Side tracks, copied from the
Navidrome library on NASBOX (`NASBOX:/NAS/data/MusicFolder`) — the same rip,
the same `676 + 336 + 8` end padding this page reports from Hambench:

| libmpv | versions | result |
|---|---|---|
| `libmpv-0.41.0-2` | mpv 0.41.0, ffmpeg n8.1.2 | **+1,026 samples — FAIL** |
| rebuilt | mpv 0.41.0, ffmpeg **n9.0.2** | **+6 samples — PASS** |

**+6 is the identical number the upstream SourceForge DLL scored**, which is
the strongest form the check can give: our build now agrees with a known-good
one to a tenth of a millisecond. FLAC is unaffected — both libraries decode the
same three FLAC tracks to 37,274,502 samples exactly.

**linux-arm64 was rebuilt the same day**, natively on the Pi 5 at Pi5-POE
(qemu binfmt is not set up on Techbench), and measures **PASS at +6 samples
too — the identical 40,874,826 total the x64 library produced.** All three
platforms are published as **`libmpv-0.41.0-3`** (pre-release, as always).

**What is left is on Hambench:** the Windows DLL is rebuilt from the same pins
but nothing on the Linux side can run it, so `gapless-check` has to be run
there once more against `libmpv-0.41.0-3-win-x64.zip`. When it passes,
`get-libmpv.ps1` can finally point at our release instead of SourceForge.

**Run on Hambench the same afternoon: the Windows DLL PASSES.** Downloaded from
the release, SHA-256 matching `SHA256SUMS`, and measured on the same three Dark
Side tracks as the FAIL above, with the upstream DLL beside it as the control:

| libmpv | versions | decoded | result |
|---|---|---|---|
| `libmpv-0.41.0-3` win-x64 | mpv 0.41.0, ffmpeg n9.0.2 | 38,702,166 | **+6 samples — PASS** |
| upstream SourceForge | mpv 0.41.0-1012, ffmpeg git | 38,702,166 | **+6 samples — PASS** |

Not merely both inside the tolerance: **the identical sample count.** Three
MP3s (no iTunSMPB, so nothing to check against) also decode to the identical
27,389,046 through both. So all three platforms of -3 are proven, and nothing
measurable separates our 9.9 MB DLL from the 120 MB upstream one on this
library. `get-libmpv.ps1` still fetches SourceForge — not changed here; the
repository is private, so pointing it at the release means `gh release
download` and a logged-in `gh`, which is a decision rather than a one-liner.

**Decided the same day: "point get-libmpv.ps1 at our release".** It now fetches
the pinned tag (`libmpv-0.41.0-3`) through `gh`, refuses a zip that does not
match the release's `SHA256SUMS`, and stamps `native\win-x64\libmpv-source.txt`
with what it fetched, so the next run — and the next person — can tell ours from
upstream's without guessing from the file size. No `gh`, or not signed in: it
tries the plain URL, which will work once the repository is public, and until
then fails saying exactly what to install. `-Upstream` is the old behavior,
kept as the rollback and the control; `-Force` refetches. All four paths were
run here under Windows PowerShell 5.1, and the DLL it leaves is byte-identical
(SHA-256 `c2857e0e…`) to the one measured above.

One trap it now steps around: the project copies the DLL to the output folder
with `PreserveNewest`, which goes by date, and Copy-Item keeps the source's.
Rolling back to a DLL BUILT EARLIER than the one in `bin` would therefore never
reach the exe. The script dates the DLL at the moment it fetched it.

One thing about the tool, found on the way: run from Git Bash, the track paths
arrived mangled, mpv loaded nothing, and the tool died with a
FileNotFoundException on its own WAV — which reads like a broken libmpv and is
not. It now checks that each track exists before starting and says so.

### UNINSTALL did not remove the program — and the app put itself back

His report: "i uninstalled from the about, the app closed and presumably
uninstalled. then I clicked on the pinned icon and it opened up?"

Read from outside the sandbox afterwards: the shortcuts and the Installed-apps
entry HAD been removed (the next start logged `desktop shortcut created`, not
`already there`), but `AlbumWall.exe` was still in `%LocalAppData%/Programs/
AlbumWall`, untouched. His taskbar pin points at it, so it started, and since
registration is re-asserted at every launch it re-registered everything: a
complete, silent re-install. The helper script had deleted ITSELF, so it ran
to the end; its one `Remove-Item` had failed with `-ErrorAction
SilentlyContinue` and nobody to tell.

The family already knew all of this. LP-100A's CLAUDE.md lists "three rules
the uninstall path learned on 2026-09-04, each from a real failure", and
FlexPad carries them. This installer was ported from **Shack Power, which does
not** — it still has the one-shot delete. All three are now ported here:

1. **Retry the delete for ten seconds.** The wait loop sees the process id
   vanish a moment before the exe's mapping is released; one Remove-Item in
   that gap fails on the locked file.
2. **Give the helper its own working directory** (`%TEMP%`). An installed copy
   runs with its install folder as its working directory — the shortcut sets
   it — the helper inherited it, and Windows will not remove a directory that
   is any live process's current directory.
3. **`Environment.Exit(0)` three seconds after cleanup**, as a backstop for a
   close made from inside a dialog's click leaving a windowless process.

Plus the extraction cache: the uninstall looked for it at `~/.net/AlbumWall`
on both platforms. On Windows the host unpacks to `%TEMP%/.net/AlbumWall`
(that is where test3's 9.3 MB libmpv actually was), so it was never found and
never removed. Now `ExtractionRoot`, as in LP-100A, honoring
`DOTNET_BUNDLE_EXTRACT_BASE_DIR`.

Checked in isolation, since his installed copy is the player he is using: a
throwaway program run from its own folder, removed by the old helper and the
new one. Old: folder and exe still there. New: removed completely. **The real
thing is untested until the next published build** — only a single-file build
installs, and those come from Fedora.

A taskbar pin is the user's, not the app's, and survives an uninstall by
design; with the exe gone Windows offers to remove it.

### The mixer said "song title - mpv"

`audio-client-name` (06c4868) is what PulseAudio, PipeWire and JACK read.
WASAPI has no client name: mpv names the Windows session from its window-title
option, default `<media title> - mpv`. `Player` now sets `title` to AlbumWall
on Windows only. Not in test3; first seen in whatever is built next.

### Icon, identity, Start Menu

The icon is drawn by `tools/make_icon.py` (needs Pillow; the outputs in
`Assets/` are committed). The app claims the AppUserModelID `gsa700.AlbumWall`
at startup, and that alone is enough for the media flyout to call it AlbumWall
rather than `AlbumWall.App.exe` — I had written that it also took a shortcut,
and he found otherwise by not making one. **Preferences > About > Add to the
Start Menu** writes a shortcut carrying the same ID, which is what lets Start
search find it and a pinned taskbar button group with the running window. The
ID is permanent: existing shortcuts carry it.

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
