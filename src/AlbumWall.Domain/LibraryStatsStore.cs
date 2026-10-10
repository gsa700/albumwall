// AlbumWall — a library's statistics, kept from its last scan.
//
// LibraryStats is worked out in memory from the albums on the wall and was
// thrown away when the library left it, so Preferences could only ever count
// the library on the wall. Now every finished scan writes its numbers to one
// small JSON file per library, with when, and the Library tab shows any
// library's numbers from that: stale at worst since its last scan, which is
// the only time its files are read anyway. A cache, so it lives in the cache
// directory, can be deleted, and costs one scan.
//
// It also answers whether a library holds FLAC files at all, which is what
// decides whether the integrity check is offered for it.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbumWall.Domain;

public static class LibraryStatsStore
{
    public sealed record Stored(DateTime AsOf, LibraryStats Stats)
    {
        /// Whether the integrity check has anything to check here.
        public bool HasFlac => Stats.Types.Any(t => t.Type.Equals("FLAC", StringComparison.OrdinalIgnoreCase));
    }

    public static void Save(string path, LibraryStats stats)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(DateTime.UtcNow, stats), StatsJson.Default.Stored));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[stats] could not save {path}: {e.Message}");
        }
    }

    public static Stored? Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), StatsJson.Default.Stored);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[stats] could not read {path}: {e.Message}");
        }
        return null;
    }

    public static void Forget(string path)
    {
        try { File.Delete(path); } catch { /* a cache */ }
    }
}

// Source-generated, like IntegrityJson: reflection-based serialization is off
// in the trimmed builds.
[JsonSerializable(typeof(LibraryStatsStore.Stored))]
internal sealed partial class StatsJson : JsonSerializerContext;
