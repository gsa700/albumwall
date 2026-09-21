// AlbumWall — per-album view model.
//
// Deliberately hand-rolled INotifyPropertyChanged rather than pulling in an
// MVVM framework: this needs exactly one notifying property, and the spec's
// rule is not to add an abstraction before a second implementation asks for it.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
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
        // A deterministic color per album, so a cover that is missing or slow
        // still reads as a distinct object on the wall rather than a gray hole.
        Placeholder = PlaceholderBrushFor(album);
    }

    public string Title => Album.Title;

    /// What a screen reader says for the cover: the record and who made it. The
    /// tile is a button whose content is a picture and two labels, and a button
    /// with no name of its own is announced as the type of its content.
    public string Spoken => Album.Year > 0 ? $"{Album.Title}, by {Album.AlbumArtist}, {Album.Year}"
                                           : $"{Album.Title}, by {Album.AlbumArtist}";
    public string Artist => Album.AlbumArtist;
    public string Year => Album.Year > 0 ? Album.Year.ToString() : "";
    public Avalonia.Media.IBrush Placeholder { get; }

    private Bitmap? _cover;
    public Bitmap? Cover
    {
        get => _cover;
        private set
        {
            _cover = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CoverOpacity));
            OnPropertyChanged(nameof(GroundOpacity));
            OnPropertyChanged(nameof(Face));
            OnPropertyChanged(nameof(FaceMax));
            OnPropertyChanged(nameof(FaceStretch));
        }
    }

    // ---- the flip ---------------------------------------------------------
    //
    // Selecting an album turns its sleeve over IN THE WALL, where the click
    // was, rather than in the panel. His idea, and his framing of it: "this is
    // an experience not really informational" -- which is why nothing here
    // tries to make a back readable, and why a tile with no back.jpg simply
    // does not flip rather than apologizing for it.
    //
    // HOW IT TURNS. Avalonia's transform-string parser is 2D only: rotate3d,
    // rotateY and perspective are all FormatException, measured, so the
    // transition-on-a-string trick the transport bar uses cannot do this.
    // Rotate3DTransform can, and it is Animatable, so the angle is animated on
    // the transform object itself.
    //
    // It is a HALF flip twice, not one 180 degree turn: the face rotates to 90
    // (edge on, invisible), the bitmap is swapped there, and it rotates back
    // from -90 to 0. One Image, no second face to keep in step, and no mirrored
    // back -- the horizontal scale is cos(angle), which is positive either side
    // of the swap. A full 180 would need the back pre-rotated to avoid coming
    // out as its own reflection.
    //
    // PURE RENDER TRANSFORM, no layout. The transport bar had to be rewritten
    // for exactly that reason (8111fdc): animating layout made every open album
    // re-centre on every frame. Nothing here changes a size, so the wall never
    // hears about it.
    private const int FlipMs = 260;

    private Bitmap? _back;
    private bool _showingBack;
    private Rotate3DTransform? _turn;
    private int _flipGeneration;

    /// Which way round the sleeve currently is. Read by the wall so that a
    /// rescan, which replaces every view model, can put the open album back the
    /// way it was.
    public bool ShowingBack => _showingBack;

    /// What the tile actually draws: the front, or the back once it has turned.
    public Bitmap? Face => _showingBack ? _back : _cover;

    /// The transform the tile's sleeve is bound to.
    ///
    /// Created LAZILY, on the UI thread. View models are built on the scan
    /// thread and a Transform is an AvaloniaObject carrying dispatcher
    /// affinity -- the same trap the immutable-brush rule in the scanner exists
    /// for. Null until the first flip, which is also why an album that is never
    /// opened costs nothing.
    public Transform? Turn => _turn;

    /// Turn the sleeve over, or back. Safe to call for an album with no back:
    /// it does nothing at all, so the caller does not have to know.
    public async void Flip(bool toBack, bool animate = true)
    {
        if (toBack == _showingBack) return;
        if (toBack && !Album.HasBack) return;

        var mine = ++_flipGeneration;

        if (!animate)
        {
            if (toBack && _back is null)
            {
                _back = await ArtCache.GetBackAsync(Album, BackBucket).ConfigureAwait(true);
                if (_back is null || mine != _flipGeneration) return;
            }
            ShowFace(toBack);
            return;
        }

        _turn ??= new Rotate3DTransform { Depth = 220 };
        OnPropertyChanged(nameof(Turn));

        // THE DECODE GOES BEHIND THE TURN, not in front of it.
        //
        // Awaiting it first made the sleeve sit still for 21 to 191 ms before
        // it moved -- measured over five albums -- while the panel below had
        // already started rolling out. Worse, the second open of the same album
        // was instant, so the same gesture looked different depending on
        // whether a bitmap happened to be cached. The first half of the turn is
        // 130 ms of cover for exactly this, and edge-on is the one moment in
        // the whole animation where nothing is visible anyway.
        var decoding = toBack && _back is null
            ? ArtCache.GetBackAsync(Album, BackBucket)
            : Task.FromResult(_back);

#if DEBUG
        var turnStarted = Environment.TickCount64;
#endif
        await Tween(_turn, 0, 90, FlipMs / 2);
        if (mine != _flipGeneration) return;
#if DEBUG
        var edgeOn = Environment.TickCount64;
#endif

        if (toBack)
        {
            // Edge-on until the bitmap lands. Slower than the turn only on a
            // cold cache, and a held pause reads as the card being turned by a
            // hand rather than as a dropped frame.
            _back = await decoding.ConfigureAwait(true);
            if (mine != _flipGeneration) return;
            if (_back is null)
            {
                // Nothing to show after all: turn the front back rather than
                // leaving the tile edge-on forever.
                await Tween(_turn, -90, 0, FlipMs / 2);
                return;
            }
        }

        ShowFace(toBack);
#if DEBUG
        Console.WriteLine($"[flip] {Album.Title}: first half {edgeOn - turnStarted} ms, "
                        + $"edge-on hold {Environment.TickCount64 - edgeOn} ms");
#endif
        await Tween(_turn, -90, 0, FlipMs / 2);
    }

    private void ShowFace(bool back)
    {
        _showingBack = back;
        OnPropertyChanged(nameof(Face));
        OnPropertyChanged(nameof(FaceMax));
        OnPropertyChanged(nameof(FaceStretch));
    }

    /// Backs are decoded at the larger bucket: the files are 500 px and the
    /// wall may be showing covers bigger than the tile default.
    private const int BackBucket = 512;

    /// Tweens the transform's AngleY, by hand.
    ///
    /// Avalonia's Animation cannot do this: RunAsync takes an Animatable but
    /// casts it to Visual internally, so animating a Transform OBJECT throws
    /// InvalidCastException ("Unable to cast Rotate3DTransform to Visual"),
    /// which is how this was found. And the declarative route is out too, since
    /// the transform-string parser has no rotate3d. So a timer it is: eight
    /// frames of sine easing, on the UI thread, setting one double.
    private static Task Tween(Rotate3DTransform t, double from, double to, int ms)
    {
        var done = new TaskCompletionSource();
        var startedAt = Environment.TickCount64;
        t.AngleY = from;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var p = Math.Clamp((Environment.TickCount64 - startedAt) / (double)ms, 0, 1);
            // Sine in-out: the sleeve leaves and arrives slowly and is quickest
            // edge-on, which is where a real card moves fastest too.
            t.AngleY = from + (to - from) * (0.5 - 0.5 * Math.Cos(Math.PI * p));
            if (p < 1) return;
            timer.Stop();
            done.TrySetResult();
        };
        timer.Start();
        return done.Task;
    }

    /// The size caps and stretch rule, for whichever face is showing. A back is
    /// a scan of the whole tray card and runs to 1.27 wide, so it is
    /// letterboxed like any other oblong art rather than cropped -- a crop
    /// takes the track listing off the edge.
    public double FaceMax => _showingBack
        ? (Album.BackWidth > 0 && Album.BackHeight > 0
            ? Math.Max(Album.BackWidth, Album.BackHeight) * MaxEnlarge / Math.Max(1, Scaling)
            : double.PositiveInfinity)
        : CoverMax;

    public Avalonia.Media.Stretch FaceStretch => _showingBack
        ? (Album.BackWidth > 0 && Album.BackHeight > 0
           && Math.Abs(Album.BackWidth - Album.BackHeight)
              > 0.06 * Math.Max(Album.BackWidth, Album.BackHeight)
            ? Avalonia.Media.Stretch.Uniform
            : Avalonia.Media.Stretch.UniformToFill)
        : CoverStretch;

    /// The ring takes the album's OWN accent color, so the selected tile is
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

    /// How far a cover may be enlarged before it is shown smaller instead.
    ///
    /// Do the best with what is available — his phrase, and the whole policy. A
    /// 150 px thumbnail stretched across a 278 px tile is a blur; the same image
    /// at up to one and a half times its size, centered on the album's own
    /// ground, is a small print on a mat, and reads as deliberate. 1.5 is where
    /// enlargement stops being invisible; most covers never get near it.
    private const double MaxEnlarge = 1.5;

    /// Physical pixels per logical one, set by the window once it knows. The cap
    /// below is in pixels of the IMAGE, so it has to be converted: on a 2x
    /// display a 300 px cover fills a 185-unit tile at 1.23x and is fine.
    public static double Scaling { get; set; } = 1;

    /// The most room the cover may take, in layout units, on its longer side.
    /// Infinite when the size is unknown: unknown must never be read as small.
    public double CoverMax =>
        Album.ArtWidth > 0 && Album.ArtHeight > 0
            ? Math.Max(Album.ArtWidth, Album.ArtHeight) * MaxEnlarge / Math.Max(1, Scaling)
            : double.PositiveInfinity;

    /// Whole, or filled. A cover that is properly oblong is shown whole, with
    /// the ground either side, because cropping it cuts the lettering off the
    /// edge. One that is square to within a few percent — 600x595 is everywhere —
    /// is filled and loses a hair, because shown whole it grows a one-pixel
    /// stripe of ground down each side that reads as a rendering fault.
    public Avalonia.Media.Stretch CoverStretch =>
        Album.ArtWidth > 0 && Album.ArtHeight > 0
        && Math.Abs(Album.ArtWidth - Album.ArtHeight) > 0.06 * Math.Max(Album.ArtWidth, Album.ArtHeight)
            ? Avalonia.Media.Stretch.Uniform
            : Avalonia.Media.Stretch.UniformToFill;

    /// The colored ground is a stand-in for a cover, so it leaves when the cover
    /// arrives.
    ///
    /// It used to stay underneath, which nobody could see while every cover
    /// filled its tile. Once small and oblong covers were shown at their own
    /// size it became a colored border round them, in a hue hashed from the
    /// album's name and so unrelated to the artwork. His suggestion on seeing
    /// it: "how about transparency instead of the colored borders". A small
    /// cover now sits directly on the wall, and the only tiles that keep a
    /// ground are the ones with nothing else to show.
    public double GroundOpacity => _cover is null ? 1 : 0;

    private bool _artFailed;

    /// No picture to show: none in the files, or one that would not decode. The
    /// tile then carries the title and artist itself, so it reads as a plain
    /// sleeve rather than a colored hole. Known from the scan, not from the
    /// cover having failed to arrive yet — that would flash text under every
    /// cover on the wall while it loaded.
    public bool HasNoArt => !Album.HasArt || _artFailed;

    /// Drives the fade-in. Art loads asynchronously, so a cover would otherwise
    /// appear abruptly the moment it finished decoding.
    public double CoverOpacity => _cover is null ? 0 : 1;

    private int _loadedBucket = -1;

    /// Called when the tile is realized by the virtualizing layout, so art is
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
        if (bmp is null)
        {
            if (Album.HasArt)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _artFailed = true;
                    OnPropertyChanged(nameof(HasNoArt));
                });
            return;
        }
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

    /// Turns CoverMax into the cap for one particular slot: the cover's own
    /// limit, unless that limit nearly fills the slot anyway, in which case no
    /// cap at all.
    ///
    /// Without this a 180 px cover in a 185-unit tile sat inside a 2 px frame of
    /// its ground color — the same stripe-as-fault problem as CoverStretch, met
    /// from the other direction. A mat has to be wide enough to look meant. The
    /// view model cannot decide this alone because it does not know the slot:
    /// the same cover is shown in a tile and in the panel's sleeve.
    public sealed class FitConverter : Avalonia.Data.Converters.IMultiValueConverter
    {
        public static readonly FitConverter Instance = new();

        /// Below this share of the slot the cover is matted; above, it fills.
        private const double Fills = 0.86;

        public object Convert(IList<object?> values, Type targetType, object? parameter,
                              System.Globalization.CultureInfo culture) =>
            values is [double max, double slot, ..] && slot > 0 && max < slot * Fills
                ? max
                : double.PositiveInfinity;
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
