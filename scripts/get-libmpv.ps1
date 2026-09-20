# Fetches libmpv for Windows.
#
# The app plays everything through libmpv, which is a C library and therefore
# not something NuGet can bring in. On Linux a development build uses the
# distribution's (mpv-libs on Fedora, libmpv2 on Debian). On Windows there is no
# package, so this fetches OUR build -- the audio-only one from
# scripts/build-libmpv, published on this repository's releases -- and drops the
# DLL where the project expects it. It is the same file a release carries inside
# its exe, so a development build here plays through what ships.
#
# It is NOT committed to the repository: it is a binary of someone else's
# GPL/LGPL projects, and vendoring it would mean shipping their source alongside
# ours. Fetching it at setup keeps that obligation with the release it came from.
#
#   .\scripts\get-libmpv.ps1              fetch the pinned build, if it is not here already
#   .\scripts\get-libmpv.ps1 -Force       fetch it again regardless
#   .\scripts\get-libmpv.ps1 -Upstream    the official full build from SourceForge instead
#
# Then `dotnet run --project src\AlbumWall.App` as usual.
#
# THE REPOSITORY IS PRIVATE, so the download goes through the GitHub CLI and
# needs `gh auth login` to have been done once on this machine. When the
# repository is public the plain URL works and gh is no longer needed; the
# script tries that by itself when gh is missing or not signed in.
#
# WHY OURS AND NOT UPSTREAM'S. Until 2026-09-20 this fetched the newest build
# from SourceForge: 120 MB, video and all, a moving target nobody here built.
# Ours is 10 MB, pinned, and reproducible. It took that long because ours had to
# EARN it: libmpv-0.41.0-2 was not gapless on iTunes AAC (it played each track's
# encoder padding -- the "barely audible burp" in v0.1.0-test3), and
# tools/gapless-check.cs is what showed it. -3 moved the ffmpeg pin and measures
# identical to upstream on Hambench, sample for sample. See docs/windows-notes.md.
#
# -Upstream is the rollback, and the control for the next time the two need
# comparing. It needs 7-Zip; the default path does not.

param(
    [switch]$Force,
    [switch]$Upstream
)

$ErrorActionPreference = 'Stop'

# BUMP THIS WHEN A NEW libmpv RELEASE IS PUBLISHED -- after tools/gapless-check.cs
# has passed against it ON WINDOWS. A cross-compiled DLL that has only been
# measured on Linux has been measured on a different file.
$tag  = 'libmpv-0.41.0-3'
$repo = 'gsa700/albumwall'

$root   = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'native\win-x64'
$dll    = Join-Path $target 'libmpv-2.dll'
$stamp  = Join-Path $target 'libmpv-source.txt'
$want   = if ($Upstream) { 'upstream' } else { $tag }

# What is there now, by the stamp this script leaves. A DLL with no stamp came
# from the script as it used to be, which only ever fetched upstream's.
$have = $null
if (Test-Path $dll) {
    $have = if (Test-Path $stamp) { (Get-Content $stamp -TotalCount 1).Trim() } else { 'upstream' }
}

if ($have -and -not $Force -and ($have -eq $want -or ($Upstream -and $have -like 'upstream*'))) {
    Write-Host "Already here: $dll  ($have)"
    Write-Host "Run with -Force to fetch it again."
    exit 0
}
if ($have) { Write-Host "Replacing $have with $want" }

$work = Join-Path $env:TEMP "albumwall-libmpv-$PID"
New-Item -ItemType Directory -Force -Path $work | Out-Null
New-Item -ItemType Directory -Force -Path $target | Out-Null

try {
    if ($Upstream) {
        # 7-Zip, because that is the only format these builds are published in.
        $sevenZip = @(
            'C:\Program Files\7-Zip\7z.exe',
            'C:\Program Files (x86)\7-Zip\7z.exe'
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if (-not $sevenZip) {
            # No ?. here: a stock Windows box has PowerShell 5.1, which cannot parse it.
            $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
            if ($cmd) { $sevenZip = $cmd.Source }
        }
        if (-not $sevenZip) {
            Write-Error "7-Zip is needed to unpack the upstream build. Install it with: winget install 7zip.7zip"
        }

        # The newest plain x86_64 build. NOT the -v3 one: that is compiled for
        # x86-64-v3 and will not start on an older processor.
        Write-Host "Looking up the latest upstream libmpv build..."
        $rss  = Invoke-RestMethod 'https://sourceforge.net/projects/mpv-player-windows/rss?path=/libmpv'
        $item = $rss | Where-Object { $_.link -match 'mpv-dev-x86_64-\d{8}-git-[0-9a-f]+\.7z' } | Select-Object -First 1
        if (-not $item) { Write-Error "Could not find an upstream libmpv build to download." }

        # The link ends in /download, so the name comes from the match, not the leaf.
        $name    = [regex]::Match($item.link, 'mpv-dev-x86_64-\d{8}-git-[0-9a-f]+\.7z').Value
        $archive = Join-Path $work $name

        # SourceForge redirects to a mirror only for clients that do not look like
        # a browser. PowerShell's default user agent does, and gets the HTML
        # countdown page saved as the archive instead.
        Write-Host "Downloading $name"
        Invoke-WebRequest -Uri $item.link -OutFile $archive -UserAgent 'curl/8.0'

        Write-Host "Extracting libmpv-2.dll"
        & $sevenZip e $archive "-o$work" 'libmpv-2.dll' -y | Out-Null
        if (-not (Test-Path (Join-Path $work 'libmpv-2.dll'))) { Write-Error "libmpv-2.dll was not in the archive." }

        $want = "upstream $name"
    }
    else {
        $zip  = "$tag-win-x64.zip"
        $sums = 'SHA256SUMS'

        # gh when it is there and signed in, which a private repository requires.
        # Asked with the error preference relaxed: PowerShell 5.1 turns anything a
        # native program writes to stderr into a terminating error under 'Stop'.
        $gh = Get-Command gh.exe -ErrorAction SilentlyContinue
        $signedIn = $false
        if ($gh) {
            $ErrorActionPreference = 'Continue'
            & $gh.Source auth status 2>$null | Out-Null
            $signedIn = ($LASTEXITCODE -eq 0)
            $ErrorActionPreference = 'Stop'
        }

        if ($signedIn) {
            Write-Host "Downloading $zip from the $tag release (gh)"
            & $gh.Source release download $tag --repo $repo --pattern $zip --pattern $sums --dir $work --clobber
            if ($LASTEXITCODE -ne 0) { Write-Error "gh could not download $tag from $repo." }
        }
        else {
            Write-Host "Downloading $zip from the $tag release"
            try {
                foreach ($f in $zip, $sums) {
                    Invoke-WebRequest -Uri "https://github.com/$repo/releases/download/$tag/$f" `
                                      -OutFile (Join-Path $work $f) -UseBasicParsing
                }
            }
            catch {
                Write-Error ("Could not download $zip. While the repository is private this needs the GitHub CLI, " +
                             "signed in:  winget install GitHub.cli  then  gh auth login")
            }
        }

        # Checked against the release's own list, every time. It is an executable
        # library about to be loaded into the player.
        $line = Get-Content (Join-Path $work $sums) | Where-Object { $_ -match [regex]::Escape($zip) } | Select-Object -First 1
        if (-not $line) { Write-Error "$sums does not list $zip." }
        $expected = ($line -split '\s+')[0].ToLowerInvariant()
        $actual   = (Get-FileHash (Join-Path $work $zip) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) { Write-Error "SHA-256 mismatch for ${zip}: expected $expected, got $actual." }
        Write-Host "SHA-256 matches $sums"

        Expand-Archive (Join-Path $work $zip) $work -Force
        if (-not (Test-Path (Join-Path $work 'libmpv-2.dll'))) { Write-Error "libmpv-2.dll was not in $zip." }
    }

    Copy-Item (Join-Path $work 'libmpv-2.dll') $dll -Force

    # Dated now, not when it was built. The project copies it to the output folder
    # with PreserveNewest, which goes by the file's date -- and a DLL fetched to
    # REPLACE another is quite often the older build of the two, the rollback
    # especially. Left with its own date it would sit here, correct, and never
    # reach the exe.
    (Get-Item $dll).LastWriteTime = Get-Date

    # Which versions of what are inside it. Ours says; upstream's has no such file,
    # and a stale one describing the DLL that was just replaced must not stay.
    $info = Join-Path $target 'libmpv-build-info.txt'
    if (Test-Path (Join-Path $work 'libmpv-build-info.txt')) { Copy-Item (Join-Path $work 'libmpv-build-info.txt') $info -Force }
    elseif (Test-Path $info) { Remove-Item $info -Force }

    Set-Content -Path $stamp -Value $want -Encoding ascii
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Done: $dll  ($want, $([math]::Round((Get-Item $dll).Length / 1MB, 1)) MB)"
Write-Host "The project copies it next to the executable when you build."
