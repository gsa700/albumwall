// AlbumWall — library index.
//
// Remembers what the scanner read from each file, so that a file which has not
// changed does not have to be OPENED again. That is the whole point, and it is
// about opening rather than reading: on Windows every open of a file Defender
// has not seen lately is a virus scan, and 17,658 of them are 158 s — measured
// on Hambench, 2026-09-20, the first launch of the day, with the app itself
// using a tenth of one core and an NVMe drive 4% busy while MsMpEng burned a
// whole core in step with it. The same scan had taken 4.2 s a few hours before.
// Listing the tree, sizes and times included, costs nothing either way.
//
// THE FILES ARE THE TRUTH AND THIS IS A CACHE (spec §6). What follows from that:
//
//   * Deleting index.db is a supported, boring operation. So is a corrupt one,
//     or one from another version: it is thrown away and the next scan rebuilds
//     it. Nothing the user made lives here and nothing ever may.
//   * An honest rescan — every file opened, nothing here believed — is always
//     available. It is what the Rescan command does.
//   * A file is believed unchanged when its path, size and modified time all
//     match. The spec asks for a tag hash as well, and THIS DOES NOT KEEP ONE,
//     because a hash can only be checked by opening the file, which is the cost
//     being avoided. What is lost: a tag edit that preserves both size and
//     modified time (`metaflac --preserve-modtime` into existing padding), made
//     WHILE THE APP IS CLOSED, is not seen until Rescan. While the app is open
//     the watcher names the file and Touch() drops it from here regardless of
//     what its timestamps claim. Closing the rest needs the inode change time,
//     which no tool can preserve and .NET does not expose — open, see the notes.
//
// BUMP Version WHEN THE SCANNER LEARNS TO READ SOMETHING DIFFERENTLY — a new
// fallback, a new ImageSize.Repair shape, another field. An unchanged file is
// never re-read, so without the bump it keeps the old answer for good.

using Microsoft.Data.Sqlite;

namespace AlbumWall.Domain;

public sealed class LibraryIndex
{
    // 1  2026-09-20  first
    // 2  2026-09-20  bitrate, for the Statistics tab
    private const int Version = 2;

    /// Everything the scanner takes from one audio file. The strings are as the
    /// scanner resolved them — fallbacks applied — so that folding a remembered
    /// file into an album is the same code as folding one just read.
    public sealed record TrackFacts(
        long Size, long Modified, bool Readable,
        string AlbumArtist, string Album, int TagYear,
        int Disc, int Number, string Title, string Artist,
        long DurationTicks, int SampleRate, int BitDepth, int Bitrate,
        bool HasCover, int CoverWidth, int CoverHeight);

    /// A sidecar's measured size. Measuring one means opening it too.
    public sealed record ArtFacts(long Size, long Modified, int Width, int Height);

    /// How paths are compared, here and by anything handing paths in. The
    /// scanner's spelling of a path and the watcher's are not promised to agree
    /// on Windows, where case is not part of a file's name.
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _dbPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackFacts> _tracks = new(PathComparer);
    private readonly Dictionary<string, ArtFacts> _art = new(PathComparer);

    // When each path was last named by the watcher, on a clock that only counts.
    // A scan notes the clock as it starts, and what it read from a file touched
    // after that is not kept: it may have read the file before the change.
    private readonly Dictionary<string, long> _touchedAt = new(PathComparer);
    private long _clock;

    public int TrackCount { get { lock (_gate) return _tracks.Count; } }

    private LibraryIndex(string dbPath) => _dbPath = dbPath;

    /// Never throws. An index that cannot be read is an empty one.
    public static LibraryIndex Open(string dbPath)
    {
        var index = new LibraryIndex(dbPath);
        try { index.Load(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[index] unreadable, starting again: {ex.Message}");
            index._tracks.Clear();
            index._art.Clear();
            index.Discard();
        }
        return index;
    }

    public long Now() { lock (_gate) return _clock; }

    public TrackFacts? Find(string path, long size, long modified)
    {
        lock (_gate)
            return _tracks.TryGetValue(path, out var f) && f.Size == size && f.Modified == modified ? f : null;
    }

    public ArtFacts? FindArt(string path, long size, long modified)
    {
        lock (_gate)
            return _art.TryGetValue(path, out var f) && f.Size == size && f.Modified == modified ? f : null;
    }

    /// The watcher saw something happen to this file. Whatever its size and
    /// times say afterwards, it is read again.
    ///
    /// The path goes through GetFullPath first because the scanner's paths have
    /// been: it expands 8.3 short names, and a watcher given a root spelled
    /// C:\Users\DAVIDE~1\... reports every file under that spelling. Found by
    /// the first test of this method, which touched a file and saw it trusted
    /// anyway.
    public void Touch(string path)
    {
        try { path = Path.GetFullPath(path); } catch { /* as given, then */ }

        lock (_gate)
        {
            _touchedAt[path] = ++_clock;
            _tracks.Remove(path);
            _art.Remove(path);
        }
    }

    /// Takes what a scan found. A finished scan REPLACES the index — a file it
    /// did not see is gone. One that was cut short only adds to it, so that
    /// quitting two minutes into a first scan does not throw the two minutes away.
    public void Commit(IReadOnlyDictionary<string, TrackFacts> tracks,
                       IReadOnlyDictionary<string, ArtFacts> art,
                       long startedAt, bool complete)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int written = 0, removed = 0;

        lock (_gate)
        {
            bool Stale(string path) => _touchedAt.TryGetValue(path, out var at) && at > startedAt;

            var putTracks = tracks.Where(kv => !Stale(kv.Key)
                && !(_tracks.TryGetValue(kv.Key, out var old) && old == kv.Value)).ToList();
            var putArt = art.Where(kv => !Stale(kv.Key)
                && !(_art.TryGetValue(kv.Key, out var old) && old == kv.Value)).ToList();
            var dropTracks = complete ? _tracks.Keys.Where(p => !tracks.ContainsKey(p)).ToList() : [];
            var dropArt = complete ? _art.Keys.Where(p => !art.ContainsKey(p)).ToList() : [];

            foreach (var (p, f) in putTracks) _tracks[p] = f;
            foreach (var (p, f) in putArt) _art[p] = f;
            foreach (var p in dropTracks) _tracks.Remove(p);
            foreach (var p in dropArt) _art.Remove(p);

            written = putTracks.Count + putArt.Count;
            removed = dropTracks.Count + dropArt.Count;
            if (written + removed == 0) return;

            // The memory copy is already right, and it is the one the next scan
            // in this run uses. Failing to save costs the next LAUNCH a re-read.
            try { Save(putTracks, putArt, dropTracks, dropArt); }
            catch (Exception ex)
            {
                Console.WriteLine($"[index] not saved: {ex.Message}");
                return;
            }
        }

        Console.WriteLine($"[index] saved {written} changed, {removed} gone in {sw.ElapsedMilliseconds} ms");
    }

    // ---------------------------------------------------------------- storage

    // Pooling off: a pooled connection keeps the file open after Dispose, and
    // then "delete index.db" is not the boring operation it is promised to be.
    private SqliteConnection Connect()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString());
        c.Open();
        return c;
    }

    private void Discard()
    {
        try { File.Delete(_dbPath); } catch { /* the next save will say why */ }
    }

    private static void Run(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Load()
    {
        if (!File.Exists(_dbPath)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var c = Connect();
        using (var v = c.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(v.ExecuteScalar()) != Version)
            {
                Console.WriteLine("[index] from another version, starting again");
                c.Close();
                Discard();
                return;
            }
        }

        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT path, size, modified, readable, album_artist, album, tag_year, disc, "
                          + "number, title, artist, duration, sample_rate, bit_depth, bitrate, has_cover, cover_w, cover_h "
                          + "FROM tracks";
            using var r = q.ExecuteReader();
            while (r.Read())
                _tracks[r.GetString(0)] = new TrackFacts(
                    r.GetInt64(1), r.GetInt64(2), r.GetInt64(3) != 0,
                    r.GetString(4), r.GetString(5), r.GetInt32(6),
                    r.GetInt32(7), r.GetInt32(8), r.GetString(9), r.GetString(10),
                    r.GetInt64(11), r.GetInt32(12), r.GetInt32(13), r.GetInt32(14),
                    r.GetInt64(15) != 0, r.GetInt32(16), r.GetInt32(17));
        }

        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT path, size, modified, width, height FROM art";
            using var r = q.ExecuteReader();
            while (r.Read())
                _art[r.GetString(0)] = new ArtFacts(r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), r.GetInt32(4));
        }

        Console.WriteLine($"[index] {_tracks.Count} tracks, {_art.Count} sidecars remembered, "
                        + $"loaded in {sw.ElapsedMilliseconds} ms");
    }

    private void Save(List<KeyValuePair<string, TrackFacts>> putTracks,
                      List<KeyValuePair<string, ArtFacts>> putArt,
                      List<string> dropTracks, List<string> dropArt)
    {
        using var c = Connect();
        using var tx = c.BeginTransaction();

        Run(c, "CREATE TABLE IF NOT EXISTS tracks (path TEXT PRIMARY KEY, size INTEGER, modified INTEGER, "
             + "readable INTEGER, album_artist TEXT, album TEXT, tag_year INTEGER, disc INTEGER, "
             + "number INTEGER, title TEXT, artist TEXT, duration INTEGER, sample_rate INTEGER, "
             + "bit_depth INTEGER, bitrate INTEGER, has_cover INTEGER, cover_w INTEGER, cover_h INTEGER) WITHOUT ROWID");
        Run(c, "CREATE TABLE IF NOT EXISTS art (path TEXT PRIMARY KEY, size INTEGER, modified INTEGER, "
             + "width INTEGER, height INTEGER) WITHOUT ROWID");
        Run(c, $"PRAGMA user_version = {Version}");

        using (var put = c.CreateCommand())
        {
            put.CommandText = "INSERT OR REPLACE INTO tracks VALUES "
                            + "($p,$a,$b,$c,$d,$e,$f,$g,$h,$i,$j,$k,$l,$m,$n,$o,$q,$r)";
            var names = new[] { "$p", "$a", "$b", "$c", "$d", "$e", "$f", "$g", "$h",
                                "$i", "$j", "$k", "$l", "$m", "$n", "$o", "$q", "$r" };
            var ps = names.Select(n => put.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
            foreach (var (path, f) in putTracks)
            {
                object[] values = [path, f.Size, f.Modified, f.Readable ? 1 : 0, f.AlbumArtist, f.Album,
                                   f.TagYear, f.Disc, f.Number, f.Title, f.Artist, f.DurationTicks,
                                   f.SampleRate, f.BitDepth, f.Bitrate, f.HasCover ? 1 : 0, f.CoverWidth, f.CoverHeight];
                for (var i = 0; i < ps.Length; i++) ps[i].Value = values[i];
                put.ExecuteNonQuery();
            }
        }

        using (var put = c.CreateCommand())
        {
            put.CommandText = "INSERT OR REPLACE INTO art VALUES ($p,$a,$b,$c,$d)";
            var ps = new[] { "$p", "$a", "$b", "$c", "$d" }
                .Select(n => put.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
            foreach (var (path, f) in putArt)
            {
                object[] values = [path, f.Size, f.Modified, f.Width, f.Height];
                for (var i = 0; i < ps.Length; i++) ps[i].Value = values[i];
                put.ExecuteNonQuery();
            }
        }

        foreach (var (table, paths) in new[] { ("tracks", dropTracks), ("art", dropArt) })
        {
            using var drop = c.CreateCommand();
            drop.CommandText = $"DELETE FROM {table} WHERE path = $p";
            var p = drop.Parameters.Add(new SqliteParameter { ParameterName = "$p" });
            foreach (var path in paths) { p.Value = path; drop.ExecuteNonQuery(); }
        }

        tx.Commit();
    }
}
