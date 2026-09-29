# AlbumWall as a dedicated player (kiosk)

A machine that does nothing but play music: AlbumWall full screen on its main
screen, and optionally a **front panel** — a small bar display showing what is
playing, with the cover, the progress, and a pair of VU meters, in the playing
album's color. Built and run on a Raspberry Pi 5 with a 4K touchscreen and a
Waveshare 11.9" bar display (1480 x 320), meant to sit in a stereo like a CD
player. Nothing here is specific to that hardware except the defaults.

It is a separate login session, **AlbumWall Kiosk**, next to the normal desktop:
the machine logs into one or the other, and one command switches between them.
The normal desktop and its settings are not touched.

## What you need

- Raspberry Pi OS (bookworm or later, 64-bit) with its labwc desktop, or another
  Linux that logs in with **lightdm** and has **labwc** — the kiosk is labwc with a
  config of its own.
- `wlr-randr` (sets the screens up; `sudo apt install wlr-randr`), `dbus-send`
  and `wpctl` (already on a Pi OS desktop).
- AlbumWall installed for the user the machine logs in as: download
  `AlbumWall-linux-arm64.zip` from the releases, unzip it, run it once and let it
  install itself.
- **Automatic login** in lightdm (`raspi-config` → System → Boot / Auto login →
  Desktop Autologin), so there is an `autologin-session=` line to switch.

## Setting it up

From a copy of this repository on the machine (or just its `kiosk/` folder):

```sh
kiosk/install.sh --main HDMI-A-1 --panel HDMI-A-2 \
                 --touch "Silicon Works Multi-touch SW4101C (USB 3-1.2)"
albumwall-kiosk-mode on now
```

- `--main` is the screen the wall fills, `--panel` the front panel's screen, or
  `none`. Names are the compositor's: run `wlr-randr` in a desktop session.
- `--touch` pins a touchscreen to the main screen; without it a second screen can
  take the touches. The name is what `libinput list-devices` shows.
- `--scale`, `--mode` and `--panel-transform` (0, 90, 180, 270) default to a 4K
  screen at 2x and a bar panel standing on its side.

The settings are kept in `~/.config/albumwall-kiosk/kiosk.conf`; running
`install.sh` again keeps them and changes only what is given. It keeps each file
it replaces beside the new one as `*.bak`.

**Switching:** `albumwall-kiosk-mode on|off|status [now]`. `off` goes back to the
desktop (`rpd-labwc` by default, `DESKTOP_SESSION_NAME` in `kiosk.conf`); `now`
restarts the display at once, otherwise it takes effect at the next login. In the
kiosk there is no way out but this, from SSH or a keyboard's text console:
labwc's own shortcuts are switched off.

## What is where

| file | what it is |
|---|---|
| `/usr/local/bin/albumwall-kiosk` | the session: starts labwc with the kiosk's config, sets the screens up, and runs AlbumWall in a loop |
| `/usr/local/bin/albumwall-kiosk-mode` | switches lightdm between the kiosk and the desktop |
| `/usr/share/wayland-sessions/albumwall-kiosk.desktop` | the session entry lightdm offers |
| `~/.config/albumwall-kiosk/kiosk.conf` | the settings |
| `~/.config/albumwall-kiosk/labwc/` | labwc's config for the kiosk only: window rules, media keys, an empty menu |
| `~/.cache/albumwall-kiosk.log` | the session's log, AlbumWall's console included |

The sources are in `kiosk/`; `rc.xml.in` is the template `install.sh` fills in.

## How it works, and why each part is the way it is

**labwc, not Cage.** Cage, a compositor made for running one app, was tried first
(2026-09-27); AlbumWall hit a Wayland protocol error under it, "buffer size is not
divisible by scale". labwc with a stripped config does the same job and is what a
Pi desktop already runs.

**The app does not place its windows; the compositor does.** On Wayland an
application cannot choose which screen a window goes to. So `rc.xml` has a rule
per window, matched by **title**: `AlbumWall` (the wall) is moved to the main
screen and made full screen; `AlbumWall Front Panel` is moved to the panel and
made full screen. Three things were learned the hard way:

- **Move first, then full screen.** labwc will not move a full-screen window.
- **Move the wall too.** labwc opens a new window on the screen under the pointer;
  with the panel switched on, the wall once came up on the panel, under the front
  panel, and the main screen stayed black.
- **Match by title only.** AlbumWall sets its app ID just after a window appears,
  and labwc applies rules when it appears, so a rule that also asked for
  `identifier="albumwall"` missed the front panel.

**The front panel** is AlbumWall's own window, opened by `--front-panel`, which
only the kiosk passes. It has no controls and never takes the keyboard; the wall
keeps it. It shows the playing screen in the album's color and a clock after two
minutes paused.

**Supervised updates.** The loop restarts AlbumWall whenever it exits — after a
crash, or after an update. It sets `ALBUMWALL_SUPERVISED=1`, and with that set an
update puts the new program in place before AlbumWall exits and leaves the
restart to the loop. Without it the updater starts one copy and the loop another
(this happened on the first update). Updating is then just Preferences › About.
The loop gives up after three exits within 20 seconds of starting, rather than
spinning; the log says why.

**Media keys** from a keyboard or a remote are bound in `rc.xml` and sent to
AlbumWall over MPRIS, whether or not it has the focus. Volume keys set the
output's volume, capped at 100%.

**The desktop keeps the panel off.** If the desktop session should not show the
panel, switch it off there (on Pi OS, in `~/.config/kanshi/config`:
`output HDMI-A-2 disable`); the kiosk switches it on for itself.

## When something is wrong

- **Which windows exist, and are they full screen:** `wlrctl toplevel list`
  (`sudo apt install wlrctl`), run with `XDG_RUNTIME_DIR=/run/user/$(id -u)
  WAYLAND_DISPLAY=wayland-0` from SSH.
- **After editing `rc.xml`:** reload labwc with `kill -HUP $(pgrep -x labwc)`
  (`labwc -r` needs the compositor's PID in its environment), then restart
  AlbumWall so its windows are placed again.
- **After editing the launcher or `kiosk.conf`:** restart the session
  (`albumwall-kiosk-mode on now`). A running shell script does not re-read its
  file, so a change there does nothing until then. To check what a running
  AlbumWall was really started with:
  `tr '\0' '\n' < /proc/$(pgrep -x AlbumWall)/environ | grep ALBUMWALL`.
- **Two AlbumWalls:** `pgrep -a -x AlbumWall`. One is expected, with
  `--front-panel` if there is a panel.
- **No sound after a boot with the screen off:** WirePlumber can leave HDMI audio
  on its "off" profile; `systemctl --user restart wireplumber` with the screen on.
