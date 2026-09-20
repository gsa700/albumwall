# Roadmap

What is wanted and not yet built, in his words where there are any. Not a
schedule and not in order. When something here gets built, move what was
learned into the commit message and delete the entry — this file is for what
is ahead, the history is for what is behind.

## Wanted

- **Right-click context menu in the main window**, for albums and tracks
  ("files etc"). Nothing is decided about what goes in it. The obvious
  candidates, none of them asked for yet: play, play next, show in the file
  manager, copy path, and — once tag editing exists — edit tags.

- **A fully tabbed Preferences dialog**, "with options built out as we go".
  Started 2026-09-19: Library, Startup, About. It is a proper decorated window
  in the format of the family's Setup windows (FlexPad, LP-100A, W2, Shack
  Power) — system title bar, fixed size, one instance, position remembered, the
  family's tab chrome from `App.axaml`. **About is always the last tab**, and
  the last item in the menu, by convention; new tabs go before it. Library,
  Startup and Colors exist. Add a tab when the second option for it arrives,
  not before.

- **A file-properties window**, in the SAME FORMAT as Preferences — "we'll use
  the same format with the file properties window when we get to it". The
  format is written down at the top of `PrefsWindow.axaml`; the shared styles
  are already application-wide for this reason. Presumably reached from the
  right-click menu above.

- **The same for Linux: a `.desktop` entry with the icon.** Windows has its
  half (see `WindowsShell.cs` and the About tab). `Assets/app-icon.png` is
  the 256 px frame of the icon, there for exactly this; the station tools
  write `~/.local/share/applications/<name>.desktop` and the icon into the
  hicolor theme from their own Setup windows, and the About tab is where a
  matching button would go. The MPRIS `DesktopEntry` is already "albumwall".

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
     pre-release `v0.1.0-test2`.
  2. ~~The native libraries published as release assets of their own~~ —
     done, see the entry below. A release script should fetch them by tag.
  3. `InstallService` — which also fixes the entry below, and whose Linux half
     is the `.desktop` entry above.
  4. `UpdateService` — built now, switched on later: it reads
     `/releases/latest` unauthenticated, which a private repository answers
     with 404. It goes live with the public snapshot.

  **A release does not go out without these two, and they are paperwork, not
  engineering.** We distribute other people's GPL and LGPL code inside the
  exe, and that carries obligations that are easy to meet and embarrassing to
  miss:
  - **Notices.** The license texts and credits for everything bundled, in a
    third-party notices file and reachable from the About tab: mpv (GPLv2+),
    FFmpeg (LGPLv2.1+ as we configure it — never add `--enable-gpl` or
    `--enable-nonfree` without revisiting this), libplacebo and FriBidi
    (LGPLv2.1+), libass (ISC), FreeType (FTL, which asks for a credit),
    HarfBuzz (MIT), zlib; and on the .NET side Avalonia, TagLib#, Tmds.DBus and
    the Inter font.
  - **Corresponding source, attached to the same release as the binaries.**
    Pointing at upstream tags is common practice, but the distributor is the
    one responsible and upstream can vanish. The source tarballs for the exact
    pinned versions go up next to the native-library assets. The build recipe
    is part of that source and is already in the repository.

- **Our own audio-only libmpv for Windows: BUILT, NOT YET PROVEN.** "we don't
  need any of that extra stuff, lets leave it out." `scripts/build-libmpv/build.sh
  win-x64` cross-compiles a 9.4 MB `libmpv-2.dll` from the same pins and decoder
  list as the Linux library, replacing an upstream DLL of about 100 MB that
  carries H.264, HEVC and VVC decoders the app never calls. Verified from Linux:
  it imports only Windows system DLLs and exports everything `Mpv.cs` uses.
  Gapless through the LINUX sibling of this build was confirmed by ear on
  2026-09-20 ("gapless is FB here"), which is the same mpv and the same ffmpeg.
  **What is left is on Hambench: that it loads, plays, and stays GAPLESS** — the
  test is at the end of `docs/windows-bringup.md`. When it passes,
  `get-libmpv.ps1` should fetch our release instead of SourceForge. Wanted
  before the repository goes public; the upstream DLL is the rollback until then.

- **The native libraries are release assets now**: tag `libmpv-0.41.0-2`, a
  PRE-release on purpose so `/releases/latest` (and with it the app's updater)
  never mistakes it for an app version. linux-x64 and win-x64 are there;
  **linux-arm64 is still to build**, natively on a Pi, with the same script. Bump
  the trailing number when the recipe changes without the mpv version changing.

- **An installed copy, separate from the build folder.** The Start Menu
  shortcut points at wherever the app is running from, which today is
  `bin/Debug`. That works, but it means the player he is listening to has to be
  closed before every rebuild. The station tools copy themselves to
  `%LocalAppData%/Programs` and update in place (`InstallService` in FlexPad);
  worth porting once there are releases to install.

- **Tag editing.** The button in the album panel is there and disabled on
  purpose.

- **A seek bar in the Windows media flyout.** `Smtc.cs` tells Windows what is
  playing and takes its buttons, but does not send timeline properties, so the
  flyout shows the track without a position. Nobody has asked.

## Decided

- **The name is AlbumWall.** Settled 2026-09-19, in his words: "let's mark this
  settled: AlbumWall is the name." It was the working name all along, so there
  is no rename work; the one thing left is on the Techbench side, which is to
  strike "working title" from the spec. The candidates it beat, and why, are in
  the commit that settled it. Do not reopen this unless he does.
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
- The repository stays private.
