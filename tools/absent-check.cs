#:project ../src/AlbumWall.Domain/AlbumWall.Domain.csproj
#:package TagLibSharp@2.3.0

// AlbumWall — does a library that is not there get treated as absent, not deleted?
//
//   dotnet run tools/absent-check.cs -- <album folder> <scratch dir>
//
// Copies one album folder into the scratch directory as a library of its own,
// scans it into a scratch index, and then takes the library away the ways a NAS
// or a USB drive does: emptied (an unmounted mount point), removed, and one
// file that cannot be opened. Each case says what must happen and whether it
// did. Only reads the album folder; everything it changes is in the scratch
// directory, which it empties first.

using AlbumWall.Domain;
using Reason = AlbumWall.Domain.LibraryUnreachableException.Reason;

if (args.Length < 2)
{
    Console.WriteLine("usage: dotnet run tools/absent-check.cs -- <album folder> <scratch dir>");
    return 2;
}

var album = args[0];
var scratch = Path.GetFullPath(args[1]);
if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
var root = Path.Combine(scratch, "library");
var aside = Path.Combine(scratch, "aside");
var db = Path.Combine(scratch, "index.db");

void Fill()
{
    var into = Path.Combine(root, Path.GetFileName(Path.TrimEndingDirectorySeparator(album)));
    Directory.CreateDirectory(into);
    foreach (var f in Directory.GetFiles(album))
        File.Copy(f, Path.Combine(into, Path.GetFileName(f)), overwrite: true);
}

int Rows(LibraryIndex ix) => ix.TrackCount;

var failures = 0;
void Check(string what, bool ok, string detail)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}: {detail}");
    if (!ok) failures++;
}

var lastTracks = 0;
Reason? Unreachable(Func<IReadOnlyList<Album>> scan, out int albums)
{
    albums = -1;
    try
    {
        var found = scan();
        albums = found.Count;
        lastTracks = found.Sum(a => a.Tracks.Count);
        return null;
    }
    catch (LibraryUnreachableException ex) { return ex.Why; }
}

Fill();
var index = LibraryIndex.Open(db);
var s = new LibraryScanner();
var first = s.Scan(root, index: index);
var full = first.Count;
var tracks = first.Sum(a => a.Tracks.Count);
var rows = Rows(index);
Console.WriteLine($"library: {full} album(s), {rows} indexed track(s) in {root}");
if (full == 0) { Console.WriteLine("no albums in that folder; give it one with audio files"); return 2; }

Console.WriteLine("EMPTIED (an unmounted mount point, a reassigned drive letter)");
Directory.Move(root, aside);
Directory.CreateDirectory(root);
var why = Unreachable(() => s.Scan(root, index: LibraryIndex.Open(db)), out _);
Check("trusting scan, index knows it", why == Reason.Empty, $"{why?.ToString() ?? "scanned"}");
Check("index kept", Rows(LibraryIndex.Open(db)) == rows, $"{Rows(LibraryIndex.Open(db))} of {rows} rows");

why = Unreachable(() => s.Scan(root, expectMusic: true), out _);
Check("no index, caller expects music (Linux)", why == Reason.Empty, $"{why?.ToString() ?? "scanned"}");

why = Unreachable(() => s.Scan(root), out var n);
Check("no index, nothing expected (a new, empty folder)", why is null && n == 0, $"{why?.ToString() ?? $"{n} albums"}");

why = Unreachable(() => s.Scan(root, index: LibraryIndex.Open(db), trustIndex: false), out n);
Check("Rescan believes it", why is null && n == 0, $"{why?.ToString() ?? $"{n} albums"}");
Check("...and prunes", Rows(LibraryIndex.Open(db)) == 0, $"{Rows(LibraryIndex.Open(db))} rows left");

Console.WriteLine("REMOVED (the path is not there at all)");
Directory.Delete(root);
Directory.Move(aside, root);
s.Scan(root, index: LibraryIndex.Open(db));                // put the rows back
Directory.Move(root, aside);
why = Unreachable(() => s.Scan(root, index: LibraryIndex.Open(db)), out _);
Check("missing root", why == Reason.Missing, $"{why?.ToString() ?? "scanned"}");
Check("index kept", Rows(LibraryIndex.Open(db)) == rows, $"{Rows(LibraryIndex.Open(db))} of {rows} rows");
Directory.Move(aside, root);

Console.WriteLine("ONE FILE CANNOT BE OPENED (locked, or the share blinked)");
var victim = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .First(f => f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
             || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
             || f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase));
var fresh = Path.Combine(scratch, "gap.db");
File.Copy(db, fresh);
File.SetUnixFileMode(victim, UnixFileMode.None);
try
{
    // Honest, so the file is really opened rather than recognized.
    var ix = LibraryIndex.Open(fresh);
    why = Unreachable(() => s.Scan(root, index: ix, trustIndex: false), out n);
    Check("scan still shows the rest", why is null && n == full, $"{why?.ToString() ?? $"{n} albums"}");
    Check("unopened file not pruned", Rows(LibraryIndex.Open(fresh)) == rows, $"{Rows(LibraryIndex.Open(fresh))} of {rows} rows");
}
finally
{
    File.SetUnixFileMode(victim, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}
// And once it opens again, a TRUSTING scan must show every track: had the
// failed open been remembered as "not audio", the index would go on saying so
// until the file's size or time changed, and the track would stay missing.
why = Unreachable(() => s.Scan(root, index: LibraryIndex.Open(fresh)), out n);
Check("readable again, every track back", why is null && n == full && lastTracks == tracks,
      $"{n} albums, {lastTracks} of {tracks} tracks");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
