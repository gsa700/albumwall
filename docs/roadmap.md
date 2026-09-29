# Roadmap

What is wanted and not yet built, in his words where there are any. Not a
schedule and not in order. When something here gets built, move what was
learned into the commit message and delete the entry — this file is for what
is ahead, the history is for what is behind.

## Wanted

- **Compact view, and the same view as a hardware front panel.** His idea,
  2026-09-28, while mocking up a front panel for a stereo component: "what if we
  do add it to the main app as well as a 'Compact' display option? ... the full
  albumwall could compact itself to mimic the little display while playing ...
  then a button back to the full wall, pick a new album, hit it again and your
  compact again." And the rule over all of it: "a great little player for the
  stereo and still be awesome as a desktop app at the same time" — the component
  side must never cost the desktop app anything.

  ONE VIEW, TWO HOMES. It is designed at 1480 x 320, the native size of the
  Waveshare 11.9" bar panel (HDMI, native portrait 320 x 1480, rotated 90; about
  144 PPI; the glass has ~20 px rounded corners, so nothing that matters goes in
  the 24 x 24 px square at any corner), and scales uniformly to whatever size it
  is shown at. Mockup, with his changes: Design canvas
  https://claude.ai/artifact/F9MdxL1VavZV6cgLLEbJoJ — now playing (cover flush
  left, big title, 44 px times), browse strip, open album, standby (clock and
  "paused at" card either side of a separator at the exact centre), and two meter
  variants he liked: A, a pair of vintage yellowed analog VU meters with no L/R
  letters; B, album-tinted ground with 32-segment L/R LED bars.

  Order, each step usable on its own:
  1. **Compact mode in the desktop app.** A button in the top bar (and a key)
     shrinks the window to the view; a button in the view goes back to the wall
     exactly as it was. Now playing and standby, with play controls on hover
     (the panel itself has none; a remote drives it). Always-on-top is the
     desktop's business: Wayland gives an app no way to ask.
  2. **The kiosk front panel.** The same view in a second window, full screen on
     the bar display; the kiosk's labwc places it by a window rule (on Wayland an
     app cannot choose its output). Pi5-POE, HDMI-A-2.
  3. **Meters.** Levels from libmpv's `astats` filter, PROVEN bit-exact before
     it goes near the kiosk (the dedicated device is the bit-perfect one); a
     PipeWire monitor tap is the fallback. VU needles simulated with real
     ballistics (spring and damper, ~300 ms), LED bars with fast attack, slow
     release and peak hold.
  4. **Browse strip and open album on the panel**, with the remote. Needs the
     arrow-key navigation below, and settles its open question: on a remote the
     arrows must move, so seeking has to go somewhere else.
  5. **Kiosk finish**: night dimming (the yellow VU faces are the brightest
     thing on the panel), USB-to-S/PDIF coax output bit-perfect, the remote's
     buttons (Flirc + the aluminium Apple Remote is the leading pick).

  Later, maybe: a track list the compact view can drop open, as iTunes' mini
  player did. His, 2026-09-28: "I think on itunes they used a menued tracklist
  but lets not for now at least". The "Next ..." line is gone and not coming
  back ("not needed").

- **Kiosk mode: a bigger, look-ahead cover cache.** His ask, 2026-09-26, after
  the first 4K scroll test on the Pi 5 (Pi5-POE, full FLAC library, 294 albums):
  "pretty good ... a little stutter is expected but really not bad at all. as
  good or better than other apps on there at 4k." The stutter lines up with CPU
  bursts of 100-190% (of 400%) while scrolling, dropping to 5-20% between: covers
  being decoded as they come into view. Memory sat at 415-500 MB of 8 GB, never
  throttled. Two levers, both for kiosk mode only (a desktop shares its RAM):
  1. **Budget.** `ArtCache.Budget` is 160 MB. At the Pi's 2x scale a tile is
     roughly 536 px square, ~1.1 MB decoded, so 294 albums is ~340 MB: the
     wall does not fit and scrolling back re-decodes what was evicted. A kiosk
     budget sized to the machine (say a quarter of RAM) holds the whole
     library after one pass.
  2. **Decode ahead.** Request art for a screen or two beyond the viewport in the
     scroll direction, at low priority, so tiles arrive already decoded.
  Measure before and after with the same fling on the same library; the status
  bar already reports decoded count and art MB. Rides the kiosk runtime switch
  (see Decided: audio), not a preference.

- **Right-click context menu in the main window**, for albums and tracks
  ("files etc"). Nothing is decided about what goes in it. The obvious
  candidates, none of them asked for yet: play, play next, show in the file
  manager, copy path, and — once tag editing exists — edit tags.

- **A fully tabbed Preferences dialog**, "with options built out as we go".
  Started 2026-09-19: Library, Startup, About. It is a proper decorated window
  in the format of the family's Setup windows (FlexPad, LP-100A, W2, Shack
  Power) — system title bar, fixed size, one instance, position remembered, the
  family's tab chrome from `App.axaml`. **About is always the last tab**, and
  by convention; new tabs go before it. Library, Statistics, Startup and
  Appearance exist. Add a tab of OPTIONS when the second option for it arrives,
  not before. Statistics (2026-09-20) is the exception that is not one: it holds
  no options at all, it is a page to read — file types, bitrates, lossless
  formats, cover art — reached by pressing the library counts in the main
  window's bottom bar.

- **A file-properties window**, in the SAME FORMAT as Preferences — "we'll use
  the same format with the file properties window when we get to it". The
  format is written down at the top of `PrefsWindow.axaml`; the shared styles
  are already application-wide for this reason. Presumably reached from the
  right-click menu above.

- **RELEASES — decided 2026-09-19: "I'd like to get to the release stage soon...
  I see no reason to hold any longer."** The shape is the family's, ported from
  Shack Power (`InstallService` + `UpdateService`): a single-file self-contained
  exe per platform, zipped as `AlbumWall-<rid>.zip` on a GitHub release; the app
  installs itself per-user and updates in place. No installers, no RPM/DEB,
  no AppImage, no Flatpak — each was weighed and none beats this for an app
  that carries its own player. What is left to build, roughly in order:
  1. ~~The single-file publish with libmpv folded INSIDE it~~ — done. The
     bundler recognizes our library as native on its own (it sniffs ELF and PE
     files), and the resolver finds the self-extracted copy: on Linux the log
     shows `[mpv] library: ~/.net/AlbumWall/<hash>/libmpv.so.2`. All three
     platforms publish from one machine. The Windows exe (136 MB — the WinRT
     projection assembly is 24 MB of that) awaits its first run on Hambench:
     pre-release `v0.1.0-test3`.
  2. ~~The native libraries published as release assets of their own~~ —
     done, see the entry below. A release script should fetch them by tag.
  3. ~~`InstallService`~~ — done, ported from Shack Power into
     `src/AlbumWall.App/Install/`. The app installs itself per-user
     (`%LocalAppData%\Programs\AlbumWall`, `~/.local/share/albumwall`), offers to
     when a release is run loose, re-asserts its registration at every start,
     and removes itself with `--uninstall`. Verified end to end on Linux —
     install, run, uninstall, settings kept. **And on Windows, for real, on
     2026-09-20** — quiet install, the installed-apps entry, both shortcuts, a
     run from the Start Menu, the quiet uninstall and the interactive one with
     the settings box ticked: all pass, with a build published on Hambench
     itself. The table is in `docs/windows-notes.md`. Not yet looked at: taskbar
     pin grouping, the About tab in installed mode, and the desktop shortcut
     staying deleted. Only a single-file release installs; a development build
     says so in About and is never offered.
  4. ~~`UpdateService`~~ — done 2026-09-20 (this entry used to say "built now",
     and it was not: there was no such class until that day). Ported from
     FlexPad, the newest of the family's four, and tested END TO END on Hambench
     against a local fake feed: a published 0.1.0 found 0.1.1 at launch, offered
     it on the About tab, verified the download, swapped its own exe and was
     running as 0.1.1 1.6 s after the button. A wrong checksum is refused with
     nothing swapped. It reads `/releases/latest` unauthenticated, which a
     private repository answers with 404 — shown as "No release has been
     published yet", which is true — so it goes live, by itself, with the
     public snapshot. `ALBUMWALL_UPDATE_FEED` points it elsewhere for testing.
     **THE FIRST PUBLIC RELEASE MUST BUMP `<Version>`**: every test build so far
     reports 0.1.0, and nobody is offered the version they already have.
     Releases must carry `SHA256SUMS`; the updater refuses one that does not.
     Not run on Linux.

  **A release does not go out without these two, and they are paperwork, not
  engineering.** We distribute other people's GPL and LGPL code inside the
  exe, and that carries obligations that are easy to meet and embarrassing to
  miss:
  - **Notices.** DONE (2026-09-20). `THIRD-PARTY-NOTICES.md` names every bundled
    component with what it does, its license and its source; `licenses/` holds
    the license TEXTS, fetched verbatim from each upstream at the pinned version
    by `tools/fetch-licenses.py` (never typed), with origin and SHA-256 in
    `licenses/README.md`. Both are carried inside the exe and open from the About
    tab as "What's inside", the texts each behind a fold. RUN THE SCRIPT AGAIN
    WHEN A PIN MOVES. The About tab's other links (source, license, the audio
    engine's releases, issues) all hang off `App.ProjectUrl`, which moves once,
    to the public snapshot. The original item: the license texts and credits for everything bundled, in a
    third-party notices file and reachable from the About tab: mpv (GPLv2+),
    FFmpeg (LGPLv2.1+ as we configure it — never add `--enable-gpl` or
    `--enable-nonfree` without revisiting this), libplacebo and FriBidi
    (LGPLv2.1+), libass (ISC), FreeType (FTL, which asks for a credit),
    HarfBuzz (MIT), zlib; and on the .NET side Avalonia, TagLib#, Tmds.DBus and
    the Inter font.
  - **Corresponding source, attached to the same release as the binaries.**
    DONE for `libmpv-0.41.0-3` (2026-09-20): one archive per pinned project, plus
    the recipe, built by `scripts/build-libmpv/source-archives.sh` and uploaded
    beside the libraries with `SHA256SUMS-sources`. The script checks each archive
    against `licenses/` and refuses to finish if they differ — it has to, because
    `git archive` applies `core.autocrlf`, and on Windows the first set came out
    CRLF throughout and looked fine. EVERY NEW `libmpv-*` RELEASE NEEDS ITS OWN.
    Pointing at upstream tags is common practice, but the distributor is the
    one responsible and upstream can vanish. The source tarballs for the exact
    pinned versions go up next to the native-library assets. The build recipe
    is part of that source and is already in the repository.

- **Our own audio-only libmpv for Windows: BUILT AND PROVEN, not yet in a release.** "we don't
  need any of that extra stuff, lets leave it out." `scripts/build-libmpv/build.sh
  win-x64` cross-compiles a 9.4 MB `libmpv-2.dll` from the same pins and decoder
  list as the Linux library, replacing an upstream DLL of about 100 MB that
  carries H.264, HEVC and VVC decoders the app never calls. Verified from Linux:
  it imports only Windows system DLLs and exports everything `Mpv.cs` uses.
  It loads and plays on Hambench (v0.1.0-test3, 2026-09-20). It was NOT gapless:
  the by-ear check on Linux had only ever been run on FLAC, which has no padding
  and cannot fail. `tools/gapless-check.cs` measured it, and the cause was the
  ffmpeg pin — see `docs/windows-notes.md`. **Fixed by `FFMPEG=n9.0.2`**, which
  now measures PASS on iTunes AAC, matching the upstream DLL's score exactly.
  Both Linux libraries now measure PASS; `libmpv-0.41.0-3` is published.
  **The Windows DLL passes too** (Hambench, 2026-09-20: +6 samples, the
  identical sample count the upstream DLL produces — see the notes), and
  **`get-libmpv.ps1` fetches our release now**, pinned to the tag and checked
  against `SHA256SUMS`, so a development build on Windows plays through what
  ships. `-Upstream` still fetches SourceForge's: the rollback. **What is left:
  a test4 carrying it.**

- **The native libraries are release assets now**: tag `libmpv-0.41.0-2`, a
  PRE-release on purpose so `/releases/latest` (and with it the app's updater)
  never mistakes it for an app version. Bump the trailing number when the recipe
  changes without the mpv version changing. **Current: `libmpv-0.41.0-3`** (the
  ffmpeg 9 pin), all three platforms. linux-arm64 has no cross-build here — it
  is built natively on the Pi 5 at Pi5-POE, which is also the only arm64
  machine that can run the gapless check.

- **Tag editing.** The button in the album panel is there and disabled on
  purpose. It is the first time the app would WRITE to his music, and the README
  says today that it never does — so it wants a design, not a button handler:
  how a write is made safe, what happens to the file's date (he tags with
  `metaflac --preserve-modtime`), and how the index and the watcher are kept
  honest about a change the app made itself.

- **Lyrics, where they are embedded** (2026-09-20): "when we get to the metadata
  editing and right click and so on I want to be able to view and edit lyrics if
  they are embedded." VIEWING is cheap and can come with the properties window:
  TagLib# already reads them (ID3 `USLT`, MP4 `©lyr`, Vorbis `LYRICS`), fetched
  when a track's properties are opened and NOT kept in the index. EDITING is tag
  editing, above. Unsynced lyrics first; timed ones (`SYLT`, LRC) are a separate
  and much larger feature.

- **Arrow-key navigation of the wall**, and the rest of keyboard and screen-reader
  access. The shortcuts and the Help page are DONE (2026-09-20: Space, Ctrl+arrows,
  arrows to seek, Ctrl+Up/Down, Ctrl+F or `/`, Esc, Ctrl+L, Ctrl+`,`, F1; the Help
  tab lists them and the `art:` searches, and must be kept in step with
  `MainWindow.OnShortcutKey`). Asked whether that helps accessibility, the honest
  answer was "partly": what a keyboard-only or screen-reader user still lacks is
  - moving about the wall with the ARROW keys, with a focus ring that can be
    seen — Tab reaches every cover today, one at a time, through a thousand;
  - an audit of what each control is CALLED. The Preferences tabs all said
    "Avalonia.Controls.StackPanel" until the updater's test tripped on it, and
    the covers had no name until they were given "Title, by Artist, year".
    Track rows, the play controls and the sliders have not been looked at;
  - "reduce motion": the sliding play controls and the unfolding album ignore
    the system setting;
  - contrast, which depends on his Light and Tint.
  `--help` and `--version` go with the command-line work below; a man page for
  Linux can be generated from the Help tab's text, later.

- **One instance, and a command line to it** (2026-09-20, from "remote control
  and/or specialized command line arguments?"). Three things, in this order:
  1. SINGLE INSTANCE: a second launch brings the running window forward instead
     of starting a copy that fights the first over the settings and the session.
     "Never run both" was a rule to be remembered all through 2026-09-20; it
     should be a thing that cannot happen.
  2. COMMANDS TO THE RUNNING COPY: `AlbumWall --next`, `--play-pause`,
     `--play "dark side"` — nearly free once (1) exists, since the second launch
     hands over its arguments. Linux has most of it through MPRIS and
     `playerctl`; Windows has nothing. It would also replace the DEBUG
     trigger-file channel the test rig uses with something real.
  3. A NETWORK REMOTE (a phone, a web page): not now. A listening socket with
     the security questions that come with one, for no concrete use yet.

- **Libraries that are not always there** (2026-09-25, Hambench, the day the
  NAS's FLAC library was added: "I am also thinking of other users with
  different network topologies and NAS or no NAS or USB drive or???"). The
  two kinds of library cover every setup: a FOLDER is an internal disk, a USB
  drive, a Windows share, an NFS or SMB mount, a Mac's /Volumes; a SERVER is
  Navidrome or anything else speaking Subsonic, from anywhere. What other
  people's setups exercise is not the kind but what happens when a library is
  ABSENT, and with one library on an internal disk that never came up. Before
  anyone else runs this, the first two:
  1. UNREACHABLE IS NOT "WRONG FOLDER". A missing root shows "{root} does not
     exist. Point the app at wherever your records live." For a sleeping NAS
     or an unplugged drive that is the wrong advice, and it invites repointing
     a library that is fine. Say it is not reachable now, name the library,
     offer to try again, and leave the playing session alone.
     DONE (2026-09-25): "{name} cannot be reached" with Try again and Choose
     another folder; a wall of that library already up stays up, with a note
     on the status line; looked for again every 30 s, recovering by itself.
  2. PRESENT BUT EMPTY IS ABSENT, NOT DELETED. An unmounted Linux mount point
     is an empty directory, and so is a drive letter that now belongs to a
     different USB stick. A complete scan that finds nothing under a root the
     index knows to be full would conclude everything in it is gone, and the
     next time the drive is there the user pays the whole first scan again
     (on Windows, Defender opening every file). A root that was full and is
     now empty should be treated as unreachable (1), and neither prune the
     index nor empty the wall. Since e7ad028 a scan only prunes under its own
     root, so this is per library, not all of them.
     DONE (2026-09-25): LibraryUnreachableException. "Was full" is the
     library's track count from its last scan (settings), or on Windows the
     index; Rescan is the one scan that believes an empty folder. Found on the
     way: a file that could not be OPENED was remembered as "not audio" and
     stayed off the wall until its size or time changed; now it is only
     skipped, and a scan with skips prunes nothing. `tools/absent-check.cs`.
  3. TRUSTING THE INDEX, PER LIBRARY. Linux opens every file on every scan -
     David's decision for Techbench, where `metaflac --preserve-modtime` means
     size and mtime cannot be trusted to change. For somebody else's library
     on a NAS that is a minute or more at every launch. Probably a setting of
     the library, defaulting by platform, rather than a rule of the platform.
  4. WATCHING A NETWORK FOLDER. Over NFS the watcher generally hears nothing
     done from another machine, and over SMB it is unreliable. The scan at
     launch catches it all; a library on a share might also want an
     occasional quiet recheck while the app is running.
  Small, found the same day: Forget leaves the forgotten library's rows in
  `index.db`. Harmless (a few MB nothing reads), but Forget should drop them.

  5. RESCANNING A LIBRARY THAT IS NOT ON THE WALL (2026-09-28: "would it work
     on a library in the background? like could I rescan Music while
     listening to NAS Music?"). Not today: Rescan is on the row of the library
     on the wall only, and the app runs one scan at a time. It is safe to
     build, since e7ad028 a scan only touches index rows under its own root,
     so a background scan of one library refreshes its rows and leaves the
     wall, the playing library and every other library alone; it would show
     its progress on its own row. Deliberately NOT built yet, because it buys
     little: switching already runs a scan through the index, which re-reads
     every file whose size or date moved, so additions, removals and nearly
     every tag edit show up without it. Rescan is only for an edit that kept
     both, and on Linux, which reads everything on every switch, it buys
     nothing at all. It becomes worth building with libraries that change
     while nobody is looking at them - a Navidrome server, a USB drive - where
     "keep the other libraries fresh in the background" is a feature rather
     than a button.

  How David's own is laid out, decided the same day: Techbench stays the first
  copy, where files are added and tagged. Its `~/Music` backup moves out of the
  machine backup into a folder of its own on the NAS (a stable path that does
  not depend on how Techbench is backed up; rsync keeps modification times, so
  the index holds overnight). Hambench reads that folder directly as a folder
  library - the full experience, back covers and statistics included - and
  Navidrome serves the same folder, over a read-only NFS export if it does not
  run on the NAS itself, for everything off the LAN. A second, identical copy
  was considered and dropped: two mirrors with --delete lose an accidental
  deletion together. If protection against that is wanted, it is --backup-dir
  or no --delete on the one job, not another mirror.


## Decided

- **The name is AlbumWall.** Settled 2026-09-19, in his words: "let's mark this
  settled: AlbumWall is the name." It is what the project had been called from
  the first commit, so there was no rename work. The candidates it beat, and
  why, are in the commit that settled it. Do not reopen this unless he does.
  He did, once (2026-09-20): "aWall", with a Greek alpha, "alpha wall for first
  or best". As a name it lost — `awall` is Alpine's firewall tool and two
  commercial products, "AlphaWall" is a security company, and an alpha cannot
  be typed, sorts after Z, and falls back to `awall` wherever ASCII is needed.
  "AlbumWall stays." **The alpha lives in the WORDMARK instead**
  (`App.Wordmark`): the header above the wall and the top of the About tab
  draw the A as α. Everything the system, a search or a screen reader reads
  still says AlbumWall. Said aloud, aWall is a fine nickname.
- **Any companion tool is called Deadwax.** "If this thing ever gets a companion
  app for any reason: we name it deadwax no question."

- **AAC stays in the build.** It is the one codec we ship with a live patent
  question: the base AAC-LC patents (1997-2000) have largely aged out, but the
  HE-AAC extensions (SBR and Parametric Stereo, 2003-2006) may still be live in
  places for a few more years, and FFmpeg's decoder handles all of it. Fedora's
  answer is a decoder stripped to LC only. Ours is to ship it, as VLC and mpv
  have for two decades: decode-only, free, non-commercial, from an individual
  is not a profile patent pools pursue, and his Windows library is 6,876 M4A
  files. Everything else in the build — FLAC, Vorbis, Opus, ALAC, PCM, and MP3
  since its patents expired in 2017 — is clean. The conservative lever exists
  and is one line: drop `aac,aac_latm` from the decoder list in
  `scripts/build-libmpv/inner.sh`. **Revisit with an actual attorney if the app
  ever becomes commercial.** None of this is legal advice; it is the reasoning,
  written down so it does not have to be reconstructed.

- **Desktop audio: be a good neighbour, not "bit-perfect".** Settled 2026-09-26:
  "I think being a good neighbor is more important than saying we're bit
  perfect. We cannot anticipate a thousand or a million different users audio
  setup so we should let the OS handle it and just hand it the best 44.1 we
  can." So on a desktop the player hands the OS the decoded audio at the file's
  own rate (`audio-samplerate=0`) and stops there. No exclusive mode by
  default, no forcing PipeWire's clock (`clock.force-rate`), and no writing the
  user's PipeWire `allowed-rates` for them. If the OS resamples (stock PipeWire
  runs at 48 kHz only; Windows shared mode mixes at the device's control-panel
  rate), that is the user's system and the user's call.
  **A dedicated device is the exception:** "the kiosk or another hardware
  device running albumwall is a different matter: in that application I say we
  force bit perfect because it would likely be the only app running the audio
  stack." A jukebox/appliance setup therefore configures native-rate output as
  part of its setup (the Pi5-POE got the allowed-rates drop-in on 2026-09-26,
  verified at 44.1 kHz on the HDMI hardware); how "force" is implemented there
  (allowed-rates vs exclusive ALSA) is decided when the kiosk mode is built.
  **It is a runtime switch, not a preference:** kiosk mode would likely hide
  Preferences anyway, so bit-perfect output is turned on from the command line
  or the launcher shortcut (his call, 2026-09-26).

## Decided against, for now

- **Fetching album art from the internet** — "at least not yet". The app does
  the best with the art in the files, and `art:missing`, `art:small` and
  `art:nonsquare` in the search box list the albums whose files want attention.

- **Detecting blank covers.** The two blankest real covers in his library are
  Spinal Tap's black album and the White Album.

## Standing rules (from the bring-up notes, still true)

- One library at a time. Do not merge libraries.
- No touch-sized controls yet; the design should flow toward touch, not become
  a touch app.
- libmpv is never committed: FETCHED on Windows (`scripts/get-libmpv.ps1`),
  BUILT on Linux (`scripts/build-libmpv/` — our own audio-only build, because
  the distribution's drags in 338 shared libraries). "That is the best match to
  windows and we should keep them the same when we can." Both land in the
  git-ignored `native/<rid>/`, and the app prefers its own copy over the
  system's.
- Any machine can publish every platform: the target framework follows the
  RuntimeIdentifier, not the build host. Keep it that way.
- The repository is PUBLIC, from 2026-09-28 ("can we sanitize the current repo
  and make it public?"). Its history was rewritten on the way: LAN addresses
  became machine names and the author email became GitHub's no-reply address
  (his calls: "use noreply email, name and callsign are fine ... keep machine
  names"). Nothing private goes into a commit: no addresses, no keys, no
  personal email. The pre-rewrite history stays in the private
  `albumwall-private`, and is never pushed here.
