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

- **A proper way to start it on Windows: shortcut, icon, app identity.** Today it
  is launched from a terminal or by double-clicking the exe in `bin`. It wants a
  Start Menu shortcut with an icon, and the shortcut should carry an
  AppUserModelID that the app also sets on itself at startup
  (`SetCurrentProcessExplicitAppUserModelID`). That ID is what Windows uses to
  name an unpackaged program: without it the media flyout labels the player
  `AlbumWall.App.exe`, and the taskbar cannot group or pin it properly. There
  is no icon yet either — the windows have none. His other tools install
  themselves with an `install.ps1`; the same shape would suit. **Wait for the
  name to be locked before baking it into an ID** — see below.

- **The name is not locked.** `AlbumWall` is the front-runner and he is living
  with it on purpose ("lets keep rockin it for a while more"); do not push him
  to decide. If it stays there is no rename work: folder, solution, assemblies,
  namespaces, settings folder and the header already say it. The full history
  is in the Techbench project notes. Settled regardless: any companion tool is
  called **Deadwax**.

- **Tag editing.** The button in the album panel is there and disabled on
  purpose.

- **A seek bar in the Windows media flyout.** `Smtc.cs` tells Windows what is
  playing and takes its buttons, but does not send timeline properties, so the
  flyout shows the track without a position. Nobody has asked.

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
- libmpv is fetched, never committed. The repository stays private.
