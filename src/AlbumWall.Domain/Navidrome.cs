// AlbumWall — a Navidrome server as a library.
//
// The second kind of library: not a folder the scanner walks but a server that
// already knows what it holds. It speaks Subsonic, the API Navidrome serves,
// and what comes back is turned into the same Album and Track the scanner
// makes, so the wall, the panel and the player do not know the difference.
//
// READ-ONLY, ALL OF IT. Nothing here writes to the server: no tags, no ratings,
// no play counts. The files are asked for as they are (`format=raw`), which was
// measured byte-identical to the originals and gapless before any of this was
// written.
//
// WHAT IS KEPT ON THIS MACHINE, and why each:
//   * The album list, as one JSON file, so the wall opens at once and is then
//     checked against the server behind it. No secrets in it.
//   * The covers, as files. Everything that shows a cover here reads a file
//     (the wall, the palette, the lock screen), and a cover address on the
//     server would have to carry the sign-in with it wherever it went. A
//     server's cover id changes when the picture does, so a file named for the
//     id is never stale.
//
// A TRACK'S PATH IS NOT ITS ADDRESS. It is `navidrome://<library>/<song>.<ext>`,
// which says which song without saying how to sign in, because a track's path
// goes to places a sign-in must not: the session file, the log, other programs
// asking what is playing. StreamUrl turns it into the real address at the one
// moment the player needs it.
//
// THE PASSWORD IS NEVER KEPT. Subsonic signs each request with a salt and
// md5(password + salt); those two are made once at sign-in and stored, and the
// password is forgotten.

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbumWall.Domain;

/// The server said no, or did not say anything.
public sealed class NavidromeException(string message, bool unreachable, Exception? inner = null)
    : Exception(message, inner)
{
    /// Not answering, as opposed to answering with a refusal: a server that is
    /// off or asleep is looked for again, a wrong password is not.
    public bool Unreachable { get; } = unreachable;
}

/// How the kept album list is written and read: generated at build time, so it
/// works the same in the app and in a tool that has reflection switched off.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(Navidrome.Cache))]
internal sealed partial class NavidromeJson : JsonSerializerContext;

public sealed class Navidrome
{
    public const string Scheme = "navidrome://";

    private const string ApiVersion = "1.16.1";
    private const string ClientName = "albumwall";
    private const int Page = 500;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _library;
    private readonly string _server;
    private readonly string _auth;
    private readonly string _only;

    /// One of the server's own libraries ("music folders" to Subsonic): the
    /// lossless one and the lossy one, say.
    public sealed record Folder(string Id, string Name);

    /// `library` is the id the track paths carry, so a path finds its way back
    /// to the server it came from. `folder` is the one library of the server's
    /// this is; without it, it is everything the user may see there, which is
    /// what a library added before there was a choice still is.
    public Navidrome(string library, string server, string user, string salt, string token,
                     string? folder = null)
    {
        _library = library;
        _only = string.IsNullOrEmpty(folder) ? "" : $"&musicFolderId={Uri.EscapeDataString(folder)}";
        _server = server.TrimEnd('/');
        _auth = $"u={Uri.EscapeDataString(user)}&t={token}&s={salt}&v={ApiVersion}&c={ClientName}";
    }

    /// What is stored instead of the password.
    public static (string Salt, string Token) Credentials(string password)
    {
        var salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        return (salt, Md5.Hex(Encoding.UTF8.GetBytes(password + salt)));
    }

    /// What he typed, as an address: "nas:4533" is http://nas:4533, and a
    /// pasted address may end in a slash or in the API's own /rest.
    public static string NormalizeServer(string typed)
    {
        var s = typed.Trim().TrimEnd('/');
        if (s.EndsWith("/rest", StringComparison.OrdinalIgnoreCase)) s = s[..^5];
        if (s.Length > 0 && !s.Contains("://")) s = "http://" + s;
        return s;
    }

    public static bool IsTrack(string path) => path.StartsWith(Scheme, StringComparison.Ordinal);

    /// The library a track path belongs to, or null if it is not one of these.
    public static string? LibraryOf(string path)
    {
        if (!IsTrack(path)) return null;
        var rest = path[Scheme.Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : null;
    }

    /// Where the player gets the song: the file as it is, never transcoded.
    public string StreamUrl(string path) =>
        $"{_server}/rest/stream?id={Uri.EscapeDataString(IdOf(path))}&format=raw&{_auth}";

    /// The server's own id for the song a track path names.
    private static string IdOf(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// Asks the server whether it knows us. Throws NavidromeException if not.
    public void Ping(CancellationToken ct = default) => Get("ping", "", ct);

    /// The server's libraries this user may see, in the server's order. Throws
    /// NavidromeException.
    public IReadOnlyList<Folder> Folders(CancellationToken ct = default)
    {
        using var doc = Get("getMusicFolders", "", ct);
        return Items(doc, "musicFolders", "musicFolder")
            .Select(f => new Folder(
                f.TryGetProperty("id", out var id) ? id.ToString() : "",
                Text(f, "name")))
            .Where(f => f.Id.Length > 0)
            .ToList();
    }

    // ---- one song, for Properties -------------------------------------------

    /// What the server says of one song: its tags as the server read them out
    /// of the file, and its lyrics. `Problem` is set, and the rest empty, if
    /// the server could not be asked.
    public sealed record SongFacts(IReadOnlyList<(string Label, string Value)> Tags, int Channels,
                                   string? Lyrics, string? Problem);

    /// Asks the server about one song. Two requests, made when Properties is
    /// opened on it and not kept: the file is not fetched to be described.
    /// Never throws; not for the UI thread.
    public SongFacts Describe(string path, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(IdOf(path));
        try
        {
            var tags = new List<(string, string)>();
            int channels;
            using (var doc = Get("getSong", $"id={id}", ct))
            {
                if (!doc.RootElement.GetProperty("subsonic-response").TryGetProperty("song", out var s))
                    return new SongFacts([], 0, null, "the server does not know this song");

                void Add(string label, string value) { if (!string.IsNullOrWhiteSpace(value)) tags.Add((label, value)); }
                // A list of names, whether the server gives them bare or as
                // things with a name.
                string Names(string list) =>
                    s.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array
                        ? string.Join("; ", items.EnumerateArray()
                            .Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : Text(i, "name"))
                            .Where(n => n.Length > 0))
                        : "";
                string Or(string first, string second) => first.Length > 0 ? first : second;

                // The same lines, in the same order, as a file's own tags are
                // shown in when they have codes for names.
                Add("Title", Text(s, "title"));
                Add("Artist", Or(Text(s, "displayArtist"), Text(s, "artist")));
                Add("Album artist", Or(Text(s, "displayAlbumArtist"), Names("albumArtists")));
                Add("Album", Text(s, "album"));
                Add("Year", Number(s, "year") is > 0 and var year ? year.ToString() : "");
                Add("Track", Number(s, "track") is > 0 and var track ? track.ToString() : "");
                Add("Disc", Number(s, "discNumber") is > 0 and var disc ? disc.ToString() : "");
                Add("Genre", Or(Names("genres"), Text(s, "genre")));
                Add("Composer", Text(s, "displayComposer"));
                Add("Comment", Text(s, "comment"));
                Add("ISRC", Names("isrc"));
                Add("MusicBrainz recording", Text(s, "musicBrainzId"));
                channels = Number(s, "channelCount");
            }

            string? lyrics = null;
            try
            {
                using var doc = Get("getLyricsBySongId", $"id={id}", ct);
                // Plain ones before timed ones: it is the words that are shown.
                foreach (var one in Items(doc, "lyricsList", "structuredLyrics")
                             .OrderBy(l => l.TryGetProperty("synced", out var timed) && timed.ValueKind == JsonValueKind.True))
                {
                    if (!one.TryGetProperty("line", out var lines) || lines.ValueKind != JsonValueKind.Array) continue;
                    var text = string.Join("\n", lines.EnumerateArray().Select(l => Text(l, "value"))).Trim();
                    if (text.Length == 0) continue;
                    lyrics = text;
                    break;
                }
            }
            catch (NavidromeException ex) when (!ex.Unreachable)
            {
                // A server that has no such call has no lyrics to give; the
                // tags are still worth showing.
            }
            return new SongFacts(tags, channels, lyrics, null);
        }
        catch (NavidromeException ex)
        {
            return new SongFacts([], 0, null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new SongFacts([], 0, null, "it did not answer in time");
        }
    }

    // ---- what is kept: the album list ---------------------------------------

    internal sealed class Cache
    {
        public int Version { get; set; } = 1;
        public List<CachedAlbum> Albums { get; set; } = [];
        public List<CachedSong> Songs { get; set; } = [];
    }

    internal sealed class CachedAlbum
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Artist { get; set; } = "";
        public int Year { get; set; }
        public string? CoverArt { get; set; }
    }

    internal sealed class CachedSong
    {
        public string Id { get; set; } = "";
        public string AlbumId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public int Track { get; set; }
        public int Disc { get; set; }
        public int Duration { get; set; }
        public string Suffix { get; set; } = "";
        public int BitRate { get; set; }
        public long Size { get; set; }
        public int SampleRate { get; set; }
        public int BitDepth { get; set; }
    }

    /// The library as it was when last fetched, or nothing. Never throws, and
    /// never asks the server: this is what makes the wall open at once.
    public IReadOnlyList<Album> Load(string cacheFile, string coverDir)
    {
        try
        {
            if (!File.Exists(cacheFile)) return [];
            var cache = JsonSerializer.Deserialize(File.ReadAllText(cacheFile), NavidromeJson.Default.Cache);
            return cache is { Version: 1 } ? Build(cache, coverDir) : [];
        }
        catch (Exception)
        {
            return [];      // a cache that cannot be read is a cache that is fetched again
        }
    }

    /// The library as the server has it now: every album, every song, and the
    /// covers not already here. Remembered for Load. Throws NavidromeException.
    public IReadOnlyList<Album> Fetch(string cacheFile, string coverDir,
                                      Action<LibraryScanner.Progress>? onProgress = null,
                                      CancellationToken ct = default)
    {
        var cache = new Cache();

        for (var offset = 0; ; offset += Page)
        {
            using var doc = Get("getAlbumList2", $"type=alphabeticalByName&size={Page}&offset={offset}{_only}", ct);
            var page = Items(doc, "albumList2", "album");
            foreach (var a in page)
                cache.Albums.Add(new CachedAlbum
                {
                    Id = Text(a, "id"),
                    Name = Text(a, "name"),
                    Artist = Text(a, "artist"),
                    Year = Number(a, "year"),
                    CoverArt = a.TryGetProperty("coverArt", out var c) ? c.GetString() : null,
                });
            if (page.Count < Page) break;
        }

        // An empty search is every song there is, a page at a time: thirty-odd
        // requests for a library that would be a thousand asked album by album.
        var total = 0;
        for (var offset = 0; ; offset += Page)
        {
            using var doc = Get("search3", $"query=&artistCount=0&albumCount=0&songCount={Page}&songOffset={offset}{_only}", ct);
            var page = Items(doc, "searchResult3", "song");
            foreach (var s in page)
                cache.Songs.Add(new CachedSong
                {
                    Id = Text(s, "id"),
                    AlbumId = Text(s, "albumId"),
                    Title = Text(s, "title"),
                    Artist = Text(s, "artist"),
                    Track = Number(s, "track"),
                    Disc = Number(s, "discNumber"),
                    Duration = Number(s, "duration"),
                    Suffix = Text(s, "suffix"),
                    BitRate = Number(s, "bitRate"),
                    Size = s.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                    SampleRate = Number(s, "samplingRate"),
                    BitDepth = Number(s, "bitDepth"),
                });
            total += page.Count;
            onProgress?.Invoke(new LibraryScanner.Progress(total, Math.Max(total, 1), cache.Albums.Count, null));
            if (page.Count < Page) break;
        }

        FetchCovers(cache, coverDir, total, onProgress, ct);

        var albums = Build(cache, coverDir);
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        var temp = cacheFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(cache, NavidromeJson.Default.Cache));
        File.Move(temp, cacheFile, overwrite: true);
        return albums;
    }

    /// Forgets what was kept for a library: its list and its covers.
    public static void Forget(string cacheFile, string coverDir)
    {
        try
        {
            if (File.Exists(cacheFile)) File.Delete(cacheFile);
            if (Directory.Exists(coverDir)) Directory.Delete(coverDir, recursive: true);
        }
        catch (Exception)
        {
            // Left behind, then: a few megabytes nothing reads.
        }
    }

    // ---- covers -------------------------------------------------------------

    /// A cover id as a file name, without its extension. Navidrome's are
    /// already safe ("al-…_6a3b0daf"); this is for a server whose are not.
    private static string CoverName(string coverDir, string id) =>
        Path.Combine(coverDir, string.Concat(id.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')));

    /// The kinds of picture a cover is kept as. The extension is what the rest
    /// of the app knows a picture file by, so a cover is given its real one.
    private static readonly string[] CoverKinds = [".jpg", ".png", ".webp", ".gif"];

    /// The cover's file, if it is here.
    private static string? CoverFile(string coverDir, string id)
    {
        var name = CoverName(coverDir, id);
        foreach (var kind in CoverKinds)
            if (File.Exists(name + kind)) return name + kind;
        return null;
    }

    private void FetchCovers(Cache cache, string coverDir, int songs,
                             Action<LibraryScanner.Progress>? onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(coverDir);
        var wanted = cache.Albums.Where(a => !string.IsNullOrEmpty(a.CoverArt))
                          .Select(a => a.CoverArt!).Distinct().ToList();
        var missing = wanted.Where(id => CoverFile(coverDir, id) is null).ToList();

        var done = 0;
        Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, id =>
        {
            try
            {
                using var response = Send($"getCoverArt?id={Uri.EscapeDataString(id)}", ct);
                // A cover the server cannot produce comes back as an error in
                // JSON or XML, not as a picture: that album simply has none.
                if (response.Content.Headers.ContentType?.MediaType is { } type && type.StartsWith("image/"))
                {
                    var file = CoverName(coverDir, id) + type switch
                    {
                        "image/png" => ".png",
                        "image/webp" => ".webp",
                        "image/gif" => ".gif",
                        _ => ".jpg",
                    };
                    var temp = file + ".tmp";
                    using (var to = File.Create(temp))
                        response.Content.ReadAsStream(ct).CopyTo(to);
                    File.Move(temp, file, overwrite: true);
                }
            }
            catch (NavidromeException)
            {
                // One cover is not the library. It is asked for again next time.
            }
            var n = Interlocked.Increment(ref done);
            onProgress?.Invoke(new LibraryScanner.Progress(songs, Math.Max(songs, 1), cache.Albums.Count,
                                                           $"covers {n:N0} of {missing.Count:N0}"));
        });

        // A cover whose id is no longer anyone's has been replaced or removed.
        var keep = wanted.Select(id => CoverFile(coverDir, id)).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(coverDir))
            if (!keep.Contains(file))
                try { File.Delete(file); } catch (Exception) { }
    }

    // ---- into the app's own terms -------------------------------------------

    private List<Album> Build(Cache cache, string coverDir)
    {
        // The same identity as everywhere else: (album artist, title). Two
        // entries the server keeps apart under one name are one album here, as
        // two folders of one album are to the scanner.
        var albums = new Dictionary<(string, string), Album>();
        var byId = new Dictionary<string, Album>();
        foreach (var a in cache.Albums)
        {
            var key = (a.Artist, a.Name);
            if (!albums.TryGetValue(key, out var album))
            {
                album = new Album { AlbumArtist = a.Artist, Title = a.Name, Year = a.Year };
                albums[key] = album;
            }
            byId[a.Id] = album;

            if (album.ArtPath is null && !string.IsNullOrEmpty(a.CoverArt)
                && CoverFile(coverDir, a.CoverArt) is { } cover)
                TakeCover(album, cover);
        }

        // THE SAME FILE TYPES AS A FOLDER LIBRARY: FLAC, MP3, M4A, Ogg, Opus
        // and WAV, the scanner's list, which is also what the audio engine is
        // built to read. A server indexes whatever it finds, and Hambench's
        // iTunes-era library turned out to hold 24 AIFF files, every one a 0 s
        // menu click (Exit, Limit, Selection, SelectionChange) from the iTunes
        // LP booklets bundled with six albums, under "[Unknown Album]" in the
        // Statistics as AIF (2026-10-04). Our libmpv has no AIFF demuxer, so
        // none of them could have played; leaving them out makes the counts
        // what can be played, and makes a server's wall agree with a folder
        // scan of the same files.
        var skipped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in cache.Songs)
        {
            if (!byId.TryGetValue(s.AlbumId, out var album)) continue;
            var suffix = s.Suffix.Length > 0 ? "." + s.Suffix : "";
            if (!LibraryScanner.AudioExtensions.Contains(suffix, StringComparer.OrdinalIgnoreCase))
            {
                skipped[suffix.Length > 0 ? suffix : "(none)"] = skipped.GetValueOrDefault(suffix.Length > 0 ? suffix : "(none)") + 1;
                continue;
            }
            album.Tracks.Add(new Track(
                Disc: s.Disc > 0 ? s.Disc : 1,
                Number: s.Track,
                Title: s.Title,
                Artist: s.Artist,
                Duration: TimeSpan.FromSeconds(s.Duration),
                Path: $"{Scheme}{_library}/{s.Id}{suffix}",
                SampleRate: s.SampleRate,
                BitDepth: LosslessDepth(suffix, s.BitDepth, s.BitRate),
                Bitrate: s.BitRate,
                Size: s.Size));
        }

        foreach (var a in albums.Values)
        {
            a.Tracks.Sort((x, y) => x.Disc != y.Disc
                ? x.Disc.CompareTo(y.Disc)
                : x.Number.CompareTo(y.Number));
            a.SearchText = $"{a.AlbumArtist}\n{a.Title}".ToLowerInvariant();
        }

        if (skipped.Count > 0)
            Console.WriteLine("[navidrome] left out, not a type this app plays: "
                            + string.Join(", ", skipped.Select(kv => $"{kv.Value} {kv.Key}")));

        // An album the server lists with no songs has nothing to play.
        return albums.Values.Where(a => a.Tracks.Count > 0)
                     .OrderBy(a => a.SortKey, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// The bit depth as the rest of the app means it: zero for a lossy file,
    /// which is how the Statistics, the format line and the compact view tell
    /// lossy from lossless (and AAC from ALAC). The scanner gets that from the
    /// file; a server does not say it the same way - Navidrome reports 16 bits
    /// for AAC - so Hambench's all-lossy iTunes library came up in Statistics
    /// as 6,889 "lossless" tracks, and Shinedown's Amaryllis, AAC at 48 kHz, as
    /// the "12 odd files" at 48 kHz / 16-bit (2026-10-04). MP3, Ogg and Opus
    /// are lossy whatever is reported. An .m4a is AAC or ALAC: ALAC at 16 bits
    /// and 44.1 kHz runs to many hundreds of kbit/s and AAC tops out around
    /// 320, so under 500 is AAC.
    internal static int LosslessDepth(string suffix, int reported, int bitrate) =>
        suffix.ToLowerInvariant() switch
        {
            ".mp3" or ".ogg" or ".opus" => 0,
            ".m4a" when bitrate > 0 && bitrate < 500 => 0,
            _ => reported,
        };

    private static void TakeCover(Album album, string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0) return;
            album.ArtPath = file;
            album.ArtSize = info.Length;
            album.ArtModified = info.LastWriteTimeUtc.Ticks;

            // The size is in the first few kilobytes of any picture this reads.
            var head = new byte[(int)Math.Min(info.Length, 256 * 1024)];
            using (var from = info.OpenRead()) from.ReadExactly(head);
            if (ImageSize.Read(head) is { } size)
            {
                album.ArtWidth = size.Width;
                album.ArtHeight = size.Height;
            }
        }
        catch (Exception)
        {
            // No cover, then, rather than no album.
        }
    }

    // ---- the wire -----------------------------------------------------------

    private HttpResponseMessage Send(string call, CancellationToken ct)
    {
        var url = $"{_server}/rest/{call}{(call.Contains('?') ? '&' : '?')}{_auth}&f=json";
        try
        {
            var response = Http.Send(new HttpRequestMessage(HttpMethod.Get, url),
                                     HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new NavidromeException($"the server answered {status}", unreachable: status >= 500);
            }
            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                      or InvalidOperationException or UriFormatException)
        {
            // The message of the innermost exception is the useful one ("Connection
            // refused"), and none of them carries the address with the sign-in on it.
            throw new NavidromeException(ex.GetBaseException().Message, unreachable: true, ex);
        }
    }

    private JsonDocument Get(string call, string query, CancellationToken ct)
    {
        using var response = Send(query.Length > 0 ? $"{call}?{query}" : call, ct);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(response.Content.ReadAsStream(ct));
        }
        catch (JsonException ex)
        {
            throw new NavidromeException("the answer was not a Subsonic one; is this a Navidrome server?",
                                         unreachable: false, ex);
        }

        if (!doc.RootElement.TryGetProperty("subsonic-response", out var root))
        {
            doc.Dispose();
            throw new NavidromeException("the answer was not a Subsonic one; is this a Navidrome server?",
                                         unreachable: false);
        }
        if (Text(root, "status") != "ok")
        {
            var code = root.TryGetProperty("error", out var error) ? Number(error, "code") : 0;
            var said = root.TryGetProperty("error", out error) ? Text(error, "message") : "";
            doc.Dispose();
            throw new NavidromeException(code is 40 or 41 ? "wrong user name or password"
                                         : said.Length > 0 ? said : "the server refused",
                                         unreachable: false);
        }
        return doc;
    }

    private static List<JsonElement> Items(JsonDocument doc, string holder, string list) =>
        doc.RootElement.GetProperty("subsonic-response").TryGetProperty(holder, out var h)
        && h.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().ToList()
            : [];

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    // ---- MD5 ----------------------------------------------------------------

    /// MD5 in plain code, because Subsonic's sign-in is defined in terms of it
    /// and the framework's own goes through the system's OpenSSL on Linux, where
    /// a distribution may ship one that .NET cannot load. It guards nothing
    /// here that a stronger hash would guard better: it is the protocol's.
    private static class Md5
    {
        private static readonly int[] Shift =
        [
            7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
            5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
            4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
            6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
        ];

        private static readonly uint[] K = Enumerable.Range(0, 64)
            .Select(i => (uint)(long)Math.Floor(Math.Abs(Math.Sin(i + 1)) * 4294967296.0)).ToArray();

        public static string Hex(byte[] message)
        {
            uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;

            var padded = new byte[(message.Length + 8) / 64 * 64 + 64];
            message.CopyTo(padded, 0);
            padded[message.Length] = 0x80;
            BitConverter.TryWriteBytes(padded.AsSpan(padded.Length - 8), (ulong)message.Length * 8);
            if (!BitConverter.IsLittleEndian) padded.AsSpan(padded.Length - 8).Reverse();

            var m = new uint[16];
            for (var block = 0; block < padded.Length; block += 64)
            {
                for (var i = 0; i < 16; i++)
                    m[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(block + i * 4));

                uint a = a0, b = b0, c = c0, d = d0;
                for (var i = 0; i < 64; i++)
                {
                    uint f; int g;
                    switch (i / 16)
                    {
                        case 0: f = (b & c) | (~b & d); g = i; break;
                        case 1: f = (d & b) | (~d & c); g = (5 * i + 1) % 16; break;
                        case 2: f = b ^ c ^ d; g = (3 * i + 5) % 16; break;
                        default: f = c ^ (b | ~d); g = 7 * i % 16; break;
                    }
                    f += a + K[i] + m[g];
                    a = d; d = c; c = b;
                    b += uint.RotateLeft(f, Shift[i]);
                }
                a0 += a; b0 += b; c0 += c; d0 += d;
            }

            var digest = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(digest, a0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d0);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
    }
}
