// AlbumWall — per-album view model.
//
// Deliberately hand-rolled INotifyPropertyChanged rather than pulling in an
// MVVM framework: this needs exactly one notifying property, and the spec's
// rule is not to add an abstraction before a second implementation asks for it.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AlbumWall.App;

public sealed class AlbumVm : INotifyPropertyChanged
{
    public Domain.Album Album { get; }

    /// Set by the wall. Opening an album is the wall's business — it has to move
    /// rows around — so the tile raises it rather than handling it.
    public Action<AlbumVm>? OnOpen { get; set; }

    public System.Windows.Input.ICommand OpenCommand { get; }

    public AlbumVm(Domain.Album album)
    {
        Album = album;
        OpenCommand = new Relay(() => OnOpen?.Invoke(this));
        // A deterministic colour per album, so a cover that is missing or slow
        // still reads as a distinct object on the wall rather than a grey hole.
        Placeholder = PlaceholderBrushFor(album);
    }

    public string Title => Album.Title;
    public string Artist => Album.AlbumArtist;
    public string Year => Album.Year > 0 ? Album.Year.ToString() : "";
    public Avalonia.Media.IBrush Placeholder { get; }

    private Bitmap? _cover;
    public Bitmap? Cover
    {
        get => _cover;
        private set { _cover = value; OnPropertyChanged(); OnPropertyChanged(nameof(CoverOpacity)); }
    }

    /// The ring takes the album's OWN accent colour, so the selected tile is
    /// visibly tied to the panel that opened below it.
    public Avalonia.Media.IBrush RingBrush => Palette.For(Album).Accent;

    private bool _selected;
    /// Drives the selection ring. The open album has to stay identifiable while
    /// its panel is open, or a wall of covers gives no clue what you opened.
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; OnPropertyChanged(); }
    }

    /// Drives the fade-in. Art loads asynchronously, so a cover would otherwise
    /// appear abruptly the moment it finished decoding.
    public double CoverOpacity => _cover is null ? 0 : 1;

    private int _loadedBucket = -1;

    /// Called when the tile is realised by the virtualising layout, so art is
    /// only fetched for albums actually on screen.
    public async void EnsureCover(int displayPx)
    {
        var bucket = ArtCache.BucketFor(displayPx);

        // UPGRADE ONLY, never downgrade. The panel wants a 260 px cover and the
        // tiles want 185 px, which are different buckets — so opening an album and
        // closing it again used to decode that cover twice and then keep flipping
        // between the two sizes, holding both. Once the larger bitmap exists it is
        // what gets shown at either size; the GPU scaling it down costs nothing.
        if (_loadedBucket >= bucket) return;
        _loadedBucket = bucket;

        var bmp = await ArtCache.GetAsync(Album, displayPx).ConfigureAwait(false);
        if (bmp is null) return;
        await Dispatcher.UIThread.InvokeAsync(() => Cover = bmp);
    }

    private static Avalonia.Media.IBrush PlaceholderBrushFor(Domain.Album a)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in a.AlbumArtist + a.Title) { h ^= c; h *= 16777619; }
            var hue = h % 360;
            var sat = 0.26 + (h >> 9) % 22 / 100.0;
            var lig = 0.30 + (h >> 17) % 16 / 100.0;
            // IMMUTABLE brush, not SolidColorBrush: view models are built on
            // the scan thread, and a mutable AvaloniaObject brush carries
            // thread affinity — the renderer throws the moment it touches one
            // created off the UI thread. An immutable brush has no dispatcher.
            return new Avalonia.Media.Immutable.ImmutableSolidColorBrush(FromHsl(hue, sat, lig));
        }
    }

    private static Avalonia.Media.Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Avalonia.Media.Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// A one-line command. An MVVM framework would supply this; the spec's rule
    /// is not to take a dependency before a second use asks for it.
    private sealed class Relay(Action run) : System.Windows.Input.ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
