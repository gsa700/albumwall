# AlbumWall - retake the README's screenshots (docs/screenshots/*.webp).
#
#   .\tools\screenshots.ps1 -Exe <a DEBUG AlbumWall.exe> -Config <scratch settings folder> -Out <folder for the PNGs>
#
# Then convert: the README uses WebP at 1800 px wide, quality 86 (Pillow does it
# in four lines), which is ~200 KB a shot where the PNG is 2-3 MB.
#
# The app photographs ITSELF: a DEBUG build watches the file named by
# ALBUMWALL_SNAP, and touching it renders the window's own visual tree to
# <path>.png - the whole window, since the app draws its own title bar - and
# Preferences to <path>.prefs.png. No screen capture, so nothing else on the
# desktop can end up in a picture, and the display's scaling is the picture's
# resolution. See docs/windows-bringup.md for the trigger commands.
#
# SILENTLY. A shot with the play controls up needs something loaded, and a
# script that plays Nirvana through the speakers of whoever runs it is not
# acceptable - the first time these were taken, the person whose library it is
# was listening to something else in his own copy. So: play the track at volume
# 0 for a few seconds so that a session is saved, quit, rewrite the saved
# position (and put the volume slider somewhere that looks used), and start
# again. The app resumes PAUSED with the album unfolded. Nothing is ever heard.
#
# -Config MUST be a scratch folder, never the real settings: this rewrites
# settings.json and session.json. The library it points at is only read.

param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Config,
    [Parameter(Mandatory)][string]$Out,
    [int]$Width = 1600,
    [int]$Height = 1000
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Config, $Out | Out-Null
$real = Join-Path $env:APPDATA 'albumwall'
if ((Resolve-Path $Config).Path.TrimEnd('\') -ieq $real) { throw "-Config is the real settings folder. Use a scratch one." }

$env:ALBUMWALL_CONFIG_DIR = (Resolve-Path $Config).Path
$snap = Join-Path (Resolve-Path $Out).Path 's'
$env:ALBUMWALL_SNAP = $snap
$settings = Join-Path $Config 'settings.json'
$session = Join-Path $Config 'session.json'

function Set-Look($volume) {
    $st = if (Test-Path $settings) { Get-Content $settings -Raw | ConvertFrom-Json } else { New-Object psobject }
    foreach ($kv in @{ Volume = $volume; WindowWidth = $Width; WindowHeight = $Height; Maximized = $false }.GetEnumerator()) {
        $st | Add-Member -NotePropertyName $kv.Key -NotePropertyValue $kv.Value -Force
    }
    [IO.File]::WriteAllText($settings, ($st | ConvertTo-Json))
}
function Send($command, $ms = 1700) { Set-Content "$snap.cmd" $command -Encoding ascii; Start-Sleep -Milliseconds $ms }
function Shot($name, $which = '') {
    # The old picture goes first, and a missing new one is an error: a crashed
    # app otherwise leaves the last run's PNG to be mistaken for this run's.
    $file = "$snap$which.png"
    if (Test-Path $file) { [IO.File]::Delete($file) }
    Set-Content $snap $name
    for ($i = 0; $i -lt 25 -and -not (Test-Path $file); $i++) { Start-Sleep -Milliseconds 400 }
    Start-Sleep -Milliseconds 1000
    if (-not (Test-Path $file)) { throw "no snapshot for $name - is this a DEBUG build, and is it still running?" }
    Copy-Item $file (Join-Path $Out "$name.png") -Force
    "  $name.png"
}
function Start-App {
    $script:app = Start-Process $Exe -PassThru
    Start-Sleep -Seconds 8                      # the scan, and the covers on screen
    if ($script:app.HasExited) { throw "the app exited at startup" }
}
function Stop-App {
    if (-not $script:app.HasExited) { $script:app.CloseMainWindow() | Out-Null }
    for ($i = 0; $i -lt 20 -and -not $script:app.HasExited; $i++) { Start-Sleep -Milliseconds 500 }
}

function Shot-Loaded($name, $album, $track, $seconds) {
    "== $name"
    if (Test-Path $session) { [IO.File]::Delete($session) }
    Set-Look 0
    Start-App; Send "open $album" 3000; Send "play $track" 6000; Stop-App
    $s = Get-Content $session -Raw | ConvertFrom-Json
    $s.PositionSeconds = $seconds
    [IO.File]::WriteAllText($session, ($s | ConvertTo-Json -Depth 5))
    Set-Look 62
    Start-App; Start-Sleep -Seconds 3; Shot $name; Stop-App
}

# What the README shows. The album names are substrings, matched the way the
# `open` trigger command matches them; change them to suit the library.
Shot-Loaded 'album-open' 'nevermind' 1 97
Shot-Loaded 'dark-side'  'dark side' 3 151

"== wall and preferences"
if (Test-Path $session) { [IO.File]::Delete($session) }
Set-Look 62
Start-App
Shot 'wall'
Send 'prefs stats' 2500;      Shot 'statistics' '.prefs'
Send 'prefs appearance' 2500; Shot 'appearance' '.prefs'
Stop-App
Set-Look 0
