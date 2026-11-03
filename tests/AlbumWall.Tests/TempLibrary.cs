namespace AlbumWall.Tests;

/// A copy of Fixtures/Library in a folder of its own, so a test can change it
/// (and an index can remember it) without touching the fixtures or another test.
public sealed class TempLibrary : IDisposable
{
    public string Root { get; }
    public string IndexPath { get; }
    private readonly string _dir;

    public TempLibrary()
    {
        _dir = Path.Combine(Path.GetTempPath(), "albumwall-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Root = Path.Combine(_dir, "Library");
        IndexPath = Path.Combine(_dir, "index.db");
        Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Library"), Root);
    }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
