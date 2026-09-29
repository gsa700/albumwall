#:project ../src/AlbumWall.Domain/AlbumWall.Domain.csproj

// AlbumWall - the two pieces of the updater that are pure logic, checked.
//
//   dotnet run tools/update-check.cs
//
// 1. VersionOrder: which of two versions is newer. The cases are the family's unit tests
//    (FlexPad.Core.Tests/VersionOrderTests.cs), this repository having check tools where the
//    family has a test project. Getting this wrong has two shapes and both are bad: nobody is
//    ever offered the update, or everybody is offered a downgrade.
// 2. UpdateApplyScript: the helper that swaps the exe. Not run here - tools cannot swap an exe -
//    but READ: the things it must contain for the reasons in its own header. The end-to-end
//    test is a published build against a local feed; docs/windows-notes.md says how.
//
// Exit code 0 when everything holds.

using AlbumWall.Domain;

var failures = 0;
void Check(bool ok, string what) { if (!ok) { failures++; Console.WriteLine("FAIL  " + what); } }
void Newer(string newer, string older)
{
    Check(VersionOrder.IsNewer(newer, older), $"{newer} should be newer than {older}");
    Check(!VersionOrder.IsNewer(older, newer), $"{older} should NOT be newer than {newer}");
}
void Same(string a, string b)
{
    Check(VersionOrder.Compare(a, b) == 0, $"{a} and {b} should be the same version");
    Check(!VersionOrder.IsNewer(a, b) && !VersionOrder.IsNewer(b, a), $"neither of {a} / {b} is newer");
}

// ---- higher numbers win, as numbers
Newer("0.10.0-beta", "0.9.0-beta");         // the numeric trap: 10 > 9, not "1" < "9"
Newer("1.0.0-beta1", "0.10.0-beta");
Newer("0.9.1-beta", "0.9.0-beta");
Newer("2.0.0", "1.9.9");
Newer("0.1.1", "0.1.0");                     // the pair the end-to-end test uses

// ---- pre-release suffixes order by their trailing number
Newer("1.0.0-beta2", "1.0.0-beta1");
Newer("1.0.0-beta10", "1.0.0-beta2");        // lexically "beta10" < "beta2"; numerically it is not
Newer("1.0.0-beta.2", "1.0.0-beta.1");
Newer("1.0.0-beta1", "1.0.0-alpha9");
Newer("1.0.0-beta1", "1.0.0-beta");          // a bare suffix precedes a numbered one

// ---- the real release beats its own pre-releases: the case a truncating comparer gets wrong
Newer("1.0.0", "1.0.0-beta9");
Newer("0.1.0", "0.1.0-test3");               // this project's own test builds
Newer("0.1.0-test4", "0.1.0-test3");

// ---- equivalent spellings
Same("1.0.0", "1.0.0");
Same("v1.0.0", "1.0.0");                     // tags carry a leading v, assemblies do not
Same("1.0.0-beta1", "v1.0.0-beta1");
Same("1.0", "1.0.0");                        // a missing field is zero
Same("1.0.0+abc123", "1.0.0");               // build metadata is not part of the order
Same("0.1.0+50302c7f44b416e6b53163a9e8a1a810c4208acf", "v0.1.0");   // what an assembly really reports

// ---- unparseable is "don't know", never "newer"
foreach (var (a, b) in new (string?, string?)[] { (null, "1.0.0"), ("1.0.0", null), ("", "1.0.0"),
                                                   ("not-a-version", "1.0.0"), ("1.0.0", "garbage"),
                                                   ("libmpv-0.41.0-3", "0.1.0") })
{
    Check(VersionOrder.Compare(a, b) is null, $"'{a}' vs '{b}' should be unknown");
    Check(!VersionOrder.IsNewer(a, b), $"'{a}' must not count as newer than '{b}'");
}

// ---- a whole series, the way a person would order it
string[] series = ["0.9.0-beta", "0.10.0-beta", "1.0.0-alpha1", "1.0.0-beta", "1.0.0-beta1", "1.0.0-beta2",
                   "1.0.0-beta10", "1.0.0-rc1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
for (var i = 1; i < series.Length; i++) Newer(series[i], series[i - 1]);

// ---- the helper script: what it must contain, and must not
const string Odd = @"C:\Users\O'Brien\AppData\Local\Programs\AlbumWall";
var win = UpdateApplyScript.Windows(4242, Odd + @"\stage\AlbumWall.exe", Odd + @"\AlbumWall.exe", Odd + @"\.failed",
                                    Odd, @"C:\Temp\AlbumWall-update", @"C:\Temp\.net\AlbumWall\AbC123", @"C:\Temp\apply.ps1");
Check(win.Contains("Get-Process -Id 4242"), "windows: waits for the pid");
Check(win.Contains("$i -lt 40") && win.Contains("-ErrorAction Stop"), "windows: the copy is retried, and its failure is catchable");
Check(win.Contains("O''Brien") && !win.Contains("O'Brien\\"), "windows: an apostrophe in a path is doubled, never raw");
Check(win.Contains(@"*\AbC123\*") && win.Contains("$inUse"), "windows: the unpacked folder is removed only if no running copy uses it");
Check(!win.Contains(@"-LiteralPath 'C:\Temp\.net\AlbumWall' "), "windows: never removes the unpacking ROOT");
Check(win.IndexOf("New-Item -ItemType File", StringComparison.Ordinal) > win.IndexOf("} else {", StringComparison.Ordinal),
      "windows: the failure marker is written only on the else branch");
Check(win.Contains("-WorkingDirectory"), "windows: the relaunch is given the install folder to start in");

var none = UpdateApplyScript.Windows(1, "a", "b", "c", "d", "e", null, "f");
Check(!none.Contains("$inUse") && !none.Contains(".net"), "windows: a build that unpacked nothing removes nothing");

var unix = UpdateApplyScript.Unix(4242, "/tmp/s/AlbumWall", "/home/o'brien/.local/share/albumwall/AlbumWall", "/x/.failed",
                                  "/home/o'brien/.local/share/albumwall", "/tmp/AlbumWall-update", "/home/u/.net/AlbumWall/AbC123", "/tmp/apply.sh");
Check(unix.StartsWith("#!/bin/sh\n"), "unix: is a shell script");
Check(unix.Contains("kill -0 4242"), "unix: waits for the pid");
Check(unix.Contains(@"o'\''brien"), "unix: an apostrophe in a path is closed, escaped and reopened");
Check(unix.Contains("pgrep -x AlbumWall") && unix.Contains("rm -rf '/home/u/.net/AlbumWall/AbC123'"), "unix: removes its own unpacked folder, unless another copy runs");
Check(!unix.Contains("\r"), "unix: no carriage returns");
Check(unix.Contains("AlbumWall' &)"), "unix: relaunches with no arguments when it was started with none");

// Relaunch as launched (2026-09-28: the Pi kiosk's copy runs with --front-panel).
var withArgs = UpdateApplyScript.Unix(4242, "/tmp/s/AlbumWall", "/opt/AlbumWall", "/x/.failed", "/opt", "/tmp/AlbumWall-update",
                                      null, "/tmp/apply.sh", ["--front-panel", "it's"]);
Check(withArgs.Contains("'/opt/AlbumWall' '--front-panel' 'it'\\''s' &)"), "unix: relaunches with the arguments it was started with, quoted");
var winArgs = UpdateApplyScript.Windows(1, "a", "b", "c", "d", "e", null, "f", ["--front-panel"]);
Check(winArgs.Contains("-ArgumentList '--front-panel'"), "windows: relaunches with its arguments");

// Supervised (a kiosk's loop restarts the app): the helper swaps nothing and launches nothing,
// or the loop's copy and the helper's both run - the Pi's two walls.
var sup = UpdateApplyScript.Unix(4242, "/tmp/s/AlbumWall", "/opt/AlbumWall", "/x/.failed", "/opt", "/tmp/AlbumWall-update",
                                 "/home/u/.net/AlbumWall/Old", "/tmp/apply.sh", ["--front-panel"], supervised: true);
Check(sup.Contains("kill -0 4242"), "supervised: still waits for the pid");
Check(!sup.Contains("cp -f") && !sup.Contains(" &)"), "supervised: no swap and no relaunch");
Check(sup.Contains("rm -rf '/home/u/.net/AlbumWall/Old'") && sup.Contains("rm -rf '/tmp/AlbumWall-update'"), "supervised: still tidies up");

Console.WriteLine(failures == 0 ? "PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
