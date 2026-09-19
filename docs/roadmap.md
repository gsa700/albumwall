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
  Started 2026-09-19 with two tabs, Library and Startup. Still to come as tabs:
  - **Colours** — the Light / Tint / chrome levers that used to live in the top
    bar. They were hidden once the values settled (21 / 2 / ink), but he
    expects the right numbers to differ per screen and wants them back here.
    Until then `ALBUMWALL_BENCH=1` shows the old controls.
  - whatever else turns out to need a setting. Add the tab when the second
    option for it arrives, not before.

- **Tag editing.** The button in the album panel is there and disabled on
  purpose.

- **Media keys on Windows.** They go through MPRIS on Linux; Windows needs
  SystemMediaTransportControls and nobody has written it.

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
