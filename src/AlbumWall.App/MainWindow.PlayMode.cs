// AlbumWall - what happens when an album ends, the wall that follows it, and
// the menu under the gear.
//
// His ask, 2026-11-01: "when an album finishes the play stops and there is no
// way to change this ... keep the current behavior as a default, but also have
// it keep going to the next album in order? Shuffle by album, song, etc." And:
// "there should be a button on the interface somewhere as well as in prefs",
// and "the wall should move with the shuffle, but ... if someone is actively
// scrolling then it would let them, when the scrolling stops plus a minute or
// so, return to the following behavior".
//
// FOUR CHOICES. Stop, as it always was, is the default. The next album on the
// wall; a random album (each in its own running order, none twice until every
// one has played); every song shuffled. Next and random take the albums the
// wall is showing: the library on it, and the search in the box, so "Billy
// Joel" in the box keeps going through Billy Joel. ReplayGain follows the
// mode unless he has picked one: album gain for albums, track gain for songs
// (as the panel's Shuffle already does).
//
// THE WALL FOLLOWS. When the playing album changes on its own (the next
// album, or the next song of a shuffle), the wall opens it at once, as a click
// on its cover would: "The album change or song change in shuffle should move
// the displayed song/album right away" (Techbench, 2026-11-01). Only a hand on
// the wall at that very moment (a scroll, a click or a key in it, a search
// typed in the last two seconds) puts it off until two seconds after.
// Between changes, someone who has browsed away is left to it: a minute
// after the last of it the wall goes back to what is playing, even
// mid-album, so a wall left running as a display keeps up by itself.
// Only in the three modes that keep going, only while music plays, and never
// in compact, where there is no wall to move. Choosing a mode is not browsing:
// it once counted as such, and on Techbench (2026-11-01) the next album began
// ten seconds after he picked "next album" and the wall stayed put.
//
// THE MENU UNDER THE GEAR came back for this, and the top bar's full-screen
// button went into it (2026-11-01: "drop full screen only, keep compact"). It went on 2026-09-20 (da37455)
// because it held two doors into one window, Preferences and About. Now it
// holds things to do: the play mode and ReplayGain, rescan, compact, full
// screen, and then Preferences, Keys and search, About. Cmd/Ctrl+, still opens
// Preferences straight away. The play controls carry a button for the play
// mode beside Next, dimmed at Stop and lit otherwise; it opens the same four
// choices.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace AlbumWall.App;

/// What happens when the last track of an album ends.
public enum AfterAlbum { Stop, NextAlbum, RandomAlbum, ShuffleSongs }

public partial class MainWindow
{
    /// The words for each choice, used by the menus and Preferences alike.
    internal static string Words(AfterAlbum mode) => mode switch
    {
        AfterAlbum.NextAlbum => "Play the next album",
        AfterAlbum.RandomAlbum => "Play a random album",
        AfterAlbum.ShuffleSongs => "Shuffle all songs",
        _ => "Stop",
    };

    internal AfterAlbum AfterAlbumMode
    {
        get => Enum.TryParse<AfterAlbum>(_settings.AfterAlbum, out var m) ? m : AfterAlbum.Stop;
        set
        {
            if (value == AfterAlbumMode) return;
            _settings.AfterAlbum = value == AfterAlbum.Stop ? null : value.ToString();
            _settings.Save();
            Console.WriteLine($"[play] when an album ends: {Words(value)}");
            _randomBag.Clear();
            ApplySongEnd();
            ShowPlayMode();
            _prefs?.Fill();
        }
    }

    // ---- WHAT PLAYS NEXT ----------------------------------------------------------

    /// True while the player's list is every song shuffled (StartSongShuffle):
    /// the album playing is then whichever the current song comes from.
    private bool _songShuffle;

    /// Random album: the albums not yet played this time round.
    private readonly List<AlbumVm> _randomBag = [];

    /// Next, from any of its buttons and keys. Past the last track it goes on to
    /// what the play mode says, where it used to do nothing.
    private void PlayNext()
    {
        if (_player is null) return;
        WantToSeePlaying();
        // Shuffling songs is chosen and an album is playing: the next song is
        // the shuffle's, not the album's.
        if (AfterAlbumMode == AfterAlbum.ShuffleSongs && !_songShuffle && _player.Index >= 0) { StartSongShuffle(); return; }
        if (_player.Index >= 0 && _player.Index + 1 < _player.Count) _player.Next();
        else if (_player.Index >= 0) AdvanceAfterAlbum();
    }

    /// Previous, from any of its buttons and keys.
    private void PlayPrevious()
    {
        if (_player is null) return;
        WantToSeePlaying();
        _player.Previous();
    }

    /// Next or Previous pressed: a hand on the play controls, not on the wall,
    /// and a wish to see what plays. The minute's wait after browsing is
    /// dropped and the wall goes to it at once ("if I hit next track button or
    /// media key then it should move right away", Techbench, 2026-11-01).
    private void WantToSeePlaying()
    {
        _lastBrowse = long.MinValue / 2;
        FollowNow();
    }

    /// The album (or the shuffle) ran out. Starts what the mode says and returns
    /// true, or false for Stop or when there is nothing to play.
    private bool AdvanceAfterAlbum()
    {
        var mode = AfterAlbumMode;
        if (mode == AfterAlbum.Stop) return false;

        if (mode == AfterAlbum.ShuffleSongs) return StartSongShuffle();

        var albums = _visible.Where(a => a.Album.Tracks.Count > 0).ToList();
        if (albums.Count == 0) return false;

        AlbumVm next;
        if (mode == AfterAlbum.NextAlbum)
        {
            // Wall order, round to the start after the last. An album the
            // search has hidden starts again from the first one shown.
            var at = _playingAlbum is null ? -1 : albums.IndexOf(_playingAlbum);
            next = albums[(at + 1) % albums.Count];
        }
        else
        {
            _randomBag.RemoveAll(a => !albums.Contains(a));
            if (_randomBag.Count == 0)
                _randomBag.AddRange(albums.Where(a => !ReferenceEquals(a, _playingAlbum) || albums.Count == 1));
            next = _randomBag[Random.Shared.Next(_randomBag.Count)];
            _randomBag.Remove(next);
        }

        Console.WriteLine($"[play] album ended: {Words(mode).ToLowerInvariant()}, {next.Artist} - {next.Title}");
        StartPlayback(next, 0);
        FollowNow();
        return true;
    }

    /// Every song of every album on the wall, in a random order, as one list.
    private bool StartSongShuffle()
    {
        var paths = _visible.SelectMany(a => a.Album.Tracks).Select(t => t.Path).ToList();
        if (paths.Count == 0) return false;
        try
        {
            EnsurePlayer();
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(paths));
            if (!_gainChosen) _player.Gain = Playback.GainMode.Track;
            _songShuffle = true;
            _playingPaths = paths;
            _playingAlbum = AlbumOf(paths[0]) ?? _playingAlbum;
            _sessionOver = false;
            _player.Play(paths.Select(Playable).ToList(), 0, names: paths);
            ApplySongEnd();
            if (_playingAlbum is not null) CoverForTransport(_playingAlbum);
            MarkPlayingTrack();
            Console.WriteLine($"[play] shuffling all {paths.Count} songs on the wall");
            FollowNow();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[play] shuffle unavailable: {ex.Message}");
            return false;
        }
    }

    /// "Shuffle all songs" is a mode by the song, not the album: chosen while an
    /// album plays, it takes over when the SONG ends, not the record ("Per song
    /// it's weird so if shuffle songs is selected, it should start right away
    /// when the current song ends", Techbench, 2026-11-01). So with that mode
    /// and an album's own list, the player is told to end with the current
    /// track, and Finished then starts the shuffle (AdvanceAfterAlbum). After
    /// every Play and every change of mode.
    private void ApplySongEnd()
    {
        if (_player is null) return;
        _player.EndAfterCurrent = AfterAlbumMode == AfterAlbum.ShuffleSongs && !_songShuffle;
    }

    /// The album a path belongs to, as the wall has it now (a rescan makes new
    /// view models, so nothing older is kept).
    private AlbumVm? AlbumOf(string path) =>
        _all.FirstOrDefault(a => a.Album.Tracks.Any(t => t.Path == path));

    /// From OnTrackChanged, before the transport is drawn: while every song is
    /// shuffled, the playing album is the current song's.
    private void SongShuffleTrackChanged()
    {
        if (!_songShuffle || _player is not { Index: >= 0 } p || p.Index >= _playingPaths.Count) return;
        if (AlbumOf(_playingPaths[p.Index]) is not { } album || ReferenceEquals(album, _playingAlbum)) return;
        _playingAlbum = album;
        CoverForTransport(album);
        FollowNow();
    }

    // ---- THE WALL FOLLOWS -----------------------------------------------------------

    /// When someone last looked round the wall: a scroll, a click or a key in
    /// it, or the search box changing. Following waits a minute after it.
    private long _lastBrowse = long.MinValue / 2;
    private static readonly TimeSpan FollowAfter = TimeSpan.FromSeconds(60);

    /// A change waits only for a hand that is on the wall right now.
    private static readonly TimeSpan HandsOff = TimeSpan.FromSeconds(2);

    /// The playing album changed and the wall has not caught up with it yet.
    private bool _followPending;

    /// Someone has browsed since the wall last went to what plays. Even with
    /// the playing album still open it may be scrolled out of sight: a scroll
    /// alone, no click, never came back (Techbench, 2026-11-01), because all
    /// this asked was whether the album was open.
    private bool _strayed;
    private DispatcherTimer? _followTimer;

    private void WireFollowing()
    {
        void Browsed(object? _, EventArgs e) => MarkBrowsed(e.GetType().Name.Replace("EventArgs", ""));
        var tunnel = Avalonia.Interactivity.RoutingStrategies.Tunnel;
        WallScroller.AddHandler(PointerWheelChangedEvent, Browsed, tunnel, handledEventsToo: true);
        WallScroller.AddHandler(PointerPressedEvent, Browsed, tunnel, handledEventsToo: true);
        WallScroller.AddHandler(KeyDownEvent, Browsed, tunnel, handledEventsToo: true);
        SearchBox.TextChanged += (_, _) => MarkBrowsed("search");

        // One timer rather than one per browse, each second: a change put off by a
        // hand on the wall goes as soon as the hand stops, and the wall drifting
        // off what is playing is caught a minute after the last browse.
        _followTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Follow());
        _followTimer.Start();
    }

    /// Someone is looking round the wall. Said once per spell of browsing, so
    /// the log shows why the wall waited. Also the DEBUG "browse" command.
    internal void MarkBrowsed(string how)
    {
        if (Environment.TickCount64 - _lastBrowse > FollowAfter.TotalMilliseconds && AfterAlbumMode != AfterAlbum.Stop)
            Console.WriteLine($"[play] browsing ({how}): the wall waits a minute before it follows");
        _lastBrowse = Environment.TickCount64;
        _strayed = true;
    }

    /// The playing album just changed on its own: follow it now unless someone
    /// is browsing, in which case the timer will once they stop.
    private void FollowNow() => Dispatcher.UIThread.Post(() =>
    {
        _followPending = true;
        var since = Environment.TickCount64 - _lastBrowse;
        if (since < HandsOff.TotalMilliseconds && AfterAlbumMode != AfterAlbum.Stop)
            Console.WriteLine("[play] a hand is on the wall: following as soon as it stops");
        Follow();
    }, DispatcherPriority.Background);

    private AlbumVm? _followedTo;
    private long _followedAt;

    private void Follow()
    {
        if (AfterAlbumMode == AfterAlbum.Stop || IsCompact) { _followPending = false; return; }
        if (_player is not { IsPlaying: true } || _playingAlbum is not { } album) return;
        if (!_visible.Contains(album)) { _followPending = false; return; }
        var open = ReferenceEquals(_open, album);
        if (open && !_strayed) { _followPending = false; return; }
        var quiet = Environment.TickCount64 - _lastBrowse;
        if (quiet < (_followPending ? HandsOff : FollowAfter).TotalMilliseconds) return;
        _followPending = false;
        _strayed = false;
        if (open)
        {
            // Open already. Still on screen (a click on one of its tracks counts
            // as browsing too): leave it be. Scrolled out of sight: bring it back.
            if (OpenAlbumInView(WallScroller.Offset.Y) is not null) return;
            Console.WriteLine($"[play] the wall goes back to {album.Artist} - {album.Title}");
            AnchorOn(album);
            return;
        }
        // Asked twice for one change (Next posts a follow, and so does the shuffle
        // it starts): the album is opening already, and a second SetOpen while
        // it does could fold it straight back up.
        if (ReferenceEquals(album, _followedTo) && Environment.TickCount64 - _followedAt < 1500) return;
        _followedTo = album;
        _followedAt = Environment.TickCount64;
        Console.WriteLine($"[play] the wall follows to {album.Artist} - {album.Title}");
        SetOpen(album);
    }

    // ---- THE BUTTON IN THE PLAY CONTROLS, AND THE MENUS ----------------------------

    private void WirePlayMode()
    {
        PlayModeButton.Click += (_, _) => PlayModeMenu().ShowAt(PlayModeButton);
        SettingsButton.Click += (_, _) => GearMenu().ShowAt(SettingsButton);
        WireFollowing();
        ShowPlayMode();
    }

    /// The play controls' button, always there (his call, 2026-11-01: shown only
    /// while the music would go on, it could not be found until the mode had
    /// been found elsewhere, and the controls moved when it came). Dimmed at
    /// Stop, lit otherwise, as players show shuffle and repeat: an arrow for the
    /// next album (and, dimmed, for Stop), crossed arrows for either shuffle.
    private void ShowPlayMode()
    {
        var mode = AfterAlbumMode;
        PlayModeButton.Opacity = mode == AfterAlbum.Stop ? 0.4 : 1;
        PlayModeButton.Content = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(mode is AfterAlbum.NextAlbum or AfterAlbum.Stop
                ? "M 0 6 L 11 6 M 7 2 L 11 6 L 7 10 M 14 1 L 14 11"
                : "M 0 2 L 3 2 L 10 10 L 14 10 M 11 7 L 14 10 L 11 13 M 0 10 L 3 10 L 10 2 L 14 2 M 11 -1 L 14 2 L 11 5"),
            Stroke = (PrevButton.Content as Avalonia.Controls.Shapes.Path)?.Stroke ?? Brushes.White,
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var words = $"When this album ends: {Words(mode).ToLowerInvariant()}";
        ToolTip.SetTip(PlayModeButton, words);
        Avalonia.Automation.AutomationProperties.SetName(PlayModeButton, words);
        UpdateMacPlayMode();
    }

    /// The four choices, ticked one, as a menu of their own (the play controls'
    /// button) or as the gear menu's submenu.
    private List<MenuItem> PlayModeItems() =>
        Enum.GetValues<AfterAlbum>().Select(mode =>
        {
            var item = new MenuItem
            {
                Header = Words(mode),
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "afteralbum",
                IsChecked = mode == AfterAlbumMode,
            };
            item.Click += (_, _) => AfterAlbumMode = mode;
            return item;
        }).ToList();

    private MenuFlyout PlayModeMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        menu.Items.Add(new MenuItem { Header = "When an album ends", IsEnabled = false });
        foreach (var item in PlayModeItems()) menu.Items.Add(item);
        return menu;
    }

    /// The one menu (three lines on the top bar). What the dot on it is about
    /// comes first, as an item that goes there: damaged files (Preferences,
    /// Library) and a waiting update (Preferences, Updates). His call,
    /// 2026-11-01: "an Update available item at the top of the menu".
    private MenuFlyout GearMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        var damaged = _integrity.Values.Sum(s => s.Problems);
        if (damaged > 0)
            menu.Items.Add(Command($"{damaged} damaged file{(damaged == 1 ? "" : "s")} found", () => ShowPrefs(PrefsWindow.Tab.Library)));
        if (App.LatestUpdate is { UpdateAvailable: true } update)
            menu.Items.Add(Command($"Update available: {update.LatestTag.TrimStart('v', 'V')}", () => ShowPrefs(PrefsWindow.Tab.Updates)));
        else if (App.LastUpdateFailed)
            menu.Items.Add(Command("The last update did not finish", () => ShowPrefs(PrefsWindow.Tab.Updates)));
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var after = new MenuItem { Header = "When an album ends" };
        foreach (var item in PlayModeItems()) after.Items.Add(item);
        menu.Items.Add(after);

        // The mode in the words, so it reads at a glance with the RG button gone.
        var gain = new MenuItem { Header = $"ReplayGain: {GainWords(GainMode)}" };
        foreach (var mode in new[] { Playback.GainMode.Album, Playback.GainMode.Track, Playback.GainMode.Off })
        {
            var item = new MenuItem { Header = GainWords(mode), ToggleType = MenuItemToggleType.Radio, GroupName = "gain", IsChecked = GainMode == mode };
            item.Click += (_, _) => { GainMode = mode; _prefs?.Fill(); };
            gain.Items.Add(item);
        }
        menu.Items.Add(gain);
        menu.Items.Add(Command("Equalizer…", () => EqualizerWindow.ShowFrom(this)));
        menu.Items.Add(new Separator());

        menu.Items.Add(Command("Rescan the library", () => ScanLibrary(asked: true)));
        menu.Items.Add(Command("Compact view", EnterCompact, new KeyGesture(Key.M, Shortcut.Primary | Shortcut.CompactExtra)));
        // Not on the Mac, where the green button zooms and full screen is off
        // (MacWindow.ZoomInsteadOfFullScreen); worth another look on a later macOS.
        if (!OperatingSystem.IsMacOS())
            menu.Items.Add(Command(WindowState == WindowState.FullScreen ? "Exit full screen" : "Full screen",
                                   ToggleFullScreen, new KeyGesture(Key.F11)));
        menu.Items.Add(new Separator());

        menu.Items.Add(Command("Preferences…", () => ShowPrefs(PrefsWindow.Tab.Library), new KeyGesture(Key.OemComma, Shortcut.Primary)));
        menu.Items.Add(Command("Keys and search", () => ShowPrefs(PrefsWindow.Tab.Help)));
        menu.Items.Add(Command($"About {App.DisplayName}", () => ShowPrefs(PrefsWindow.Tab.About)));
        return menu;

        static string GainWords(Playback.GainMode mode) => mode switch
        {
            Playback.GainMode.Album => "By album",
            Playback.GainMode.Track => "By track",
            _ => "Off",
        };

        static MenuItem Command(string header, Action run, KeyGesture? key = null)
        {
            var item = new MenuItem { Header = header, InputGesture = key };
            item.Click += (_, _) => { Console.WriteLine($"[menu] chose {header}"); run(); };
            return item;
        }
    }

    // ---- THE EQUALIZER (Playback/Equalizer.cs, EqualizerWindow.cs) ------------

    /// As kept, four bands in range; off and flat when never set.
    internal AlbumWall.Playback.EqSettings EqualizerSettings
    {
        get => (_settings.Equalizer ?? new AlbumWall.Playback.EqSettings()).Checked();
        set
        {
            _settings.Equalizer = value;
            if (_player is not null) _player.Equalizer = value;
            _prefs?.Fill();
            SaveEqualizerSoon();
        }
    }

    /// A slider swept by ear changes the setting many times a second: it is
    /// written once it has rested half a second.
    private Avalonia.Threading.DispatcherTimer? _eqSave;
    private void SaveEqualizerSoon()
    {
        _eqSave ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), Avalonia.Threading.DispatcherPriority.Background,
            (_, _) => { _eqSave!.Stop(); _settings.Save(); Console.WriteLine($"[eq] {(_settings.Equalizer?.On == true ? "on" : "off")}: "
                + string.Join(", ", (_settings.Equalizer?.Bands ?? []).Select(b => $"{b.Frequency:0} Hz {b.Gain:+0.0;-0.0;0} dB"))); });
        _eqSave.Stop();
        _eqSave.Start();
    }
}
