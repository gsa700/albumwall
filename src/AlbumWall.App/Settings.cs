// AlbumWall — the handful of things the app should remember between runs.
//
// Deliberately small. This is not a preferences system and should not grow into
// one: it holds the things a person would otherwise have to set EVERY TIME they
// open the app, which is a different and much shorter list than everything that
// could be configured.
//
// Stored under the platform's own config location — ~/.config/albumwall on Linux,
// %AppData% on Windows — because a dotfile in $HOME is the kind of thing that is
// fine for one's own tool and rude in one that gets shared.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbumWall.App;

public sealed class Settings
{
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// Physical pixels, which is what Avalonia's Position uses.
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    public bool Maximized { get; set; }

    /// ReplayGain mode, as the name of the enum member.
    public string? Gain { get; set; }

    [JsonIgnore]
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "albumwall", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new Settings();
        }
        catch
        {
            // A corrupt or unreadable settings file must never stop the app
            // starting. Defaults are always a valid answer.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[wall] could not save settings: {ex.Message}");
        }
    }
}
