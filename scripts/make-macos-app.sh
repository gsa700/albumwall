#!/usr/bin/env bash
# AlbumWall - build AlbumWall.app, the Mac's form of the program. On a Mac only.
#
#   scripts/make-macos-app.sh            dist/AlbumWall.app, for the machine's own arch
#   scripts/make-macos-app.sh --install  and copy it to /Applications (replacing one there)
#
# WHAT IS IN IT: the same single-file program every platform publishes (the
# engine, our own libmpv.2.dylib from native/osx-<arch>/, is inside it, as on
# Linux and Windows), in the folder layout macOS expects of an application:
#
#   AlbumWall.app/Contents/Info.plist          name, version, identity, icon
#   AlbumWall.app/Contents/MacOS/AlbumWall     the program
#   AlbumWall.app/Contents/Resources/AlbumWall.icns
#
# Installing on a Mac IS putting that folder in /Applications: Finder,
# Launchpad, Spotlight and the Dock find it there, and Now Playing and the
# media keys name the app from it (docs/roadmap.md, macOS).
#
# SIGNED WITH A LOCAL CERTIFICATE when this Mac has one, AD HOC otherwise.
# Neither is Apple's Developer ID: both are enough for Apple silicon to run what
# was built here, neither lets a DOWNLOADED copy past Gatekeeper. Real signing
# and notarization are his call (roadmap, macOS point 3); until then this is for
# building at home, not for a release.
#
# Why a local certificate at all: macOS remembers a permission (the Music
# folder, first of all) against the app's signature. An ad hoc signature IS the
# build's own hash, so every rebuild was a stranger and asked again ("asks me
# everytime for access to Music", 2026-10-14). Signed with a certificate, the
# app is its identifier plus that certificate, the same from build to build.
# Make one once in Keychain Access (Certificate Assistant, Create a Certificate:
# Self-Signed Root, Certificate Type Code Signing), named "AlbumWall Local", or
# name yours in ALBUMWALL_SIGN_ID.
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
MIN_MACOS=13.0      # the engine's own floor (build-libmpv: MACOSX_DEPLOYMENT_TARGET)

OUT="$ROOT/dist"
APP="$OUT/AlbumWall.app"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Without our engine the app would fall back to Homebrew's, which most Macs do
# not have (the HARD RULE in the roadmap). A bundle without it is not worth making.
[ -f "$ROOT/native/$RID/libmpv.2.dylib" ] || {
    echo "no native/$RID/libmpv.2.dylib: build it first (scripts/build-libmpv/build.sh)" >&2; exit 1; }

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

SIGN_ID="${ALBUMWALL_SIGN_ID:-AlbumWall Local}"
if security find-identity -p codesigning | grep -qF "\"$SIGN_ID\""; then
    echo "== sign ($SIGN_ID)"
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
