using AlbumWall.App;
using Spindle.Playback;

namespace AlbumWall.Tests;

/// The two files the app keeps between runs, as it writes and reads them
/// (Native AOT: source-generated JSON, SettingsJson and SessionJson).
public sealed class SettingsTests : IDisposable
{
    public SettingsTests()
    {
        Assert.StartsWith(ScratchConfig.Dir, Settings.Path);     // never the real ones
        Directory.CreateDirectory(ScratchConfig.Dir);
        foreach (var f in Directory.GetFiles(ScratchConfig.Dir)) File.Delete(f);
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(ScratchConfig.Dir)) File.Delete(f);
    }

    [Fact]
    public void A_first_run_has_no_settings_and_asks_for_a_library()
    {
        Assert.False(File.Exists(Settings.Path));
        Assert.True(Settings.Load().NeedsLibrary);
    }

    [Fact]
    public void Settings_come_back_as_they_were_saved()
    {
        var eq = new EqSettings { On = true };
        eq.Bands[2].Gain = -3.5;
        var saved = new Settings
        {
            WindowWidth = 1600.5, WindowX = -1200, Maximized = true,
            Gain = "Track", AfterAlbum = "RandomAlbum", Volume = 73, TextSize = 125, EdgeChannel = true,
            Equalizer = eq,
            Libraries =
            [
                Library.Folder("/Volumes/Music/FLAC"),
                Library.Navidrome("http://nas:4533", "dave", "0123456789abcdef", "deadbeef", null),
            ],
        };
        saved.CurrentLibrary = saved.Libraries[1].Id;
        saved.Save();

        var loaded = Settings.Load();
        Assert.Equal((1600.5, -1200, true), (loaded.WindowWidth!.Value, loaded.WindowX!.Value, loaded.Maximized));
        Assert.Equal(("Track", "RandomAlbum", 73, 125, true), (loaded.Gain, loaded.AfterAlbum, loaded.Volume!.Value, loaded.TextSize!.Value, loaded.EdgeChannel!.Value));
        Assert.True(loaded.Equalizer!.On);
        Assert.Equal(-3.5, loaded.Equalizer.Bands[2].Gain);
        Assert.Equal(saved.Libraries.Select(l => (l.Id, l.Kind, l.Path, l.Server, l.Token)), loaded.Libraries!.Select(l => (l.Id, l.Kind, l.Path, l.Server, l.Token)));
        Assert.Equal(saved.CurrentLibrary, loaded.CurrentLibrary);
        Assert.Null(loaded.NeedsLibrary);
    }

    /// It holds a Navidrome sign-in token: made readable by its owner only, and
    /// never left half written (security review 2026-10-10).
    [Fact]
    public void The_settings_file_is_its_owners_alone_and_whole()
    {
        new Settings { Volume = 10 }.Save();
        new Settings { Volume = 20 }.Save();
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Settings.Path));
        Assert.False(File.Exists(Settings.Path + ".tmp"));
        Assert.Equal(20, Settings.Load().Volume);
    }

    [Fact]
    public void A_settings_file_that_cannot_be_read_starts_the_app_on_defaults()
    {
        Directory.CreateDirectory(ScratchConfig.Dir);
        File.WriteAllText(Settings.Path, "{ \"Volume\": 50, broken");
        var s = Settings.Load();
        Assert.Null(s.Volume);
        Assert.Null(s.NeedsLibrary);      // a file was there: not a first run
    }

    [Fact]
    public void A_session_comes_back_and_a_broken_one_is_none()
    {
        Assert.Null(Session.Load());
        new Session { AlbumArtist = "Pink Floyd", Album = "The Wall", Queue = ["/a/1.flac", "/a/2.flac"], Index = 1, PositionSeconds = 61.25, OpenAlbum = "The Wall" }.Save();
        var s = Session.Load()!;
        Assert.Equal(("Pink Floyd", "The Wall", 1, 61.25), (s.AlbumArtist, s.Album, s.Index, s.PositionSeconds));
        Assert.Equal(["/a/1.flac", "/a/2.flac"], s.Queue);
        Assert.False(File.Exists(Session.Path + ".tmp"));

        File.WriteAllText(Session.Path, "not json");
        Assert.Null(Session.Load());
        Session.Clear();
        Assert.False(File.Exists(Session.Path));
    }
}
