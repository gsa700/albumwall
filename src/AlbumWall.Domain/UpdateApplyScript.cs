namespace AlbumWall.Domain;

/// <summary>
/// Builds the detached helper the updater launches: it waits for the running app to exit, puts the
/// freshly staged build in place of the installed one, starts it, and puts the old one back if the
/// new one falls over at once. A running exe cannot overwrite itself, hence the wait-then-swap
/// helper. Ported from the family (FlexPad.Core/UpdateApplyScript); grown into a PACKAGE installer in
/// the bridge release (docs/roadmap.md, the road to Native AOT, step 4), after Deadwax 0.7.6.
///
/// A PACKAGE, NOT A PROGRAM. A Native AOT build has its native libraries beside the program on Linux
/// and Windows (InstallLayout.NativeLibraries); the single file kept them inside. The helper backs up
/// what it will replace, copies the libraries FIRST and the program LAST (a program never runs
/// against the wrong libraries: until it is in place the old one is), starts the new program, and if
/// it has fallen over within a few seconds puts every old file back, starts the old program and
/// leaves the marker beside the exe, which the app reports on its next start. A failed copy does the
/// same without trying the new one. Nobody is left without a player, and no update claims success
/// it did not have.
///
/// WHAT IT KEEPS FROM BEFORE.
///
/// 1. THE COPY IS RETRIED, for up to ten seconds on Windows. The wait loop sees the process id vanish
///    a moment before Windows lets go of the executable's file, and a single Copy-Item in that gap
///    fails on the locked exe. That is exactly how this app's first uninstall on Windows left a
///    137 MB exe behind (c7b545f, 2026-09-20).
///
/// 2. THE OLD BUILD'S UNPACKED LIBRARIES ARE REMOVED after a successful swap, when that build was a
///    single file: it unpacked them (Skia, HarfBuzz, SQLite) into a folder named for itself, under
///    %TEMP%/.net/AlbumWall or ~/.net/AlbumWall, which nothing else removes. ONLY THAT ONE FOLDER,
///    never the root, and not while another copy has it in use.
///
/// 3. THE RELAUNCH TAKES THE INSTALL FOLDER AS ITS WORKING DIRECTORY, never the staging one: a folder
///    that is some process's working directory cannot be deleted. And it is given the arguments the
///    app was started with: a copy started with --front-panel must come back with it.
///
/// 4. VALUES ARE NEVER SCRIPT TEXT ON LINUX: the shell helper is fixed text and every path is an
///    argument (UnixArguments). On Windows they are quoted for PowerShell, curly quotes included
///    (PowerShellText; the security review of 2026-10-26 found an O’Brien would have broken it).
/// </summary>
public static class UpdateApplyScript
{
    /// <summary>How long the new program must stay up (or end with 0) to count as started.</summary>
    public const int StartCheckSeconds = 5;

    /// <summary>The PowerShell helper.</summary>
    /// <param name="from">The staged package's folder.</param>
    /// <param name="to">The install folder.</param>
    /// <param name="libraries">The native libraries in the package (none for a single file).</param>
    /// <param name="ownExtractionDir">The folder THIS build unpacked into, or null if it unpacked nothing.</param>
    /// <param name="helperDir">The helper's own folder: its backups, removed last.</param>
    public static string Windows(int pid, string from, string to, string exeName, IReadOnlyList<string> libraries,
        string failedMarker, string stageRoot, string? ownExtractionDir, string helperDir,
        IReadOnlyList<string> relaunchArgs, int startCheckSeconds = StartCheckSeconds)
    {
        static string Q(string value) => PowerShellText.Quote(value);
        var argList = relaunchArgs.Count > 0 ? " -ArgumentList " + Q(PowerShellText.CommandLine(relaunchArgs)) : "";
        var names = "@(" + string.Join(", ", libraries.Append(exeName).Select(Q)) + ")";   // the program last
        var exe = to.TrimEnd('\\', '/') + "\\" + exeName;
        var back = helperDir.TrimEnd('\\', '/') + "\\old";
        return
            $"$from = {Q(from)}; $to = {Q(to)}; $back = {Q(back)}; $names = {names}\n" +
            $"while (Get-Process -Id {pid} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 300 }}\n" +
            "New-Item -ItemType Directory -Path $back -Force | Out-Null\n" +
            "$ok = $true\n" +
            "foreach ($n in $names) {\n" +
            "  $t = Join-Path $to $n\n" +
            "  if (Test-Path -LiteralPath $t) {\n" +
            "    try { Copy-Item -LiteralPath $t -Destination (Join-Path $back $n) -Force -ErrorAction Stop } catch { $ok = $false; break }\n" +
            "  } else { New-Item -ItemType File -Path (Join-Path $back \"$n.absent\") -Force | Out-Null }\n" +
            "}\n" +
            "function Restore {\n" +
            "  foreach ($n in $names) {\n" +
            "    $t = Join-Path $to $n\n" +
            "    if (Test-Path -LiteralPath (Join-Path $back \"$n.absent\")) { Remove-Item -LiteralPath $t -Force -ErrorAction SilentlyContinue }\n" +
            "    elseif (Test-Path -LiteralPath (Join-Path $back $n)) {\n" +
            "      for ($i = 0; $i -lt 40; $i++) { try { Copy-Item -LiteralPath (Join-Path $back $n) -Destination $t -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 250 } }\n" +
            "    }\n" +
            "  }\n" +
            "}\n" +
            "if ($ok) {\n" +
            "  foreach ($n in $names) {\n" +
            "    $copied = $false\n" +
            "    for ($i = 0; $i -lt 40 -and -not $copied; $i++) {\n" +
            "      try { Copy-Item -LiteralPath (Join-Path $from $n) -Destination (Join-Path $to $n) -Force -ErrorAction Stop; $copied = $true }\n" +
            "      catch { Start-Sleep -Milliseconds 250 }\n" +
            "    }\n" +
            "    if (-not $copied) { $ok = $false; break }\n" +
            "  }\n" +
            "  if (-not $ok) { Restore }\n" +
            "}\n" +
            "if ($ok) {\n" +
            // Two copies of the SAME build share one unpacked folder: not while another has it loaded.
            // Matched on the folder's own name, the bundle id, because the same path turns up spelled
            // both ways on Windows (DAVIDE~1 and in full); split on both slashes, for the checks that
            // run on Linux too.
            (ownExtractionDir is null ? "" :
            $"  $mine = {Q("*\\" + ownExtractionDir.TrimEnd('\\', '/').Split('\\', '/')[^1] + "\\*")}\n" +
            "  $inUse = Get-Process -Name AlbumWall -ErrorAction SilentlyContinue | Where-Object { $_.Modules | Where-Object { $_.FileName -like $mine } }\n" +
            "  if (-not $inUse) {\n" +
            $"    Remove-Item -LiteralPath {Q(ownExtractionDir)} -Recurse -Force -ErrorAction SilentlyContinue\n" +
            "  }\n") +
            $"  $p = Start-Process -FilePath {Q(exe)}{argList} -WorkingDirectory $to -PassThru\n" +
            "  $null = $p.Handle   # keeps the exit code readable once it has gone\n" +
            $"  for ($i = 0; $i -lt {startCheckSeconds * 2} -and -not $p.HasExited; $i++) {{ Start-Sleep -Milliseconds 500 }}\n" +
            "  if ($p.HasExited -and $p.ExitCode -ne 0) { $ok = $false; Restore }\n" +
            "}\n" +
            "if ($ok) {\n" +
            $"  Remove-Item -LiteralPath {Q(failedMarker)} -ErrorAction SilentlyContinue\n" +
            "} else {\n" +
            $"  New-Item -ItemType File -Path {Q(failedMarker)} -Force | Out-Null\n" +
            $"  Start-Process -FilePath {Q(exe)}{argList} -WorkingDirectory $to\n" +
            "}\n" +
            $"Remove-Item -LiteralPath {Q(stageRoot)} -Recurse -Force -ErrorAction SilentlyContinue\n" +
            $"Remove-Item -LiteralPath {Q(helperDir)} -Recurse -Force -ErrorAction SilentlyContinue\n";
    }

    /// <summary>The POSIX shell helper for Linux (the Mac has MacUpdate.ApplyScript): the same steps
    /// as the Windows one, every value an argument (<see cref="UnixArguments"/>). Each file goes in
    /// under a temporary name and is renamed over the old one, so no file is ever half written.</summary>
    public const string Unix = """
        #!/bin/sh
        # $1 pid  $2 staged folder  $3 install folder  $4 program  $5 failure marker
        # $6 staging folder  $7 own folder  $8 old build's unpacked libraries, or ""
        # $9 seconds the new program must last; then the libraries, "--", the relaunch arguments
        pid=$1 from=$2 to=$3 exe=$4 marker=$5 stage=$6 helper=$7 oldx=$8 secs=$9
        shift 9
        names=""
        while [ $# -gt 0 ] && [ "$1" != "--" ]; do names="$names $1"; shift; done
        [ "$1" = "--" ] && shift
        names="$names $exe"
        while kill -0 "$pid" 2>/dev/null; do sleep 0.3; done
        back="$helper/old"
        mkdir -p "$back"
        put() { cp -p "$1" "$to/.$2.new" && mv -f "$to/.$2.new" "$to/$2"; }
        restore() {
          for n in $names; do
            if [ -e "$back/$n.absent" ]; then rm -f "$to/$n"
            elif [ -e "$back/$n" ]; then put "$back/$n" "$n"; fi
            rm -f "$to/.$n.new"
          done
        }
        launch() {
          if command -v setsid >/dev/null 2>&1; then
            (cd "$to" && exec setsid -w "$to/$exe" "$@" </dev/null >/dev/null 2>&1) &   # -w: its exit code, for the start check
          else
            (cd "$to" && exec "$to/$exe" "$@" </dev/null >/dev/null 2>&1) &
          fi
        }
        ok=1
        for n in $names; do
          if [ -e "$to/$n" ]; then cp -p "$to/$n" "$back/$n" || { ok=0; break; }
          else : > "$back/$n.absent"; fi
        done
        if [ "$ok" = 1 ]; then
          for n in $names; do
            copied=0
            for i in 1 2 3 4 5; do
              if put "$from/$n" "$n"; then copied=1; break; fi
              sleep 0.5
            done
            [ "$copied" = 1 ] || { ok=0; break; }
          done
          if [ "$ok" = 1 ]; then chmod +x "$to/$exe"; else restore; fi
        fi
        if [ "$ok" = 1 ]; then
          # The OLD build's unpacked native libraries; a new single file unpacks into a folder of its own.
          if [ -n "$oldx" ]; then pgrep -x "$exe" >/dev/null 2>&1 || rm -rf "$oldx"; fi
          launch "$@"
          child=$!
          i=0
          while [ "$i" -lt $((secs * 2)) ] && kill -0 "$child" 2>/dev/null; do sleep 0.5; i=$((i + 1)); done
          if ! kill -0 "$child" 2>/dev/null; then
            wait "$child" || { ok=0; restore; }
          fi
        fi
        if [ "$ok" = 1 ]; then
          rm -f "$marker"
        else
          : > "$marker"
          launch "$@"
        fi
        rm -rf "$stage" "$helper"
        """;

    /// <summary>The arguments <see cref="Unix"/> reads, in its order, after the script's own path.</summary>
    public static IEnumerable<string> UnixArguments(string script, int pid, string from, string to, string exeName,
        IReadOnlyList<string> libraries, string failedMarker, string stageRoot, string? ownExtractionDir, string helperDir,
        IReadOnlyList<string> relaunchArgs, int startCheckSeconds = StartCheckSeconds) =>
        [script, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), from, to, exeName, failedMarker, stageRoot,
         helperDir, ownExtractionDir ?? "", startCheckSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
         .. libraries, "--", .. relaunchArgs];

    /// <summary>SUPERVISED (a kiosk's launcher restarts the app): the app has already put the new
    /// build in place before exiting, keeping the one it replaced in previous/ for the launcher to
    /// put back if the new one keeps falling over (kiosk/albumwall-kiosk). The helper swaps nothing
    /// and launches nothing, or the launcher's copy and this one both run (2026-09-28, the Pi: two
    /// walls). It only tidies up once the app has gone. $1 pid, $2 the old build's unpacked
    /// libraries or "", $3 the staging folder, $4 its own folder.</summary>
    public const string UnixSupervised = """
        #!/bin/sh
        pid=$1 oldx=$2 stage=$3 helper=$4
        while kill -0 "$pid" 2>/dev/null; do sleep 0.3; done
        [ -n "$oldx" ] && rm -rf "$oldx"
        rm -rf "$stage" "$helper"
        """;
}
