// AlbumWall - compact mode: the window becomes the now-playing view.
//
// His idea, 2026-09-28, while mocking up a front panel for a stereo component:
// "the full albumwall could compact itself to mimic the little display while
// playing ... then a button back to the full wall, pick a new album, hit it again
// and your compact again." The view is NowPlayingPanel, the same one the kiosk's
// front panel will show; this file is only what a WINDOW needs around it.
//
// THE WALL IS SWITCHED OFF, NOT SQUEEZED. MainDock goes invisible, which in
// Avalonia means it is not measured or arranged at all, so the wall never lays
// itself out at 740 x 160 and comes back exactly where it was left - the same
// columns, the same scroll, the same open album.
//
// ITS OWN SIZE, NOT THE WALL'S. The compact size is remembered separately
// (Settings.CompactWidth/Height), and RememberNormalGeometry ignores the window
// while it is compact, so neither size can be written down as the other.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AlbumWall.App;

public partial class MainWindow
{
    private bool _compact;
    private WindowState _beforeCompact = WindowState.Normal;
    private long _compactSince;
    private DateTime? _pausedSince;
    private string _compactShown = "";

    /// The first compact size, as a SHARE OF THE SCREEN, in the panel's shape.
    /// On his 6K (3072 wide in desktop units) 740 was "a little small", 1480 too
    /// big, and "halfway between", 1110, first chosen: 36% of the width. A fixed
    /// 1110 would have been more than half a 1080p screen ("thats pretty big on a
    /// 1080p screen, maybe we should make it a ratio", 2026-09-28). Then, living
    /// with 36%: "too big. can we reduce the percent by 25% or so" - 27%, about
    /// 829 x 179 on his. Bounded so a tiny screen still gets a readable strip
    /// and a huge one not a banner.
    private const double CompactShareOfWidth = 0.27;
    private const double CompactMinWidth = 600;
    private const double CompactMaxWidth = 1480;
    private const double CompactFallbackWidth = 1110;       // screen unknown
    private const double CompactAspect = 1480.0 / 320.0;

    /// The compact window's title size, in the view's own 1480 x 320 units.
    private const double CompactTitleSize = 54;

    /// The meters' levels are read where mpv decodes, which is ahead of the
    /// speakers by its output buffer. Holding them back this long puts the
    /// needles with the sound rather than a beat ahead of it.
    private static readonly TimeSpan MeterDelay = TimeSpan.FromMilliseconds(200);
    private readonly Queue<(long At, double L, double R)> _levels = new();
    private DispatcherTimer? _meterTimer;
    private DispatcherTimer? _aspectSnap;

    /// Paused for this long, the view gives way to the standby clock. Short
    /// enough that a paused record stops shouting its title across the room,
    /// long enough that answering the door does not change the screen.
    private static readonly TimeSpan StandbyAfter = TimeSpan.FromMinutes(2);

    public bool IsCompact => _compact;

    private void SetUpCompact()
    {
        CompactButton.Click += (_, _) => EnterCompact();
        Compact.TitleSize = CompactTitleSize;

        Compact.ExpandRequested += ExitCompact;
        Compact.PlayPauseRequested += () => { if (_player is not null && _mprisState.HasTrack) _player.TogglePause(); };
        Compact.NextRequested += () => { if (_player is not null && _mprisState.HasTrack) _player.Next(); };
        Compact.PreviousRequested += () => { if (_player is not null && _mprisState.HasTrack) _player.Previous(); };
        Compact.SeekRequested += fraction =>
        {
            if (_player is null || _player.Duration <= TimeSpan.Zero) return;
            _player.Seek(_player.Duration * fraction);
            CompactTick();
            Dispatcher.UIThread.Post(PushTimeline, DispatcherPriority.Background);
        };
        Compact.BackgroundPressed += e =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            // Double-click goes back to the wall, as double-clicking a title bar
            // is the one gesture every desktop agrees means "make this bigger".
            if (e.ClickCount == 2) { ExitCompact(); e.Handled = true; return; }
            if (e.ClickCount > 2 || WindowState != WindowState.Normal) return;
            BeginMoveDrag(e);
        };

        // The window buttons the desktop asks for, minus Maximize: a compact window
        // has one bigger size and the expand button is it.
        var (left, right) = WindowButtons.Layout();
        foreach (var kind in left.Concat(right))
        {
            if (kind == WindowButtons.Kind.Maximize) continue;
            Compact.WindowButtons.Children.Add(WindowButtons.Create(kind, () =>
            {
                if (kind == WindowButtons.Kind.Minimize) WindowState = WindowState.Minimized;
                else Close();
            }));
        }
    }

#if DEBUG
    /// For the snapshot rig, which has no pointer to hover with and cannot wait
    /// two minutes: "compact hover" shows the controls, "compact standby" skips
    /// the wait.
    private void CompactDebug(string what)
    {
        if (what == "hover") Compact.ShowControlsForTest();
        if (what == "standby") { _pausedSince = DateTime.UtcNow - StandbyAfter - TimeSpan.FromSeconds(1); CompactTick(); }
    }
#endif

    private void ToggleCompact()
    {
        if (_compact) ExitCompact(); else EnterCompact();
    }

    private void EnterCompact()
    {
        if (_compact) return;
        _compact = true;
        _compactSince = Environment.TickCount64;
        _compactShown = "";

        _beforeCompact = WindowState == WindowState.FullScreen ? _beforeFullScreen : WindowState;
        if (_beforeCompact != WindowState.Maximized) _beforeCompact = WindowState.Normal;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

        MainDock.IsVisible = false;
        CompactHost.IsVisible = true;
        MinWidth = CompactMinWidth / 2;
        MinHeight = MinWidth / CompactAspect;

        // After the state change has been handed to the window manager, or leaving
        // maximized can land its own size on top of this one.
        var w = _settings.CompactWidth ?? DefaultCompactWidth();
        var h = _settings.CompactHeight ?? Math.Round(w / CompactAspect);
        ApplyCompactSize(w, h);

        _settings.Compact = true;
        ScheduleSave();
        Compact.ShowMeters = _player?.Metering == true;
        StartMeters();
        CompactTick();
        Console.WriteLine($"[compact] on  {w}x{h}  (was {_beforeCompact})");
    }

    /// Asks for the compact size, and asks again for a moment if it has not
    /// arrived: how long a window manager takes to honour a resize right after a
    /// state change is not knowable from here (the same lesson as
    /// PutBackNormalGeometry), and a strip left wall-sized is the one outcome
    /// that must not stick.
    private void ApplyCompactSize(double w, double h)
    {
        var tries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            if (!_compact) { timer.Stop(); return; }
            if (++tries > 8)
            {
                timer.Stop();
                Console.WriteLine($"[compact] the desktop kept the window at {Bounds.Width:0}x{Bounds.Height:0} "
                                + $"(asked {w:0}x{h:0}); a tiled or snapped window ignores the app's size");
                return;
            }
            if (Math.Abs(Bounds.Width - w) < 2 && Math.Abs(Bounds.Height - h) < 2) { timer.Stop(); return; }
            Width = w;
            Height = h;
        };
        Width = w;
        Height = h;
        timer.Start();
    }

    /// The screen's width in desktop units. WorkingArea is in PHYSICAL pixels on
    /// X11 and Windows (with Scaling the real factor) but already logical on
    /// native Wayland, where Scaling reads 1 (measured on his 6K: 3072 x 1728,
    /// Scaling 1, RenderScaling 2) - dividing by Scaling is right for both.
    private double DefaultCompactWidth()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null || screen.WorkingArea.Width <= 0) return CompactFallbackWidth;
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var width = screen.WorkingArea.Width / scale;
        return Math.Round(Math.Clamp(width * CompactShareOfWidth, CompactMinWidth, CompactMaxWidth));
    }

    private void ExitCompact()
    {
        if (!_compact) return;
        _compact = false;

        _meterTimer?.Stop();
        _meterTimer = null;
        _aspectSnap?.Stop();
        CompactHost.IsVisible = false;
        MainDock.IsVisible = true;
        MinWidth = MinHeight = 0;

        if (_normalGeometry is { } g) { Width = g.W; Height = g.H; }
        else { Width = 1444; Height = 1080; }
        if (_beforeCompact == WindowState.Maximized) WindowState = WindowState.Maximized;

        _settings.Compact = false;
        ScheduleSave();
        Console.WriteLine($"[compact] off  back to {(_beforeCompact == WindowState.Maximized ? "maximized" : $"{Width}x{Height}")}");
    }

    /// The compact size, written down as he leaves it. Not in the first half
    /// second after going compact: until the new size has landed, the window is
    /// still the wall's size, and that is not a compact size anyone chose.
    private void RememberCompactSize()
    {
        if (WindowState != WindowState.Normal) return;
        if (Environment.TickCount64 - _compactSince < 500) return;
        if (double.IsNaN(Width) || double.IsNaN(Height) || Width <= 0 || Height <= 0) return;
        // A strip is wide. Anything taller than half its width is not a compact
        // size anyone chose: it is a window the desktop has refused to resize (a
        // tiled window ignores the app's size), and writing it down would make
        // every later compact come up that size too.
        if (Height > Width / 2) return;
        _settings.CompactWidth = Width;
        _settings.CompactHeight = Height;
        SnapToAspect();
    }

    /// Once a resize has stopped, the window takes the panel's own shape, so the
    /// view fills it with no bands ("the resize is not perfect"). Not during the
    /// drag: the window manager owns the size while the edge is held, and a
    /// window that argues with the pointer is worse than a band.
    private void SnapToAspect()
    {
        _aspectSnap?.Stop();
        _aspectSnap = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _aspectSnap.Tick += (_, _) =>
        {
            _aspectSnap?.Stop();
            if (!_compact || WindowState != WindowState.Normal) return;
            var h = Math.Round(Width / CompactAspect);
            if (Math.Abs(Height - h) < 1.5) return;
            Height = h;
            _settings.CompactHeight = h;
            ScheduleSave();
        };
        _aspectSnap.Start();
    }

    /// Reads the levels about thirty times a second while compact and playing,
    /// and hands the view the reading from MeterDelay ago.
    private void StartMeters()
    {
        _levels.Clear();
        if (_player?.Metering != true) return;
        _meterTimer?.Stop();
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (_, _) =>
        {
            if (!_compact || _player is null) return;
            var now = Environment.TickCount64;
            var (l, r) = _player.IsPlaying ? _player.Levels() : (double.NegativeInfinity, double.NegativeInfinity);
            if (!_player.IsPlaying) _levels.Clear();         // a pause drops the needles now, not in 200 ms
            _levels.Enqueue((now, l, r));
            var due = (At: now, L: l, R: r);
            while (_levels.Count > 0 && now - _levels.Peek().At >= MeterDelay.TotalMilliseconds)
                due = _levels.Dequeue();
            if (_player.IsPlaying && _levels.Count > 0 && now - due.At < MeterDelay.TotalMilliseconds) return;
            Compact.SetLevels(due.L, due.R);
        };
        _meterTimer.Start();
    }

    /// Four times a second while compact: the position always, the rest only
    /// when what should be on screen has changed.
    private void CompactTick()
    {
        if (!_compact) return;

        var playing = _player?.IsPlaying == true;
        if (playing) _pausedSince = null;
        else _pausedSince ??= DateTime.UtcNow;

        var i = _player?.Index ?? -1;
        var hasTrack = _player is not null && _playingAlbum is not null && i >= 0 && i < _playingPaths.Count;
        var standby = !hasTrack || (!playing && DateTime.UtcNow - _pausedSince > StandbyAfter);
        // The cover is part of the key: a restored session gets here before the
        // art has been decoded, and it has to be shown when it arrives.
        var key = hasTrack ? $"{_playingPaths[i]}|{playing}|{standby}|{_playingAlbum!.Cover is not null}" : "none";

        if (key != _compactShown)
        {
            _compactShown = key;
            if (!hasTrack) Compact.ShowStandby(null, null, null, TimeSpan.Zero);
            else
            {
                // The playing screen is kept current under standby too: a pointer
                // over the view wakes it.
                Compact.ShowPlaying(NowPlayingInfo(i, playing));
                if (standby)
                    Compact.ShowStandby(_playingAlbum!.Cover, TrackTitle(_playingPaths[i]),
                                        $"{_playingAlbum.Artist}  \u00b7  {_playingAlbum.Title}", _player!.Position);
            }
        }

        if (hasTrack) Compact.SetPosition(_player!.Position, _player.Duration);
    }

    private NowPlaying NowPlayingInfo(int i, bool playing)
    {
        var vm = _playingAlbum!;
        var album = vm.Album;
        var path = _playingPaths[i];
        var track = album.Tracks.FirstOrDefault(t => t.Path == path);

        // "Track 12 of 14" in the album's own numbering, not the queue's: shuffle
        // reorders the queue, and the record's track 12 is still track 12. A set
        // with more than one disc says which.
        string line;
        var discs = album.Tracks.Select(t => t.Disc).Distinct().Count();
        if (track is null || track.Number <= 0) line = $"Track {i + 1} of {_playingPaths.Count}";
        else if (discs > 1)
            line = $"Disc {track.Disc}  \u00b7  Track {track.Number} of {album.Tracks.Count(t => t.Disc == track.Disc)}";
        else line = $"Track {track.Number} of {album.Tracks.Count}";

        return new NowPlaying(
            Cover: vm.Cover,
            TrackLine: line,
            Format: FormatOf(track, path),
            Title: TrackTitle(path),
            Artist: vm.Artist,
            Album: vm.Title,
            Year: album.Year,
            Accent: Palette.For(album).Light,
            IsPlaying: playing);
    }

    private string TrackTitle(string path) =>
        _playingAlbum?.Album.Tracks.FirstOrDefault(t => t.Path == path)?.Title
        ?? Path.GetFileNameWithoutExtension(path);

    /// "FLAC · 16 / 44.1" for lossless, "MP3 · 320 kbps" for lossy: the two
    /// numbers each kind is actually judged by.
    private static string FormatOf(Domain.Track? track, string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (track is null) return ext;
        // An .m4a is AAC or ALAC, and only ALAC has a bit depth.
        var codec = ext == "M4A" ? (track.BitDepth > 0 ? "ALAC" : "AAC") : ext;
        var khz = track.SampleRate > 0 ? (track.SampleRate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "";
        if (track.BitDepth > 0 && khz.Length > 0) return $"{codec} \u00b7 {track.BitDepth} / {khz}";
        if (track.Bitrate > 0) return $"{codec} \u00b7 {track.Bitrate} kbps";
        return khz.Length > 0 ? $"{codec} \u00b7 {khz} kHz" : codec;
    }
}
