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
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Threading;

namespace AlbumWall.App;

public partial class MainWindow : Window
{
    // Label block under each cover: title + artist + StackPanel spacing.
    // Measured, not guessed — at 11/10 px this is what the two lines occupy.
    private const int LabelHeight = 40;

    private readonly ObservableCollection<AlbumVm> _shown = [];
    private List<AlbumVm> _all = [];
    private string _filter = "";
    private long _scanMs;
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

        Wall.ItemsSource = _shown;
        Wall.ElementPrepared += OnElementPrepared;
        SizeSlider.PropertyChanged += OnSliderChanged;
        SearchBox.PropertyChanged += OnSearchChanged;

        ApplyCoverSize(CoverPx);
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
        ApplyGround();

        Loaded += OnLoaded;

        // Cheap running read of how much art the wall has actually pulled in.
        DispatcherTimer.Run(() =>
        {
            if (_all.Count == 0 || _filter.Length > 0) return true;

            var n = ArtCache.Decoded;
            StatusText.Text = $"{_scanMs} ms scan \u00b7 {n} of {_all.Count} covers decoded";
#if DEBUG
            // Echoed to stdout as well: the decode count is the measurement
            // that tells us virtualisation is real, and it is easier to trust
            // from a log than from a glance at the status bar.
            if (n != _lastReported)
            {
                _lastReported = n;
                Console.WriteLine($"[wall] realised covers decoded: {n}/{_all.Count}");
            }
#endif
            return true;
        }, TimeSpan.FromMilliseconds(500));
    }

    private int CoverPx => (int)Math.Round(SizeSlider.Value);

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Borderless windows are a per-platform negotiation, not a setting that
        // simply takes. Report what the window manager actually granted.
        Console.WriteLine($"[wall] transparency={ActualTransparencyLevel} "
                        + $"corner={Surface.CornerRadius.TopLeft}");
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

    private void OnSliderChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty) ApplyCoverSize(CoverPx);
    }

    private void OnSearchChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.TextProperty) return;
        _filter = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        ApplyFilter();
    }

    /// One resource drives every tile's geometry, so a slider drag re-lays out
    /// the wall without rebuilding items or touching per-tile bindings.
    private void ApplyCoverSize(int px)
    {
        Resources["CoverPx"] = (double)px;

        if (Wall.Layout is UniformGridLayout grid)
        {
            grid.MinItemWidth = px;
            grid.MinItemHeight = px + LabelHeight;
        }

        SizeText.Text = $"{px} px";
    }

    private void ApplyFilter()
    {
        var wanted = _filter.Length == 0
            ? _all
            : _all.Where(v => v.Album.SearchText.Contains(_filter)).ToList();

        // Rebuild in place rather than reassigning ItemsSource: reassigning
        // drops the scroll position and re-realises everything.
        _shown.Clear();
        foreach (var v in wanted) _shown.Add(v);

        if (_filter.Length > 0)
            StatusText.Text = $"{wanted.Count} of {_all.Count} albums";
    }

    /// Fired as the layout realises a container. This — not item creation — is
    /// the moment we know a cover is about to be visible.
    private void OnElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element.DataContext is AlbumVm vm) vm.EnsureCover(CoverPx);
    }
}
