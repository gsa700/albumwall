// AlbumWall — what the integrity check has seen, per library.
//
// One small JSON file per library beside the settings: every FLAC's size and
// modification time when it was last checked, when that was, and what was
// wrong if anything. It is how the check knows what is due without decoding
// the whole library at every launch:
//   - a file not seen before, or whose size or time has changed, is due now;
//   - every other file is due again a month after it was last checked.
// So a new rip is checked within a session, and the whole library is checked
// over again every month, a little at a time.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbumWall.Domain;

public sealed class IntegrityRecord
{
    public sealed class Entry
    {
        public long Size { get; set; }
        public long Modified { get; set; }       // UTC ticks
        public DateTime Checked { get; set; }    // UTC
        public string? Problem { get; set; }     // null = fine
        public bool NoChecksum { get; set; }
    }

    public static readonly TimeSpan Every = TimeSpan.FromDays(30);

    /// Relative path (from the library root) to what was found.
    public Dictionary<string, Entry> Files { get; set; } = [];

    /// When the last pass over everything due finished.
    public DateTime? LastPass { get; set; }

    public static IntegrityRecord Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), IntegrityJson.Default.IntegrityRecord) ?? new();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[integrity] could not read {path}: {e.Message}; starting over");
        }
        return new();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, IntegrityJson.Default.IntegrityRecord));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[integrity] could not save {path}: {e.Message}");
        }
    }

    public bool IsDue(string relative, FileInfo file, DateTime now) =>
        !Files.TryGetValue(relative, out var e)
        || e.Size != file.Length
        || e.Modified != file.LastWriteTimeUtc.Ticks
        || now - e.Checked > Every;

    public IEnumerable<(string Path, string Problem)> Problems =>
        Files.Where(f => f.Value.Problem is not null)
             .OrderBy(f => f.Key, StringComparer.Ordinal)
             .Select(f => (f.Key, f.Value.Problem!));
}

// Source-generated, like NavidromeJson: reflection-based serialization is off
// in the trimmed builds.
[JsonSerializable(typeof(IntegrityRecord))]
internal sealed partial class IntegrityJson : JsonSerializerContext;
