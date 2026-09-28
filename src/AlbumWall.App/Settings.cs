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

    /// Where the Preferences window was, in screen pixels.
    public int? PrefsX { get; set; }
    public int? PrefsY { get; set; }

    /// ReplayGain mode, as the name of the enum member.
    public string? Gain { get; set; }

    /// Startup. Whether to pick up the album, track and position from the last
    /// run — on unless he turns it off — and whether to start it PLAYING, which
    /// is off unless he turns it on. His words: "let's NOT have it auto play
    /// though, that will be an option in the settings page." An app that makes
    /// noise because it was opened is a surprise, and not a good one.
    public bool? ResumeSession { get; set; }
    public bool? AutoPlay { get; set; }

    /// Whether to ask, once as it opens, if there is a newer version. On unless
    /// turned off. It is one small request to GitHub and nothing is ever
    /// downloaded or installed without being asked for in Preferences.
    public bool? CheckForUpdates { get; set; }

    /// Whether the play controls sit above the wall instead of below it. Off is
    /// the layout everything else was designed around, and stays the default:
    /// "default to bottom as now but make it an option in settings."
    public bool? TransportAtTop { get; set; }

    /// Where the music lives. Null means the platform's own Music folder, which
    /// is the right default everywhere and the only one that needs no setup.
    ///
    /// This is the one setting the app CANNOT derive: a fresh machine has an
    /// empty Music folder and the records are on a NAS. It is why there is a
    /// preferences window at all.
    public string? LibraryPath { get; set; }

    /// Every library he has told the app about, and which one the wall shows.
    ///
    /// ONE AT A TIME, NEVER MERGED — the standing rule. His FLAC library and the
    /// NAS's lossy one hold many of the same records, so a merged wall shows
    /// them twice, and more of them with every album re-ripped. A picker in the
    /// bottom bar switches between them instead (decided 2026-09-22).
    ///
    /// Null in a settings file from before 2026-09-22; Libraries() then makes
    /// the one library there was, from LibraryPath, which stays readable for
    /// that and is not written to again.
    public List<Library>? Libraries { get; set; }
    public string? CurrentLibrary { get; set; }

    /// The list, made from LibraryPath the first time it is asked for.
    public List<Library> AllLibraries()
    {
        if (Libraries is { Count: > 0 }) return Libraries;
        Libraries = [Library.Folder(LibraryPath)];
        CurrentLibrary = Libraries[0].Id;
        return Libraries;
    }

    /// The library the wall shows. The first, if the one named has gone.
    public Library Current()
    {
        var all = AllLibraries();
        return all.FirstOrDefault(l => l.Id == CurrentLibrary) ?? all[0];
    }

    /// The app's own playback volume, 0-100.
    public int? Volume { get; set; }

    /// Ground lightness in percent. How light the room is.
    public int? Lightness { get; set; }

    /// Ground saturation in PERCENT (2 = 0.02): how much of the library's hue
    /// shows in the ground.
    public int? Tint { get; set; }

    /// How the bars separate from the wall, by name — "recede", "lift", "ink",
    /// "warm". By name rather than index so reordering the list cannot silently
    /// change someone's setting.
    public string? Chrome { get; set; }

    /// Compact mode: whether the window was left as the now-playing strip, and
    /// the strip's own size. Kept apart from WindowWidth/Height, which are the
    /// wall's, so going compact and back never costs the wall its size.
    public bool? Compact { get; set; }
    public double? CompactWidth { get; set; }
    public double? CompactHeight { get; set; }
    /// Where the strip was left, where the platform lets an app know (Windows,
    /// X11). Native Wayland never tells an app its position, so there the strip
    /// and the wall share one.
    public int? CompactX { get; set; }
    public int? CompactY { get; set; }

    /// ALBUMWALL_CONFIG_DIR moves the settings somewhere else, for test runs. A
    /// second instance pointed at the real file fights the first over window
    /// geometry and the library path, and whichever closes last wins — so
    /// anything launched to try a change gets a scratch directory instead.
    [JsonIgnore]
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("ALBUMWALL_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "albumwall"),
        "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new Settings();
        }
        catch (Exception e)
        {
            // A corrupt or unreadable settings file must never stop the app
            // starting. Defaults are always a valid answer — but say so, because
            // silently falling back to defaults is indistinguishable from reading
            // a file that happens to hold the defaults.
            Console.WriteLine($"[settings] {Path} could not be read, using defaults: {e.Message}");
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

            // His alone: a Navidrome library keeps a sign-in token in here. Not
            // the password (see Library.Token), but enough to sign in with.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[wall] could not save settings: {ex.Message}");
        }
    }
}

/// One library: a folder the scanner walks, or (from step 2) a Navidrome server.
public sealed class Library
{
    /// Stable across renames, so CurrentLibrary survives one.
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// What the picker says. Made from the folder's name until he changes it.
    public string Name { get; set; } = "";

    /// "folder" today. By name, like Chrome, so a new kind cannot be misread
    /// as an old one.
    public string Kind { get; set; } = "folder";

    /// A folder library's root. Null means the platform's Music folder, which
    /// is where a fresh install looks and the only root that needs no setup.
    public string? Path { get; set; }

    /// How many tracks the last finished scan found. It is how the app tells a
    /// folder that is empty from one that is ABSENT: an unmounted share and an
    /// unplugged drive both look like an empty folder, and a library that held
    /// music last time is not believed to have none now. Null until the first
    /// scan, which has nothing to compare with.
    public int? Tracks { get; set; }

    [JsonIgnore]
    public bool IsFolder => Kind == "folder";

    /// Where a folder library actually is, with null resolved.
    [JsonIgnore]
    public string Root => string.IsNullOrWhiteSpace(Path) ? DefaultRoot : Path;

    public static string DefaultRoot =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");

    public static Library Folder(string? path) => new() { Path = path, Name = NameFor(path) };

    /// "Music" for the default, else the folder's own name ("MusicFolder").
    public static string NameFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Music";
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
