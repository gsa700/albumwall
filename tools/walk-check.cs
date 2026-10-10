#:project ../src/AlbumWall.Domain/AlbumWall.Domain.csproj

// AlbumWall - the library walk (FolderWalk), checked.
//
//   dotnet run tools/walk-check.cs [LIBRARY...]
//
// 1. In a scratch folder: a `loop -> ..` link must not list a track more than once, and a folder
//    linked in twice is walked once.
// 2. For each LIBRARY given: the walk must find exactly the files .NET's own recursion does when
//    there are no loops (the walk changed how, not what).
// Exit code 0 when everything holds.

using AlbumWall.Domain;

var failures = 0;
void Check(bool ok, string what) { if (!ok) { failures++; Console.WriteLine("FAIL  " + what); } }

var tmp = Directory.CreateTempSubdirectory("walk-check-").FullName;
try
{
    var album = Directory.CreateDirectory(Path.Combine(tmp, "lib", "Artist", "Album")).FullName;
    File.WriteAllText(Path.Combine(album, "a.flac"), "");
    Directory.CreateSymbolicLink(Path.Combine(album, "loop"), "..");
    Directory.CreateSymbolicLink(Path.Combine(tmp, "lib", "Again"), Path.Combine(tmp, "lib", "Artist"));
    var found = FolderWalk.Files(Path.Combine(tmp, "lib"), "*", FileAttributes.System).ToList();
    Check(found.Count == 1, $"a loop and a second link list the track once (listed {found.Count} times)");
}
finally { Directory.Delete(tmp, recursive: true); }

foreach (var lib in args)
{
    var old = new DirectoryInfo(lib).EnumerateFiles("*", new EnumerationOptions
        { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
        .Select(f => f.FullName).Order(StringComparer.Ordinal).ToList();
    var now = FolderWalk.Files(lib, "*", FileAttributes.System).Select(f => f.FullName).Order(StringComparer.Ordinal).ToList();
    Check(old.SequenceEqual(now), $"{lib}: the same files as before ({old.Count} then, {now.Count} now)");
    Console.WriteLine($"{lib}: {now.Count:N0} files, same as before: {old.SequenceEqual(now)}");
}

Console.WriteLine(failures == 0 ? "PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
