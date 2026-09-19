// AlbumWall — what was playing, so the next run can pick it up.
//
// Kept apart from settings.json on purpose. Settings are things he chose and
// change when he changes them; this is a running record that is rewritten every
// few seconds while music plays. Mixing them would mean rewriting his
// preferences file constantly, and would put a hundred file paths in the middle
// of a file that is otherwise pleasant to read and edit by hand.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbumWall.App;

public sealed class Session
{
    /// Album identity, the same (AlbumArtist, Title) rule as everywhere else.
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }

    /// The queue exactly as it was, not just "this album": a shuffled run has an
    /// order that exists nowhere else, and resuming into a different shuffle
    /// would replay tracks already heard.
    public List<string> Queue { get; set; } = [];

    public int Index { get; set; }
    public double PositionSeconds { get; set; }

    [JsonIgnore]
    public static string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Settings.Path)!, "session.json");

    public static Session? Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<Session>(File.ReadAllText(Path))
                : null;
        }
        catch
        {
            // A session that cannot be read is a session that is not resumed.
            // It must never be the reason the app fails to start.
            return null;
        }
    }

    /// Written to a temporary file and moved into place. This runs every few
    /// seconds for as long as music plays, which makes "the power went while it
    /// was half written" a matter of time rather than bad luck, and the whole
    /// point of the periodic write is to survive exactly that kind of exit.
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[session] could not save: {ex.Message}");
        }
    }

    public static void Clear()
    {
        try { File.Delete(Path); } catch { /* nothing to resume either way */ }
    }
}
