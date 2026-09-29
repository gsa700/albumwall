# Windows bring-up — notes for the session doing it

Written on Techbench (Fedora), 2026-09-19, for whoever picks this up on
Hambench. Nothing here has ever run on Windows. That is the whole job: find out
what happens, fix what is cheap, write down what is not.

## What this is

A music player for a local library. The window is a wall of album covers;
clicking one unfolds a panel in place, tinted from that album's artwork, and
the sleeve in the panel is the play button. Audio is libmpv. About 5,000 lines,
three assemblies: `Domain` (scanning), `Playback` (libmpv), `App` (everything
you can see).

It has been running daily on Fedora for two days and is in good shape there.

## Git on that machine

**The repository is public since 2026-09-28**, so a plain
`git clone https://github.com/gsa700/albumwall.git` works. To push, sign in:

```powershell
winget install GitHub.cli        # if gh is not there already
gh auth login                    # account: gsa700
gh repo clone gsa700/albumwall
```

`gh` then acts as the credential helper and pushes work without further
prompting. If the box already has git credentials for that account, those are
fine too.

Identity, so the history stays consistent with the commits already there:

```powershell
git config user.name  "David Erickson"
git config user.email "298062495+gsa700@users.noreply.github.com"
```

**The branch is `master`, not `main`.** Work on it directly — this is a
one-person project and a pull request to yourself is ceremony. Use a branch
only if you are trying something you might abandon.

**Pull before you start and again before you push.** The Fedora side is active
and commits to the same branch, so both ends move. **Never force-push and never
rewrite history**: the other machine may already have those commits, and there
is no recovering someone else's work from a rewritten remote.

The history WAS rewritten once, deliberately, for going public (2026-09-28).
A clone from before that shows `ahead N, behind M` with every commit
different. If it is clean and nothing in it is missing upstream
(`git log --cherry-pick --left-only master...origin/master` lists only
commits whose subjects exist upstream), keep the old history on a local
branch and move across: `git branch pre-rewrite-2026-09-28 master`,
`git reset --hard origin/master`. Never push that branch.

Push as you go rather than in one lump at the end. If the Windows session ends
badly, whatever is pushed is what survives.

## Getting it up

```powershell
cd albumwall
.\scripts\get-libmpv.ps1
dotnet run --project src\AlbumWall.App
```

**Needs the .NET 10 SDK.** The projects target `net10.0` and nothing older will
restore. If the box has 8 or 9, that is the first thing to fix:
`winget install Microsoft.DotNet.SDK.10`.

**`get-libmpv.ps1`** fetches OUR libmpv from the release named in
`scripts/LIBMPV_RELEASE`, checks it against the release's `SHA256SUMS`, and
leaves a `libmpv-source.txt` beside it saying which one it is. It uses the
GitHub CLI when it is there and the plain URL otherwise.

**Run it again whenever `scripts/LIBMPV_RELEASE` changes** - compare it with
`native/win-x64/libmpv-source.txt` after every pull. A build with the old DLL
still builds and plays, and silently loses whatever the new engine was for:
on 2026-09-28 that was the VU meters, which need astats (`-4`) and hide
themselves without it. The DLL lands in
`native/win-x64/` (git-ignored) and the project copies it beside the exe.

`-Upstream` fetches the official full build from SourceForge instead — the
rollback, and the control when the two need comparing. That path needs 7-Zip
(`winget install 7zip.7zip`), because those builds are published only as `.7z`,
and takes the plain x86_64 build deliberately, not the `-v3` one, which is
compiled for a newer instruction set and will not start on an older CPU.

## What "working" looks like

The Music folder on that machine is expected to be empty, so **the first run
should show the empty state**, not a blank window: a heading "No music here
yet", the folder it looked in, and a button to choose another. If you get a
bare window with nothing in it, that is a bug — find out why.

Then point it at a library through the hamburger menu (top right) →
Preferences → Choose folder. The FLAC library lives on the NAS at
`NASBOX:/NAS/data/Techbench/Music` (a nightly mirror of the Fedora box's
`~/Music`), so a mapped drive or a UNC path is the likely route. **Mount it
read-only if you can**: that copy is overwritten nightly by rsync from
Techbench, so anything written there is lost at 03:30.

Once it has a library, the things worth checking, roughly in order of how
likely they are to be wrong on a new platform:

- The wall renders, scrolls smoothly, and covers appear
- Window chrome: the app draws its own. `WindowButtons.cs` should give
  minimize/maximize/close on the **right**, square rather than round. Rounded
  corners and window transparency are deliberately Linux-only
- DPI: the Fedora box is a 2x display. Whatever Hambench is, check the window
  restores to a sane size and the text is not tiny or enormous
- Fonts: the UI uses Inter (embedded) and Consolas for the mono bits
- Clicking an album opens a panel, centered; a box set fills the window instead
- Playback, and **gapless across a track boundary** — that is a hard
  requirement of this project, and it has only ever been verified on Linux
- The volume slider (top right) changes only this app's loudness

## Expect these, they are not bugs

- ~~No media keys.~~ Written since: `Smtc.cs` registers with the System Media
  Transport Controls on Windows. `Mpris.StartAsync` still fails there, by
  design and silently apart from one log line — that is correct behavior, and
  `[smtc] registered…` is the line to look for instead.
- **No rounded window corners**, as above.
- Tag editing is not built; the button is disabled on purpose.

## The trigger files — read this, it will save you hours

Under `DEBUG` the app can be watched and driven through the filesystem, which
matters because you may not be able to see the window you are debugging.

Set `ALBUMWALL_SNAP` to a path before launching. Then:

- `New-Item -ItemType File -Force $env:ALBUMWALL_SNAP` — the app renders itself
  to `<path>.png` (there is no `touch` in PowerShell). It
  draws its own visual tree, so it captures the app and nothing else. Wait for
  the `[wall] snapshot` line in stdout before reading the file; it takes a
  second or two to write and reading early gets you zero bytes.
- Write a command into `<path>.cmd` and it acts on it
  (`Set-Content "$env:ALBUMWALL_SNAP.cmd" "open dirt" -NoNewline`):
  `open <text>` (substring match on artist or album), `close`, `play <n>`,
  `search <text>` (bare `search` clears), `scroll <px>`, `volume <n>`, `rescan`, `rescan stop`,
  `reveal`, `prefs`, `prefs stats`, `prefs help`, `prefs startup`, `about`, `notices`, `key <name>` (a key press: `key space`, `key ctrl+right`, `key f1`, `key /`), `sheetoff`, `light <n>`, `tint <n>`, `chrome`,
  `hover` / `hover transport`, `trace`, `hit <control name>` (what a click on that
  control's centre would land on: REACHABLE or BLOCKED, and by what).

`prefs`, `prefs startup`, `prefs colors` and `about` open the Preferences window at that tab and
`sheetoff` closes it. It is a separate window, so while it is open a snapshot
also writes `<path>.prefs.png` — its content only; the title bar is the
system's and cannot be drawn from inside the app.

`trace` prints the wall's geometry — panel height, scroll offset, extent,
viewport, and every row's realization state. Every scroll bug in this project
was found by reading that and finding two numbers that disagreed.

## Things not to do

- **Do not commit libmpv.** It is 40 MB of someone else's GPL project;
  vendoring it drags in their source obligation. The script exists for this.
- **Do not merge libraries.** One library at a time is a deliberate design
  rule, not an oversight.
- **Do not add touch-sized controls yet.** It has come up; the answer so far is
  that the concept should *flow* toward touch, not that this is a touch app.
- **Do not make the repo public.** When it goes public it will be a fresh
  snapshot into a new repo, not a flip of this one.

## Conventions

**US English, everywhere** — comments, docs, commit messages, the interface.
His rule: "Always use US English." The repository was written in British
spelling until 2026-09-19 and converted in one sweep; keep it that way. Quoting
someone verbatim keeps their spelling.

Commit messages: imperative subject line, then prose explaining **why**, with
the reasoning and anything that cost time to discover. They are long here on
purpose — the history is the design record. End with:

```
Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

Settings live in `%AppData%\albumwall\settings.json` — window geometry, library
path, volume, ReplayGain mode, and three palette values. Deleting it is a
clean first run.

## Leave notes back

Append what you find to this file, or add `docs/windows-notes.md`, and push.
The Fedora side cannot see that machine, so anything you learn there only
exists if you write it down. Particularly worth recording: what broke, what the
DPI and chrome actually did, and whether gapless survived.

## 2026-09-20 — our own libmpv for Windows needs testing there

Until now Windows has played through the upstream mpv DLL that
`get-libmpv.ps1` fetches: about 100 MB, the whole player, including H.264,
HEVC and VVC decoders the app never calls. The Fedora side now builds an
**audio-only `libmpv-2.dll` of 9.4 MB** from the same pinned recipe as the Linux
library (`scripts/build-libmpv/`, cross-compiled with mingw — which is also how
mpv's own Windows builds are made). Same mpv 0.41.0, same decoder list, one
recipe for every platform.

What was verifiable from Linux has been verified: it imports nothing but DLLs
that are part of Windows (the build now FAILS if the mingw runtime leaks into
the import table — an earlier attempt depended on `libstdc++-6.dll` and would
have loaded nowhere), and it exports all 13 `mpv_*` functions `Mpv.cs` imports.
What cannot be verified from here is everything that matters: that it loads,
that WASAPI plays, and that **gapless still holds**.

**UPDATE, later the same night — test the single-file build instead; it proves
more in one go.** The app now publishes as a true single file with libmpv
folded inside (`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`, the
family's standard), and the Fedora box can publish all three platforms. On
Linux this is verified end to end: the exe unpacks its library to
`~/.net/AlbumWall/<hash>/` and the log shows `[mpv] library:` pointing
there, not at the system's copy. The Windows exe was published from Fedora and
contains our exact DLL, byte for byte — but it has never run. One test on
Hambench therefore covers four unknowns at once: the exe starts, the DLL
self-extracts and loads, WASAPI plays, and gapless holds.

```powershell
gh release download v0.1.0-test3 --repo gsa700/albumwall --pattern "*win-x64*" --dir $env:TEMP
Expand-Archive "$env:TEMP\AlbumWall-win-x64.zip" "$env:USERPROFILE\AlbumWall-test" -Force
explorer.exe "$env:USERPROFILE\AlbumWall-test\AlbumWall.exe"    # through Explorer, per windows-notes
```

**The executable is now `AlbumWall.exe`, not `AlbumWall.App.exe`** — "drop the
.App", the family's convention (ShackPower.exe). The project folder and the
namespace keep their names; only what lands on disk changed, so a development
build is now `bin\Debug\net10.0-windows10.0.19041.0\AlbumWall.exe`. An existing
Start Menu shortcut still points at the old name: the About tab will notice
and offer to re-point it. Anything in `windows-notes.md` that says
`AlbumWall.App.exe` predates this.

It is a Release build: no trigger files, no snapshot. It uses the real
`%AppData%\albumwall` settings, so it should come up on the usual library.
Then the same checks as below — MP3 and M4A play, GAPLESS on Dark Side, volume
and mixer name. If it misbehaves, the loose-DLL steps that follow isolate
whether the DLL or the bundling is at fault.

The original loose-DLL test (the repository is private, so this goes through `gh`):

```powershell
cd albumwall
git pull
Remove-Item native\win-x64\libmpv-2.dll          # the upstream one
gh release download libmpv-0.41.0-2 --pattern "*win-x64*" --dir $env:TEMP
Expand-Archive "$env:TEMP\libmpv-0.41.0-2-win-x64.zip" native\win-x64 -Force
dotnet run --project src\AlbumWall.App
```

Keep a copy of the upstream DLL somewhere until this one has proved itself;
putting it back is the whole rollback.

Worth checking, in this order:

1. **It plays at all.** If the panel opens and nothing happens when the sleeve
   is pressed, the DLL did not load — look for `playback unavailable` in the
   log.
2. **MP3 and M4A both play.** The decoder list is short on purpose (FLAC, MP3,
   AAC, ALAC, Vorbis, Opus, PCM) and that library is entirely the second and
   third of those. On Linux all seven were played through the sibling build.
3. **GAPLESS, by ear, on Dark Side** — the same test as before, because this is
   a different ffmpeg from the one that passed it. This is the one that decides
   whether the DLL ships.
4. Volume still changes only the app, and the mixer should now name the
   session **AlbumWall** rather than mpv (`audio-client-name`, new in `06c4868`).

If it passes, `get-libmpv.ps1` should learn to fetch this release instead of
SourceForge — that change belongs to the Windows side, where it can be run.
Write what happens into `windows-notes.md`. **(Done 2026-09-20, for `-3`: it
passed, and the script now fetches it. The steps above are what the script does.)**

## 2026-09-20 — the installer needs its Windows half tested

`InstallService` is ported from Shack Power (`src/AlbumWall.App/Install/`) and
verified end to end on Linux. **None of the Windows code in it has ever run.**
Test it with the single-file exe from the newest `v0.1.0-test*` pre-release —
only a single-file build installs; a development build says so in the About
tab and is never offered.

Run the downloaded `AlbumWall.exe` from wherever it was unzipped (through
Explorer). It should offer to install. Accept, and check:

1. It copies itself to `%LocalAppData%\Programs\AlbumWall\AlbumWall.exe`,
   relaunches from there, and the loose copy exits.
2. **Settings → Apps → Installed apps lists AlbumWall**, with the icon, version
   and publisher. That entry is ONE `reg import` (the family's hard-won rule),
   verified and retried once. `%AppData%\albumwall\registration.log` has a line
   per attempt — if the entry is missing, that log says what reg.exe returned.
3. **The Start Menu shortcut exists and carries the AppUserModelID** — it is
   written by `WindowsShell.WriteShortcut`, not Windows Script Host, for exactly
   that reason. Pin it to the taskbar: the running window should group with the
   pin rather than appearing beside it.
4. A desktop shortcut appears, once. Delete it and start the app again: it
   comes back (never overwrites, but does recreate — the family's behavior).
5. Every later start logs `[install] startup: ok` — registration is re-asserted
   at every launch, never check-and-skipped.
6. **Uninstall from Installed apps** runs `--uninstall --quiet`: program, entry
   and shortcuts go; settings stay. Then install again and try
   `AlbumWall.exe --uninstall` without `--quiet`: it should ask, with a checkbox
   for the settings. The removal itself is a detached PowerShell helper that
   waits for the app to exit — check nothing is left in
   `%LocalAppData%\Programs\AlbumWall` a few seconds later.
7. About tab: says "Installed in …" with an Uninstall button, and the old
   "Add to the Start Menu" section is hidden (the installer owns that shortcut
   now; it still shows for loose and development copies).

Remember the sandbox trap from `windows-notes.md`: launch through Explorer, or
the tool shell's private `%AppData%` will make all of this look broken.
