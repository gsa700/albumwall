// AlbumWall — the wall.
//
// Three jobs here, and nothing else: scan the library off the UI thread, keep
// the tile geometry in step with the size slider, and load cover art only for
// tiles the virtualising layout has actually realised.
//
// The art rule is the whole reason this window is worth building before the
// player: 194 albums today, ~1100 when the lossy collection is folded in. If
// art loaded per item rather than per realised tile, opening the window would
// decode the entire library.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Threading;

namespace AlbumWall.App;

public partial class MainWindow : Window
{
    // Label block under each cover: title + artist + StackPanel spacing.
    // Measured, not guessed — at 11/10 px this is what the two lines occupy.
    private const int LabelHeight = 40;

    private readonly ObservableCollection<WallItem> _rows = [];
    private List<AlbumVm> _all = [];
    private string _filter = "";
    private long _scanMs;
    private List<AlbumVm> _visible = [];
    private List<AlbumRow> _albumRows = [];
    private int _columns = 1;

    /// Index of the open panel within _rows, or -1. Tracked rather than searched
    /// so removing it is one operation.
    private int _panelAt = -1;

    /// Identifies the current chunking. While it is unchanged the album rows are
    /// reused as they are, which is what lets a panel open without resetting the
    /// wall's scroll position.
    private string _chunkKey = "";

    /// Created lazily and only if libmpv is present. A machine without it gets a
    /// wall that works and a transport that explains itself, not a crash.
    private readonly Settings _settings = Settings.Load();

    /// Set once the ReplayGain button is used. After that the app stops choosing
    /// the mode by context: the spec says the setting is user-overridable, and an
    /// override that the next Play silently undoes is not an override.
    private bool _gainChosen;
    private Playback.Player? _player;
    private bool _playerFailed;
    private AlbumVm? _open;

    /// Gap between covers, and between rows. One value, because the grid reads as
    /// a grid only when both match.
    private const double RowSpacing = 15;
    private int _lastReported = -1;

    private readonly int _rung = Ground.DefaultRung;
    private readonly int _chrome = Ground.DefaultChrome;
    private Ground.Ramp _ramp = new(Ground.FallbackHue, 0,
                                    Ground.Rungs[Ground.DefaultRung],
                                    Ground.Chromes[Ground.DefaultChrome]);
    private string _counts = "scanning\u2026";

    public MainWindow()
    {
        InitializeComponent();

        // Size before the window is shown; position after, once the screens are
        // known. Both come back from last time — see RestorePosition.
        if (_settings.WindowWidth is > 320) Width = _settings.WindowWidth.Value;
        if (_settings.WindowHeight is > 240) Height = _settings.WindowHeight.Value;

        Wall.ItemsSource = _rows;
        Wall.ElementPrepared += OnElementPrepared;
        WallScroller.SizeChanged += OnWallResized;
        SearchBox.PropertyChanged += OnSearchChanged;

        StatusText.Text = "scanning…";


        // There are no system decorations, so there is no system resize border
        // either and we own that too. Tunnelling, because the wall and the
        // controls sit over the edges and would otherwise swallow the press.
        AddHandler(PointerMovedEvent, OnPointerMovedForResize, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressedForResize, RoutingStrategies.Tunnel);

        // The top bar IS the title bar now, so it owes the window the three
        // behaviours the system one provided: drag to move, double-click to
        // maximise, and the buttons the desktop asked for.
        TopBar.PointerPressed += OnTitleBarPressed;
        TopBar.DoubleTapped += (_, _) => ToggleMaximised();
        BuildWindowButtons();
        BuildTransport();

        // Position ticks on its own, faster and far cheaper than the full
        // now-playing refresh, which recomputes the palette and rewrites every
        // label and has no business running four times a second.
        DispatcherTimer.Run(() =>
        {
            if (_player is not null && Transport.IsVisible) UpdatePosition();
            return true;
        }, TimeSpan.FromMilliseconds(250));
        ApplyGround();

        Opened += (_, _) => { _restored = true; RestorePosition(); };
        Closing += (_, _) => SaveSettings();

        // Saving ONLY on close loses the window setup to anything that is not a
        // clean exit — a crash, a logout, or a SIGTERM. Geometry is cheap to
        // write, so it is persisted shortly after it settles instead.
        PositionChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();

        Loaded += OnLoaded;

        // Cheap running read of how much art the wall has actually pulled in.
        DispatcherTimer.Run(() =>
        {
#if DEBUG
            CheckSnapshotRequest();
#endif

            if (_all.Count == 0 || _filter.Length > 0) return true;

            var n = ArtCache.Decoded;
            StatusText.Text = $"{_scanMs} ms scan \u00b7 {n} covers held \u00b7 "
                            + $"{ArtCache.HeldBytes / (1024 * 1024)} MB art";
#if DEBUG
            // Echoed to stdout as well: the decode count is the measurement
            // that tells us virtualisation is real, and it is easier to trust
            // from a log than from a glance at the status bar.
            if (n != _lastReported)
            {
                _lastReported = n;
                Console.WriteLine($"[wall] covers held: {n} · "
                                + $"{ArtCache.HeldBytes / (1024 * 1024)} MB");
            }
#endif
            return true;
        }, TimeSpan.FromMilliseconds(500));
    }

    private int CoverPx { get; set; } = 185;

    /// The size a cover would ideally be. Not a limit — the actual size is
    /// whatever divides the window evenly nearest to this.
    private const int TargetCover = 185;

    /// How far the covers may be stretched or squeezed from the target before the
    /// column count is the better thing to change.
    private const int MinCover = 132;
    private const int MaxCover = 268;

    /// Chooses the column count and the cover size together, so a row fills the
    /// window EXACTLY.
    ///
    /// This replaced a cover-size slider. The slider set a fixed size, which left
    /// a ragged strip of unused wall at the right edge at almost every window
    /// width — the leftover of dividing the width by a number that did not go
    /// into it. Choosing the count nearest the target size and then stretching
    /// the covers to fit removes the leftover entirely, and means resizing the
    /// window scales the art rather than only re-flowing it.
    ///
    /// Cover size still drives the column count, which was the rule all along.
    /// The window now supplies the size instead of a control.
    private (int Columns, int Cover) ComputeLayout()
    {
        var available = WallScroller.Bounds.Width
                      - WallScroller.Padding.Left - WallScroller.Padding.Right;
        if (available <= 0) return (_columns, CoverPx);

        var columns = Math.Max(1, (int)Math.Round((available + RowSpacing)
                                                / (TargetCover + RowSpacing)));

        var cover = (int)((available - (columns - 1) * RowSpacing) / columns);

        // At the extremes the covers would have to distort further than looks
        // right, so give up filling the row exactly rather than show covers the
        // wrong size.
        if (cover < MinCover && columns > 1)
        {
            columns--;
            cover = (int)((available - (columns - 1) * RowSpacing) / columns);
        }

        return (columns, Math.Clamp(cover, MinCover, MaxCover));
    }

    /// Puts the window back where it was, corrected to fit the screen it lands on.
    ///
    /// The first version only checked for SOME overlap with a screen — 200x100 px
    /// — and accepted anything that passed. That is far too weak: a window almost
    /// entirely off the edge satisfies it, which is exactly what happened. What is
    /// wanted is not a yes/no test but a correction, so the saved geometry is
    /// clamped into the target screen's working area instead of being trusted or
    /// discarded wholesale.
    private void RestorePosition()
    {
        if (_settings.Maximized) { WindowState = WindowState.Maximized; return; }

        // No saved position means a first run. WindowStartupLocation is Manual so
        // that a saved position is honoured exactly, but with nothing to honour
        // that would drop the window in the top-left corner.
        if (_settings.WindowX is not { } x || _settings.WindowY is not { } y)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        // The screen the saved top-left corner sits on, found from the POINT so
        // that the screen's own scaling can be used for the conversion below.
        var screen = Screens.ScreenFromPoint(new PixelPoint(x, y)) ?? Screens.Primary;
        if (screen is null) return;

        // The SCREEN's scaling, not RenderScaling. RenderScaling is not reliably
        // settled when Opened fires, and reading 1.0 on a 2x display makes the
        // window appear half its real size to this arithmetic — which then places
        // a window twice as large as calculated, hanging off the bottom edge.
        var scale = screen.Scaling > 0 ? screen.Scaling : RenderScaling;
        var w = (int)(Width * scale);
        var h = (int)(Height * scale);

        var area = screen.WorkingArea;

        // Never larger than the space available.
        if (w > area.Width) { w = area.Width; Width = w / scale; }
        if (h > area.Height) { h = area.Height; Height = h / scale; }

        // Then fully inside it.
        var cx = Math.Clamp(x, area.X, area.X + area.Width - w);
        var cy = Math.Clamp(y, area.Y, area.Y + area.Height - h);

        if (cx != x || cy != y)
            Console.WriteLine($"[wall] saved geometry {x},{y} did not fit "
                            + $"{area.Width}x{area.Height}; corrected to {cx},{cy} {w}x{h}");

        Position = new PixelPoint(cx, cy);
    }

    private bool _restored;
    private DispatcherTimer? _saveDebounce;

    /// Coalesces a drag or resize into one write once it stops. Both events fire
    /// continuously while the mouse is down, and writing a file per frame would
    /// be absurd.
    private void ScheduleSave()
    {
        // Ignore the layout churn that happens before the saved geometry has
        // been applied, or the app overwrites last session's position with the
        // default one it briefly had on the way up.
        if (!_restored) return;

        _saveDebounce?.Stop();
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce?.Stop();
            _saveDebounce = null;
            SaveSettings();
        };
        _saveDebounce.Start();
    }

    private void SaveSettings()
    {
        _settings.Maximized = WindowState == WindowState.Maximized;

        // Only record geometry from a normal window. Saving a maximised or
        // minimised window's bounds means restoring to something that was never
        // deliberately chosen.
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }

        _settings.Gain = (_player?.Gain ?? Playback.GainMode.Album).ToString();
        _settings.Save();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Borderless windows are a per-platform negotiation, not a setting that
        // simply takes. Report what the window manager actually granted.
        Console.WriteLine($"[wall] transparency={ActualTransparencyLevel} "
                        + $"corner={Surface.CornerRadius.TopLeft}");
        foreach (var sc in Screens.All)
            Console.WriteLine($"[wall] screen bounds={sc.Bounds} working={sc.WorkingArea} "
                            + $"scaling={sc.Scaling} primary={sc.IsPrimary}");
        Console.WriteLine($"[wall] window pos={Position} size={Width}x{Height} "
                        + $"renderScaling={RenderScaling}");

        Console.WriteLine($"[wall] decorations={WindowDecorations} "
                        + $"extended={IsExtendedIntoWindowDecorations} "
                        + $"decorationMargin={WindowDecorationMargin}");

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");

        // Scan on a worker thread: a cold scan of this library is ~0.1 s but it
        // is bounded by tag reads, and a NAS-backed root will be far slower.
        _ = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            IReadOnlyList<Domain.Album> albums;
            try
            {
                albums = new Domain.LibraryScanner().Scan(root);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => StatusText.Text = $"scan failed: {ex.Message}");
                return;
            }
            sw.Stop();

            var vms = albums.Select(a => new AlbumVm(a)).ToList();
            var tracks = albums.Sum(a => a.Tracks.Count);
            var artists = albums.Select(a => a.AlbumArtist).Distinct().Count();

            Dispatcher.UIThread.Post(() =>
            {
                _all = vms;
                foreach (var vm in vms) vm.OnOpen = SetOpen;
                _counts = $"{albums.Count} albums · {tracks} tracks · {artists} artists";
                ApplyGround();
                _scanMs = sw.ElapsedMilliseconds;
                ApplyFilter();
            });

            // Ground hue comes from the art, so it can only be derived after the
            // scan. Separate task: the wall must be on screen immediately, not
            // waiting on 200 thumbnail decodes.
            var gsw = Stopwatch.StartNew();
            var (hue, concentration) = Ground.HueOf(albums);
            gsw.Stop();
            Console.WriteLine($"[wall] ground hue derived in {gsw.ElapsedMilliseconds} ms");

            Dispatcher.UIThread.Post(() =>
            {
                // A library whose covers agree on nothing gets the fallback
                // rather than an arbitrary hue dressed up as a derived one.
                var h = concentration < 0.2 ? Ground.FallbackHue : hue;
                _ramp = new Ground.Ramp(h, concentration, Ground.Rungs[_rung],
                                        Ground.Chromes[_chrome]);
                ApplyGround();
            });
        });
    }

    /// Draws the window controls the desktop asked for, on the side it asked
    /// for. On this GNOME that is minimise and close on the right and NO
    /// maximise button, because the user turned it off; double-clicking the bar
    /// still maximises, which is how GNOME expects it to be done.
    private void BuildWindowButtons()
    {
        var (left, right) = WindowButtons.Layout();
        Console.WriteLine($"[wall] window buttons left=[{string.Join(",", left)}] "
                        + $"right=[{string.Join(",", right)}]");

        foreach (var (panel, kinds) in new[] { (LeftButtons, left), (RightButtons, right) })
            foreach (var kind in kinds)
                panel.Children.Add(WindowButtons.Create(kind, () => Invoke(kind)));

        void Invoke(WindowButtons.Kind k)
        {
            switch (k)
            {
                case WindowButtons.Kind.Minimize: WindowState = WindowState.Minimized; break;
                case WindowButtons.Kind.Maximize: ToggleMaximised(); break;
                default: Close(); break;
            }
        }
    }

    /// Width of the invisible grab band around the window edge. 7 px is the
    /// smallest that stays comfortable to hit; GNOME's own CSD band is similar.
    private const double ResizeBand = 7;

    private StandardCursorType _cursor = StandardCursorType.Arrow;

    private WindowEdge? EdgeAt(Point p)
    {
        if (WindowState != WindowState.Normal) return null;   // nothing to resize

        double w = Bounds.Width, h = Bounds.Height;
        bool l = p.X <= ResizeBand, r = p.X >= w - ResizeBand;
        bool t = p.Y <= ResizeBand, b = p.Y >= h - ResizeBand;

        return (l, r, t, b) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null
        };
    }

    private void OnPointerMovedForResize(object? sender, PointerEventArgs e)
    {
        var cursor = EdgeAt(e.GetPosition(this)) switch
        {
            WindowEdge.North or WindowEdge.South => StandardCursorType.SizeNorthSouth,
            WindowEdge.West or WindowEdge.East => StandardCursorType.SizeWestEast,
            // All four corners named individually. These are four distinct arrow
            // glyphs on X11, not one shared diagonal, so pairing NW with SE drew
            // an arrow pointing up-left at the bottom-right corner.
            WindowEdge.NorthWest => StandardCursorType.TopLeftCorner,
            WindowEdge.NorthEast => StandardCursorType.TopRightCorner,
            WindowEdge.SouthWest => StandardCursorType.BottomLeftCorner,
            WindowEdge.SouthEast => StandardCursorType.BottomRightCorner,
            _ => StandardCursorType.Arrow
        };

        // Only assign on change: setting Cursor on every mouse move churns the
        // platform cursor and makes the pointer flicker over the wall.
        if (_cursor == cursor) return;
        _cursor = cursor;
        Cursor = new Cursor(cursor);
    }

    private void OnPointerPressedForResize(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var edge = EdgeAt(e.GetPosition(this));
        if (edge is null) return;
        BeginResizeDrag(edge.Value, e);
        e.Handled = true;
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a press on the bar's own background starts a drag. A press that
        // landed on the search box or a window button belongs to that control.
        if (e.Source is not Border) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void ToggleMaximised() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void ApplyGround()
    {
        Surface.Background = SolidColorBrush.Parse(_ramp.GroundHex);
        TopBar.Background = BottomBar.Background = SolidColorBrush.Parse(_ramp.BarHex);
        TopBar.BorderBrush = BottomBar.BorderBrush = SolidColorBrush.Parse(_ramp.EdgeHex);

        // Controls sitting on the bars have to track the bar, not the wall.
        var field = SolidColorBrush.Parse(_ramp.FieldHex);
        var fieldEdge = SolidColorBrush.Parse(_ramp.FieldEdgeHex);
        SearchBox.Background = field;
        SearchBox.BorderBrush = fieldEdge;

        CountsText.Text = _counts;
        Console.WriteLine($"[wall] ground {_ramp.GroundHex} L{_ramp.Lightness}% "
                        + $"hue {_ramp.Hue:0.#} \u00b7 chrome {_ramp.Chrome.Name} "
                        + $"bar {_ramp.BarHex} field {_ramp.FieldHex}");
    }

    private void OnSearchChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.TextProperty) return;
        _filter = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        ApplyFilter();
    }

    /// One resource drives every tile's geometry, so a slider drag re-lays out
    /// the wall without rebuilding items or touching per-tile bindings.
    private void ApplyFilter()
    {
        _visible = _filter.Length == 0
            ? _all.ToList()
            : _all.Where(v => v.Album.SearchText.Contains(_filter)).ToList();

        // A filter that hides the open album has to close it: a panel pointing at
        // a cover that is no longer on the wall is worse than no panel.
        if (_open is not null && !_visible.Contains(_open)) SetOpen(null);

        if (_filter.Length > 0)
            StatusText.Text = $"{_visible.Count} of {_all.Count} albums";

        Rebuild();
    }

    /// A resize only matters if it changes how many covers fit. Dragging a window
    /// edge fires this continuously, and re-chunking 194 albums on every pixel is
    /// work for no visible change.
#if DEBUG
    /// Renders the window to a PNG when a trigger file appears.
    ///
    /// This exists because there is no other way for the person building this to
    /// SEE it. GNOME refuses screenshots to anything but the Shell and the portal
    /// (ScreenshotWindow, ScreenshotArea and Screenshot all return AccessDenied),
    /// there is no grim or xdotool on this machine, and ImageMagick's `import`
    /// cannot reach an XWayland surface. So the app renders its own visual tree on
    /// request, which captures exactly the app and nothing else on the desktop.
    ///
    /// Trigger: touch $ALBUMWALL_SNAP. The PNG is written beside it.
    private void CheckSnapshotRequest()
    {
        var trigger = Environment.GetEnvironmentVariable("ALBUMWALL_SNAP");
        if (string.IsNullOrEmpty(trigger) || !File.Exists(trigger)) return;

        try
        {
            File.Delete(trigger);   // first, so a failure cannot spin

            var w = (int)Math.Ceiling(Bounds.Width * RenderScaling);
            var h = (int)Math.Ceiling(Bounds.Height * RenderScaling);
            if (w <= 0 || h <= 0) return;

            var dpi = 96 * RenderScaling;
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize(w, h), new Vector(dpi, dpi));
            rtb.Render(this);

            var png = Path.ChangeExtension(trigger, null) + ".png";
            rtb.Save(png);
            Console.WriteLine($"[wall] snapshot {w}x{h} -> {png}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[wall] snapshot failed: {ex.Message}");
        }
    }
#endif

    private void OnWallResized(object? sender, SizeChangedEventArgs e) => Relayout();

    /// Applies the window's current size to the wall.
    ///
    /// Cover size changes on every pixel of a drag, but the ROW CHUNKING only
    /// changes when the column count does — so the tiles are resized through the
    /// shared resource, which costs a layout pass, and the rows are rebuilt only
    /// when they actually need to be.
    private void Relayout()
    {
        var (columns, cover) = ComputeLayout();

        if (cover != CoverPx)
        {
            CoverPx = cover;
            Resources["CoverPx"] = (double)cover;
        }

        if (columns != _columns) Rebuild();
        else if (_open is not null) SchedulePanelRefresh();
    }

    private DispatcherTimer? _panelRefresh;

    /// The open panel caches the cover size for its arrow, so it has to be rebuilt
    /// when the covers resize — but not on every frame of a drag.
    private void SchedulePanelRefresh()
    {
        _panelRefresh?.Stop();
        _panelRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _panelRefresh.Tick += (_, _) =>
        {
            _panelRefresh?.Stop();
            _panelRefresh = null;
            SyncPanel(false);
        };
        _panelRefresh.Start();
    }

    /// Lays the wall out.
    ///
    /// Opening an album must NOT disturb the rows above it: the panel is a single
    /// insertion, and everything else is unchanged. So the album rows are kept as
    /// stable objects and re-chunked only when something genuinely changes their
    /// shape — column count, filter, cover size. Rebuilding them on every click
    /// resets the repeater and throws the wall back to the top, which is exactly
    /// what it did.
    private void Rebuild(bool unfold = false)
    {
        if (_all.Count == 0 && _visible.Count == 0) return;

        var (columns, _) = ComputeLayout();
        var key = $"{columns}|{_visible.Count}|{_filter}";

        if (key != _chunkKey)
        {
            _chunkKey = key;
            _columns = columns;

            _albumRows = [];
            for (var i = 0; i < _visible.Count; i += columns)
                _albumRows.Add(new AlbumRow { Albums = _visible.Skip(i).Take(columns).ToList() });

            _rows.Clear();
            foreach (var r in _albumRows) _rows.Add(r);
            _panelAt = -1;
        }

        SyncPanel(unfold);
    }

    /// Inserts or removes the open album's panel, touching only the row that
    /// changes, and holds the wall still while it happens.
    private void SyncPanel(bool unfold)
    {
        // ItemsRepeater does not know how tall the rows it has not realised are.
        // It ESTIMATES the total extent from the average height of the rows it
        // has, and every row is one cover tall until a panel several times taller
        // appears. That changes the estimate, so the ScrollViewer's absolute pixel
        // offset suddenly points somewhere else in the list.
        //
        // The error is proportional to distance from the top, which is why it
        // showed up past the halfway mark and looked like nothing near the start.
        // Anchoring happens after the rows have been mutated — see SetOpen.
        if (_panelAt >= 0 && _panelAt < _rows.Count && _rows[_panelAt] is PanelRow)
        {
            _rows.RemoveAt(_panelAt);
            _panelAt = -1;
        }

        if (_open is null) return;

        var index = _visible.IndexOf(_open);
        if (index < 0) return;

        _panelAt = index / _columns + 1;
        _rows.Insert(_panelAt, new PanelRow
        {
            Album = _open,
            ArrowColumn = index % _columns,
            CoverPx = CoverPx,
            Spacing = RowSpacing,
            Palette = Palette.For(_open.Album),
            Unfold = unfold,
            PlayFrom = Playback.Player.IsAvailable && !_playerFailed ? StartPlayback : null
        });

        MarkPlayingTrack();
    }

    /// Queues the whole album and starts at the chosen track.
    ///
    /// The ALBUM is the queue even when starting from track 4, so playing a track
    /// from the middle carries on into the rest of the record rather than
    /// stopping at the end of one song.
    private void StartPlayback(AlbumVm album, int from, bool shuffle)
    {
        try
        {
            _player ??= new Playback.Player(
                Enum.TryParse<Playback.GainMode>(_settings.Gain, out var saved)
                    ? saved
                    : Playback.GainMode.Album);

            var paths = album.Album.Tracks.Select(t => t.Path).ToList();

            // Context picks the mode only while the user has not picked one.
            if (_gainChosen)
            {
                if (shuffle) from = Shuffle(paths);
            }
            else if (shuffle)
            {
                // Shuffle uses TRACK gain: album gain preserves the relative
                // loudness a record was mastered with, which is exactly what you
                // do not want once the running order is gone.
                from = Shuffle(paths);
                _player.Gain = Playback.GainMode.Track;
            }
            else
            {
                _player.Gain = Playback.GainMode.Album;
            }

            _player.TrackChanged -= OnTrackChanged;
            _player.TrackChanged += OnTrackChanged;
            _player.StateChanged -= OnPlaybackState;
            _player.StateChanged += OnPlaybackState;

            _playingAlbum = album;
            _playingPaths = paths;
            _player.Play(paths, from);
            MarkPlayingTrack();

            Console.WriteLine($"[wall] requested track {from + 1}: "
                            + $"{(from < album.Album.Tracks.Count ? album.Album.Tracks[from].Title : "?")}");
            Console.WriteLine($"[wall] play {album.Artist} - {album.Title} "
                            + $"from {from + 1}/{paths.Count}{(shuffle ? " shuffled" : "")}");
        }
        catch (Exception ex)
        {
            _playerFailed = true;
            StatusText.Text = ex.Message;
            Console.WriteLine($"[wall] playback unavailable: {ex.Message}");
        }
    }

    /// Fisher-Yates, in place. Returns the index to start from.
    private static int Shuffle(List<string> paths)
    {
        var rng = Random.Shared;
        for (var i = paths.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (paths[i], paths[j]) = (paths[j], paths[i]);
        }
        return 0;
    }

    private AlbumVm? _playingAlbum;
    private List<string> _playingPaths = [];

    private void OnTrackChanged(object? sender, Playback.TrackChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => { UpdateNowPlaying(); MarkPlayingTrack(); });

    private void OnPlaybackState(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(UpdateNowPlaying);

    private bool _draggingPosition;

    private void BuildTransport()
    {
        // Vector glyphs, same reasoning as the window buttons: an icon font we
        // cannot guarantee is an icon we cannot draw.
        // Bar on the side you are heading TOWARDS, triangle pointing that way:
        // previous is bar-left + triangle-left, next is bar-right + triangle-right.
        PrevButton.Content = Glyph("M 1 0 L 1 12 M 9 0 L 1 6 L 9 12 Z");
        NextButton.Content = Glyph("M 9 0 L 9 12 M 1 0 L 9 6 L 1 12 Z");
        SetPlayGlyph(false);

        PrevButton.Click += (_, _) => _player?.Previous();
        NextButton.Click += (_, _) => _player?.Next();
        PlayPauseButton.Click += (_, _) => _player?.TogglePause();

        GainButton.Click += (_, _) =>
        {
            if (_player is null) return;
            _gainChosen = true;
            _player.Gain = _player.Gain switch
            {
                Playback.GainMode.Album => Playback.GainMode.Track,
                Playback.GainMode.Track => Playback.GainMode.Off,
                _ => Playback.GainMode.Album
            };
            UpdateNowPlaying();
        };

        // A drag must not fight the position updates coming from the player, so
        // the ticker stops writing to the slider while the handle is held.
        PositionSlider.AddHandler(PointerPressedEvent, (_, _) => _draggingPosition = true,
                                  RoutingStrategies.Tunnel);
        PositionSlider.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _draggingPosition = false;
            if (_player is null) return;
            var duration = _player.Duration;
            if (duration > TimeSpan.Zero)
                _player.Seek(duration * PositionSlider.Value);
        }, RoutingStrategies.Tunnel);
    }

    private void Recolour(IBrush on)
    {
        foreach (var b in new Button[] { PrevButton, PlayPauseButton, NextButton })
            if (b.Content is Avalonia.Controls.Shapes.Path path)
            {
                path.Fill = on;
                path.Stroke = on;
            }
    }

    private static Avalonia.Controls.Shapes.Path Glyph(string data) => new()
    {
        Data = Geometry.Parse(data),
        Fill = new SolidColorBrush(Color.Parse("#F3EEE6")),
        Stroke = new SolidColorBrush(Color.Parse("#F3EEE6")),
        StrokeThickness = 1.6,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private bool _showingPause;

    private void SetPlayGlyph(bool playing)
    {
        if (_showingPause == playing && PlayPauseButton.Content is not null) return;
        _showingPause = playing;
        PlayPauseButton.Content = playing
            ? Glyph("M 2 0 L 2 14 M 9 0 L 9 14")           // pause
            : Glyph("M 2 0 L 12 7 L 2 14 Z");              // play

        if (_playingAlbum is not null)
            Recolour(Palette.For(_playingAlbum.Album).OnLight);
    }

    /// Flags the playing track in the open panel, if the open panel happens to be
    /// the album that is playing.
    ///
    /// Matched by PATH rather than by index: shuffle reorders the queue, so the
    /// player's position is an index into the SHUFFLED list and means nothing to
    /// a track list shown in album order.
    private void MarkPlayingTrack()
    {
        if (_panelAt < 0 || _panelAt >= _rows.Count || _rows[_panelAt] is not PanelRow panel)
            return;

        string? playing = null;
        if (_player is not null && _playingAlbum is not null)
        {
            var i = _player.Index;
            if (i >= 0 && i < _playingPaths.Count) playing = _playingPaths[i];
        }

        foreach (var line in panel.AllLines)
            line.IsPlaying = playing is not null && line.Source?.Path == playing;
    }

    private void UpdateNowPlaying()
    {
        if (_player is null || _playingAlbum is null) { Transport.IsVisible = false; return; }

        var i = _player.Index;
        if (i < 0 || i >= _playingPaths.Count) { Transport.IsVisible = false; return; }

        var path = _playingPaths[i];
        var track = _playingAlbum.Album.Tracks.FirstOrDefault(t => t.Path == path);

        Transport.IsVisible = true;
        TransportTitle.Text = track?.Title ?? Path.GetFileNameWithoutExtension(path);
        TransportArtist.Text = $"{_playingAlbum.Artist}  \u00b7  {_playingAlbum.Title}";
        TransportArt.Source = _playingAlbum.Cover;

        SetPlayGlyph(_player.IsPlaying);

        GainButton.Content = _player.Gain switch
        {
            Playback.GainMode.Album => "RG ALBUM",
            Playback.GainMode.Track => "RG TRACK",
            _ => "RG OFF"
        };

        // The transport takes a LIGHT tone from the playing album's art. It both
        // ties the bar to what is playing and breaks the window out of being one
        // temperature from top to bottom — the wall is deliberately near-neutral,
        // so the colour has to live somewhere.
        var palette = Palette.For(_playingAlbum.Album);
        Transport.Background = palette.Light;
        Transport.BorderBrush = palette.OnLightHover;

        TransportTitle.Foreground = palette.OnLight;
        TransportArtist.Foreground = palette.OnLightDim;
        ElapsedText.Foreground = RemainingText.Foreground = palette.OnLightDim;

        foreach (var b in new[] { PrevButton, PlayPauseButton, NextButton })
            b.Foreground = palette.OnLight;

        GainButton.Foreground = palette.OnLight;
        GainButton.Background = new SolidColorBrush(Color.Parse("#18000000"));
        GainButton.BorderBrush = new SolidColorBrush(Color.Parse("#33000000"));

        // The glyphs are Paths whose stroke was fixed at build time; they have to
        // be recoloured for the light bar.
        Recolour(palette.OnLight);

        UpdatePosition();
    }

    /// Elapsed, remaining, and the slider. Remaining counts DOWN and is signed,
    /// because "how much is left" is the question being asked.
    private void UpdatePosition()
    {
        if (_player is null) return;

        var pos = _player.Position;
        var dur = _player.Duration;

        ElapsedText.Text = Clock(pos);
        RemainingText.Text = dur > TimeSpan.Zero ? "-" + Clock(dur - pos) : "";

        if (!_draggingPosition)
            PositionSlider.Value = dur > TimeSpan.Zero
                ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1)
                : 0;
    }

    private static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private async void SetOpen(AlbumVm? album)
    {
        var closing = ReferenceEquals(_open, album) || album is null;

        // Fold the old panel away before the rows move, so closing is the reverse
        // of opening rather than a row vanishing from under the cursor.
        if (_open is not null) await FoldAwayAsync();

        if (_open is not null) _open.IsSelected = false;
        // Clicking the open album again closes it, which is the only way back to
        // an unbroken wall.
        _open = closing ? null : album;
        if (_open is not null) _open.IsSelected = true;

        Rebuild(unfold: _open is not null);

        // Anchor on whichever album the action was about: the one just opened, or
        // the one just closed, so closing leaves you looking at where you were
        // rather than wherever the removed panel's height dropped you.
        if (_open is not null) AnchorOn(_open);
        else if (album is not null) AnchorOn(album);
    }

    /// Collapses the realised panel element to nothing, then returns so the
    /// caller can rebuild. If the panel is scrolled out of view there is no
    /// element to animate and nothing to wait for.
    private async Task FoldAwayAsync()
    {
        var index = -1;
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i] is PanelRow) { index = i; break; }
        if (index < 0) return;

        var el = Wall.TryGetElement(index);
        Console.WriteLine($"[wall] fold away: rowIndex={index} element={(el is null ? "NOT REALISED" : "ok")}");
        if (el is null) return;

        el.Transitions = Unfolding();
        el.Height = 0;
        el.Opacity = 0;
        await Task.Delay(UnfoldMs);
    }

    // ---- Motion. Gathered here because these are feel, not logic, and get
    // ---- adjusted by watching rather than by reasoning.

    /// How long a panel takes to unfold or roll away.
    private const int UnfoldMs = 330;

    /// How long the anchoring scroll is allowed to keep converging.
    private const int AnchorMs = 750;

    /// Fraction of the remaining distance the anchor covers each 16 ms tick.
    /// Lower is slower and smoother; this is the shape of the ease, and it is
    /// self-correcting because the target is recomputed every tick.
    private const double AnchorEase = 0.17;

    private DispatcherTimer? _anchor;

    /// Keeps the album you acted on in a predictable place, instead of trying to
    /// keep a pixel offset.
    ///
    /// Pinning the offset was not enough, and could not be. Opening an album in
    /// row 3 while row 1's panel is still open REMOVES several hundred pixels of
    /// content from above the viewport, so the offset that was correct a moment
    /// ago now points somewhere else entirely. The fixed point is not a number of
    /// pixels, it is a row.
    ///
    /// The row is placed one row below the top of the viewport, so the album you
    /// opened has its neighbours visible above it and its panel below, rather
    /// than being jammed against the top edge.
    ///
    /// The target is recomputed on every tick rather than solved once, because
    /// ItemsRepeater is still revising its extent estimate and the unfold is still
    /// growing the panel while this runs. Easing toward a moving target converges;
    /// jumping to a stale one does not.
    /// The wall does not take clicks while it is moving.
    ///
    /// Opening an album unfolds a panel and scrolls the wall, which together take
    /// the better part of a second. A click during that lands on whatever row has
    /// slid under the cursor, not the one that was aimed at — which is how
    /// clicking disc 2 track 6 started disc 1 track 1, and why clicking the same
    /// place again worked. The app was not choosing the wrong track; it was told
    /// the wrong track, by a row that had moved.
    private void FreezeWall(bool frozen)
    {
        if (Wall.IsHitTestVisible == !frozen) return;
        Wall.IsHitTestVisible = !frozen;

        // A dead-safe release. A wall that stays unclickable because an animation
        // did not finish cleanly is far worse than the mis-click this prevents,
        // and this path does not depend on any of the timers above completing.
        if (frozen)
            DispatcherTimer.RunOnce(() => Wall.IsHitTestVisible = true,
                                    TimeSpan.FromMilliseconds(AnchorMs + UnfoldMs + 200));
    }

    /// Brings the opened album to a consistent place near the top of the view.
    ///
    /// This ALWAYS moves the wall, which was tried the other way and rejected: a
    /// minimum-movement version left the album wherever it happened to be, and the
    /// unfold then felt like something appearing rather than the wall turning to
    /// show you the record you picked. His words — "I liked the scrolling the old
    /// way where the window moved to show the selected album when it unfolded".
    ///
    /// The mis-clicks this used to cause are handled by freezing the wall while it
    /// is in motion, rather than by refusing to move it.
    private double TargetOffsetFor(Control element)
    {
        // One album row of context above the opened one, so it does not sit jammed
        // against the top edge.
        var lead = CoverPx + LabelHeight + RowSpacing;
        var max = Math.Max(0, WallScroller.Extent.Height - WallScroller.Viewport.Height);
        return Math.Clamp(element.Bounds.Y - lead, 0, max);
    }

    private void AnchorOn(AlbumVm album)
    {
        var index = _visible.IndexOf(album);
        if (index < 0) return;
        var rowIndex = index / _columns;

        _anchor?.Stop();
        FreezeWall(true);
        var until = DateTime.UtcNow.AddMilliseconds(AnchorMs);

        _anchor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _anchor.Tick += (_, _) =>
        {
            var element = Wall.TryGetElement(rowIndex);
            if (element is null || DateTime.UtcNow > until)
            {
                Settle();
                return;
            }

            var target = TargetOffsetFor(element);

            var y = WallScroller.Offset.Y;
            if (Math.Abs(target - y) < 0.5)
            {
                // Settled early, but the unfold may still be growing the panel,
                // so hold the freeze until that is done too.
                Settle();
                return;
            }

            // Exponential approach: smooth, and self-correcting when the target
            // moves under it.
            WallScroller.Offset = WallScroller.Offset.WithY(y + (target - y) * AnchorEase);
        };
        _anchor.Start();
        return;

        void Settle()
        {
            _anchor?.Stop();
            _anchor = null;

            // The unfold and the scroll are separate animations and either can
            // finish first. Release only once the slower of the two can no longer
            // be moving anything.
            DispatcherTimer.RunOnce(() => FreezeWall(false),
                                    TimeSpan.FromMilliseconds(UnfoldMs + 60));
        }
    }

    private static Transitions Unfolding() =>
    [
        new DoubleTransition
        {
            Property = HeightProperty,
            Duration = TimeSpan.FromMilliseconds(UnfoldMs),
            Easing = new CubicEaseOut()
        },
        new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(UnfoldMs),
            Easing = new CubicEaseOut()
        }
    ];

    /// Animates a freshly opened panel from nothing to its natural height,
    /// pushing the rows below it down as it grows.
    ///
    /// The target height has to be MEASURED, not assumed: a panel is as tall as
    /// its track list, and animating to a guessed value would either clip a box
    /// set or leave a gap under a single.
    private int _unfoldGen;

    private void Unfold(Control el, PanelRow row)
    {
        row.Unfolded = true;

        // Every unfold gets a token. The timer below runs after the animation and
        // clears the explicit height; by then the element may have been recycled
        // into an entirely different row, and clearing its height mid-animation
        // would break that one instead.
        var gen = ++_unfoldGen;

        el.Transitions = null;      // no transition while we set the start state
        el.Height = 0;
        el.Opacity = 0;

        // Measure after the element has been through a layout pass, otherwise its
        // desired size is still zero and there is nothing to animate towards.
        Dispatcher.UIThread.Post(() =>
        {
            var width = Math.Max(0, WallScroller.Bounds.Width
                                  - WallScroller.Padding.Left - WallScroller.Padding.Right);
            el.Height = double.NaN;
            el.Measure(new Size(width, double.PositiveInfinity));
            var target = el.DesiredSize.Height;
            if (target <= 0) { el.Opacity = 1; return; }

            el.Height = 0;
            el.Transitions = Unfolding();
            el.Height = target;
            el.Opacity = 1;

            // Release the explicit height once the unfold is done, so the panel can
            // still grow when its large cover finishes decoding.
            DispatcherTimer.RunOnce(() =>
            {
                if (gen != _unfoldGen) return;
                el.Transitions = null;
                el.Height = double.NaN;
            }, TimeSpan.FromMilliseconds(UnfoldMs + 40));
        }, DispatcherPriority.Loaded);
    }

    /// Fired as the layout realises a container. This — not item creation — is
    /// the moment we know a cover is about to be visible.
    private void OnElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        var px = CoverPx;
        switch (e.Element.DataContext)
        {
            case AlbumRow row:
                foreach (var vm in row.Albums) vm.EnsureCover(px);
                break;
            case PanelRow panel:
                // The panel shows a 260 px cover, which needs the larger bucket
                // even when the wall's tiles are tiny.
                panel.Album.EnsureCover(260);
                if (panel.Unfold && !panel.Unfolded) Unfold(e.Element, panel);
                break;
        }
    }
}
