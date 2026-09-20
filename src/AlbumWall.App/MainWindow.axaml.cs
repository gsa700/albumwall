// AlbumWall — the wall.
//
// Three jobs here, and nothing else: scan the library off the UI thread, keep
// the tile geometry in step with the size slider, and load cover art only for
// tiles the virtualizing layout has actually realized.
//
// The art rule is the whole reason this window is worth building before the
// player: 194 albums today, ~1100 when the lossy collection is folded in. If
// art loaded per item rather than per realized tile, opening the window would
// decode the entire library.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Animation;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
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

    /// The app's own volume, 0-100, remembered between runs.
    private int _volume = 100;

    /// The tuning bench's three levers, his to set and remembered between runs.
    private int _lightness = Ground.DefaultLightness;
    private int _chrome = Ground.DefaultChrome;
    private Ground.Ramp _ramp = new(Ground.FallbackHue, 0,
                                    Ground.DefaultLightness,
                                    Ground.Chromes[Ground.DefaultChrome]);
    private string _counts = "scanning\u2026";

    public MainWindow()
    {
        InitializeComponent();

        // The markup says "AlbumWall" so the previewer has something to show;
        // what he actually sees comes from one place. See App.DisplayName.
        Title = App.DisplayName;
        AppName.Text = App.DisplayName;

        // Size before the window is shown; position after, once the screens are
        // known. Both come back from last time — see RestorePosition.
        if (_settings.WindowWidth is > 320) Width = _settings.WindowWidth.Value;
        if (_settings.WindowHeight is > 240) Height = _settings.WindowHeight.Value;

        Wall.RowHeight = CoverPx + LabelHeight;
        Wall.ItemsSource = _rows;
        Wall.ElementPrepared += OnElementPrepared;
        WallScroller.SizeChanged += OnWallResized;
        SearchBox.PropertyChanged += OnSearchChanged;



        // There are no system decorations, so there is no system resize border
        // either and we own that too. Tunnelling, because the wall and the
        // controls sit over the edges and would otherwise swallow the press.
        AddHandler(PointerMovedEvent, OnPointerMovedForResize, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressedForResize, RoutingStrategies.Tunnel);

        // The top bar IS the title bar now, so it owes the window the three
        // behaviors the system one provided: drag to move, double-click to
        // maximize, and the buttons the desktop asked for.
        TopBar.PointerPressed += OnTitleBarPressed;
        TopBar.DoubleTapped += (_, _) => ToggleMaximized();
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
        SetUpBench();
        ApplyGround();

        Opened += (_, _) => { _restored = true; RestorePosition(); };
        Closing += (_, _) => { SaveSession(); SaveSettings(); _watcher?.Dispose(); };

        // The position is only worth as much as its last write, and a crash, a
        // logout or a pulled plug does not run the Closing handler. Five seconds
        // is the most he can lose; the file is a few kilobytes.
        DispatcherTimer.Run(() =>
        {
            if (_player is { IsPlaying: true }) SaveSession();
            return true;
        }, TimeSpan.FromSeconds(5));

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
            CheckCommandRequest();
            CheckSnapshotRequest();
#endif

            if (_all.Count == 0 || _filter.Length > 0 || _scanning) return true;

            var n = ArtCache.Decoded;
            StatusText.Text = $"{_scanMs} ms scan \u00b7 {n} covers held \u00b7 "
                            + $"{ArtCache.HeldBytes / (1024 * 1024)} MB art";
#if DEBUG
            // Echoed to stdout as well: the decode count is the measurement
            // that tells us virtualization is real, and it is easier to trust
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
        // that a saved position is honored exactly, but with nothing to honor
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

        // Only record geometry from a normal window. Saving a maximized or
        // minimized window's bounds means restoring to something that was never
        // deliberately chosen.
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }

        _settings.Gain = (_player?.Gain ?? Playback.GainMode.Album).ToString();
        _settings.Volume = _volume;
        _settings.Lightness = _lightness;
        _settings.Tint = (int)Math.Round(Ground.Saturation * 100);
        _settings.Chrome = Ground.Chromes[_chrome].Name;
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

        // Before the first tile is built: how far a cover may be enlarged is
        // counted in its own pixels, so the tiles need to know what a pixel is.
        AlbumVm.Scaling = RenderScaling;

        SetUpMenu();
        ScanLibrary();

        // The shell wants a media player on the bus whether or not anything is
        // playing yet, so this starts with the app rather than with playback.
        //
        // THE RESULT MUST BE HELD. Nothing else refers to the connection, so
        // discarding it lets the GC take the whole thing — the app claims the
        // name, logs that it did, and then quietly vanishes off the bus.
        _ = Mpris.StartAsync(MprisState, MprisCommand)
                 .ContinueWith(t => _mpris = t.Result, TaskScheduler.Default);

#if WINDOWS
        // The same thing for Windows, which has no D-Bus. It needs this window's
        // handle, which exists by now, and hands its keys to the same
        // MprisCommand the Linux side uses.
        _smtc = Smtc.Start(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, MprisCommand);
#endif
    }

    /// Where the music lives: his setting, or the platform's Music folder.
    ///
    /// The default is right on a machine that keeps its music where the OS
    /// suggests, and wrong on every machine where the records are on a NAS —
    /// which is why it is settable and why an empty result explains itself.
    private string LibraryRoot =>
        string.IsNullOrWhiteSpace(_settings.LibraryPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music")
            : _settings.LibraryPath;

    private Domain.LibraryWatcher? _watcher;
    private CancellationTokenSource? _scan;

    /// What the scanner read last time, so that an unchanged file is not opened
    /// again — see LibraryIndex for why opening is the cost. Beside the settings,
    /// so a scratch ALBUMWALL_CONFIG_DIR gets a scratch index. Loaded on first
    /// use, which is on the scan's thread and not this one.
    private readonly Lazy<Domain.LibraryIndex> _index = new(() =>
        Domain.LibraryIndex.Open(Path.Combine(Path.GetDirectoryName(Settings.Path)!, "index.db")));

    /// What the wall is currently showing, as (root, fingerprint of the scan).
    /// A rescan that comes back identical is dropped rather than shown.
    private (string Root, int Print) _showing = ("", 0);

    /// Scans the library and shows the result.
    ///
    /// `asked` is true when a person wanted this — startup, the Rescan button, a
    /// new folder — and false when the watcher did. The difference is only in
    /// what happens when nothing turns out to have changed: someone who pressed
    /// Rescan gets a rescan, while the watcher, which fires for reasons that
    /// often come to nothing, must not so much as flicker the wall.
    ///
    /// `honest` opens every file and believes nothing the index says. It is what
    /// Rescan means, and only Rescan: it is the way out when the index is wrong,
    /// and on Windows it is minutes where a trusting scan is a fraction of a
    /// second, so nothing that happens by itself may ask for it.
    private void ScanLibrary(bool asked = true, bool honest = false)
    {
        // One scan at a time. A person's request replaces whatever is running —
        // they may have just chosen a different folder. The watcher's does NOT:
        // a first scan of this library is 160 s, a tagger working through it
        // fires the watcher every few seconds, and a scan that restarts each
        // time never finishes. So the watcher's request waits its turn, and
        // however many arrive meanwhile, one scan afterwards answers them all.
        if (!asked && _scanning)
        {
            _scanAgain = true;
            return;
        }

        var root = LibraryRoot;
        if (asked) EmptyState.IsVisible = false;
        WatchLibrary(root);

        _scan?.Cancel();
        var ct = (_scan = new CancellationTokenSource()).Token;
        _scanning = true;
        _scanAgain = false;
        _honestRunning = honest;
        _scanStarted = Environment.TickCount64;
        ShowScanProgress(null);
        Console.WriteLine($"[scan] start ({(asked ? "asked" : "watcher")}{(honest ? ", honest" : "")}) {root}");

        // The UI is told at most ~10 times a second however fast the files go
        // by; a warm scan does thousands a second and the bar does not need them.
        long lastTold = 0;
        void Progress(Domain.LibraryScanner.Progress p)
        {
            var now = Environment.TickCount64;
            if (p.FilesSeen < p.Total && now - lastTold < 100) return;
            lastTold = now;
            Dispatcher.UIThread.Post(() => { if (!ct.IsCancellationRequested) ShowScanProgress(p); });
        }

        // Every way a scan can end comes through here, on the UI thread, unless
        // it was replaced — in which case its successor owns all of this.
        void Finished(Action? then = null) => Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            _scanning = false;
            _honestRunning = false;
            HideScanProgress();
            then?.Invoke();
            if (_scanAgain) ScanLibrary(asked: false);
        });

        // Scan on a worker thread: a cold scan of this library is ~0.1 s but it
        // is bounded by tag reads, and a NAS-backed root will be far slower.
        _ = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var scanner = new Domain.LibraryScanner();
            IReadOnlyList<Domain.Album> albums;
            try
            {
                albums = scanner.Scan(root, Progress, ct, _index.Value, trustIndex: !honest);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[scan] replaced after {sw.ElapsedMilliseconds} ms");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[scan] failed: {ex.Message}");
                Finished(() =>
                {
                    StatusText.Text = $"scan failed: {ex.Message}";
                    if (honest) _prefs?.ShowRescanResult($"It failed: {ex.Message}");
                });
                return;
            }
            sw.Stop();
            Console.WriteLine($"[scan] done in {sw.ElapsedMilliseconds} ms, {albums.Count} albums, "
                            + $"{scanner.FilesOpened} files opened");

            var vms = albums.Select(a => new AlbumVm(a)).ToList();
            var tracks = albums.Sum(a => a.Tracks.Count);
            var artists = albums.Select(a => a.AlbumArtist).Distinct().Count();
            var print = Fingerprint(albums);

            Finished(() =>
            {
                _trackCount = tracks;
                if (honest)
                    _prefs?.ShowRescanResult($"Done — {tracks:N0} tracks read in {Spoken(sw.Elapsed)}.");

                if (!asked && _showing == (root, print))
                {
                    Console.WriteLine($"[watch] rescan in {sw.ElapsedMilliseconds} ms, nothing changed");
                    return;
                }
                if (!asked)
                    Console.WriteLine($"[watch] rescan in {sw.ElapsedMilliseconds} ms, "
                                    + $"{_all.Count} -> {vms.Count} albums");
                _showing = (root, print);

                foreach (var vm in vms) vm.OnOpen = a => SetOpen(a);
                _counts = $"{albums.Count} albums · {tracks} tracks · {artists} artists";
                _scanMs = sw.ElapsedMilliseconds;
                Adopt(vms);
                ApplyGround();
                ShowEmptyState(albums.Count == 0, root);
                _prefs?.Fill();

                // Once, and only after the first scan: there is nothing to
                // resume INTO until the library exists.
                if (!_resumeTried)
                {
                    _resumeTried = true;
                    ResumeSession();
                }

                // Ground hue comes from the art, so it can only be derived after
                // the scan. Separate task: the wall must be on screen
                // immediately, not waiting on 200 thumbnail decodes.
                _ = Task.Run(() =>
                {
                    var gsw = Stopwatch.StartNew();
                    var (hue, concentration) = Ground.HueOf(albums);
                    gsw.Stop();
                    Console.WriteLine($"[wall] ground hue derived in {gsw.ElapsedMilliseconds} ms");

                    Dispatcher.UIThread.Post(() =>
                    {
                        // A library whose covers agree on nothing gets the fallback
                        // rather than an arbitrary hue dressed up as a derived one.
                        var h = concentration < 0.2 ? Ground.FallbackHue : hue;
                        _ramp = new Ground.Ramp(h, concentration, _lightness,
                                                Ground.Chromes[_chrome]);
                        ApplyGround();
                        _prefs?.Fill();     // the Colors tab shows the hue just found
                    });
                });
            });
        });
    }

    private bool _scanning;
    private bool _scanAgain;

    /// The scan now running is the honest one, asked for in Preferences — the
    /// only scan that can be stopped, because it is the only one with a wall
    /// already behind it and nothing to lose by giving up.
    private bool _honestRunning;
    private long _scanStarted;
    private int _trackCount;

    /// Shows how far a scan has got. Null means it has started but does not yet
    /// know how much there is — the tree is still being listed.
    ///
    /// Two displays, chosen by whether there is a wall to protect. With nothing
    /// on screen the progress IS the screen: a heading, a bar, the artist being
    /// read. With a wall already up it is a line along the bottom bar and a
    /// count in the corner, because a rescan — his or the watcher's — has no
    /// business taking the wall away from someone who is using it.
    private void ShowScanProgress(Domain.LibraryScanner.Progress? p)
    {
        var fraction = p is { Total: > 0 } ? (double)p.FilesSeen / p.Total : 0;
        var count = p is { Total: > 0 } ? $"{p.FilesSeen:N0} of {p.Total:N0} tracks" : "looking for music…";

        if (_all.Count == 0)
        {
            ScanState.IsVisible = !EmptyState.IsVisible;
            ScanFill.Width = fraction * 420;
            ScanDetail.Text = p?.Current is { } artist ? $"{count}  ·  {artist}" : count;
            StatusText.Text = "";
        }
        else
        {
            ScanState.IsVisible = false;
            StatusText.Text = p is { Total: > 0 } ? $"rescanning · {count}" : "rescanning…";
        }

        // One bar at a time: the line is for when the big one is not showing.
        ScanBar.IsVisible = !ScanState.IsVisible;
        ScanBar.Width = fraction * BottomBar.Bounds.Width;

        // Preferences is probably sitting on top of all of the above. It is told
        // about the scan he asked for there at once, and about any other only
        // when it has lasted long enough to be worth a glance: a scan through
        // the index is over in a fifth of a second, and a bar that appears for
        // that long is a flicker, not information.
        if (_honestRunning || Environment.TickCount64 - _scanStarted > 600)
            _prefs?.ShowScan(fraction, p?.Current is { } who ? $"{count}  ·  {who}" : count, _honestRunning);
    }

    private void HideScanProgress()
    {
        ScanState.IsVisible = false;
        ScanBar.IsVisible = false;
        ScanBar.Width = 0;
        _prefs?.ScanEnded();
    }

    /// "2 min 38 s", for a sentence. The log keeps the milliseconds.
    private static string Spoken(TimeSpan t) =>
        t.TotalSeconds < 1 ? "under a second"
        : t.TotalMinutes < 1 ? $"{t.Seconds} s"
        : $"{(int)t.TotalMinutes} min {t.Seconds} s";

    /// Watches the library folder, so music that arrives while the app is open
    /// appears without anyone having to know there is a Rescan button.
    ///
    /// Best effort, and says so rather than failing: a root that does not exist
    /// cannot be watched, and a network mount generally reports nothing at all —
    /// inotify does not see changes made on the far side of NFS or SMB. Rescan
    /// stays in the menu for exactly those.
    private void WatchLibrary(string root)
    {
        if (_watcher?.Root == root) return;

        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = new Domain.LibraryWatcher(root,
                () => Dispatcher.UIThread.Post(() => ScanLibrary(asked: false)),
                path => _index.Value.Touch(path));
            Console.WriteLine($"[watch] watching {root}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[watch] not watching {root}: {ex.Message}");
        }
    }

    /// Everything about a scan that the wall or a panel would show differently.
    /// In-process only, so string hashes being salted per run does not matter.
    private static int Fingerprint(IReadOnlyList<Domain.Album> albums)
    {
        var h = new HashCode();
        foreach (var a in albums)
        {
            h.Add(a.AlbumArtist); h.Add(a.Title); h.Add(a.Year);
            h.Add(a.ArtPath); h.Add(a.ArtEmbeddedIn); h.Add(a.ArtWidth); h.Add(a.ArtHeight);
            foreach (var t in a.Tracks) h.Add(t);      // a record: every field counts
        }
        return h.ToHashCode();
    }

    /// Replaces the library under the wall WITHOUT losing your place.
    ///
    /// A scan returns all-new objects, and everything that says where you are —
    /// the open album, the playing one, the search detour — is a reference to an
    /// old one. Left alone, the open panel closes because its album is "no
    /// longer on the wall", the wall returns to the top because its rows were
    /// rebuilt, and the transport's cover stops finding what is playing. That
    /// was tolerable behind a button nobody pressed mid-album. It is not
    /// tolerable from a watcher, which fires while you are listening, because
    /// somebody copied a record in.
    ///
    /// So each reference is carried across by album identity — the same
    /// (AlbumArtist, Title) rule the scanner groups by — and the scroll position
    /// is restored against the row that was at the top, not as a pixel offset,
    /// since new albums sorting in above it move every offset below them.
    private void Adopt(List<AlbumVm> vms)
    {
        var byId = new Dictionary<(string, string), AlbumVm>();
        foreach (var vm in vms) byId.TryAdd((vm.Artist, vm.Title), vm);
        AlbumVm? Carry(AlbumVm? old) =>
            old is not null && byId.TryGetValue((old.Artist, old.Title), out var vm) ? vm : null;

        // The landmark is whatever you are looking at: the open album if any of
        // it is on screen, otherwise the row at the top. Holding the top row
        // still while a panel is open was tried first and is wrong — albums
        // sorting in between the two shove the track list you were reading a
        // whole row down the window.
        var offset = WallScroller.Offset.Y;
        var top = OpenAlbumInView(offset) ?? TopAlbumInView();
        var into = top is null ? 0 : offset - Wall.OffsetOf(RowIndexOf(top));

        if (_open is not null) _open.IsSelected = false;
        _open = Carry(_open);
        if (_open is not null) _open.IsSelected = true;

        _beforeSearchOpen = Carry(_beforeSearchOpen);
        _beforeSearchTop = Carry(_beforeSearchTop);

        // What is playing keeps playing whatever the scan says — mpv has its
        // own list of paths — so if the album has gone, the old object stays and
        // the transport goes on describing it. The transport keeps the bitmap it
        // already has; the new object only needs its cover by the next track.
        if (Carry(_playingAlbum) is { } playing)
        {
            _playingAlbum = playing;
            playing.EnsureCover(260);
        }

        _all = vms;
        _chunkKey = "";      // same count and columns is still a different library
        ApplyFilter();

        // After layout, as in RestoreViewAfterSearch: the panel's height is only
        // known once it has been built. Set, not eased — this is not an action
        // anyone took and should not look like one.
        top = Carry(top);
        Dispatcher.UIThread.Post(() =>
        {
            var y = top is not null && _visible.Contains(top)
                ? Wall.OffsetOf(RowIndexOf(top)) + into
                : offset;
            var max = Math.Max(0, WallScroller.Extent.Height - WallScroller.Viewport.Height);
            WallScroller.Offset = WallScroller.Offset.WithY(Math.Clamp(y, 0, max));
        }, DispatcherPriority.Background);
    }

    /// The open album, if its tile or any of its panel is within the viewport.
    private AlbumVm? OpenAlbumInView(double offset)
    {
        if (_open is null || _panelAt < 0 || !_visible.Contains(_open)) return null;

        var from = Wall.OffsetOf(RowIndexOf(_open));
        var to = Wall.OffsetOf(_panelAt) + Wall.HeightOf(_panelAt);
        return to > offset && from < offset + WallScroller.Viewport.Height ? _open : null;
    }

    /// An album's index in _rows, which is its row on the wall plus one if the
    /// open panel sits above it.
    private int RowIndexOf(AlbumVm album)
    {
        var row = _visible.IndexOf(album) / Math.Max(1, _columns);
        return _panelAt >= 0 && _panelAt <= row ? row + 1 : row;
    }

    /// Draws the window controls the desktop asked for, on the side it asked
    /// for. On this GNOME that is minimize and close on the right and NO
    /// maximize button, because the user turned it off; double-clicking the bar
    /// still maximizes, which is how GNOME expects it to be done.
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
                case WindowButtons.Kind.Maximize: ToggleMaximized(); break;
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

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// The menu, the preferences sheet and the empty state — all one concern,
    /// because they exist for one reason: the app cannot know where the music is.
    private void SetUpMenu()
    {
        var prefs = new MenuItem { Header = "Preferences\u2026" };
        prefs.Click += (_, _) => ShowPrefs(PrefsWindow.Tab.Library);

        var about = new MenuItem { Header = "About" };
        about.Click += (_, _) => ShowPrefs(PrefsWindow.Tab.About);

        // ABOUT IS LAST, by convention, here and as a tab in the sheet it
        // opens. Anything new goes above the separator.
        //
        // No Rescan either, since the index. It used to be here, when it cost
        // four seconds and was the only way to pick up new music. The watcher
        // does that now, and what Rescan has become — every file opened again,
        // minutes of it on Windows — belongs next to a description of itself,
        // not one slip below Preferences. It is on the Library tab.
        //
        // No Quit. The window has a close button, which he put there himself,
        // and a second way to do the same thing at the bottom of a three-item
        // menu was only ever in the way: "lets get rid of quit, no need".
        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ItemsSource = new object[]
            {
                prefs, new Separator(), about
            }
        };

        MenuButton.Click += (_, _) => flyout.ShowAt(MenuButton);

        EmptyChoose.Click += async (_, _) => await ChooseLibraryFolder(this);
    }

    private PrefsWindow? _prefs;

    /// Opens Preferences at a tab, or brings it forward if it is already open.
    /// One instance, like the family's Setup windows: a second Preferences
    /// window would be two views of the same settings disagreeing.
    private void ShowPrefs(PrefsWindow.Tab tab)
    {
        if (_prefs is null)
        {
            _prefs = new PrefsWindow(this);
            _prefs.Closed += (_, _) => _prefs = null;
            _prefs.Place(this);
            _prefs.Show(this);      // owned: stays above the wall, goes when it goes
        }
        _prefs.Select(tab);
        _prefs.Activate();
    }

    // What the Preferences window needs from here. It reads and writes the
    // settings itself; anything that touches the LIBRARY comes back through
    // these, because only this window knows how to scan.
    internal Settings AppSettings => _settings;
    internal string LibraryRootPath => LibraryRoot;
    internal string LibraryCounts => _counts;
    internal int LibraryTrackCount => _trackCount;
    internal bool RescanRunning => _honestRunning;
    internal void Rescan() => ScanLibrary(honest: true);

    /// Gives up on the honest scan. The wall is as it was, since a scan changes
    /// nothing until it finishes, and the index keeps what had been re-read —
    /// the scanner commits what it has on the way out. Then the watcher gets
    /// the turn it may have been waiting for.
    internal void StopRescan()
    {
        if (!_honestRunning) return;
        _scan?.Cancel();
        _scanning = false;
        _honestRunning = false;
        HideScanProgress();
        _prefs?.ShowRescanResult("Stopped. The library is as it was.");
        if (_scanAgain) ScanLibrary(asked: false);
    }

    internal void UseDefaultLibrary()
    {
        _settings.LibraryPath = null;
        _settings.Save();
        ScanLibrary();
    }

    private void ShowEmptyState(bool empty, string root)
    {
        EmptyState.IsVisible = empty;
        if (!empty) return;

        // The status line is driven by a timer that gives up when there is
        // nothing to count, so without this it sits on "scanning…" forever —
        // which on a fresh machine reads as a hang rather than an empty folder.
        StatusText.Text = "";

        EmptyWhere.Text = Directory.Exists(root)
            ? $"Nothing playable was found in {root}. If your records live somewhere else — another drive, or a share on the network — point the app at them."
            : $"{root} does not exist. Point the app at wherever your records live.";
    }

    /// Asks for a folder and rescans if it changed.
    /// `from` is the window the picker belongs to, so it opens over whichever
    /// one asked — Preferences, or this one from the empty state.
    internal async Task ChooseLibraryFolder(Window from)
    {
        var picked = await from.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose your music folder",
            AllowMultiple = false
        });

        var path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;

        // A folder the app cannot reach by path is no use to a scanner that
        // walks the filesystem — a phone over MTP, for instance.
        if (string.IsNullOrWhiteSpace(path)) return;

        _settings.LibraryPath = path;
        _settings.Save();
        ScanLibrary();
    }

    /// The tuning bench: three levers over the palette the library derived.
    ///
    /// None of them touches the HUE — that is the collection's own color and is
    /// not a matter of taste. What is adjustable is how light the room is, how
    /// much of the hue shows in it, and how the furniture separates from the
    /// wall. Each one writes straight through to a rebuilt ramp so the whole
    /// window moves together rather than in pieces.
    private void SetUpBench()
    {
        _lightness = Math.Clamp(_settings.Lightness ?? Ground.DefaultLightness,
                                Ground.MinLightness, Ground.MaxLightness);
        var tint = Math.Clamp(_settings.Tint ?? Ground.DefaultTint, 0, 14);
        Ground.Saturation = tint / 100.0;

        var named = Array.FindIndex(Ground.Chromes,
                                    c => c.Name.Equals(_settings.Chrome,
                                                       StringComparison.OrdinalIgnoreCase));
        _chrome = named >= 0 ? named : Ground.DefaultChrome;

        Palette.SetGroundLightness(_lightness);

        // Volume lives in the same bar but is not part of the palette bench:
        // it is a control, not a setting, and it survives the bench being
        // retired once the colors settle.
        _volume = Math.Clamp(_settings.Volume ?? 100, 0, 100);
        VolumeSlider.Value = _volume;
        VolumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _volume = (int)Math.Round(VolumeSlider.Value);
            if (_player is not null) _player.Volume = _volume;
            RefreshMprisState();
            ScheduleSave();
        };
    }

    /// Rebuilds the ground from the current levers and repaints everything that
    /// took a color from it — including the open panel, whose palette was
    /// snapshotted when it opened and would otherwise stay at the old lightness.
    // The three levers, as the Colors tab in Preferences sees them. Each one
    // takes effect at once, on every window, and is saved a moment later — there
    // is nothing to press to find out what a number looks like, which is the
    // only way anybody ever chose these.
    internal int ColorLightness
    {
        get => _lightness;
        set
        {
            _lightness = Math.Clamp(value, Ground.MinLightness, Ground.MaxLightness);
            Palette.SetGroundLightness(_lightness);
            Retune();
        }
    }

    internal int ColorTint
    {
        get => (int)Math.Round(Ground.Saturation * 100);
        set
        {
            Ground.Saturation = Math.Clamp(value, 0, 14) / 100.0;
            Retune();
        }
    }

    /// The hue the window is built from, and whether it really came from the
    /// covers or is the fallback — a library whose art agrees on nothing, or an
    /// empty one. Read-only: this is the one thing about the colors that is
    /// not his to set, and the Colors tab says so.
    internal double ColorHue => _ramp.Hue;
    internal bool ColorIsFromLibrary => _ramp.Concentration >= 0.2;

    internal int ColorChrome
    {
        get => _chrome;
        set
        {
            _chrome = Math.Clamp(value, 0, Ground.Chromes.Length - 1);
            Retune();
        }
    }

    private void Retune()
    {
        _ramp = new Ground.Ramp(_ramp.Hue, _ramp.Concentration, _lightness,
                                Ground.Chromes[_chrome]);
        ApplyGround();
        if (_open is not null) SyncPanel(unfold: false);
        ScheduleSave();
    }

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

        // The sheet is furniture too, and has to move with the rest of it.
        // On the APPLICATION, not this window: Preferences is a window of its
        // own and a DynamicResource only looks up its own tree and then here.
        var shared = Application.Current!.Resources;
        shared["SheetBg"] = SolidColorBrush.Parse(_ramp.BarHex);
        shared["SheetEdge"] = SolidColorBrush.Parse(_ramp.FieldEdgeHex);
        shared["SheetField"] = field;

        // The progress line has to read on a ground he can slide from near
        // black to near white, so it takes the opposite end rather than a hue.
        Resources["ScanInk"] = SolidColorBrush.Parse(_ramp.Lightness < 55 ? "#B8FFFFFF" : "#B8000000");

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
    /// Where the wall was before a search started, so backing out of the search
    /// can put it back.
    private AlbumVm? _beforeSearchOpen;
    private AlbumVm? _beforeSearchTop;
    private bool _searching;

    private void ApplyFilter()
    {
        var filtering = _filter.Length > 0;

        // SEARCHING IS A DETOUR, NOT A DESTINATION. Typing into the box replaces
        // the whole wall, and clearing it used to dump you at the top of the
        // library with the album you were listening to closed — so the cost of
        // looking something up was losing your place. Remember where we were on
        // the way in, on the way in ONLY: every keystroke re-runs this, and
        // capturing on each one would remember the search instead of the wall.
        if (filtering && !_searching)
        {
            _beforeSearchOpen = _open;
            _beforeSearchTop = TopAlbumInView();
            _searching = true;
        }

        _visible = _filter.Length == 0
            ? _all.ToList()
            : ArtQuery(_filter) is { } wanted
                ? _all.Where(v => wanted(v.Album)).ToList()
                : _all.Where(v => v.Album.SearchText.Contains(_filter)).ToList();

        // A filter that hides the open album has to close it: a panel pointing at
        // a cover that is no longer on the wall is worse than no panel.
        if (_open is not null && !_visible.Contains(_open)) SetOpen(null);

        if (filtering)
            StatusText.Text = $"{_visible.Count} of {_all.Count} albums";

        Rebuild();

        if (!filtering && _searching)
        {
            _searching = false;
            RestoreViewAfterSearch();
        }
    }

    /// A cover shorter than this on its shorter side is "small". It is the
    /// panel's sleeve at 100% scaling, near enough: below it a cover cannot fill
    /// the biggest place the app shows it without being enlarged.
    private const int SmallArtPx = 300;

    /// art:missing, art:small, art:nonsquare — the albums whose artwork wants
    /// attention, typed into the search box.
    ///
    /// The app does the best it can with the art it is given and will not fetch
    /// any: the files are the truth, and better art belongs IN them, put there
    /// once with a tagger. What the app can do is say which albums those are,
    /// which no tagger shows as a wall. Deliberately not in the menu — it is a
    /// maintenance query, not something a listener needs to see.
    ///
    /// An unmeasured cover (size 0) matches none of them: unknown is not small.
    private static Func<Domain.Album, bool>? ArtQuery(string filter) => filter switch
    {
        "art:missing" => a => !a.HasArt,
        "art:small" => a => a.ArtWidth > 0 && Math.Min(a.ArtWidth, a.ArtHeight) < SmallArtPx,
        "art:nonsquare" => a => a.ArtWidth > 0
                             && Math.Abs(a.ArtWidth - a.ArtHeight) > 0.05 * Math.Max(a.ArtWidth, a.ArtHeight),
        _ => null
    };

    /// Puts the wall back where the search interrupted it.
    ///
    /// Re-opening the album is what matters most — it is almost always the one
    /// playing, and it carries its own scroll position with it because opening
    /// anchors. Failing that, the album that was at the top of the view is a
    /// good enough landmark; an absolute offset is not, because the rows have
    /// been rebuilt underneath it and the repeater's extent estimate has moved.
    private void RestoreViewAfterSearch()
    {
        var open = _beforeSearchOpen;
        var top = _beforeSearchTop;
        _beforeSearchOpen = null;
        _beforeSearchTop = null;

        if (open is not null && _visible.Contains(open))
        {
            // After the rebuild has been laid out, not during it: the rows have
            // just been replaced wholesale and the anchor needs something real to
            // measure against.
            Dispatcher.UIThread.Post(() => SetOpen(open, animate: false), DispatcherPriority.Background);
            return;
        }

        if (top is not null && _visible.Contains(top))
            Dispatcher.UIThread.Post(() => AnchorOn(top), DispatcherPriority.Background);
    }

    /// The first album still showing at the top of the viewport — the landmark a
    /// person would say they were "at".
    private AlbumVm? TopAlbumInView()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i] is not AlbumRow row || row.Albums.Count == 0) continue;

            var el = Wall.TryGetElement(i);
            if (el?.TranslatePoint(default, WallScroller)?.Y is not { } y) continue;

            // The first row whose bottom edge has not yet passed the top of the
            // viewport: the topmost row you can actually still see.
            if (y + el.Bounds.Height > 0) return row.Albums[0];
        }
        return null;
    }

    /// A resize only matters if it changes how many covers fit. Dragging a window
    /// edge fires this continuously, and re-chunking 194 albums on every pixel is
    /// work for no visible change.
#if DEBUG
    /// Acts on a command written to `$ALBUMWALL_SNAP.cmd`, DEBUG only.
    ///
    /// The snapshot trigger made the app VISIBLE to whoever is building it; this
    /// makes it DRIVABLE. There is no input path to this window otherwise —
    /// This machine has no xdotool, wtype, ydotool, xte, wmctrl or python-Xlib, and
    /// GNOME will not synthesise events for an unsandboxed caller — so every UI
    /// change had to be verified by asking him to click something. Now a UI
    /// change can be opened, photographed and checked without taking his hands
    /// off what he is doing.
    ///
    ///     open &lt;text&gt;   first album whose artist or title contains &lt;text&gt;
    ///     close          fold the panel away
    ///
    /// Deliberately substring-matched rather than indexed: an index means
    /// counting rows in a scan order nobody can see, and the point is to say
    /// "open the long Zeppelin one" and have it happen.
    private void CheckCommandRequest()
    {
        var trigger = Environment.GetEnvironmentVariable("ALBUMWALL_SNAP");
        if (string.IsNullOrEmpty(trigger)) return;
        var path = trigger + ".cmd";
        if (!File.Exists(path)) return;

        string text;
        try
        {
            text = File.ReadAllText(path).Trim();
            File.Delete(path);      // before acting, so a throw cannot spin
        }
        catch { return; }

        // Toggles the real :pointerover pseudoclass on the sleeve — the same switch
        // Avalonia's input system flips when a pointer arrives.
        //
        // A hover-only style cannot otherwise be checked from here: there is no
        // pointer to move. Forcing the overlay's Opacity to 1 in the markup and
        // photographing it is NOT the same test — it proves the overlay renders
        // and says nothing about whether hovering ever reaches it, which is
        // exactly how a broken one shipped: Opacity="0" in the markup is a LOCAL
        // value, it outranks every style setter, and :pointerover could never win.
        if (text.StartsWith("hover", StringComparison.OrdinalIgnoreCase))
        {
            // Two buttons wear this class now — the panel's sleeve and the
            // transport's cover — so "hover transport" picks the second.
            var wantTransport = text.Contains("transport", StringComparison.OrdinalIgnoreCase);
            var buttons = this.GetVisualDescendants()
                              .OfType<Button>()
                              .Where(b => b.Classes.Contains("coverplay"))
                              .ToList();
            var btn = wantTransport
                ? buttons.FirstOrDefault(b => b.GetVisualDescendants().OfType<Panel>().Any(x => x.Name == "RevealOverlay"))
                : buttons.FirstOrDefault(b => b.GetVisualDescendants().OfType<Panel>().Any(x => x.Name == "PlayOverlay"));
            if (btn is null) { Console.WriteLine("[wall] hover: no coverplay button"); return; }

            var on = !btn.Classes.Contains(":pointerover");
            ((Avalonia.Controls.IPseudoClasses)btn.Classes).Set(":pointerover", on);
            Console.WriteLine($"[wall] hover: :pointerover {(on ? "set" : "cleared")} on the sleeve");
            return;
        }

        // Moves a bench lever through the control itself, so what is exercised is
        // the same path a hand on the slider takes.
        if (text.StartsWith("light ", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(text[6..].Trim(), out var lv))
        {
            ColorLightness = (int)Math.Round(lv);
            _prefs?.Fill();
            Console.WriteLine($"[wall] colors: light -> {ColorLightness}");
            return;
        }

        if (text.StartsWith("tint ", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(text[5..].Trim(), out var tv))
        {
            ColorTint = (int)Math.Round(tv);
            _prefs?.Fill();
            Console.WriteLine($"[wall] colors: tint -> {ColorTint}");
            return;
        }

        if (text.Equals("chrome", StringComparison.OrdinalIgnoreCase))
        {
            ColorChrome = (ColorChrome + 1) % Ground.Chromes.Length;
            _prefs?.Fill();
            Console.WriteLine($"[wall] colors: chrome -> {Ground.Chromes[ColorChrome].Name}");
            return;
        }

        // Scrolls the wall the way a wheel would, so a bug that depends on where
        // the wall is sitting can be reproduced from here.
        if (text.StartsWith("scroll ", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(text[7..].Trim(), out var dy))
        {
            var to = Math.Clamp(WallScroller.Offset.Y + dy, 0,
                        Math.Max(0, WallScroller.Extent.Height - WallScroller.Viewport.Height));
            WallScroller.Offset = WallScroller.Offset.WithY(to);
            Console.WriteLine($"[wall] scroll -> {WallScroller.Offset.Y:F0}");
            return;
        }

        if (text.StartsWith("play ", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text[5..].Trim(), out var tn))
        {
            if (_panelAt < 0 || _rows[_panelAt] is not PanelRow pr) { Console.WriteLine("[wall] play: no panel"); return; }
            var line = pr.AllLines.Where(l => !l.IsHeader).Skip(tn - 1).FirstOrDefault();
            if (line?.PlayCommand is null) { Console.WriteLine("[wall] play: no such track"); return; }
            Trace("before play");
            line.PlayCommand.Execute(null);
            DispatcherTimer.RunOnce(() => Trace("after play"), TimeSpan.FromMilliseconds(400));
            return;
        }

        // "search foo" types into the box; a bare "search" clears it.
        if (text.Equals("search", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("search ", StringComparison.OrdinalIgnoreCase))
        {
            SearchBox.Text = text.Length > 6 ? text[7..].Trim() : "";
            Console.WriteLine($"[wall] search -> '{SearchBox.Text}'");
            return;
        }

        if (text.Equals("trace", StringComparison.OrdinalIgnoreCase))
        {
            Trace("manual");
            Console.WriteLine($"[trace] rows={_rows.Count} visible={_visible.Count} all={_all.Count} "
                            + $"columns={_columns} panelAt={_panelAt} chunkKey={_chunkKey}");
            for (var i = Math.Max(0, _panelAt - 2); i < Math.Min(_rows.Count, _panelAt + 6); i++)
            {
                var el = Wall.TryGetElement(i);
                var y = el?.TranslatePoint(default, WallScroller)?.Y;
                Console.WriteLine($"[trace]   row {i} {(_rows[i] is PanelRow ? "PANEL" : "album")} "
                    + $"{(el is null ? "UNREALIZED" : $"y={y:F0} h={el.Bounds.Height:F0} vis={el.IsVisible} op={el.Opacity:F2} " + $"el={el.GetType().Name} dc={el.DataContext?.GetType().Name ?? "null"} " + $"items={(el as ItemsControl)?.ItemCount.ToString() ?? "-"}")}");
            }
            return;
        }

        if (text.StartsWith("volume ", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(text[7..].Trim(), out var vol))
        {
            VolumeSlider.Value = vol;
            Console.WriteLine($"[wall] volume -> {VolumeSlider.Value}");
            return;
        }

        if (text.Equals("reveal", StringComparison.OrdinalIgnoreCase))
        {
            OnRevealPlaying(this, new Avalonia.Interactivity.RoutedEventArgs());
            Console.WriteLine("[wall] reveal playing");
            return;
        }

        // The menu's Rescan, which nothing else here can reach: a warm scan is
        // over in seconds, so catching its progress display means starting one
        // on demand and photographing it straight away.
        if (text.Equals("rescan", StringComparison.OrdinalIgnoreCase)) { ScanLibrary(honest: true); return; }
        if (text.Equals("rescan stop", StringComparison.OrdinalIgnoreCase)) { StopRescan(); return; }
        if (text.Equals("prefs startup", StringComparison.OrdinalIgnoreCase)) { ShowPrefs(PrefsWindow.Tab.Startup); return; }
        if (text.Equals("prefs colors", StringComparison.OrdinalIgnoreCase)) { ShowPrefs(PrefsWindow.Tab.Colors); return; }
        if (text.Equals("prefs", StringComparison.OrdinalIgnoreCase)) { ShowPrefs(PrefsWindow.Tab.Library); return; }
        if (text.Equals("about", StringComparison.OrdinalIgnoreCase)) { ShowPrefs(PrefsWindow.Tab.About); return; }
        if (text.Equals("sheetoff", StringComparison.OrdinalIgnoreCase)) { _prefs?.Close(); return; }

        if (text.Equals("close", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[wall] command: close");
            SetOpen(null);
            return;
        }

        if (text.StartsWith("open ", StringComparison.OrdinalIgnoreCase))
        {
            var want = text[5..].Trim();
            var hit = _visible.FirstOrDefault(v =>
                v.Album.Title.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                v.Album.AlbumArtist.Contains(want, StringComparison.OrdinalIgnoreCase));

            if (hit is null)
            {
                Console.WriteLine($"[wall] command: open '{want}' — NO MATCH");
                return;
            }

            Console.WriteLine($"[wall] command: open '{want}' -> {hit.Album.AlbumArtist} - {hit.Album.Title}");
            SetOpen(hit);
            return;
        }

        Console.WriteLine($"[wall] command: unrecognized '{text}'");
    }

    /// Panel geometry and scroll position in one line.
    ///
    /// Every scroll bug in this app has been a disagreement between numbers that
    /// all looked individually correct, and each one was found by printing them
    /// together rather than by reasoning about them. Worth keeping.
    private void Trace(string when)
    {
        var panel = _panelAt > 0 ? Wall.TryGetElement(_panelAt) : null;
        var top = panel?.TranslatePoint(default, WallScroller)?.Y;
        Console.WriteLine($"[trace] {when}: panelH={(panel?.Bounds.Height ?? -1):F1} "
            + $"panelTopInView={(top?.ToString("F1") ?? "n/a")} "
            + $"offset={WallScroller.Offset.Y:F1} extent={WallScroller.Extent.Height:F1} "
            + $"viewport={WallScroller.Viewport.Height:F1}");
    }

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

            // Preferences is a window of its own and is not part of this one's
            // visual tree, so it gets a picture of its own. Its CONTENT only:
            // the title bar belongs to the system and cannot be drawn from here.
            if (_prefs is { } prefs)
            {
                var pw = (int)Math.Ceiling(prefs.ClientSize.Width * prefs.RenderScaling);
                var ph = (int)Math.Ceiling(prefs.ClientSize.Height * prefs.RenderScaling);
                var pdpi = 96 * prefs.RenderScaling;
                using var shot = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new PixelSize(pw, ph), new Vector(pdpi, pdpi));
                shot.Render(prefs);
                shot.Save(Path.ChangeExtension(trigger, null) + ".prefs.png");
                Console.WriteLine($"[wall] snapshot of preferences {pw}x{ph} at {prefs.Position}");
            }
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
            Wall.RowHeight = CoverPx + LabelHeight;
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
        // The wall computes its own extent exactly (see WallView), so inserting a
        // panel no longer moves the meaning of the scroll offset. This used to be
        // guarded against an estimate that shifted the moment a panel several
        // times taller than a cover row appeared; the anchoring below is now
        // about showing the right thing, not about correcting for a guess.
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
            EnsurePlayer();
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

            _playingAlbum = album;
            _playingPaths = paths;
            _sessionOver = false;
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

    /// The player, built on first use and wired once.
    ///
    /// Throws if libmpv cannot be loaded; both callers turn that into a
    /// transport that explains itself rather than a crash.
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_player))]
    private void EnsurePlayer()
    {
        if (_player is not null) return;

        _player = new Playback.Player(
            Enum.TryParse<Playback.GainMode>(_settings.Gain, out var saved)
                ? saved
                : Playback.GainMode.Album);

        // A player built now starts at whatever the slider already says.
        _player.Volume = _volume;

        _player.TrackChanged += OnTrackChanged;
        _player.StateChanged += OnPlaybackState;

        // The record ran out: there is nothing left to pick up next time.
        //
        // BUT "FINISHED" IS NOT ONLY THE END OF A RECORD. It is mpv going idle,
        // and mpv is idle when it is first created and again for an instant on
        // the `stop` that opens every Play(). Taken at face value that marked
        // the session over the moment anything started, and nothing was ever
        // saved. A real ending is going idle FROM a track, so the test is
        // whether the player was on one — and it is made here, on mpv's own
        // thread at the moment of the event, not inside the posted lambda: by
        // the time that runs, the track that Play() asked for has usually
        // arrived and the index says "playing" again.
        _player.Finished += (_, _) =>
        {
            if (_player is not { Index: >= 0 }) return;
            Dispatcher.UIThread.Post(EndSession);
        };
    }

    private void EndSession()
    {
        _sessionOver = true;
        SaveSession();      // nothing playing any more; an open panel still counts
    }

    private bool _resumeTried;

    /// Set when the queue has played out, so the periodic save does not write
    /// the finished album straight back.
    private bool _sessionOver;

    /// Records what is playing and where, for the next run.
    private void SaveSession()
    {
        // Nothing is written until the restore has had its turn. The first thing
        // a restore does is unfold a panel, unfolding a panel saves, and a save
        // before the playing half has been put back would record "nothing
        // playing" over the very session being restored.
        if (!_resumeTried) return;

        var session = new Session { OpenAlbumArtist = _open?.Artist, OpenAlbum = _open?.Title };

        if (_player is not null && _playingAlbum is not null && !_sessionOver)
        {
            // Index is -1 until mpv has actually arrived on a track. Saving then
            // would replace a good record with "track -1 at zero" — and the
            // moment that happens is just after launch, mid-restore, which is
            // exactly when the record matters. So: leave the file alone.
            var i = _player.Index;
            if (i < 0 || i >= _playingPaths.Count) return;

            session.AlbumArtist = _playingAlbum.Artist;
            session.Album = _playingAlbum.Title;
            session.Queue = _playingPaths;
            session.Index = i;
            session.PositionSeconds = _player.Position.TotalSeconds;
        }

        if (session.Queue.Count == 0 && session.OpenAlbum is null) Session.Clear();
        else session.Save();
    }

    /// Picks up the album, track and position from the last run — paused.
    ///
    /// His words: "remember what album was playing, song, time elapsed, etc. and
    /// restore on startup ... let's NOT have it auto play though". So the
    /// transport comes back showing where he was, and nothing is heard until he
    /// presses play. AutoPlay in the settings turns that into playing.
    ///
    /// Everything is checked against the library as it is NOW, because the
    /// record is from last time and the files are the truth: an album that has
    /// gone is not resumed, tracks that have gone are dropped from the queue,
    /// and if the track he was on is one of them the album starts from the top
    /// rather than part-way through some other song.
    private void ResumeSession()
    {
        if (_settings.ResumeSession == false) return;
        if (Session.Load() is not { } last) return;

        // What he was looking at, first, and whatever becomes of the playback:
        // a machine without libmpv still has a wall to put back. Not animated,
        // and after layout, for the reasons RestoreViewAfterSearch gives — a
        // restore is not an action and should not look like one.
        if (_all.FirstOrDefault(v => v.Artist == last.OpenAlbumArtist && v.Title == last.OpenAlbum)
            is { } wasOpen)
        {
            Console.WriteLine($"[session] unfolding {wasOpen.Artist} - {wasOpen.Title}");
            Dispatcher.UIThread.Post(() => SetOpen(wasOpen, animate: false), DispatcherPriority.Background);
        }
        else if (last.OpenAlbum is not null)
            Console.WriteLine($"[session] was open but is not in the library: {last.OpenAlbumArtist} - {last.OpenAlbum}");

        if (last.Queue.Count == 0) return;
        if (!Playback.Player.IsAvailable || _playerFailed) return;

        var album = _all.FirstOrDefault(v => v.Artist == last.AlbumArtist && v.Title == last.Album);
        if (album is null)
        {
            Console.WriteLine($"[session] {last.AlbumArtist} - {last.Album} is no longer in the library");
            return;
        }

        var known = album.Album.Tracks.Select(t => t.Path).ToHashSet();
        var queue = last.Queue.Where(known.Contains).ToList();
        if (queue.Count == 0) return;

        var was = last.Index >= 0 && last.Index < last.Queue.Count ? last.Queue[last.Index] : null;
        var index = was is null ? -1 : queue.IndexOf(was);
        var at = TimeSpan.FromSeconds(Math.Max(0, last.PositionSeconds));
        if (index < 0) { index = 0; at = TimeSpan.Zero; }

        try
        {
            EnsurePlayer();
            _playingAlbum = album;
            _playingPaths = queue;
            _sessionOver = false;
            _player.Play(queue, index, at, paused: _settings.AutoPlay != true);
        }
        catch (Exception ex)
        {
            _playerFailed = true;
            Console.WriteLine($"[session] could not resume: {ex.Message}");
            return;
        }

        Console.WriteLine($"[session] resumed {album.Artist} - {album.Title}, "
                        + $"track {index + 1}/{queue.Count} at {(int)at.TotalMinutes}:{at.Seconds:00}"
                        + (_settings.AutoPlay == true ? ", playing" : ", paused"));

        // The transport shows the album's cover, and nothing has asked for it
        // yet: normally the tile was on screen long before anything played.
        void OnCover(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(AlbumVm.Cover)) return;
            album.PropertyChanged -= OnCover;
            UpdateNowPlaying();
        }
        album.PropertyChanged += OnCover;
        album.EnsureCover(260);
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

    /// What MPRIS reports, refreshed on the UI thread whenever the now-playing
    /// line changes.
    ///
    /// A snapshot rather than a live read: D-Bus calls arrive on their own
    /// thread, and reaching into view models from there to assemble an answer is
    /// how a music player earns an intermittent crash. Position is the one field
    /// read live, because it moves continuously and mpv is happy to be asked.
    private Mpris? _mpris;
#if WINDOWS
    private Smtc? _smtc;
#endif

    private volatile Mpris.State _mprisState =
        new(false, false, "", "", "", "", 0, 0, 1.0);

    private Mpris.State MprisState()
    {
        var s = _mprisState;
        var position = _player?.Position ?? TimeSpan.Zero;
        return s with { PositionMicros = (long)position.TotalMicroseconds };
    }

    private void RefreshMprisState()
    {
        if (_player is null || _playingAlbum is null)
        {
            _mprisState = new Mpris.State(false, false, "", "", "", "", 0, 0, _volume / 100.0);
            PushToSystem();
            return;
        }

        var i = _player.Index;
        var path = i >= 0 && i < _playingPaths.Count ? _playingPaths[i] : null;
        var track = path is null
            ? null
            : _playingAlbum.Album.Tracks.FirstOrDefault(t => t.Path == path);

        var art = _playingAlbum.Album.ArtPath;

        _mprisState = new Mpris.State(
            Playing: _player.IsPlaying,
            HasTrack: path is not null,
            Title: track?.Title ?? (path is null ? "" : Path.GetFileNameWithoutExtension(path)),
            Artist: _playingAlbum.Artist,
            Album: _playingAlbum.Title,
            // Only a real file on disk becomes a URL. Embedded art would have to
            // be extracted to a temporary file to have one, which is more than a
            // panel caption is worth.
            ArtUrl: art is not null && File.Exists(art) ? new Uri(art).AbsoluteUri : "",
            LengthMicros: (long)(_player.Duration.TotalMicroseconds),
            PositionMicros: 0,
            Volume: _volume / 100.0);
        PushToSystem();
    }

    /// MPRIS is asked; the Windows transport controls have to be told.
    private void PushToSystem()
    {
#if WINDOWS
        _smtc?.Update(_mprisState, _playingAlbum?.Album);
#endif
    }

    /// A media key, or a click in the shell's own media controls.
    private void MprisCommand(string command) => Dispatcher.UIThread.Post(() =>
    {
        switch (command)
        {
            case "Raise": Activate(); return;
            case "Quit": Close(); return;
        }

        if (_player is null) return;

        switch (command)
        {
            case "PlayPause": _player.TogglePause(); break;
            case "Play": _player.SetPaused(false); break;
            case "Pause": _player.SetPaused(true); break;
            case "Next": _player.Next(); break;
            case "Previous": _player.Previous(); break;
            case "Stop": _player.Stop(); break;
        }
    });

    private void OnTrackChanged(object? sender, Playback.TrackChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => { UpdateNowPlaying(); MarkPlayingTrack(); SaveSession(); });

    private void OnPlaybackState(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => { UpdateNowPlaying(); SaveSession(); });

    /// The transport is the only thing that changes the wall's height while an
    /// album is open, so it is the only place that has to hold the wall still.
    private void ShowTransport(bool visible)
    {
        if (Transport.IsVisible == visible) return;
        var before = PanelTopInView();
        Transport.IsVisible = visible;
        KeepPanelInView(before);
    }

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

    private void Recolor(IBrush on)
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
            Recolor(Palette.For(_playingAlbum.Album).OnLight);
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

    /// Where the open panel currently sits relative to the top of the viewport,
    /// or null if there is no panel or it is not realized.
    private double? PanelTopInView()
    {
        if (_panelAt <= 0 || _panelAt >= _rows.Count) return null;
        return Wall.TryGetElement(_panelAt)?.TranslatePoint(default, WallScroller)?.Y;
    }

    /// Puts the open panel back where it was on screen after something changed
    /// the wall's geometry underneath it.
    ///
    /// SHOWING OR HIDING THE TRANSPORT CHANGES THE VIEWPORT'S HEIGHT, which makes
    /// the ItemsRepeater re-estimate its extent — and the scroll offset is an
    /// absolute number into that estimate. It stays put while the content slides
    /// under it, so the album you just started playing walks off the top of the
    /// window: measured at 453 px for a 16-track album, and worse further down
    /// the library, because the error grows with distance from the top.
    ///
    /// His report: "I clicked on the first track, it started playing and then the
    /// window disapeared... if I scroll up it will be there."
    ///
    /// Correcting by the DIFFERENCE in the panel's on-screen position, rather
    /// than by any absolute figure, is what makes this independent of whatever
    /// the estimate happens to be doing.
    private void KeepPanelInView(double? before)
    {
        if (before is null) return;

        // HOLD IT, do not correct once. The viewport change, the re-measure and
        // the repeater's new extent estimate do not all land in the same layout
        // pass, so a single correction runs before the content has finished
        // sliding, measures a delta of nothing, and congratulates itself.
        _panelHold?.Stop();
        var until = DateTime.UtcNow.AddMilliseconds(500);

        _panelHold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _panelHold.Tick += (_, _) =>
        {
            if (DateTime.UtcNow > until) { _panelHold?.Stop(); _panelHold = null; return; }
            if (PanelTopInView() is not { } after) return;

            var delta = after - before.Value;
            if (Math.Abs(delta) < 0.5) return;

            var max = Math.Max(0, WallScroller.Extent.Height - WallScroller.Viewport.Height);
            WallScroller.Offset = WallScroller.Offset
                .WithY(Math.Clamp(WallScroller.Offset.Y + delta, 0, max));
            Console.WriteLine($"[hold] panel slid {delta:F1}, corrected");
        };
        _panelHold.Start();
    }

    private DispatcherTimer? _panelHold;

    /// Takes the wall to the album that is playing, and opens it.
    ///
    /// The one navigation the app was missing: everything else moves you away
    /// from what is playing and nothing brought you back. Opening it rather than
    /// merely scrolling to it is deliberate — you press this when you want to
    /// see the record, which means the track list, not just the cover.
    private void OnRevealPlaying(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_playingAlbum is null) return;

        // A search may be hiding it, and showing it is the whole point. Clearing
        // the box runs the filter and its own restore first, so the reveal is
        // queued behind that rather than fighting it.
        if (_filter.Length > 0)
        {
            SearchBox.Text = "";
            Dispatcher.UIThread.Post(Reveal, DispatcherPriority.Background);
            return;
        }

        Reveal();

        void Reveal()
        {
            if (_playingAlbum is null || !_visible.Contains(_playingAlbum)) return;

            // Already open: this is "take me there", not a toggle, so it must
            // never close the panel the person is asking to look at.
            if (ReferenceEquals(_open, _playingAlbum)) AnchorOn(_playingAlbum);
            else SetOpen(_playingAlbum);
        }
    }

    private void UpdateNowPlaying()
    {
        if (_player is null || _playingAlbum is null) { ShowTransport(false); RefreshMprisState(); return; }

        var i = _player.Index;
        if (i < 0 || i >= _playingPaths.Count) { ShowTransport(false); return; }

        var path = _playingPaths[i];
        var track = _playingAlbum.Album.Tracks.FirstOrDefault(t => t.Path == path);

        ShowTransport(true);
        RefreshMprisState();
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
        // so the color has to live somewhere.
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
        // be recolored for the light bar.
        Recolor(palette.OnLight);

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

    /// <param name="animate">
    /// False when RESTORING a panel that was already open rather than opening one
    /// in response to a click. A restore is not an action and should not look
    /// like one — and an unfold starts the panel at zero height, which right
    /// after a full row rebuild leaves the repeater unable to place the rows
    /// below it, blanking the wall under the panel.
    /// </param>
    private async void SetOpen(AlbumVm? album, bool animate = true)
    {
        var closing = ReferenceEquals(_open, album) || album is null;
        Console.WriteLine($"[wall] SetOpen({album?.Title ?? "null"}) was={_open?.Title ?? "none"} "
                        + $"{(closing ? "CLOSING" : "opening")} animate={animate} "
                        + $"inVisible={(album is null ? "-" : _visible.Contains(album).ToString())}");

        // Fold the old panel away before the rows move, so closing is the reverse
        // of opening rather than a row vanishing from under the cursor.
        if (_open is not null) await FoldAwayAsync();

        if (_open is not null) _open.IsSelected = false;
        // Clicking the open album again closes it, which is the only way back to
        // an unbroken wall.
        _open = closing ? null : album;
        if (_open is not null) _open.IsSelected = true;

        Rebuild(unfold: animate && _open is not null);

        // Anchor on whichever album the action was about: the one just opened, or
        // the one just closed, so closing leaves you looking at where you were
        // rather than wherever the removed panel's height dropped you.
        if (_open is not null) AnchorOn(_open);
        else if (album is not null) AnchorOn(album);

        SaveSession();      // what is unfolded is part of where he was
    }

    /// Collapses the realized panel element to nothing, then returns so the
    /// caller can rebuild. If the panel is scrolled out of view there is no
    /// element to animate and nothing to wait for.
    private async Task FoldAwayAsync()
    {
        var index = -1;
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i] is PanelRow) { index = i; break; }
        if (index < 0) return;

        var el = Wall.TryGetElement(index);
        Console.WriteLine($"[wall] fold away: rowIndex={index} element={(el is null ? "NOT REALIZED" : "ok")}");
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
    ///
    /// DELIBERATELY EQUAL TO THE UNFOLD. They are two halves of one gesture — the
    /// panel opening and the wall turning to show it — and running the scroll more
    /// than twice as long as the unfold meant the panel finished, and then the
    /// wall carried on sliding for another 420 ms underneath it. Two motions where
    /// the eye expects one is most of what "clunky" was.
    private const int AnchorMs = UnfoldMs;

    /// How much of the album's own row survives when its panel is too tall to
    /// fit. Zero hides the row completely and gives the panel the whole window.
    /// One number to change if that ever reads as too abrupt.
    private const double Peek = 0;

    /// Fraction of the remaining distance the anchor covers each 16 ms tick.
    /// Lower is slower and smoother; this is the shape of the ease, and it is
    /// self-correcting because the target is recomputed every tick.
    private const double AnchorEase = 0.24;

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
    /// opened has its neighbors visible above it and its panel below, rather
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
    /// Focuses the OPEN ALBUM, not merely the row it came from.
    ///
    /// Anchoring on the row alone puts a full album row of context above it. That
    /// is right for a short album and wrong for a long one: the panel is the thing
    /// that was asked for, and on a 17-track double it ran off the bottom of the
    /// window while a row of unrelated covers sat above it holding a place nobody
    /// needed. His words — "it does seem better to focus the expanded album rather
    /// than the row it came from... especially if the focus on the line the album
    /// lives in causes the bottom of it to scroll off screen".
    ///
    /// So the panel's foot is brought to the foot of the viewport, inside a range
    /// that always keeps the opened tile and its arrow on screen: never looser
    /// than `lead` (a row of context — the old behavior, which is exactly what a
    /// short panel still gets) and never tighter than `MinLead` (the tile just
    /// clear of the top bar, which is what a tall one needs). The clamp IS the
    /// fix; the short-album case falls out of it unchanged.
    /// Where the wall should sit to show the album at `rowIndex`.
    ///
    /// ARITHMETIC NOW, NOT MEASUREMENT. The wall states the exact offset of any
    /// row whether or not it has been built, so this needs no realized element,
    /// no TranslatePoint, and no walking towards a row hoping it appears. All of
    /// that existed to work around an estimated extent, and there is no estimate
    /// any more. See WallView.
    ///
    /// The two cases are his: a panel too tall to fit takes the whole window and
    /// the album's own row goes off the top — "hide the row the album is in and
    /// maximize what we can see of the selected album" — and one that fits is
    /// centered, because the thing you asked to look at belongs in the middle of
    /// the view rather than at one end of it.
    private double TargetOffset(int rowIndex)
    {
        var view = WallScroller.Viewport.Height;
        var max = Math.Max(0, WallScroller.Extent.Height - view);

        // Closing: no panel, so keep a row of context above the album that was
        // folded away and leave the eye where it was.
        if (_panelAt <= 0 || _panelAt >= _rows.Count)
        {
            var lead = CoverPx + LabelHeight + RowSpacing;
            return Math.Clamp(Wall.OffsetOf(rowIndex) - lead, 0, max);
        }

        var panelTop = Wall.OffsetOf(_panelAt);
        var panelHeight = Wall.HeightOf(_panelAt);

        if (panelHeight > view - Peek)
            return Math.Clamp(panelTop - Peek, 0, max);

        return Math.Clamp(panelTop - (view - panelHeight) / 2, 0, max);
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
            var target = TargetOffset(rowIndex);

            // Out of time. Land exactly on the target rather than wherever the
            // easing had got to: an exponential approach crawls the last few
            // pixels, and stopping mid-crawl leaves the row a hair off the top
            // with a sliver of the row above it still showing. The wall is frozen
            // while this runs, so the final step is not visible as a jump.
            if (DateTime.UtcNow > until)
            {
                WallScroller.Offset = WallScroller.Offset.WithY(target);
                Settle();
                return;
            }

            // No early exit. The panel is still growing for the whole of this
            // window, so the target is still moving, and "I have arrived" on an
            // early tick only means the panel had not finished becoming tall yet.
            var y = WallScroller.Offset.Y;

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
            //
            // One last correction before releasing: ItemsRepeater ESTIMATES the
            // extent from the rows it has realized, and that estimate keeps moving
            // as the unfold realizes more of them — so the content slides a few
            // pixels under an offset that was exactly right when it was set. Recompute
            // against where things ended up, while the wall is still frozen.
            DispatcherTimer.RunOnce(() =>
            {
                WallScroller.Offset = WallScroller.Offset.WithY(TargetOffset(rowIndex));
                FreezeWall(false);
            }, TimeSpan.FromMilliseconds(UnfoldMs + 60));
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

    /// Fired as the layout realizes a container. This — not item creation — is
    /// the moment we know a cover is about to be visible.
    private void OnElementPrepared(object? sender, WallElementEventArgs e)
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
