using System.Security.Cryptography;
using System.Text;
using AlbumWall.Domain;

namespace AlbumWall.Tests;

public sealed class NavidromeTests : IDisposable
{
    private readonly FakeSubsonic _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "albumwall-nd-" + Guid.NewGuid().ToString("N")[..8]);
    private string CacheFile => Path.Combine(_dir, "cache.json");
    private string Covers => Path.Combine(_dir, "covers");

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Navidrome Client(string? password = null)
    {
        var (salt, token) = Navidrome.Credentials(password ?? _server.Password);
        return new Navidrome("home", _server.Url, _server.User, salt, token);
    }

    private static readonly byte[] Png =
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAYAAACddGYaAAAAEklEQVR4nGP4z8DAAMQMDAwMAB3+Av+bjpuxAAAAAElFTkSuQmCC");

    private void ALibrary()
    {
        _server.Answers["getAlbumList2"] = """
            "albumList2":{"album":[
              {"id":"a1","name":"I Robot","artist":"The Alan Parsons Project","year":1977,"coverArt":"al-a1"},
              {"id":"a2","name":"The Wall","artist":"Pink Floyd","year":1979},
              {"id":"a3","name":"The Wall","artist":"Pink Floyd","year":1979},
              {"id":"a4","name":"Nothing Here","artist":"Nobody","year":2000}
            ]}
            """;
        _server.Answers["search3"] = """
            "searchResult3":{"song":[
              {"id":"s1","albumId":"a1","title":"I Robot","artist":"The Alan Parsons Project","track":1,"discNumber":1,"duration":362,"suffix":"flac","bitRate":900,"size":40000000,"samplingRate":44100,"bitDepth":16},
              {"id":"s2","albumId":"a1","title":"Exit","artist":"The Alan Parsons Project","track":0,"duration":0,"suffix":"aif"},
              {"id":"s3","albumId":"a2","title":"Hey You","artist":"Pink Floyd","track":1,"discNumber":2,"duration":280,"suffix":"mp3","bitRate":320},
              {"id":"s4","albumId":"a3","title":"In the Flesh?","artist":"Pink Floyd","track":1,"discNumber":0,"duration":199,"suffix":"m4a","bitRate":256}
            ]}
            """;
        _server.Covers["al-a1"] = ("image/png", Png);
    }

    // ---- the sign-in ------------------------------------------------------------

    /// Navidrome.cs carries its own MD5 (no System.Security.Cryptography on
    /// every platform the app once targeted): it has to be MD5, at every
    /// length that changes the padding.
    [Theory]
    [InlineData("")]
    [InlineData("hunter2")]
    [InlineData("ünïcödé 🎵")]
    [InlineData("39 bytes and then the salt makes 55...")]
    [InlineData("40 bytes and then the salt makes 56....")]
    [InlineData("a password of forty-eight bytes, then the salt.")]
    [InlineData("a password long enough to need more than one block of the hash, well past sixty-four bytes")]
    public void The_stored_token_is_md5_of_password_and_salt(string password)
    {
        var (salt, token) = Navidrome.Credentials(password);
        Assert.Matches("^[0-9a-f]{16}$", salt);
        Assert.Equal(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password + salt))).ToLowerInvariant(), token);
    }

    [Fact]
    public void Each_sign_in_gets_a_salt_of_its_own() =>
        Assert.NotEqual(Navidrome.Credentials("same").Salt, Navidrome.Credentials("same").Salt);

    [Fact]
    public void The_right_password_is_let_in_and_a_wrong_one_is_told_so()
    {
        Client().Ping();
        var e = Assert.Throws<NavidromeException>(() => Client("wrong").Ping());
        Assert.False(e.Unreachable);
        Assert.Equal("wrong user name or password", e.Message);     // the app's words for code 40
    }

    [Theory]
    [InlineData("nas:4533", "http://nas:4533")]
    [InlineData("  https://music.example.org/  ", "https://music.example.org")]
    [InlineData("http://nas:4533/rest", "http://nas:4533")]
    [InlineData("http://nas:4533/rest/", "http://nas:4533")]
    public void A_typed_address_is_made_an_address(string typed, string server) =>
        Assert.Equal(server, Navidrome.NormalizeServer(typed));

    [Fact]
    public void A_track_path_names_its_library()
    {
        Assert.Equal("home", Navidrome.LibraryOf("navidrome://home/s1.flac"));
        Assert.Null(Navidrome.LibraryOf("/Users/me/Music/a.flac"));
        Assert.True(Navidrome.IsTrack("navidrome://home/s1.flac"));
    }

    // ---- the library --------------------------------------------------------------

    [Fact]
    public void A_library_comes_in_as_the_walls_albums()
    {
        ALibrary();
        var albums = Client().Fetch(CacheFile, Covers);

        // "Nothing Here" has no songs; the two "The Wall" entries are one album.
        Assert.Equal(["I Robot", "The Wall"], albums.Select(a => a.Title));
        var wall = albums[1];
        Assert.Equal(["In the Flesh?", "Hey You"], wall.Tracks.Select(t => t.Title));      // disc 0 is disc 1
        Assert.Equal([1, 2], wall.Tracks.Select(t => t.Disc));

        var robot = albums[0];
        var track = Assert.Single(robot.Tracks);           // the AIFF is not a type the app plays
        Assert.Equal("navidrome://home/s1.flac", track.Path);
        Assert.Equal(TimeSpan.FromSeconds(362), track.Duration);
        Assert.Equal(16, track.BitDepth);
        Assert.Equal(0, wall.Tracks[1].BitDepth);          // an MP3 is lossy: no depth
    }

    [Fact]
    public void Covers_are_kept_by_their_kind_and_measured()
    {
        ALibrary();
        var robot = Client().Fetch(CacheFile, Covers)[0];
        Assert.Equal(".png", Path.GetExtension(robot.ArtPath));
        Assert.Equal((3, 2), (robot.ArtWidth, robot.ArtHeight));
        Assert.Equal(Png, File.ReadAllBytes(robot.ArtPath!));
    }

    [Fact]
    public void What_was_fetched_loads_again_without_the_server()
    {
        ALibrary();
        var fetched = Client().Fetch(CacheFile, Covers);
        _server.Dispose();
        var loaded = Client().Load(CacheFile, Covers);
        Assert.Equal(fetched.Select(a => a.ToString()), loaded.Select(a => a.ToString()));
        Assert.Equal(fetched[0].ArtPath, loaded[0].ArtPath);
    }

    [Fact]
    public void A_cache_that_cannot_be_read_is_no_library_not_a_crash()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CacheFile, "{ not json");
        Assert.Empty(Client().Load(CacheFile, Covers));
        Assert.Empty(Client().Load(Path.Combine(_dir, "none.json"), Covers));
    }

    [Fact]
    public void A_server_error_is_unreachable_and_a_refusal_is_not()
    {
        _server.Status["getAlbumList2"] = 503;
        Assert.True(Assert.Throws<NavidromeException>(() => Client().Fetch(CacheFile, Covers)).Unreachable);
        _server.Status["getAlbumList2"] = 404;
        Assert.False(Assert.Throws<NavidromeException>(() => Client().Fetch(CacheFile, Covers)).Unreachable);
    }

    [Fact]
    public void Nobody_listening_is_unreachable_and_says_nothing_of_the_sign_in()
    {
        var url = _server.Url;
        _server.Dispose();
        var (salt, token) = Navidrome.Credentials("secret");
        var e = Assert.Throws<NavidromeException>(() => new Navidrome("home", url, "dave", salt, token).Ping());
        Assert.True(e.Unreachable);
        Assert.DoesNotContain(token, e.Message);
        Assert.DoesNotContain(salt, e.Message);
    }

    // ---- lyrics --------------------------------------------------------------------

    [Fact]
    public void Timed_lyrics_are_preferred_and_the_offset_applied()
    {
        _server.Answers["getLyricsBySongId"] = """
            "lyricsList":{"structuredLyrics":[
              {"synced":false,"line":[{"value":"plain one"},{"value":"plain two"}]},
              {"synced":true,"offset":500,"line":[{"start":1500,"value":" one "},{"start":4000,"value":"two"}]}
            ]}
            """;
        var l = Client().LyricsOf("navidrome://home/s1.flac")!;
        Assert.True(l.Timed);
        Assert.Equal([("one", TimeSpan.FromSeconds(1)), ("two", TimeSpan.FromSeconds(3.5))], l.Lines.Select(x => (x.Text, x.At!.Value)));
        Assert.Contains("getLyricsBySongId", _server.Asked);
    }

    [Fact]
    public void Plain_lyrics_come_as_plain_and_none_as_null()
    {
        _server.Answers["getLyricsBySongId"] = """ "lyricsList":{"structuredLyrics":[{"synced":false,"line":[{"value":"words"}]}]} """;
        var l = Client().LyricsOf("navidrome://home/s1.flac")!;
        Assert.False(l.Timed);
        Assert.Equal("words", l.Lines.Single().Text);

        _server.Answers["getLyricsBySongId"] = """ "lyricsList":{} """;
        Assert.Null(Client().LyricsOf("navidrome://home/s1.flac"));
        _server.Status["getLyricsBySongId"] = 500;
        Assert.Null(Client().LyricsOf("navidrome://home/s1.flac"));      // never throws
    }

    [Fact]
    public void The_users_libraries_are_listed_in_the_servers_order()
    {
        _server.Answers["getMusicFolders"] = """ "musicFolders":{"musicFolder":[{"id":3,"name":"FLAC"},{"id":1,"name":"iTunes"},{"name":"no id"}]} """;
        Assert.Equal([new Navidrome.Folder("3", "FLAC"), new Navidrome.Folder("1", "iTunes")], Client().Folders());
    }
}
