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
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    public MainWindow()
    {
        InitializeComponent();

        Wall.ItemsSource = _shown;
        Wall.ElementPrepared += OnElementPrepared;
        SizeSlider.PropertyChanged += OnSliderChanged;
        SearchBox.PropertyChanged += OnSearchChanged;

        ApplyCoverSize(CoverPx);
        StatusText.Text = "scanning…";

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
                CountsText.Text = $"{albums.Count} albums · {tracks} tracks · {artists} artists";
                _scanMs = sw.ElapsedMilliseconds;
                ApplyFilter();
            });
        });
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
