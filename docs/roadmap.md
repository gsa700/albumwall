# Roadmap

What is wanted and not yet built, in his words where there are any. Not a
schedule and not in order. When something here gets built, move what was
learned into the commit message and delete the entry — this file is for what
is ahead, the history is for what is behind.

## Wanted

- **Compact view, and the same view as a hardware front panel.** His idea,
  2026-09-28, while mocking up a front panel for a stereo component: "what if we
  do add it to the main app as well as a 'Compact' display option?" And the rule
  over all of it: "a great little player for the stereo and still be awesome as a
  desktop app at the same time" — the component side must never cost the desktop
  app anything. One view at 1480 x 320 (the Waveshare 11.9" bar panel; nothing
  that matters in the 24 x 24 px corners), scaled to wherever it is shown.
  Mockup: https://claude.ai/artifact/F9MdxL1VavZV6cgLLEbJoJ

  BUILT: compact mode in the desktop app (`MainWindow.Compact.cs`), the front
  panel window for a kiosk (`FrontPanelWindow.cs`, `--front-panel`, placed by a
  labwc rule; `kiosk/`, `docs/kiosk.md`), and the vintage VU meters
  (`VuMeter.cs`). Still to do, each usable on its own:
  1. **Meter variant B**: album-tinted ground with 32-segment L/R LED bars (fast
     attack, slow release, peak hold).
  2. **Browse strip and open album on the panel**, with the remote. Needs the
     arrow-key navigation below, and settles its open question: on a remote the
     arrows must move, so seeking has to go somewhere else.
  3. **Kiosk finish**: night dimming (the yellow VU faces are the brightest
     thing on the panel), USB-to-S/PDIF coax output bit-perfect, the remote's
     buttons (Flirc + the aluminium Apple Remote is the leading pick).
  4. **A 31-band spectrum display** (2026-10-04, "can we add a 31 spectrum
     display option for albumwalls compact mode?"): third-octave, 20 Hz to
     20 kHz. Not designed. `astats` gives no per-band level, so it needs its own
     tap (or the samples in hand, once our own engine plays), and the audio must
     stay bit-exact with it in the chain.

  Later, maybe: a track list the compact view can drop open, as iTunes' mini
  player did ("lets not for now at least"). The "Next ..." line is gone and not
  coming back.

- **Kiosk covers: measure on the Pi.** Built 2026-10-21 (MainWindow.Kiosk.cs):
  with `--kiosk` the whole wall is decoded in the background once it is up,
  nearest the screen first, two at a time, and the cover cache gets a quarter
  of RAM (at most 2 GB). What was found on the way: an album keeps its decoded
  cover for as long as the library is on the wall, so scrolling BACK was never
  re-decoding; the stutter was the first pass. On the Mac (1,034 albums) the
  warm-up takes 1.4 s and holds about 385 MB more than a desktop start. Still
  to do: the same fling on Pi5-POE, before and after, by eye and with `top`
  (the 2026-09-26 baseline: CPU bursts of 100-190% of 400% while scrolling),
  and the warm-up time from its log line. The kiosk files must be reinstalled
  there for `--kiosk`, though ALBUMWALL_SUPERVISED already turns it on.

- **Desktop: covers seen stay in memory until the library reloads.** Found
  2026-10-21 while building the kiosk warm-up. ArtCache's 160 MB budget only
  drops the CACHE's reference; each AlbumVm keeps its decoded Cover for as
  long as the library is on the wall, so a full scroll of 1,034 albums holds
  them all (Techbench: RSS ~480 MB -> 1.1 GB after one pass; the Mac starts
  at ~690 MB). The status bar's "MB art" counts only the cache, so it
  understates it. Not hurting anyone yet. The fix is for a tile that leaves
  the screen (WallView.Retire) to let its albums' covers go once they are
  well out of view, re-decoding from the cache or the file when they return:
  a kiosk keeps them all on purpose (MainWindow.Kiosk.cs), a desktop would
  not. Measure before and after with the same full scroll.

- **Our own audio engine: mpv out, in steps** (agreed 2026-10-20, on Control,
  the night Deadwax 0.6.0 shipped its own FLAC encoder and decoder): "I'd like
  to do it. It simplifies the code, may increase performance / lower memory
  footprint etc. Plus, it will be fun." And: "we don't have to do it all in one
  go." What mpv costs now: seven projects per build (mpv, FFmpeg, libplacebo,
  libass, FreeType, FriBidi and the rest), two of them needed only because mpv
  will not build without them; 8-10 MB of `native/` on each of four platforms;
  a GPL engine, its source archives for every `libmpv-*` release, and FFmpeg
  CVE patches. What the app actually asks of it (`Player.cs`, `Mpv.cs`): a
  playlist, play/pause/seek/next, volume, ReplayGain, gapless, exclusive output
  and one filter.
  **The libraries it plays** (counted 2026-10-20 on Navidrome's mounts): FLAC
  4,136 files; the lossy library 8,672 MP3 and 6,857 M4A, every M4A AAC (ffprobe
  over all of them on Control's `~/MusicFolder`). So FLAC alone is a fifth of
  what is played, and MP3 and AAC cannot wait for the end.
  **The pieces:** the stream is .NET's HttpClient (`format=raw`, as now). FLAC
  is Deadwax's decoder (RFC 9639; checks every frame's CRCs and the stream MD5;
  decoded his whole FLAC library the same as FFmpeg on 2026-10-20), shared
  between the repos by copy or a small library, his call when we get there.
  Gapless is simpler than in mpv: one continuous stream of samples, and FLAC
  states its exact sample count. ReplayGain and volume are a multiply. Meters
  (Wanted, above) get the samples in hand instead of `astats`. The real work is
  the output, one per platform: CoreAudio on the Mac, WASAPI on Windows, ALSA or
  PipeWire on Linux, keeping "Desktop audio: be a good neighbour" (Decided,
  below) on a desktop and bit-perfect only on a dedicated device.
  **The steps, mpv the fallback until the last; each one ships on its own and
  a pause between any two is fine:**
  1. Our engine, FLAC only, on the Mac (CoreAudio), behind a switch; mpv
     still plays everything else and stays the default.
  2. Windows output (WASAPI), then Linux (ALSA/PipeWire); the kiosk is the
     bit-perfect test, `tools/gapless-check.cs` the measure.
  3. Every other format through a small FFmpeg of decoders only (libavcodec
     and libavformat for the containers, the same decoder list as
     `scripts/build-libmpv/inner.sh`, so Vorbis, Opus, ALAC and PCM still play
     for other people's libraries). mpv out of the path, still bundled. One
     LGPL dependency instead of seven projects; "AAC stays in the build"
     (Decided) carries over unchanged.
  4. The default, once gapless passes on all four builds.
  5. mpv and its companions removed, with their notices and source archives.
  6. Later, if wanted: the OS's own MP3/AAC decoders on the Mac and Windows
     (AudioToolbox, Media Foundation; nothing bundled), or our own MP3 decoder.
  **What it must play** (his scope, 2026-10-20): "FLAC, mp3, and aac will
  cover all my needs and most others. There are likely a few more we can
  support cheaply." While FFmpeg's decoders are bundled (step 3 on), the
  others in today's list cost a few hundred KB each and stay. Written by us,
  the cheap ones are PCM (WAV and AIFF: a header and samples) and ALAC (Apple's
  decoder is open, Apache 2.0, and it is a close cousin of FLAC; it shares the
  MP4 container with AAC). Vorbis and Opus are not cheap (Opus is RFC 6716,
  two codecs in one), so they are kept only for as long as FFmpeg is.
  Weighed and not chosen first: Navidrome transcoding everything to FLAC
  (seeking restarts the stream, no duration, gapless trimming to prove, CPU on the
  music server) and our own AAC decoder (large, a paid standard, iTunes gapless
  trims).

- **Keyboard and screen readers: hear it for real.** The arrow keys, reduce
  motion, the names and the contrast floor are built (2026-10-21; see the
  commits, MainWindow.Keyboard.cs, Motion.cs, Contrast.cs). VoiceOver on the
  Mac WORKS (his check, 0.6.22, 2026-10-21: "confirm voiceover works, it's
  REALLY verbose" - the verbosity is VoiceOver's own, name plus role plus
  any tooltip, and its users set it to taste). Not yet tried: Narrator, Orca,
  and someone who uses a screen reader every day;
  the "names" debug command shows what they are told, not how it sounds in
  use. Avalonia's Linux screen-reader support is the weakest of the three.
  `--help` and `--version` exist (CommandLine.cs); a man page for Linux can
  be generated from the Help tab's text, later.

- **Rescanning a library that is not on the wall** (2026-09-28: "could I
  rescan Music while listening to NAS Music?"). Safe to build, since e7ad028 a
  scan only touches index rows under its own root, but it buys little while
  switching already rescans through the index. Worth it with libraries that
  change while nobody looks (a server, a USB drive), as "keep the other
  libraries fresh in the background".

- **macOS: what is left.** The Mac build runs on Control from
  `scripts/make-macos-app.sh --install` with our own engine (libmpv-0.41.0-6
  carries the dylib, gapless proven), Now Playing, media keys, Cmd shortcuts and
  a menu bar. Not done:
  1. SIGNING with a Developer ID and notarization, the real gate for anyone
     else: Gatekeeper stops an unsigned download. His call: the Apple Developer
     Program is $99 a year and shows his legal name. A signed `.app` in a
     `.dmg`; not the Mac App Store, whose sandbox fights a player that reads
     arbitrary folders and shares.
  2. THE UPDATER replacing a signed bundle rather than a loose exe, without
     breaking its signature (installing is OFF in the app on macOS; the script
     makes the .app).
  3. A macOS zip in `release.sh`, once (1) and (2) exist.
  HARD RULE (2026-10-10): a release carries its own engine; Homebrew's libmpv
  was a bridge for his Mac only.
  Decided 2026-10-18, the media keys after a launch: the first press of Play
  goes to whichever app played last until AlbumWall has played once (macOS
  makes an app Now Playing only once it plays). "ok leave it as is."

## Decided

- **AlbumWall is for LISTENING; tags and art are edited elsewhere.** His
  words, 2026-10-21: "advanced tag and image editing may be better served by a
  dedicated app separate from albumwall. The goal of albumwall is simple tactile
  music LISTENING experience. I think if we add too much we will wreck that."
  And: "I don't want one huge app that tries to do it all. So I lean towards a
  small dedicated tag and art editor if we do it." So general tag editing and
  fetching art from the internet are NOT built here. What stays: the lyrics
  box in Properties (small, built, and for singing along: the one exception,
  not a first step), and the `art:` searches, which only show what needs work.
  If the editor is made, it is its own small app; Deadwax's Cover Art Archive
  and FLAC tag code are the place to start rather than a copy in AlbumWall.

- **The network remote is SSH and the command line.** 2026-10-21, after Claude
  on Control drove a test copy on Techbench with `ssh techbench AlbumWall
  --play "dark side"`; his words: "I'd say you just demonstrated #5 pretty
  effectively?" Any machine or phone with an SSH client is a remote, with
  SSH's keys and encryption, and the app opens no port of its own. A web page
  or phone app with a listener of its own comes back only if SSH falls short.
  For it to work the command socket sits beside the settings, not in the
  temp folder (SingleInstance.PipeName: a Mac's SSH login has a different
  TMPDIR from its desktop). A Mac needs Remote Login turned on to be reached.

- **Dead simple for the inexperienced, flexible for the experienced.** His
  words, 2026-10-04, while designing the first run: "the best apps are dead
  simple for the inexperienced and flexible to accommodate the more
  experienced." The test for anything new: someone who knows nothing gets a
  plain choice with examples in their own words and a default that works; the
  expert finds the knob without it being in the beginner's way.

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

- **The right-click menu and Properties** (built 2026-10-03/04). One menu on
  every library; what a library cannot do is greyed out with the reason on it,
  not left off. "Play next" is not in it, for now. Properties is one window for
  a file or a server track (Tags, Lyrics, the sleeve), light and neutral in
  the Preferences format. EDITING IS OFFERED ONLY WHERE AN EDIT LASTS: off per
  library until "Allow editing" (Library.Editable), never for a server or a
  copy that a nightly push overwrites. A write goes through Domain/TagWriter.cs:
  backup, edit a copy, read it back and compare (audio, every other tag, a
  FLAC's checksum), rename over the original; the modified time moves on
  purpose so the push and the index see it.

- **Preferences: options first, pages after, About last** (2026-10-10, 0.6.16):
  Library, Playback, Appearance, Updates, Help, About. A new tab of options only
  when its second option arrives. The Library tab is a list of libraries, each
  with a Settings window of its own (`LibrarySettingsWindow`), and the selected
  library's statistics below. Compact mode stays a runtime toggle and the
  search-box commands stay hidden test tools, on purpose.

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

- **Fetching album art from the internet.** Not in AlbumWall at all
  (2026-10-21, see Decided: AlbumWall is for listening); a small dedicated
  editor's job if one is made. The app does the best with the art in the
  files, and `art:missing`, `art:small` and `art:nonsquare` in the search box
  list the albums whose files want attention.

- **More from a server library** (2026-10-21: "I am happy with it as is for
  now"). A Navidrome server is a read-only library, gapless by ear. Writing to
  the server and a second server kind (the code speaks plain Subsonic) are not
  planned.

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
- The engine's releases are `libmpv-X.Y.Z-N` PRE-releases, so the app's
  updater never takes one for an app version; bump N when the recipe changes
  without mpv's version changing. `scripts/LIBMPV_RELEASE` names the current one.
- Paperwork goes with every release: run `tools/fetch-licenses.py` again WHEN A
  PIN MOVES (`THIRD-PARTY-NOTICES.md` and `licenses/`, carried in the exe), and
  every new `libmpv-*` release carries its own source archives
  (`scripts/build-libmpv/source-archives.sh`, with `SHA256SUMS-sources`).
  Never add `--enable-gpl` or `--enable-nonfree` to FFmpeg without revisiting
  the licenses.
- Any machine can publish every platform: the target framework follows the
  RuntimeIdentifier, not the build host. Keep it that way.
- The repository is PUBLIC, from 2026-09-28 ("can we sanitize the current repo
  and make it public?"). Its history was rewritten on the way: LAN addresses
  became machine names and the author email became GitHub's no-reply address
  (his calls: "use noreply email, name and callsign are fine ... keep machine
  names"). Nothing private goes into a commit: no addresses, no keys, no
  personal email. The pre-rewrite history stays in the private
  `albumwall-private`, and is never pushed here.
