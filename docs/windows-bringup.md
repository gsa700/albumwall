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

**The repository is PRIVATE**, so a plain clone will fail before anything else
happens. Authenticate first:

```powershell
winget install GitHub.cli        # if gh is not there already
gh auth login                    # account: gsa700
gh repo clone gsa700/albumwall
```

`gh` then acts as the credential helper and pushes work without further
prompting. If the box already has git credentials for that account, a normal
`git clone https://github.com/gsa700/albumwall.git` is fine.

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

**`get-libmpv.ps1` needs 7-Zip** — the official mpv builds are published only
as `.7z`. `winget install 7zip.7zip` if it complains. It fetches the plain
x86_64 build deliberately, not the `-v3` one, which is compiled for a newer
instruction set and will not start on an older CPU. The DLL lands in
`native/win-x64/` (git-ignored) and the project copies it beside the exe.

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
- Clicking an album opens a panel, centred; a box set fills the window instead
- Playback, and **gapless across a track boundary** — that is a hard
  requirement of this project, and it has only ever been verified on Linux
- The volume slider (top right) changes only this app's loudness

## Expect these, they are not bugs

- **No media keys.** Media keys go through MPRIS, which is D-Bus, which is
  Linux. Windows would need SystemMediaTransportControls and nobody has written
  it. `Mpris.StartAsync` swallows its own failure by design — a line in the log
  saying it is unavailable is correct behaviour.
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
  `search <text>` (bare `search` clears), `scroll <px>`, `volume <n>`, `rescan`,
  `reveal`, `prefs`, `prefs startup`, `about`, `sheetoff`, `light <n>`, `tint <n>`, `chrome`,
  `hover` / `hover transport`, `trace`.

`prefs`, `prefs startup` and `about` open the Preferences window at that tab and
`sheetoff` closes it. It is a separate window, so while it is open a snapshot
also writes `<path>.prefs.png` — its content only; the title bar is the
system's and cannot be drawn from inside the app.

`trace` prints the wall's geometry — panel height, scroll offset, extent,
viewport, and every row's realisation state. Every scroll bug in this project
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
