using AlbumWall.Domain;

namespace AlbumWall.Tests;

public sealed class LibraryScannerTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    public void Dispose() => _lib.Dispose();

    private IReadOnlyList<Album> Scan() => new LibraryScanner().Scan(_lib.Root);
    private Album Find(IReadOnlyList<Album> albums, string title) => albums.Single(a => a.Title == title);

    [Fact]
    public void Albums_come_in_the_walls_order_a_leading_article_ignored()
    {
        var albums = Scan();
        Assert.Equal(["I Robot", "The Wall", "Unknown Album", "Songs from the Big Chair"], albums.Select(a => a.Title));
    }

    [Fact]
    public void The_folders_year_beats_the_tags_reissue_year()
    {
        var bigChair = Find(Scan(), "Songs from the Big Chair");
        Assert.Equal(1985, bigChair.Year);       // the tags say 1999
        Assert.Equal(1977, Find(Scan(), "I Robot").Year);   // no year in the folder: the tag's
    }

    [Fact]
    public void Discs_in_folders_of_their_own_are_one_album_in_order()
    {
        var wall = Find(Scan(), "The Wall");
        Assert.Equal(2, wall.DiscCount);
        Assert.Equal(2, wall.Directories.Count);
        Assert.Equal(["Disc 1 Track 1", "Disc 1 Track 2", "Disc 2 Track 1", "Disc 2 Track 2"], wall.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void A_cover_in_any_track_beats_a_sidecar_found_first()
    {
        var robot = Find(Scan(), "I Robot");
        Assert.Equal(_lib.PathOf("The Alan Parsons Project/I Robot/02 I Wouldn't Want to Be Like You.flac"), robot.ArtEmbeddedIn);
        Assert.Null(robot.ArtPath);
        Assert.Equal((32, 32), (robot.ArtWidth, robot.ArtHeight));     // the embedded one's size, not Folder.jpg's
    }

    [Fact]
    public void A_sidecar_front_and_back_are_found_and_measured()
    {
        var bigChair = Find(Scan(), "Songs from the Big Chair");
        Assert.Equal("cover.jpg", Path.GetFileName(bigChair.ArtPath));
        Assert.Equal((16, 16), (bigChair.ArtWidth, bigChair.ArtHeight));
        Assert.Equal("back.jpg", Path.GetFileName(bigChair.BackPath));
        Assert.Equal((24, 24), (bigChair.BackWidth, bigChair.BackHeight));
    }

    [Fact]
    public void A_loose_file_takes_its_performer_and_file_name_and_a_broken_one_is_passed_over()
    {
        var albums = Scan();
        var loose = Find(albums, "Unknown Album");
        Assert.Equal("Someone", loose.AlbumArtist);
        Assert.Equal("untitled thing", Assert.Single(loose.Tracks).Title);
        Assert.DoesNotContain(albums.SelectMany(a => a.Tracks), t => t.Path.EndsWith("broken.flac"));
        Assert.Equal(9, albums.Sum(a => a.Tracks.Count));
    }

    // ---- the index ----------------------------------------------------------------

    [Fact]
    public void With_a_trusted_index_an_unchanged_library_opens_no_file()
    {
        var index = LibraryIndex.Open(_lib.IndexPath);
        var scanner = new LibraryScanner();
        var first = scanner.Scan(_lib.Root, index: index);
        Assert.Equal(10, scanner.FilesOpened);         // nine tracks and the broken one

        var again = scanner.Scan(_lib.Root, index: index);
        Assert.Equal(0, scanner.FilesOpened);          // the broken one is remembered as broken too
        Assert.Equal(first.Select(a => a.ToString()), again.Select(a => a.ToString()));
        Assert.Equal(first.Select(a => (a.ArtPath, a.ArtEmbeddedIn, a.ArtWidth)), again.Select(a => (a.ArtPath, a.ArtEmbeddedIn, a.ArtWidth)));
    }

    [Fact]
    public void A_changed_file_is_read_again_and_Rescan_reads_them_all()
    {
        var index = LibraryIndex.Open(_lib.IndexPath);
        var scanner = new LibraryScanner();
        scanner.Scan(_lib.Root, index: index);

        File.SetLastWriteTimeUtc(_lib.PathOf("Pink Floyd/The Wall/CD1/01 Track.flac"), DateTime.UtcNow.AddMinutes(5));
        scanner.Scan(_lib.Root, index: index);
        Assert.Equal(1, scanner.FilesOpened);

        scanner.Scan(_lib.Root, index: index, trustIndex: false);
        Assert.Equal(10, scanner.FilesOpened);
    }

    [Fact]
    public void A_reopened_index_remembers_what_was_read()
    {
        var scanner = new LibraryScanner();
        scanner.Scan(_lib.Root, index: LibraryIndex.Open(_lib.IndexPath));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        scanner.Scan(_lib.Root, index: LibraryIndex.Open(_lib.IndexPath));
        Assert.Equal(0, scanner.FilesOpened);
    }

    // ---- a library that cannot be reached ------------------------------------------

    [Fact]
    public void A_missing_library_is_said_so_not_read_as_empty()
    {
        var e = Assert.Throws<LibraryUnreachableException>(() => new LibraryScanner().Scan(_lib.PathOf("nowhere")));
        Assert.Equal(LibraryUnreachableException.Reason.Missing, e.Why);
    }

    [Fact]
    public void An_emptied_library_the_index_remembers_is_unreachable_until_Rescan_says_otherwise()
    {
        var index = LibraryIndex.Open(_lib.IndexPath);
        var scanner = new LibraryScanner();
        scanner.Scan(_lib.Root, index: index);
        foreach (var d in Directory.GetDirectories(_lib.Root)) Directory.Delete(d, recursive: true);

        // A share that came back as an empty mount point looks just like this.
        var e = Assert.Throws<LibraryUnreachableException>(() => scanner.Scan(_lib.Root, index: index));
        Assert.Equal(LibraryUnreachableException.Reason.Empty, e.Why);
        Assert.Throws<LibraryUnreachableException>(() => scanner.Scan(_lib.Root, expectMusic: true));

        // Rescan trusts nothing: the music really has gone.
        Assert.Empty(scanner.Scan(_lib.Root, index: index, trustIndex: false));
    }
}
