#!/usr/bin/env python3
# AlbumWall - fetch the license TEXTS of everything a release carries, verbatim.
#
#   python tools/fetch-licenses.py
#
# THIRD-PARTY-NOTICES.md names every bundled component and its license. A release
# also has to carry the license texts themselves, and a license text is not
# something to type from memory or paste from wherever: it has to be the file
# the project itself ships, byte for byte, at the version that was built. This
# fetches each one from the project's own repository into licenses/, and writes
# licenses/README.md saying where every file came from, at which ref, and its
# SHA-256, so that a reader can check any of them against upstream.
#
# The audio engine's versions are READ OUT OF scripts/build-libmpv/inner.sh, not
# repeated here: the recipe is the one place those pins live, and a license
# folder that quietly described last month's pins would be worse than none.
# Run this again whenever a pin moves, and look at the diff.
#
# It refuses to write a file that does not look like a license - an HTML error
# page saved as COPYING is exactly the failure this kind of script has.

import hashlib
import io
import os
import re
import sys
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "licenses")
RECIPE = os.path.join(ROOT, "scripts", "build-libmpv", "inner.sh")


def pins():
    text = io.open(RECIPE, encoding="utf-8").read()
    found = dict(re.findall(r"^([A-Z]+)=([^\s#]+)\s*$", text, flags=re.M))
    wanted = ["MPV", "FFMPEG", "LIBPLACEBO", "LIBASS", "FREETYPE", "FRIBIDI", "HARFBUZZ"]
    missing = [w for w in wanted if w not in found]
    if missing:
        sys.exit("could not read these pins from inner.sh: " + ", ".join(missing))
    return found


P = pins()

# (file to write, what it covers, repository, refs to try in order, paths to try in order)
# A ref of None means the repository's default branch, used only for components
# whose license does not change with the version and whose tags are not
# predictable from a package version. Which ref was used is recorded either way.
WANTED = [
    # ---- the audio engine: exactly the pins the recipe builds
    ("mpv-LICENSE.GPL.txt", "mpv (GPL v2 or later, as built)", "mpv-player/mpv", [P["MPV"]], ["LICENSE.GPL"]),
    ("mpv-Copyright.txt", "mpv: which parts are under which license", "mpv-player/mpv", [P["MPV"]], ["Copyright"]),
    ("ffmpeg-COPYING.LGPLv2.1.txt", "FFmpeg (LGPL v2.1 or later, as configured)", "FFmpeg/FFmpeg", [P["FFMPEG"]], ["COPYING.LGPLv2.1"]),
    ("ffmpeg-LICENSE.md", "FFmpeg: its own account of its licensing", "FFmpeg/FFmpeg", [P["FFMPEG"]], ["LICENSE.md"]),
    ("libplacebo-LICENSE.txt", "libplacebo (LGPL v2.1 or later)", "haasn/libplacebo", [P["LIBPLACEBO"]], ["LICENSE"]),
    ("libass-COPYING.txt", "libass (ISC)", "libass/libass", [P["LIBASS"]], ["COPYING"]),
    ("freetype-FTL.TXT", "FreeType (the FreeType License)", "freetype/freetype", [P["FREETYPE"]], ["docs/FTL.TXT"]),
    ("freetype-LICENSE.TXT", "FreeType: its choice of licenses", "freetype/freetype", [P["FREETYPE"]], ["LICENSE.TXT"]),
    ("fribidi-COPYING.txt", "FriBidi (LGPL v2.1 or later)", "fribidi/fribidi", [P["FRIBIDI"]], ["COPYING"]),
    ("harfbuzz-COPYING.txt", "HarfBuzz (Old MIT)", "harfbuzz/harfbuzz", [P["HARFBUZZ"]], ["COPYING"]),

    # ---- the application
    ("dotnet-runtime-LICENSE.TXT", ".NET runtime (MIT)", "dotnet/runtime", ["v10.0.0", None], ["LICENSE.TXT"]),
    ("avalonia-licence.md", "Avalonia (MIT)", "AvaloniaUI/Avalonia", ["12.1.2", None], ["licence.md", "LICENSE.md", "LICENSE"]),
    ("skiasharp-LICENSE.md", "SkiaSharp and HarfBuzzSharp (MIT)", "mono/SkiaSharp", [None], ["LICENSE.md", "LICENSE.txt", "LICENSE"]),
    ("skia-LICENSE.txt", "Skia (BSD 3-Clause)", "google/skia", [None], ["LICENSE"]),
    ("microcom-LICENSE.txt", "MicroCom (MIT)", "kekekeks/MicroCom", [None], ["LICENSE", "LICENSE.md", "LICENSE.txt", "licence.md"]),
    ("taglib-sharp-COPYING.txt", "TagLib# (LGPL v2.1)", "mono/taglib-sharp", [None], ["COPYING"]),
    ("efcore-LICENSE.txt", "Microsoft.Data.Sqlite (MIT)", "dotnet/efcore", [None], ["LICENSE.txt"]),
    ("sqlitepclraw-LICENSE.TXT", "SQLitePCLRaw (Apache 2.0)", "ericsink/SQLitePCL.raw", [None], ["LICENSE.TXT", "LICENSE.txt", "LICENSE"]),
    ("tmds-dbus-COPYING.txt", "Tmds.DBus.Protocol (MIT)", "tmds/Tmds.DBus", [None], ["COPYING", "LICENSE"]),
    ("inter-LICENSE.txt", "Inter, the typeface (SIL OFL 1.1)", "rsms/inter", [None], ["LICENSE.txt", "LICENSE"]),
]

# Something every real license in the list above contains at least one of.
LOOKS_LIKE = re.compile(
    r"licen[sc]e|permission is hereby granted|redistribution and use|copyright|warranty", re.I)


def fetch(repo, ref, path):
    url = "https://raw.githubusercontent.com/%s/%s/%s" % (repo, ref or "HEAD", path)
    request = urllib.request.Request(url, headers={"User-Agent": "albumwall-fetch-licenses"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return url, response.read()
    except urllib.error.HTTPError:
        return url, None


def acceptable(data):
    if data is None or len(data) < 200:
        return False
    head = data[:300].lstrip().lower()
    if head.startswith(b"<!doctype") or head.startswith(b"<html"):
        return False
    return bool(LOOKS_LIKE.search(data.decode("utf-8", errors="replace")))


def main():
    os.makedirs(OUT, exist_ok=True)
    rows, failed = [], []

    for name, covers, repo, refs, paths in WANTED:
        got = None
        for ref in refs:
            for path in paths:
                url, data = fetch(repo, ref, path)
                if acceptable(data):
                    got = (url, ref, path, data)
                    break
            if got:
                break

        if not got:
            failed.append("%s (%s)" % (name, repo))
            print("FAILED  %-32s %s" % (name, repo))
            continue

        url, ref, path, data = got
        with open(os.path.join(OUT, name), "wb") as f:      # bytes in, bytes out: verbatim
            f.write(data)
        digest = hashlib.sha256(data).hexdigest()
        rows.append((name, covers, repo, ref or "default branch", path, digest, len(data)))
        print("ok      %-32s %s @ %s" % (name, repo, ref or "default branch"))

    with io.open(os.path.join(OUT, "README.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("# The license texts\n\n")
        f.write("Every file here is the license file its project ships, byte for byte, fetched from\n")
        f.write("the project's own repository by `tools/fetch-licenses.py`. Nothing in this folder\n")
        f.write("was typed or edited. What each component is and what it does in AlbumWall is in\n")
        f.write("[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md); AlbumWall's own license is\n")
        f.write("[LICENSE](../LICENSE), the GNU GPL version 3.\n\n")
        f.write("The audio engine's files come from exactly the versions pinned in\n")
        f.write("`scripts/build-libmpv/inner.sh`. Where a ref says *default branch*, the project's\n")
        f.write("tags could not be predicted from a package version and its license does not vary\n")
        f.write("with it; the SHA-256 says exactly which text this is.\n\n")
        f.write("SQLite is in the public domain and ships no license file; see\n")
        f.write("https://sqlite.org/copyright.html.\n\n")
        f.write("| file | covers | from | ref | path | SHA-256 |\n|---|---|---|---|---|---|\n")
        for name, covers, repo, ref, path, digest, size in rows:
            f.write("| [%s](%s) | %s | https://github.com/%s | `%s` | `%s` | `%s` |\n"
                    % (name, name, covers, repo, ref, path, digest))

    print("\n%d written to licenses/, %d failed" % (len(rows), len(failed)))
    if failed:
        print("not fetched: " + "; ".join(failed))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
