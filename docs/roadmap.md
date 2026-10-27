# Roadmap

What is wanted and not yet built, in his words where there are any. Not a
schedule and not in order. When something here gets built, move what was
learned into the commit message and delete the entry — this file is for what
is ahead, the history is for what is behind.

## Wanted

- **The road to Native AOT, as Deadwax took it** (his direction 2026-10-23:
  Native AOT on every platform, "building per platform wouldn't be so bad if it
  makes the app better"; drafted 2026-10-25 after Deadwax 0.8.0 shipped it). What
  it bought Deadwax, measured: downloads 42-45 MB -> 19 MB, the window up 3-9x
  sooner (Mac 0.42 -> 0.13 s, Linux 0.69 -> 0.08 s, Windows 1.55 -> 0.26 s),
  about 29% less memory, no runtime unpacked into a user-writable ~/.net, and on
  the Mac the hardened runtime with no exception at all (no allow-jit).

  WHERE IT STARTS (trial 2026-10-25 on Control, 0.7.7 source): `dotnet publish
  src/AlbumWall.App -c Release -r osx-arm64 -p:PublishAot=true` BUILDS: a 27 MB
  program, with libAvaloniaNative, libSkiaSharp, libHarfBuzzSharp and
  libe_sqlite3 beside it (av_libglesv2 too on Windows). Not launched yet. Its
  warnings, few and known:
  - JSON by reflection: Settings.Load/Save, Session.Load/Save, SingleInstance
    (Send/Listen, a string[]). Fix: a JsonSerializerContext per type, as Deadwax
    did; check the files come out byte-identical.
  - WaylandShell: Deadwax's copy, already fixed there (DynamicDependency on the
    private Avalonia and NWayland members it reaches, and the reason for the
    suppression). Take that fix; the two stay in step.
  - MainWindow.SuppressDecorationPopover reaches into a private member by
    reflection: keep it alive the same way, or find a public route.
  - InstallService.IsSingleFile (IL3000): an empty Assembly.Location is the
    answer under AOT too; suppress with the reason, as Deadwax did.
  - TagLibSharp "produced trim warnings" (IL2104). THE REAL RISK: the library
    scanner and the tag writer go through it, and it finds a file's type by
    reflection. Prove tag reading for every format the scanner meets, and
    writing, in the AOT build; if it breaks, root the assembly
    (TrimmerRootAssembly) before anything cleverer.
  Not tried yet: the Windows build (SMTC goes through WinRT on the -windows
  target; CsWinRT has its own AOT mode) and linux-arm64.

  BEFORE IT, in this order: 0.7.7 to Stable (done 2026-10-25). The security
  review (last one 2026-10-10; decided 2026-10-24 to come after that Stable):
  the updater and its kiosk path, Hymie's decoders on untrusted files, the
  Navidrome client. The Mac items below (Developer ID signing exists; then
  notarization and the hardened runtime, a .dmg, the bundle updater, a macOS
  zip in release.sh): Deadwax's make-macos-app.sh and MacUpdate.cs are the
  model. CI on all three systems (only macos.yml today), plus linux-arm64 on
  GitHub's ubuntu-22.04-arm runners.

  THE STEPS (Deadwax's, with AlbumWall's own wrinkles):
  1. Clean the warnings while the single file still ships; IsAotCompatible on
     the libraries so new ones show at build time; tests and a listen unchanged.
  2. The AOT build proven on each system: Mac (Control), Windows (Hambench or
     Mobilenode), Linux x64 (Techbench), and the Pi kiosk (linux-arm64): launch,
     scan a library (tags from every format), play every codec Hymie has,
     gapless, Navidrome, Now Playing / MPRIS / SMTC, the kiosk's front panel.
  3. Releases from CI: each runner builds its own system (AOT cannot build for
     another); release.sh takes the run of the release commit (a push or a
     manual start only), checks the exact file list, the first bytes, and
     "X.Y.Z+<commit>" in the program, and packs AlbumWall-<rid>-aot.zip; the Mac
     is made on Control. Linux builds on Ubuntu 22.04 (glibc 2.34 floor; the Pi
     OS bookworm has 2.36), and CI fails if the floor rises.
  4. A BRIDGE release first, still single file: an updater that installs a
     package (the libraries first, the program last, a backup, a start check
     and a rollback), prefers the -aot name, and an installer that copies the
     libraries. Deadwax 0.7.6 is the model (UpdateService, InstallLayout.
     NativeLibraries). AlbumWall's own wrinkle: the SUPERVISED path (a kiosk's
     launcher sets ALBUMWALL_SUPERVISED=1, the program is replaced in place
     before it exits and the supervisor restarts it). A start check there needs
     its own design, or a bad build restarts for ever. His call before building.
  5. Self-update 0.7.x -> 0.8 proven on every system, the kiosk included, before
     Stable; then measure start-up, memory and size for the notes.

  LESSONS FROM DEADWAX'S ROAD: a test copy started as a systemd SERVICE takes
  the update helper down with it (use a scope, as the desktop does); a copy
  checks the update feed once at start and keeps the answer; files go in by
  copy-then-rename, never written in place under a running copy;
  GetFolderPath needs DoNotVerify for a folder that may not exist yet; on
  Windows a USERPROFILE override does not move the home folder; the Mac's
  "drutil tray open" quirk is Deadwax's alone.

- **Hymie, our own audio engine: mpv out, in steps** (agreed 2026-10-20, on Control,
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
  **The name:** Hymie, his pick on 2026-10-22, after Get Smart's robot, who
  does exactly what he is told and nothing more, as a bit-perfect engine
  should; and it answers to Control, as Hymie did. HymieEngine in the code,
  and Hymie in settings.json (OwnEngine before, not carried over: nobody else
  ran it yet).
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
     still plays everything else and stays the default (until 2026-10-22,
     when our engine became the default, his call for the Edge release: "we're
     on the edge, enable it by default"; the switch still turns it off). BUILT 2026-10-21
     (FlacEngine, Preferences > Playback > Engine; the commit says how), and
     heard on Control the same night: identical to his ear, no gap or click
     at a boundary. A stream from a server too, since 2026-10-22 (seen on
     Mobilenode that morning: a Navidrome album still went to mpv with the
     switch on). DONE the same day (TrackBytes; the commit says how): played
     as mpv plays one, from the first frames while the rest arrives, MD5
     exact off the server, heard there by him, 1984 into Jump with no gap
     the record does not have. Local files are read the same way now.
  2. The other outputs; only `AudioQueueOutput.cs` is the Mac's, the rest of
     FlacEngine is plain .NET. His call 2026-10-21 ("put that at the top of
     the list, we'll get into it next session"), in this order; (a) and (c)
     done, (b) NEXT, when Pi5-POE has a screen and audio again:
     a. Linux, through PulseAudio's simple API (libpulse-simple, which
        PipeWire serves too): the smallest of the three, and shared like mpv
        on a desktop. BUILT 2026-10-22 (PulseOutput; the commit says how),
        checked muted and through a null sink, bit-exact; heard by him on
        Techbench the same afternoon, High 'n' Dry's Bringin' On the
        Heartbreak into Switch 625: "nice gapless transition", pause and
        seeks fine. The simple API has no pause or volume of its own: a
        pause is a flush, and the slider is heard 0.2 s late, which he found
        "barely perceptible" and fine. The full API is not needed.
     b. Linux, ALSA direct, for the kiosk only: the device taken for itself,
        S/PDIF bit-perfect. Builds on (a).
     c. Windows, WASAPI shared mode: COM from .NET without a library, the
        most work. BUILT 2026-10-22 on Mobilenode, not Hambench, his call
        (WasapiOutput; the commit says how). Checked muted with
        `tools/engine-check.cs` and heard there by him on five albums:
        gapless, pause and volume fine. Simpler than (a): Stop keeps what is
        buffered, so a pause loses nothing, and the stream's own volume is
        heard within 10 ms. A seek or a jump opened the device again, which
        he heard as sluggish: measured, 420 ms, nearly all of it
        IAudioClient::Initialize (360 ms on Mobilenode whatever the format,
        so the driver's). FIXED the same day: the stream is kept, stopped,
        reset and refilled (IOutput.Restart), 10-26 ms; "seeking is instant
        now". The Mac and Pulse outputs still open anew, and need not: a
        seek on the Mac measured 34-44 ms to sound again (an AudioQueue is
        quick to make), and on Techbench he heard seeks as instant.
     The kiosk is the bit-perfect test, `tools/gapless-check.cs` the measure.
  3. MP3 and AAC by decoders of our own in C#, as FLAC is: CHANGED from a
     small FFmpeg of decoders only, his call 2026-10-22 ("replace the top 3
     decoders with native C# versions, FLAC is done, next should be mp3 and
     aac. Others later as needed"; the goal "a clean replacement decoder to
     eventually lead to total removal of mpv"). The decoders are one project,
     `src/AlbumWall.Codecs`: each format a small codec core on shared parts
     (the bytes as they arrive, ReplayGain, exact seeks, one `IAudioTrack`).
     Measured against ffmpeg by `tools/engine-check.cs compare`, since lossy
     formats carry no MD5: the same number of samples proves the gapless
     trimming is mpv's, and ISO 11172-4's full accuracy bounds the rest.
     a. MP3: BUILT 2026-10-22 on Mobilenode (Mp3Track, Mp3.cs ported line for
        line from minimp3, CC0; the commit says how). 13 tracks of four albums
        the same length as ffmpeg's, 1e-7 RMS from it; seeks exact; heard by
        him, from Navidrome, in a noisy room: "it sounds good". Worth a quiet
        listen to Abbey Road's side two.
     b. AAC: BUILT 2026-10-22 on Mobilenode (Mp4Track, Aac.cs ported from
        FAAD2's LC path, GPL v2 or later; the commit says how). AAC-LC in MP4,
        mono or stereo, which is all of his iTunes library; HE-AAC (SBR) and
        multichannel AAC are refused when opened (ALAC since (c)). 9 tracks (Dark Side,
        Wish You Were Here, Live After Death) the same length as ffmpeg's,
        2e-8 RMS from it; seeks exact; heard by him from Navidrome, Dark
        Side's joins included: "its good". A track the decoders do not play
        (ALAC or HE-AAC in an .m4a, multichannel, MP2 named .mp3) was skipped,
        the engine being chosen by the name; since the same day it is handed
        to mpv instead (UnsupportedAudioException, HymieEngine.HandOff): at once
        if it is the first, after the track before it if not, mpv playing
        the rest of the queue. Not probed ahead, which for a server's tracks
        would be a request each before the first sample. With mpv gone (5),
        such a track is passed over, and the log says why.
     c. WAV and ALAC, his pick 2026-10-22 when mpv went (Vorbis, Opus and
        HE-AAC not: none in his libraries; .ogg and .opus are no longer
        scanned). WAV: WavTrack, plain samples in RIFF chunks. ALAC: ported
        from Apple's decoder into Mp4Track beside AAC.
  4. The default: DONE 2026-10-22, 0.6.25 on Edge ("we're on the edge,
     enable it by default").
  5. mpv and its companions removed, with their notices and source archives:
     DONE 2026-10-22 for 0.7.0, his call ("I think it's worth doing now ...
     the sooner the better IMO. Less reworking later?"). MpvEngine, Mpv.cs,
     the Preferences switch, the hand-off, the bundled libmpv and its build
     recipe, fetch script, release script and pin, gapless-check, the mpv and
     FFmpeg notices; the meters' 200 ms hold-back too, Hymie measuring where
     the output plays. The libmpv-* releases stay on GitHub as history, never
     offered (ReleaseFeed).
  6. Later, if wanted: the OS's own decoders on the Mac and Windows
     (AudioToolbox, Media Foundation; nothing bundled) for the rarer formats.
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

- **An Apple TV app, as another kind of kiosk** (his question 2026-10-25,
  "could this be adapted to an Apple TV app?"). LOWER PRIORITY THAN THE MAIN
  PLAYER: a note, nothing to build before the road to Native AOT is done. What
  it would be: a companion app, not a port. tvOS IS a kiosk already (full
  screen, one job, no session or supervisor to manage), and the shared C#
  (the library model, Hymie's decoders, playback) can run there, but only
  compiled ahead of time (no JIT on tvOS), so the AOT road above is its
  groundwork. The music comes from Navidrome (an Apple TV holds no library and
  does not browse shares easily; the Navidrome client exists). The Siri Remote
  moves by focus, much like the wall's arrow keys. What does not carry over:
  the window layer (Avalonia does not target tvOS, as far as is known, so the
  wall and panels would be rebuilt in Apple's UI around the shared core; the
  bulk of the work), and the front panel (one HDMI output). Installed on his
  own Apple TV through his Developer account (Xcode or TestFlight), no App
  Store. FIRST QUESTION, before any work: the audio path. The understanding is
  that an Apple TV outputs at a fixed 48 kHz, which would resample 44.1 kHz CD
  rips; measure that on a real Apple TV, since for this player it could decide
  the whole idea.

- **Keyboard and screen readers: hear it for real.** The arrow keys, reduce
  motion, the names and the contrast floor are built (2026-10-21; see the
  commits, MainWindow.Keyboard.cs, Motion.cs, Contrast.cs). VoiceOver on the
  Mac WORKS (his check, 0.6.22, 2026-10-21: "confirm voiceover works, it's
  REALLY verbose" - the verbosity is VoiceOver's own, name plus role plus
  any tooltip, and its users set it to taste). LINUX, checked over SSH on
  Techbench through the accessibility bus Orca reads (2026-10-21): Avalonia 12
  speaks AT-SPI from its X11 backend only, so on native Wayland Orca saw
  nothing; AlbumWall now goes through XWayland when assistive tech is on
  (Program.ScreenReaderRunning), and there everything is named. The parts
  inside sliders and scroll bars ("Thumb", "RepeatButton", "Line up") still
  show in the raw tree: Avalonia's AT-SPI bridge ignores AccessibilityView,
  and they take no focus, so keyboard use never lands on them. NARRATOR on
  Windows passed (his check on Hambench, 0.7.3, 2026-10-22: "all good except
  track seek"), and found two keyboard bugs, both fixed in 0.7.4: the arrows
  ignored a panel that had opened ABOVE its album (they now follow the
  screen), and the focused position slider jumped a whole track per arrow
  without seeking (← → and Shift+← → now seek 10 s there, Home goes to the
  start). Both checked by him on the Mac in 0.7.5 (2026-10-23). The
  keyboard's ring vanished on light sleeves (his, 2026-10-23: "leaving you
  wondering what is selected"); since 0.7.6 it is a thin line round a band of
  the opposite color, light-round-dark on a light cover and dark-round-light
  on the rest, chosen from the cover's edge (KeyRing.cs). Not yet tried:
  Orca's speech itself, and someone who uses a screen
  reader every day;
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
  `scripts/make-macos-app.sh --install` with Hymie (CoreAudio through an
  AudioQueue; libmpv-0.41.0-6's dylib until 0.7.0), Now Playing, media keys, Cmd shortcuts and
  a menu bar. Not done:
  1. SIGNING with a Developer ID and notarization, the real gate for anyone
     else: Gatekeeper stops an unsigned download. His call: the Apple Developer
     Program is $99 a year and shows his legal name. A signed `.app` in a
     `.dmg`; not the Mac App Store, whose sandbox fights a player that reads
     arbitrary folders and shares.
     DONE 2026-10-27, the .dmg too (`make-macos-app.sh --dmg`, notarized
     and stapled with --notarize; not attached to a release yet) (security
     review 2026-10-26, M2):
     the Developer ID signs with the hardened runtime (allow-jit only), the
     native libraries are signed inside the bundle rather than unpacked into
     ~/.net, startup hooks are off, and `make-macos-app.sh --notarize` has
     Apple notarize and staple it.
  2. THE UPDATER replacing a signed bundle rather than a loose exe, without
     breaking its signature (installing is OFF in the app on macOS; the script
     makes the .app).
  3. A macOS zip in `release.sh`, once (1) and (2) exist.
  HARD RULE (2026-10-10): a release carries its own engine; Homebrew's libmpv
  was a bridge for his Mac only. (Moot since 0.7.0: the engine is the app's C#.)
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

- **AAC stays in the build.** (Written for FFmpeg's decoder, 2026-10; since
  0.7.0 the decoder is our FAAD2 port, AAC-LC only, which is the conservative
  lever below already pulled: no SBR, no Parametric Stereo.) It is the one codec
  we ship with a live patent question: the base AAC-LC patents (1997-2000) have
  largely aged out, but the HE-AAC extensions (SBR and Parametric Stereo,
  2003-2006) may still be live in places for a few more years, and FFmpeg's
  decoder handles all of it. Fedora's answer is a decoder stripped to LC only.
  Ours is to ship it, as VLC and mpv have for two decades: decode-only, free,
  non-commercial, from an individual is not a profile patent pools pursue, and
  his Windows library is 6,876 M4A files. Everything else in the build — FLAC,
  Vorbis, Opus, ALAC, PCM, and MP3 since its patents expired in 2017 — is clean.
  The conservative lever exists and is one line: drop `aac,aac_latm` from the
  decoder list in `scripts/build-libmpv/inner.sh`. **Revisit with an actual
  attorney if the app ever becomes commercial.** None of this is legal advice;
  it is the reasoning, written down so it does not have to be reconstructed.

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
- UNTIL 0.7.0 (2026-10-22), when libmpv went; kept as the record of how it was
  done. libmpv is never committed: FETCHED on Windows (`scripts/get-libmpv.ps1`),
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
  (until 0.7.0) every new `libmpv-*` release carried its own source archives
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
