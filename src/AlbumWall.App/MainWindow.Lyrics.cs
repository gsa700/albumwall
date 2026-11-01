// AlbumWall - when the open album shows the words of the song playing.
//
// "Lyrics" sits where the Shuffle button was, in the open album, when that
// album is the one playing and the song playing has words. Pressed, the track
// list gives way to the words (LyricsView.cs), and "Tracks" goes back. The
// choice is kept as the songs change and as the wall follows the music into
// the next album (MainWindow.PlayMode.cs), so a wall left playing can show the
// words of whatever comes; it lasts until it is turned off or the app closes.
//
// The words are read when the song changes, off the UI thread: the .lrc
// beside a file or its tag (Domain/Lyrics.ForFile), or the server's for a
// Navidrome library (Navidrome.LyricsOf). A song with none shows the tracks.

namespace AlbumWall.App;

public partial class MainWindow
{
    /// "Lyrics" was pressed and not "Tracks" since.
    private bool _lyricsOn;

    /// The words of the song playing, and which song they are for.
    private Domain.Lyrics? _songLyrics;
    private string? _lyricsFor;

    private void ToggleLyrics()
    {
        _lyricsOn = !_lyricsOn;
        Console.WriteLine($"[lyrics] {(_lyricsOn ? "shown" : "tracks again")}");
        ShowLyrics();
    }

    /// Words already read, by path: the song playing and the one after it,
    /// read ahead the way Hymie opens the next track before this one ends
    /// (his idea, 2026-11-01: "could we use the gapless playback data to help
    /// on the transitions?"). A song change then finds its words waiting and
    /// swaps them in at the boundary. A null value means "has none".
    private readonly Dictionary<string, Domain.Lyrics?> _wordsRead = [];

    /// Being read now, so a second ask for the same song waits for the first.
    private readonly HashSet<string> _wordsReading = [];

    /// From OnTrackChanged: the song playing now. Shows its words if they have
    /// been read (read ahead, usually), else reads them; then reads the next
    /// song's.
    ///
    /// THE VIEW IS NEVER HIDDEN AT A SONG CHANGE. It was: emptied, hidden, the
    /// new words put in while hidden, shown again, and what came back on screen
    /// was the old song's words with its first line out of sight ("line 1 text
    /// was off the screen on both systems", 2026-11-01). Now the words are
    /// swapped in place; a song whose words are still being read shows an
    /// empty view until they come, and one with none goes back to the tracks.
    private void LyricsForPlaying()
    {
        var index = _player is { Index: >= 0 } p && p.Index < _playingPaths.Count ? p.Index : -1;
        var path = index >= 0 ? _playingPaths[index] : null;
        if (path != _lyricsFor)
        {
            _lyricsFor = path;
            _songLyrics = path is not null && _wordsRead.TryGetValue(path, out var known) ? known : null;
            if (path is not null && !_wordsRead.ContainsKey(path)) ReadWords(path);
            // Keep only this song's and the next one's.
            var next = index >= 0 && index + 1 < _playingPaths.Count ? _playingPaths[index + 1] : null;
            foreach (var old in _wordsRead.Keys.Where(k => k != path && k != next).ToList()) _wordsRead.Remove(old);
            if (next is not null && !_wordsRead.ContainsKey(next)) ReadWords(next);
        }
        ShowLyrics();
    }

    private void ReadWords(string path)
    {
        if (!_wordsReading.Add(path)) return;
        var server = Domain.Navidrome.IsTrack(path) && Domain.Navidrome.LibraryOf(path) is { } id
                     && _settings.AllLibraries().FirstOrDefault(l => l.Id == id) is { } home
            ? NavidromeFor(home) : null;
        _ = Task.Run(() =>
        {
            Domain.Lyrics? words;
            if (server is not null)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                words = server.LyricsOf(path, wait.Token);
            }
            else words = Domain.Navidrome.IsTrack(path) ? null : Domain.Lyrics.ForFile(path);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _wordsReading.Remove(path);
                _wordsRead[path] = words;
                Console.WriteLine($"[lyrics] read {System.IO.Path.GetFileName(path)}: "
                                + (words is null ? "none" : $"{words.Lines.Count} lines, {(words.Timed ? "timed" : "plain")}")
                                + (path == _lyricsFor ? "" : " (ahead)"));
                if (path != _lyricsFor) return;
                _songLyrics = words;
                ShowLyrics();
            });
        });
    }

    /// Puts the state above on the open panel, whichever it is, or on one
    /// being made (NewPanel).
    private void ShowLyrics(PanelRow? made = null)
    {
        var panel = made ?? (_panelAt >= 0 && _panelAt < _rows.Count ? _rows[_panelAt] as PanelRow : null);
        if (panel is null) return;
        var playingHere = ReferenceEquals(panel.Album, _playingAlbum) && _player is { Index: >= 0 } && !_sessionOver;
        // Still being read: as it was, words emptied, so the view stays where it
        // is rather than flicking to the tracks and back.
        var reading = playingHere && _lyricsFor is not null && _wordsReading.Contains(_lyricsFor) && !_wordsRead.ContainsKey(_lyricsFor);
        var available = playingHere && (_songLyrics is not null || (reading && panel.LyricsAvailable));
        panel.Lyrics = playingHere ? _songLyrics : null;
        panel.LyricsAvailable = available;
        panel.LyricsShown = available && _lyricsOn;
    }
}
