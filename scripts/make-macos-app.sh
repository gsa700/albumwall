#!/usr/bin/env bash
# AlbumWall - build AlbumWall.app, the Mac's form of the program. On a Mac only.
#
#   scripts/make-macos-app.sh            dist/AlbumWall.app, for the machine's own arch
#   scripts/make-macos-app.sh --install  and copy it to /Applications (replacing one there)
#
# WHAT IS IN IT: the same single-file program every platform publishes (the
# audio engine, Hymie, is C# inside it, as on Linux and Windows), in the folder
# layout macOS expects of an application:
#
#   AlbumWall.app/Contents/Info.plist          name, version, identity, icon
#   AlbumWall.app/Contents/MacOS/AlbumWall     the program
#   AlbumWall.app/Contents/Resources/AlbumWall.icns
#
# Installing on a Mac IS putting that folder in /Applications: Finder,
# Launchpad, Spotlight and the Dock find it there, and Now Playing and the
# media keys name the app from it (docs/roadmap.md, macOS).
#
# SIGNED WITH APPLE'S DEVELOPER ID when this Mac has that certificate (since
# 2026-10-23, team 4QX5GG2276), else with a local certificate, else AD HOC.
# Not yet notarized, and without the hardened runtime: a DOWNLOADED copy still
# doesn't get past Gatekeeper. Notarization comes next (roadmap, macOS point 3);
# until then this is for building at home, not for a release.
#
# Why a certificate at all: macOS remembers a permission (the Music folder,
# first of all) against the app's signature. An ad hoc signature IS the build's
# own hash, so every rebuild was a stranger and asked again ("asks me everytime
# for access to Music", 2026-10-14). Signed with a certificate, the app is its
# identifier plus that certificate, the same from build to build. Without a
# Developer ID, make a local one once in Keychain Access (Certificate Assistant,
# Create a Certificate: Self-Signed Root, Certificate Type Code Signing), named
# "AlbumWall Local", or name any certificate in ALBUMWALL_SIGN_ID.
#
# The identity, io.github.gsa700.AlbumWall, is the Mac's reverse-domain form of
# the Windows AppUserModelID gsa700.AlbumWall (WindowsShell.cs). It is where
# macOS keeps the app's permissions and preferences, so it must not change.

set -euo pipefail

[ "$(uname -s)" = Darwin ] || { echo "make-macos-app.sh: on a Mac only" >&2; exit 1; }

INSTALL=no
for arg in "$@"; do
    case "$arg" in
        --install) INSTALL=yes ;;
        *) echo "usage: $0 [--install]" >&2; exit 2 ;;
    esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/AlbumWall.App"
case "$(uname -m)" in arm64) RID=osx-arm64 ;; x86_64) RID=osx-x64 ;; *) echo "unknown arch" >&2; exit 1 ;; esac
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT/AlbumWall.App.csproj" | head -1)"
BUNDLE_ID=io.github.gsa700.AlbumWall
MIN_MACOS=13.0      # libmpv's floor while it was bundled (until 0.7.0); unchanged since

OUT="$ROOT/dist"
APP="$OUT/AlbumWall.app"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "== publish $RID $VERSION"
dotnet publish "$PROJECT" -c Release -r "$RID" -o "$WORK/pub" -nologo -v quiet

echo "== icon"
# Rendered from icon.svg by macOS itself (sips reads SVG), every size drawn at
# that size. Apple's grid puts the artwork at 824 of 1024 with clear margin all
# round; a full-bleed square looks oversized beside every other Dock icon, so
# each size is drawn at 80.5% and centred on a transparent canvas.
ICONSET="$WORK/AlbumWall.iconset"
mkdir -p "$ICONSET"
for spec in 16:icon_16x16 32:icon_16x16@2x 32:icon_32x32 64:icon_32x32@2x \
            128:icon_128x128 256:icon_128x128@2x 256:icon_256x256 512:icon_256x256@2x \
            512:icon_512x512 1024:icon_512x512@2x; do
    size=${spec%%:*} name=${spec#*:}
    art=$(( size * 824 / 1024 ))
    sips -s format png -z "$art" "$art" "$PROJECT/Assets/icon.svg" --out "$WORK/art.png" >/dev/null
    sips -p "$size" "$size" "$WORK/art.png" --out "$ICONSET/$name.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$WORK/AlbumWall.icns"

echo "== bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$WORK/pub/AlbumWall" "$APP/Contents/MacOS/AlbumWall"
cp "$WORK/AlbumWall.icns" "$APP/Contents/Resources/AlbumWall.icns"
printf 'APPL????' > "$APP/Contents/PkgInfo"
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleIdentifier</key>
	<string>$BUNDLE_ID</string>
	<key>CFBundleName</key>
	<string>AlbumWall</string>
	<key>CFBundleDisplayName</key>
	<string>AlbumWall</string>
	<key>CFBundleExecutable</key>
	<string>AlbumWall</string>
	<key>CFBundleIconFile</key>
	<string>AlbumWall</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleShortVersionString</key>
	<string>$VERSION</string>
	<key>CFBundleVersion</key>
	<string>$VERSION</string>
	<key>CFBundleInfoDictionaryVersion</key>
	<string>6.0</string>
	<key>LSMinimumSystemVersion</key>
	<string>$MIN_MACOS</string>
	<key>LSApplicationCategoryType</key>
	<string>public.app-category.music</string>
	<key>NSHighResolutionCapable</key>
	<true/>
	<key>NSHumanReadableCopyright</key>
	<string>GPLv3. See What's inside, in the About tab.</string>
</dict>
</plist>
EOF
plutil -lint "$APP/Contents/Info.plist" >/dev/null

# Which certificate: ALBUMWALL_SIGN_ID if set (a name or a SHA-1); else this Mac's
# Apple Developer ID Application certificate when there is exactly one (two
# while one is being renewed: then name one); else the local "AlbumWall Local".
if [ -n "${ALBUMWALL_SIGN_ID:-}" ]; then
    SIGN_ID=$ALBUMWALL_SIGN_ID
else
    DEVID=$(security find-identity -v -p codesigning | sed -n 's/^ *[0-9]*) \([0-9A-F]\{40\}\) "Developer ID Application: .*"$/\1/p')
    case $(printf '%s' "$DEVID" | grep -c . || true) in
        0) SIGN_ID="AlbumWall Local" ;;
        1) SIGN_ID=$DEVID ;;
        *) echo "several Developer ID Application certificates on this Mac: name one in ALBUMWALL_SIGN_ID" >&2
           security find-identity -v -p codesigning | grep "Developer ID Application" >&2; exit 1 ;;
    esac
fi
if security find-identity -p codesigning | grep -qF -e "\"$SIGN_ID\"" -e ") $SIGN_ID "; then
    echo "== sign ($(security find-identity -p codesigning | grep -F -e "\"$SIGN_ID\"" -e ") $SIGN_ID " | head -1 | sed 's/^[^"]*"\([^"]*\)".*/\1/'))"
    codesign --force --sign "$SIGN_ID" "$APP"
else
    echo "== sign (ad hoc: no \"$SIGN_ID\" certificate on this Mac, so macOS will ask for the Music folder again)"
    codesign --force --sign - "$APP"
fi
codesign --verify --strict "$APP"

echo "== $APP"
du -sh "$APP" | cut -f1
file "$APP/Contents/MacOS/AlbumWall"

if [ "$INSTALL" = yes ]; then
    echo "== install to /Applications"
    if pgrep -xq AlbumWall; then
        echo "AlbumWall is running: quit it first" >&2; exit 1
    fi
    rm -rf /Applications/AlbumWall.app
    cp -R "$APP" /Applications/AlbumWall.app
    # Tell Launch Services about it now rather than whenever it next looks.
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f /Applications/AlbumWall.app
    echo "installed: /Applications/AlbumWall.app"
fi
