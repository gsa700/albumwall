namespace AlbumWall.Domain;

/// <summary>
/// Builds the detached helper the updater launches: it waits for the running app to exit, copies the
/// freshly staged executable over the installed one, and relaunches. A running exe cannot overwrite
/// itself, hence the wait-then-swap helper. Ported from the family (FlexPad.Core/UpdateApplyScript),
/// with three changes that AlbumWall's own history asked for.
///
/// WHAT IT KEEPS. The copy is CHECKED. Copy-Item and cp both fail without stopping the script, so the
/// family's first helpers relaunched unconditionally: a failed copy restarted the OLD exe while the
/// app had already said "restarting to apply", a silent false success. On failure a marker is left
/// beside the exe (the app says so on its next start) and the old exe is relaunched anyway, so nobody
/// is left without a player. The relaunch is given the install folder as its working directory, never
/// the staging one: a folder that is some process's working directory cannot be deleted, and the next
/// update's clean-up of it would throw.
///
/// WHAT IS NEW HERE.
///
/// 1. THE COPY IS RETRIED, for up to ten seconds. The wait loop sees the process id vanish a moment
///    before Windows lets go of the executable's file, and a single Copy-Item in that gap fails on
///    the locked exe. That is not theory: it is exactly how this app's first uninstall on Windows
///    left a 137 MB exe behind and came back from the dead (c7b545f, 2026-09-20) - the same wait,
///    the same single attempt, the same silence. An updater that loses that race "fails safe", but
///    it fails, every time, on the machines where the race goes that way.
///
/// 2. THE OLD BUILD'S UNPACKED LIBRARIES ARE REMOVED after a successful swap. A single-file build
///    unpacks its natives (libmpv, Skia, HarfBuzz, SQLite - 29 MB) into a folder named for that
///    build, under %TEMP%/.net/AlbumWall or ~/.net/AlbumWall, and nothing ever removes the last
///    build's. The old process has exited, so its folder is free; the new build unpacks its own on
///    first start. Only after a SUCCESSFUL swap - the old exe relaunched after a failed one still
///    needs what it unpacked. AND ONLY THAT ONE FOLDER, never the root: another copy may be running
///    from the folder beside it. See InstallService.OwnExtractionDir for how close the first draft
///    came to deleting libmpv out from under the copy he was listening to.
///
/// 3. PATHS ARE QUOTED FOR THE SHELL THAT READS THEM. A Windows account named O'Brien has an
///    apostrophe in every path here, and the family's helpers put paths between single quotes as
///    they came.
/// </summary>
public static class UpdateApplyScript
{
    /// <param name="workingDirectory">Directory the relaunched app starts in: the install folder.</param>
    /// <param name="stageRoot">Staging folder to remove once the swap is done.</param>
    /// <param name="ownExtractionDir">The folder THIS build unpacked into, or null if it unpacked nothing.</param>
    /// <param name="scriptPath">This script, removed last so it does not linger in temp.</param>
    /// <param name="relaunchArgs">The arguments the app was started with, passed again on relaunch:
    /// a copy started with --front-panel must come back with it (2026-09-28, the Pi kiosk).</param>
    public static string Windows(int pid, string stagedExe, string targetExe, string failedMarker,
        string workingDirectory, string stageRoot, string? ownExtractionDir, string scriptPath,
        IReadOnlyList<string>? relaunchArgs = null)
    {
        static string Q(string path) => "'" + path.Replace("'", "''") + "'";
        var argList = relaunchArgs is { Count: > 0 } a ? " -ArgumentList " + string.Join(",", a.Select(Q)) : "";
        return
            $"while (Get-Process -Id {pid} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 300 }}\n" +
            // Forty tries, a quarter second apart. -ErrorAction Stop turns Copy-Item's failure into
            // something try can see; $? after a loop of retries would only describe the last sleep.
            "$ok = $false\n" +
            "for ($i = 0; $i -lt 40 -and -not $ok; $i++) {\n" +
            $"  try {{ Copy-Item -LiteralPath {Q(stagedExe)} -Destination {Q(targetExe)} -Force -ErrorAction Stop; $ok = $true }}\n" +
            "  catch { Start-Sleep -Milliseconds 250 }\n" +
            "}\n" +
            "if ($ok) {\n" +
            $"  Remove-Item -LiteralPath {Q(failedMarker)} -ErrorAction SilentlyContinue\n" +
            // ...unless another running copy has libraries loaded from it: two copies of the SAME
            // build share one unpacked folder. Matched on the folder's own name, the bundle id,
            // because the same path turns up spelled both ways on Windows (DAVIDE~1 and in full).
            (ownExtractionDir is null ? "" :
            // The folder's own name, split on BOTH slashes: Path.GetFileName only knows this
            // machine's, and the checks in tools/update-check.cs run on Linux too.
            $"  $mine = {Q("*\\" + ownExtractionDir.TrimEnd('\\', '/').Split('\\', '/')[^1] + "\\*")}\n" +
            "  $inUse = Get-Process -Name AlbumWall -ErrorAction SilentlyContinue | Where-Object { $_.Modules | Where-Object { $_.FileName -like $mine } }\n" +
            "  if (-not $inUse) {\n" +
            $"    Remove-Item -LiteralPath {Q(ownExtractionDir)} -Recurse -Force -ErrorAction SilentlyContinue\n" +
            "  }\n") +
            "} else {\n" +
            $"  New-Item -ItemType File -Path {Q(failedMarker)} -Force | Out-Null\n" +
            "}\n" +
            $"Start-Process -FilePath {Q(targetExe)}{argList} -WorkingDirectory {Q(workingDirectory)}\n" +
            $"Remove-Item -LiteralPath {Q(stageRoot)} -Recurse -Force -ErrorAction SilentlyContinue\n" +
            $"Remove-Item -LiteralPath {Q(scriptPath)} -Force -ErrorAction SilentlyContinue\n";
    }

    /// <inheritdoc cref="Windows"/>
    /// <param name="relaunchArgs">See <see cref="Windows"/>.</param>
    /// <param name="supervised">Something else restarts the app (a kiosk's loop), and the app has
    /// already put the new executable in place before exiting: swap nothing and launch nothing, or
    /// the supervisor's copy and this one both run (2026-09-28, the Pi: two walls). Only tidy up.</param>
    public static string Unix(int pid, string stagedExe, string targetExe, string failedMarker,
        string workingDirectory, string stageRoot, string? ownExtractionDir, string scriptPath,
        IReadOnlyList<string>? relaunchArgs = null, bool supervised = false)
    {
        // Inside single quotes the shell reads everything literally, so an apostrophe is written by
        // closing the quotes, escaping one, and opening them again.
        const string Apostrophe = "'\\''";
        static string Q(string path) => "'" + path.Replace("'", Apostrophe) + "'";
        var args = relaunchArgs is { Count: > 0 } a ? " " + string.Join(" ", a.Select(Q)) : "";
        if (supervised)
            return
                "#!/bin/sh\n" +
                $"while kill -0 {pid} 2>/dev/null; do sleep 0.3; done\n" +
                // The OLD build's unpacked folder; the new one unpacks into a folder of its own.
                (ownExtractionDir is null ? "" : $"rm -rf {Q(ownExtractionDir)}\n") +
                $"rm -rf {Q(stageRoot)}\n" +
                $"rm -f {Q(scriptPath)}\n";
        return
            "#!/bin/sh\n" +
            $"while kill -0 {pid} 2>/dev/null; do sleep 0.3; done\n" +
            // Linux replaces a running file happily, so there is no lock to wait out; the retry is
            // kept short and is there for a busy filesystem, not for the race Windows has.
            "ok=0\n" +
            "for i in 1 2 3 4 5; do\n" +
            $"  if cp -f {Q(stagedExe)} {Q(targetExe)}; then ok=1; break; fi\n" +
            "  sleep 0.5\n" +
            "done\n" +
            "if [ \"$ok\" = 1 ]; then\n" +
            $"  chmod +x {Q(targetExe)}\n" +
            $"  rm -f {Q(failedMarker)}\n" +
            (ownExtractionDir is null ? "" :
            $"  pgrep -x AlbumWall >/dev/null 2>&1 || rm -rf {Q(ownExtractionDir)}\n") +
            "else\n" +
            $"  : > {Q(failedMarker)}\n" +
            "fi\n" +
            // cd first, for the same reason -WorkingDirectory is set on Windows.
            $"(cd {Q(workingDirectory)} && {Q(targetExe)}{args} &)\n" +
            $"rm -rf {Q(stageRoot)}\n" +
            $"rm -f {Q(scriptPath)}\n";
    }
}
