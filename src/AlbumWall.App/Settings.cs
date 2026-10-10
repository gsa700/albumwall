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

    /// Where the Properties window was, in screen pixels: a place of its own,
    /// not the main window's centre ("can we give the properties dialog its own
    /// window location?", 2026-10-04). Never written on native Wayland, where an
    /// app is not told where its windows are.
    public int? PropsX { get; set; }
    public int? PropsY { get; set; }

    /// Where a library's settings window was, in screen pixels (one place for
    /// all of them: it is one window, shown for one library at a time).
    public int? LibraryX { get; set; }
    public int? LibraryY { get; set; }

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

    /// Edge: every build as it is released (GitHub pre-releases included). Off, or
    /// unset, is Stable: only builds promoted after running on Edge (2026-10-10).
    public bool? EdgeChannel { get; set; }

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

    /// True from a fresh install until the first library is chosen. Such a
    /// machine is NOT given a library of its Music folder: the window welcomes
    /// instead and asks where the music is, and nothing on the disk is read
    /// until it is told (2026-10-04, "lets not scan at all, instead offer to
    /// scan for common locations"). Written to the file, so closing the app on
    /// the welcome brings the welcome back. Absent in every file from before,
    /// whose library is made from LibraryPath as it always was.
    public bool? NeedsLibrary { get; set; }

    /// Whether there is any library at all. Only a fresh install has none.
    public bool HasLibrary() => AllLibraries().Count > 0;

    /// The list, made from LibraryPath the first time it is asked for - except
    /// on a fresh install, where it stays empty until a library is chosen.
    public List<Library> AllLibraries()
    {
        if (Libraries is { Count: > 0 }) return Libraries;
        if (NeedsLibrary == true) return Libraries ??= [];
        Libraries = [Library.Folder(LibraryPath)];
        CurrentLibrary = Libraries[0].Id;
        return Libraries;
    }

    /// The library the wall shows. The first, if the one named has gone; and
    /// Library.None while there is none at all, so that nothing which asks has
    /// to be taught about a missing one.
    public Library Current()
    {
        var all = AllLibraries();
        return all.FirstOrDefault(l => l.Id == CurrentLibrary) ?? all.FirstOrDefault() ?? Library.None;
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
    /// "x11" runs under XWayland on a GNOME Wayland session (see Program.NativeWayland).
    /// Hand-edited, not in Preferences; absent means the default, native Wayland.
    public string? Backend { get; set; }

    public int? CompactX { get; set; }
    public int? CompactY { get; set; }

    /// ALBUMWALL_CONFIG_DIR moves the settings somewhere else, for test runs. A
    /// second instance pointed at the real file fights the first over window
    /// geometry and the library path, and whichever closes last wins — so
    /// anything launched to try a change gets a scratch directory instead.
    /// Where the old value is kept each time a file's lyrics are changed.
    [JsonIgnore]
    public static string TagBackups => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "tag-backups");

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

            // No file: a machine this app has never run on. See NeedsLibrary.
            return new Settings { NeedsLibrary = true };
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
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            // His alone: a Navidrome library keeps a sign-in token in here. Not
            // the password (see Library.Token), but enough to sign in with. So
            // the file is CREATED his alone (it used to be written readable to
            // all and narrowed after), and written beside itself and moved over,
            // so a crash mid-write can never leave half a file that loads as
            // defaults and saves away every library (security review 2026-10-10).
            var tmp = Path + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.Delete(tmp);   // UnixCreateMode applies only to a file that is created
            using (var to = new StreamWriter(new FileStream(tmp, options)))
                to.Write(json);
            File.Move(tmp, Path, overwrite: true);
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

    /// "folder", or "navidrome" for a server. By name, like Chrome, so a new
    /// kind cannot be misread as an old one.
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

    /// Whether the folder was on a network share the last time a scan of it
    /// finished. A library on one trusts the index, where opening every file
    /// at every launch is the better part of a minute rather than of a second
    /// (see MainWindow.TrustsIndex). Remembered, not only asked each time,
    /// because a share that is down looks like a local folder. Not kept on
    /// Windows, which trusts the index everywhere.
    public bool? Network { get; set; }

    /// A Navidrome library: where the server is and who signs in. The password
    /// is NOT here and is not anywhere: Salt and Token are what Subsonic signs
    /// requests with (the token is md5(password + salt)), made once at sign-in.
    /// They are still a way in to that server, which is why this file is the
    /// user's alone to read.
    public string? Server { get; set; }
    public string? User { get; set; }
    public string? Salt { get; set; }
    public string? Token { get; set; }

    /// Which of the server's own libraries this is (its lossless one, its lossy
    /// one), by the server's id for it, and what the server calls it. Null on a
    /// library added before there was a choice: that one is everything the user
    /// may see there, until he signs in again and it is given one.
    public string? MusicFolder { get; set; }
    public string? MusicFolderName { get; set; }

    /// Whether the files in this library may be changed from here: lyrics
    /// today, tags later (Domain/TagWriter.cs). OFF until he says otherwise,
    /// and only worth turning on where an edit lasts: on the machine that
    /// holds the master, not on a copy that is overwritten from it every
    /// night. A server is never editable, whatever this says.
    public bool Editable { get; set; }

    /// Whether the audio here is checked for damage in the background
    /// (Domain/FlacIntegrity.cs). OFF until he turns it on: "we don't want
    /// some major background process going on without the users knowledge."
    public bool CheckIntegrity { get; set; }

    /// How often every file is read again by the integrity check, in days:
    /// null is the check's own month, 0 means only new and changed files
    /// ("let the user set the frequency of the check", 2026-10-10).
    public int? IntegrityDays { get; set; }

    [JsonIgnore]
    public TimeSpan IntegrityEvery =>
        IntegrityDays is null ? Domain.IntegrityRecord.Every
        : IntegrityDays == 0 ? TimeSpan.MaxValue
        : TimeSpan.FromDays(IntegrityDays.Value);

    [JsonIgnore]
    public bool CanEdit => IsFolder && Editable;

    [JsonIgnore]
    public bool IsFolder => Kind == "folder";

    [JsonIgnore]
    public bool IsNavidrome => Kind == "navidrome";

    /// What Settings.Current() answers while there is no library at all: a
    /// fresh install, before the welcome has been answered. Never in the list
    /// and never saved.
    public static readonly Library None = new() { Id = "", Name = "No library yet", Kind = "none" };

    [JsonIgnore]
    public bool IsNone => Kind == "none";

    /// Where the library is, with null resolved: a folder library's folder, or
    /// who on which server. It is what is shown as the library's whereabouts
    /// and what tells one library's wall from another's.
    [JsonIgnore]
    public string Root => IsNavidrome ? $"{User} on {Server}{(MusicFolder is null ? "" : $", {MusicFolderName ?? MusicFolder}")}"
                        : IsNone ? ""
                        : string.IsNullOrWhiteSpace(Path) ? DefaultRoot : Path;

    public static string DefaultRoot =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");

    public static Library Folder(string? path) => new() { Path = path, Name = NameFor(path) };

    /// `named` is whether the server has more than one library to tell apart:
    /// its only one is simply "Navidrome".
    public static Library Navidrome(string server, string user, string salt, string token,
                                    Domain.Navidrome.Folder? folder = null, bool named = false) => new()
    {
        Kind = "navidrome", Name = NavidromeName(folder, named),
        Server = server, User = user, Salt = salt, Token = token,
        MusicFolder = folder?.Id, MusicFolderName = folder?.Name,
    };

    public const string NavidromeDefaultName = "Navidrome";

    public static string NavidromeName(Domain.Navidrome.Folder? folder, bool named) =>
        named && folder is { Name.Length: > 0 } ? $"{NavidromeDefaultName} · {folder.Name}" : NavidromeDefaultName;

    /// "Music" for the default, else the folder's own name ("MusicFolder").
    public static string NameFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Music";
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
