# Fetches libmpv for Windows.
#
# The app plays everything through libmpv, which is a C library and therefore
# not something NuGet can bring in. On Linux it comes from the distribution
# (mpv-libs on Fedora, libmpv2 on Debian). On Windows there is no package, so
# this pulls the official build and drops the DLL where the project expects it.
#
# It is NOT committed to the repository: it is a 40 MB binary of someone else's
# GPL project, and vendoring it would mean shipping their source alongside ours.
# Fetching it at setup keeps that obligation where it belongs.
#
#   .\scripts\get-libmpv.ps1
#
# Then `dotnet run --project src\AlbumWall.App` as usual.

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'native\win-x64'
$dll    = Join-Path $target 'libmpv-2.dll'

if (Test-Path $dll) {
    Write-Host "Already here: $dll"
    Write-Host "Delete it and run this again to update."
    exit 0
}

# 7-Zip, because that is the only format these builds are published in.
$sevenZip = @(
    'C:\Program Files\7-Zip\7z.exe',
    'C:\Program Files (x86)\7-Zip\7z.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $sevenZip) {
    $sevenZip = (Get-Command 7z.exe -ErrorAction SilentlyContinue)?.Source
}

if (-not $sevenZip) {
    Write-Error "7-Zip is needed to unpack the mpv build. Install it with: winget install 7zip.7zip"
}

# The newest plain x86_64 build. NOT the -v3 one: that is compiled for
# x86-64-v3 and will not start on an older processor.
Write-Host "Looking up the latest libmpv build..."
$rss = Invoke-RestMethod 'https://sourceforge.net/projects/mpv-player-windows/rss?path=/libmpv'
$item = $rss | Where-Object { $_.link -match 'mpv-dev-x86_64-\d{8}-git-[0-9a-f]+\.7z' } | Select-Object -First 1

if (-not $item) { Write-Error "Could not find a libmpv build to download." }

$url     = $item.link
$archive = Join-Path $env:TEMP (Split-Path $url -Leaf)

Write-Host "Downloading $(Split-Path $url -Leaf)"
Invoke-WebRequest -Uri $url -OutFile $archive

New-Item -ItemType Directory -Force -Path $target | Out-Null

Write-Host "Extracting libmpv-2.dll"
& $sevenZip e $archive "-o$target" 'libmpv-2.dll' -y | Out-Null

if (-not (Test-Path $dll)) { Write-Error "libmpv-2.dll was not in the archive." }

Remove-Item $archive -Force
Write-Host ""
Write-Host "Done: $dll"
Write-Host "The project copies it next to the executable when you build."
