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
  it cost four seconds. It is now a Rescan button on the row of the library on
  the wall, Preferences › Library (it was a section of its own, "Read
  everything again", until 2026-09-28), with a tooltip that says how many
  files, why it is slow, and when you would want it. His call: "move the rescan from
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

**And by ear, in the player, the same afternoon:** the development build
rebuilt onto our DLL (the running process has the 9.5 MB `libmpv-2.dll` loaded,
same SHA-256 as the measured one), Dark Side played through the joins that gave
the "barely audible burp" in test3. His words: "gapless works on Dark Side with
our DLL, perfect." So the measurement and the ears agree, in both directions:
-2 failed both, -3 passes both.

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

### THE INSTALLER, RUN FOR REAL (same day, late afternoon)

"only a single-file build installs, and those come from Fedora" was wrong in its
second half: the roadmap says any machine can publish every platform, and it is
true. `dotnet publish src\AlbumWall.App -c Release -r win-x64` on Hambench gives
one 137 MB `AlbumWall.exe` in six seconds, with our `libmpv-0.41.0-3` inside.
So the whole cycle was run here, against the real Programs folder, registry and
shortcuts, with today's code (the uninstall fix, c7b545f, included).

How, since it matters for whoever repeats it: every step ran through a one-off
scheduled task, because the tool shell sees a private AppData; and every
install/uninstall command ran with `ALBUMWALL_CONFIG_DIR` pointed at a throwaway
folder, so that the settings an uninstall removes — and the `index.db` it
rightly always removes — were stand-ins and not his. His daily player (the
development build) was closed only for the half minute the installed copy ran,
since the two share the real settings.

| step | result |
|---|---|
| `--install --quiet` over test3 | exit 0; exe replaced (hash = the published one); `reg import exit 0; start menu ok` |
| Installed-apps entry | real: name, version 0.1.0, publisher, icon, both uninstall strings, size |
| Start Menu + Desktop shortcuts | both point at the installed exe, working folder set |
| run it from the Start Menu shortcut | `[install] startup: ok`; index used (**0 files opened, 166 ms**); session resumed; libmpv loaded from `%TEMP%\.net\AlbumWall\<hash>\` — ours, from inside the exe |
| **quiet uninstall** (the registered `QuietUninstallString`, run verbatim) | exit 0 in 1.1 s; **install folder GONE 1.1 s after**; registry entry gone; both shortcuts gone; extraction cache gone (test3's stale one with it); test folder: `index.db` removed, `settings.json` KEPT |
| **interactive uninstall**, "Also remove my settings and the saved session" TICKED | dialog driven through UI Automation (`OptionBox`, `AffirmativeButton` — Avalonia exposes `x:Name` as the automation id); **folder gone 0.5 s after the button**; settings, session, index, logs removed; a file that was not the app's, planted beside them, LEFT ALONE |
| reinstall after each | clean; desktop shortcut "created", as a fresh install should |

So the fix holds on the real thing, both ways in, and the helper is fast: the
ten-second retry budget is insurance, not the normal case.

Two things it turned up:

- **`albumwall.log.1` survived "remove my settings."** The previous run's log,
  which `LogFile` keeps beside the current one, was not on the list of named
  files to remove. It is now.
- **A comment and the bring-up doc had the two uninstall strings the wrong way
  round.** Both are registered. The Uninstall button in Settings › Installed
  apps runs `UninstallString` — plain `--uninstall`, which comes up far enough
  to ASK — and it is silent removers (winget, management agents) that run
  `QuietUninstallString`. So item 6 of the bring-up checklist, "Uninstall from
  Installed apps runs `--uninstall --quiet`", describes the wrong button. The
  strings were read back out of the registry; the Settings button itself was
  not pressed, because its command line is the one the interactive test ran.

**Taskbar grouping: good, by his eyes.** Asked what the item meant and told (one
icon, the pin itself lit, not a second one beside it), his answer was "its been
good the whole time" — that is, since test3 and through today's reinstalls over
it. So the AppUserModelID claimed at startup and the one written into the
shortcut agree, and a pin made from one build carries over to the next.

**The installed copy, driven by him, that evening:** closed the development
build, started the installed one from his taskbar pin — "FB" — and its About tab
reads "Installed in …" with the Uninstall button, as it should. With that, every
item on the bring-up doc's installer checklist has been seen on Windows except
one: "delete the desktop shortcut and it must not come back".

Left installed at the end: today's build (5082e79 plus the log fix), so the
taskbar pin — which had been pointing at test3 — starts a good copy. It shares
the real settings with the development build; one at a time.

### The media flyout has NO progress bar, for anybody (Windows 11 25H2)

f71942d sends Windows a timeline and answers seek requests, and was sold to him
as "a progress bar in the flyout, like Spotify's or a browser's". THERE IS NO
SUCH BAR. His words after the update: "uh, no progress bar." Windows had
everything — asked directly, the media-session manager reported his running
copy at `01:31 of 03:55`, moving, `position enabled: True`, and as the session
the flyout leads with — and he then checked the control: "no seek bar on
youtube either". The quick-settings media card on this build draws the cover,
title, artist and three buttons, for every app, and nothing else.

What went wrong was not the code. It was describing what Windows draws from
memory, building on it, and testing the INTERFACE the flyout reads rather than
the flyout — then saying so only in a caveat. One look at the flyout with a
browser playing would have settled it before a line was written.

The change stays: it is the correct use of the API, costs nothing, and the
timeline and the seek request are there for whatever does read them —
third-party flyouts, Phone Link, some Bluetooth displays, a later Windows. But
nobody should promise a bar in the flyout again.

### The updater (same evening)

Ported from FlexPad's `UpdateService` — the newest of the family's four copies —
with what this project's own day had taught:

- **The swap is retried** for up to ten seconds. The family's helper does one
  `Copy-Item` the moment the process id disappears, which is the same race that
  left this app's exe behind on its first uninstall (c7b545f).
- **The download is verified** against the release's `SHA256SUMS` before it is
  unpacked, and a release without one is not installed.
- **The old build's unpacked libraries are removed** after a successful swap —
  29 MB per build that nothing else ever cleans. ITS OWN FOLDER ONLY. The first
  draft removed the whole `%TEMP%/.net/AlbumWall`, and was one test run from
  deleting libmpv — which loads only when something is first played — out from
  under the installed copy he was listening to. Caught reading the test over
  before running it, not by luck of the run. The host says where it unpacked in
  `NATIVE_DLL_SEARCH_DIRECTORIES`; the helper also skips the removal if any
  running AlbumWall has a module loaded from that folder (two copies of the
  same build share one), matching on the folder's own name because the same
  path turns up as `DAVIDE~1` and in full.
- **And every other build's, at startup** (0.6.11, 2026-10-10). The updater
  only ever removed the build it replaced; builds that arrived any other way —
  a copy tried from Downloads, a test kit, a local publish — kept their folders.
  Hambench had 13 (381 MB) and Techbench, which has the same cache under
  `~/.net/AlbumWall` (Linux unpacks too), 46 (1.1 GB). Now a copy that is the
  ONLY AlbumWall running sweeps every sibling of its own folder
  (`InstallService.SweepStaleExtractions`). Blunter than asking which folder
  each copy uses, on purpose: a copy that has not played yet has no libmpv
  mapped, so Windows would let it go, Linux always would, and that copy would
  break on its first play. A second copy just defers the sweep to the next
  lone start.
- The helper starts in the temp folder, not ours; paths are quoted for an
  apostrophe; `VersionOrder` and `UpdateApplyScript` live in Domain so
  `tools/update-check.cs` can run the family's test cases against them.

**Tested end to end, for real**, with `_aw_installtest/update-test.ps1`: two
builds published here (0.1.0, and 0.1.1 via `-p:Version=`), a local
`python -m http.server` playing GitHub, `ALBUMWALL_UPDATE_FEED` pointing at it,
a throwaway settings folder, the About tab driven through UI Automation.

| | result |
|---|---|
| launch-time check | gear tooltip "Preferences — a newer version is available" |
| About tab | "Version 0.1.1 is available. This is 0.1.0." / "Update to 0.1.1 and restart" |
| after the button | checksum matched; old process gone in 1.0 s; **0.1.1 running at 1.6 s**; exe byte-identical to the 0.1.1 build |
| left behind | nothing: no failure marker, no staging folder, no helper script; its own unpacked folder removed, the other copy's untouched |
| wrong checksum | "The download does not match the release's SHA-256 and has been thrown away. Nothing was installed." App still running, exe unchanged |
| the real feed today | `[update] nothing published (404)` — the repository is private |

**It found an accessibility bug on the way.** The test could not find the About
tab: with the family's tab template every `TabItem`'s automation name falls
through to its CONTENT, so all five tabs announced themselves to a screen
reader as "Avalonia.Controls.StackPanel". They now carry their headers.

### Release paperwork, done from Hambench (same evening)

Both "a release does not go out without these" items, which need no Fedora:

- **License texts**: `tools/fetch-licenses.py` → `licenses/`, 20 files, each the
  file its project ships at the pinned version, with origin and SHA-256 indexed.
  `.gitattributes` marks the folder `-text`, or `* text=auto eol=lf` would
  quietly normalize someone else's license. Carried in the exe; "What's inside"
  shows them behind folds.
- **Source archives**: `scripts/build-libmpv/source-archives.sh` → seven project
  archives and the recipe, 73 MB, attached to `libmpv-0.41.0-3`.

**THE TRAP, and it is a Windows one.** `git archive` is not immune to the
platform: it runs contents through the same end-of-line conversion as a
checkout, and Git for Windows installs with `core.autocrlf=true`. The first set
of archives had CRLF in every text file — thousands of files per project that
were not what upstream ships — and nothing about them looked wrong. It showed
only because the license file inside each archive was compared with the copy
`fetch-licenses.py` had fetched from the same tag over HTTPS: seven out of seven
DIFFERENT, identical apart from carriage returns. The script now archives with
`-c core.autocrlf=false -c core.eol=lf` and does that comparison itself, exiting
non-zero on a mismatch. On the Fedora box none of this would ever have shown.

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

## 2026-09-25 — two libraries on Hambench, and the index forgot one

David added the Techbench FLAC backup on the NAS as a second library
(`\\NASBOX\NAS_data\Techbench\Music`, 294 albums) beside `~\Music` (1,034):
"I cannot tell the files aren't local." First scan of the share: 54 s, 3,563
files opened; after that it opens in about a second, like the local one, and
FLAC from the share plays gapless track to track.

**The index forgot the other library.** That first NAS scan logged
`saved 4143 changed, 15556 gone`: every row of `~\Music`. The index is one
file for every library and a complete scan dropped whatever it had not seen.
Fixed in the commit "A scan of one library no longer forgets the others": a
scan prunes only under the folder it walked. Proven live afterwards: the one
rebuild of Music took 156 s (15,531 files, `0 gone`), and switching back to
NAS Music took 1.4 s with no file opened.

The layout of his own music was settled the same day and is in the roadmap
("Libraries that are not always there"): Techbench is the first copy, its
backup went to a folder of its own on the NAS (now `\\NASBOX\NAS_data\MusicFLAC`),
and Hambench reads that directly.

## 2026-09-28 — 0.2.0 on Hambench

A test kit from the release session (`AlbumWall-win-x64.zip` built from
58b27a7, `SHA256SUMS`, and `gapless-check.cs` with `libmpv-2.dll` from
`libmpv-0.41.0-4`). The zip matched its sum.

| Check | Result |
|---|---|
| gapless, `libmpv-0.41.0-4`, three iTunes AAC tracks (Amy Winehouse, iTunes Festival London 2007) from the NAS | PASS: 26,440,602 samples, within 6 of the files; the count the kit expected from Linux |
| the same with the player's meter filter in the chain (`GAPLESS_AF`, astats) | PASS, identical count: the meters cost gapless nothing |
| install over his installed copy | clean; settings carried over (backed up first); 0.2.0 in the log |
| a library whose folder is gone (the old `Techbench\Music`, removed by the move) | "cannot be reached", retried every 30 s, index kept all 19,094 rows: absent, not deleted, on the first launch that could show it |
| library picker on that screen | **could not be clicked** - fixed, below |
| repointing that library to `MusicFLAC` | one scan, then 0.7 s with no file opened |
| VU meters | there, once the build had the right engine - see "the stale DLL" |
| close while compact, reopen | came back compact, meters present |
| Rescan (new button), its Stop state and progress bar | good, by his eye |
| Preferences at the height of its tallest tab | Help fits, no scrolling, by his eye |
| compact's hover controls | work, by his eye |
| the wall and the compact strip each keeping their own position | they stick, by his eye |

### The "cannot be reached" screen swallowed every click

With the NAS library unreachable the wall showed its explanation, and the
library picker in the bottom bar - the one way to a library that WAS there -
did nothing. `EmptyState` covers the whole window and had
`Background="Transparent"`, which is hit-testable, so it took every click
meant for the bars around it: picker, search box, Preferences, the play
controls, the window's own buttons. Not new in 0.2.0: the first-run "No music
here yet" screen is the same overlay, so a new user with an empty Music folder
could not reach Preferences or close the window with the mouse. No background
now. Fixed in "The 'cannot be reached' screen no longer swallows every click".

**New in the trigger rig: `hit <control name>`** says what a click on that
control's centre would land on, REACHABLE or BLOCKED and by what. `libraries`
opens the picker from code and so could never have seen this; before the fix
`hit LibraryLink` said `Border EmptyState -> BLOCKED`.

### The stale DLL: the meters were missing from my own builds

The 0.2.0 kit had the meters; the two builds I then published here did not.
The history had been rewritten (below) and I had pulled it, but
`scripts/LIBMPV_RELEASE` had moved to `libmpv-0.41.0-4` while
`native/win-x64/` still held `-3`, which has no astats. The app does the right
thing without it - hides the meters - so nothing said anything was wrong.
`get-libmpv.ps1` fetched `-4` (SHA-256 matched, and it is byte-identical to the
kit's) and the log said `[mpv] level meters: astats in the chain` again.

**After every pull, compare `scripts/LIBMPV_RELEASE` with
`native/win-x64/libmpv-source.txt` and run `get-libmpv.ps1` if they differ.**
Worth making the build refuse a mismatched DLL (Techbench's call).

### The repository went public and its history was rewritten

Done on Techbench for the first public release: every commit has a new id and
the noreply author address. The Hambench clone was clean and every one of its
commits had an equivalent upstream, so it was moved across with
`git branch pre-rewrite-2026-09-28 master; git reset --hard origin/master`.
That branch is local, kept only for reference, and must never be pushed.

### Changed the same evening, from what he saw

- Compact mode takes the playing album's color (`Palette.Panel`, the ground
  an album opened on the wall gets); standby stays black, the front panel
  will pass no color and stay black.
- Rescan moved from its own section ("Read everything again", two paragraphs)
  to a button on the row of the library on the wall; its explanation is the
  tooltip. "Show" on the other rows is "Switch to".
- Preferences is as tall as its tallest tab, measured as it opens (729 in a
  scratch copy, was a fixed 700), and grows if the open tab's content does.
- README: the compact-mode bullet points at the Wayland window-position note.

## 2026-10-02 — the updater, for real, on Windows

The first real update on Windows, against a public release rather than the
local fake feed of 2026-09-20. His installed copy was 0.2.0 plus that week's
fixes, built locally, with `"CheckForUpdates": false`, so nothing was offered
at launch; he asked from Preferences > About. From the log:

| Step | Seen |
|---|---|
| check | `latest v0.2.6, have 0.2.0, newer=True, asset=AlbumWall-win-x64.zip` |
| download | the zip matched `SHA256SUMS` |
| swap | the helper waited for the old process to exit, replaced `AlbumWall.exe` in `%LocalAppData%\Programs\AlbumWall`, and removed that copy's own folder under `%TEMP%\.net\AlbumWall` |
| relaunch | 0.2.6 running about a second after the old one exited; the installed exe reports `0.2.6+997c9274`, the release commit |
| after | settings kept; the NAS library came up from the index with no file opened; meters present (`libmpv-0.41.0-4` is inside the release); startup said `start menu ok` and no longer mentions the desktop shortcut, 0.2.6's install-only shortcut |

So the updater is now proven on both platforms against a real release.

## 2026-10-04 — 0.5.4 on Hambench: Navidrome only

Updated from the About tab to 0.5.4. He then emptied `~\Music` and removed
both folder libraries, and the music now comes from two libraries on the
Navidrome server on NASBOX: "Music Library" (1,035 albums, 15,572 tracks) and
"FLAC" (305 albums).

Emptying `~\Music` while it was still a library was the "absent, not deleted"
rule doing its job for real: the log said `is empty, and held music last time`
and nothing was pruned until he removed the library himself.

| Check | Result |
|---|---|
| signing in and choosing the server's libraries | both added; the walls came up from the server in 2.6 s and 0.6 s |
| gapless over Navidrome, Dark Side of the Moon | gapless, by his ear (Techbench measured the streams sample-exact on Linux) |
| meters on a streamed track | present (`astats in the chain`) |
| right-click menu on albums and tracks | works, by his eye |
| Properties, for albums and server tracks | works, by his eye |
| cover in the Windows media flyout for a server album | shows |

With nothing but server libraries, the Windows-only cost the index exists for
is gone: nothing is opened on this disk, so Defender has nothing to scan. The
old rows of the removed folder libraries are still in `index.db` (about 6 MB),
which is the known "Forget should drop them" item in the roadmap.

## Test runs use a scratch config

`ALBUMWALL_CONFIG_DIR` now moves `settings.json` elsewhere. Anything launched
to try a change should set it, together with `ALBUMWALL_SNAP`, and seed the
directory with a `settings.json` whose `Libraries` list points at a scratch
library (`{ "Id": "...", "Name": "...", "Kind": "folder", "Path": "..." }`,
plus `"CurrentLibrary"` naming it; the old single `LibraryPath` still works
and is migrated) — a second instance on the real settings fights the first over window
geometry and whichever closes last wins.

A scratch library does not need real music: a 844-byte silent WAV tagged with
TagLib (`AlbumArtists`, `Album`, `Title`, `Track`) per album is enough for the
wall, and 60 of them is enough to scroll.
