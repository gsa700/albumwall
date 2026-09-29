#!/bin/sh
# AlbumWall kiosk: install or update the kiosk session on this machine. See docs/kiosk.md.
#
#   kiosk/install.sh [--main OUTPUT] [--panel OUTPUT|none] [--panel-transform 0|90|180|270]
#                    [--scale N] [--mode WxH@RHz] [--touch "DEVICE NAME"] [--desktop SESSION]
#
# Run as the user the kiosk logs in as (it asks for sudo for the system files). Safe to run
# again: the previous files are kept beside the new ones as *.bak. It does NOT switch to the
# kiosk; `albumwall-kiosk-mode on now` does. A running kiosk takes changes to the launcher
# only when its session restarts.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
CONFDIR="$HOME/.config/albumwall-kiosk"
LABWC="$CONFDIR/labwc"

# Start from the settings already there, then the defaults, then the command line.
[ -f "$CONFDIR/kiosk.conf" ] && . "$CONFDIR/kiosk.conf" || . "$HERE/kiosk.conf"
TOUCH=${TOUCH_DEVICE:-}
while [ $# -gt 0 ]; do
    case "$1" in
        --main) MAIN_OUTPUT=$2; shift ;;
        --panel) PANEL_OUTPUT=$2; [ "$2" = none ] && PANEL_OUTPUT=""; shift ;;
        --panel-transform) PANEL_TRANSFORM=$2; shift ;;
        --scale) MAIN_SCALE=$2; shift ;;
        --mode) MAIN_MODE=$2; shift ;;
        --touch) TOUCH=$2; shift ;;
        --desktop) DESKTOP_SESSION_NAME=$2; shift ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

for need in labwc wlr-randr dbus-send wpctl; do
    command -v "$need" >/dev/null || { echo "missing: $need (see docs/kiosk.md)" >&2; exit 1; }
done
# lightdm is in /usr/sbin, not on a user's PATH.
[ -x /usr/sbin/lightdm ] || command -v lightdm >/dev/null || { echo "missing: lightdm (see docs/kiosk.md)" >&2; exit 1; }
[ -x "$HOME/.local/share/albumwall/AlbumWall" ] || { echo "AlbumWall is not installed for $USER (run it once and let it install itself)" >&2; exit 1; }

keep() { [ -f "$1" ] && cp -p "$1" "$1.bak" || true; }
mkdir -p "$LABWC" "$HOME/.cache"

keep "$CONFDIR/kiosk.conf"
# The template with this machine's values filled in, comments and all.
sed -e "s|^MAIN_OUTPUT=.*|MAIN_OUTPUT=$MAIN_OUTPUT|" \
    -e "s|^MAIN_MODE=.*|MAIN_MODE=$MAIN_MODE|" \
    -e "s|^MAIN_SCALE=.*|MAIN_SCALE=$MAIN_SCALE|" \
    -e "s|^PANEL_OUTPUT=.*|PANEL_OUTPUT=$PANEL_OUTPUT|" \
    -e "s|^PANEL_TRANSFORM=.*|PANEL_TRANSFORM=$PANEL_TRANSFORM|" \
    -e "s|^TOUCH_DEVICE=.*|TOUCH_DEVICE=\"$TOUCH\"|" \
    -e "s|^DESKTOP_SESSION_NAME=.*|DESKTOP_SESSION_NAME=$DESKTOP_SESSION_NAME|" \
    "$HERE/kiosk.conf" > "$CONFDIR/kiosk.conf"

# The touchscreen, pinned to the wall's screen; without it a second screen can take the touches.
if [ -n "$TOUCH" ]; then
    TOUCHLINE="<touch deviceName=\"$TOUCH\" mapToOutput=\"$MAIN_OUTPUT\" mouseEmulation=\"no\"/>"
else
    TOUCHLINE="<!-- no touchscreen pinned: kiosk/install.sh --touch \"NAME\" (libinput list-devices) -->"
fi
keep "$LABWC/rc.xml"
sed -e "s|@MAIN_OUTPUT@|$MAIN_OUTPUT|g" -e "s|@PANEL_OUTPUT@|$PANEL_OUTPUT|g" -e "s|@TOUCH@|$TOUCHLINE|" \
    "$HERE/rc.xml.in" > "$LABWC/rc.xml.new"
[ -n "$PANEL_OUTPUT" ] || sed -i '/<!--PANEL-BEGIN-->/,/<!--PANEL-END-->/d' "$LABWC/rc.xml.new"
python3 -c 'import sys, xml.dom.minidom as m; m.parse(sys.argv[1])' "$LABWC/rc.xml.new" \
    || { echo "rc.xml did not come out as valid XML; nothing replaced" >&2; exit 1; }
mv "$LABWC/rc.xml.new" "$LABWC/rc.xml"
cp "$HERE/menu.xml" "$LABWC/menu.xml"
[ -f "$HOME/.config/labwc/environment" ] && cp "$HOME/.config/labwc/environment" "$LABWC/environment" || true

sudo sh -c "
    for f in /usr/local/bin/albumwall-kiosk /usr/local/bin/albumwall-kiosk-mode /usr/share/wayland-sessions/albumwall-kiosk.desktop; do
        [ -f \"\$f\" ] && cp -p \"\$f\" \"\$f.bak\" || true
    done
    install -m 755 '$HERE/albumwall-kiosk' /usr/local/bin/albumwall-kiosk
    install -m 755 '$HERE/albumwall-kiosk-mode' /usr/local/bin/albumwall-kiosk-mode
    install -m 644 '$HERE/albumwall-kiosk.desktop' /usr/share/wayland-sessions/albumwall-kiosk.desktop
"

echo "AlbumWall kiosk installed: wall on ${MAIN_OUTPUT}${PANEL_OUTPUT:+, front panel on $PANEL_OUTPUT}."
echo "Switch to it with:  albumwall-kiosk-mode on now     (and back:  albumwall-kiosk-mode off now)"
