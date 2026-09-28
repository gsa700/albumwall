// AlbumWall - the now-playing view. See NowPlayingPanel.axaml for what it is and
// where its design came from.
//
// It is a DISPLAY. It knows nothing about the player, the library or the window
// it sits in: the owner tells it what to show and listens for the few things a
// person can ask of it. That is what lets the same view be the compact window
// today and the kiosk's front panel later without either one knowing about the
// other.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace AlbumWall.App;

public partial class NowPlayingPanel : UserControl
{
    /// What a person asked for, from the hover controls or the progress line.
    public event Action? PreviousRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? ExpandRequested;
    /// A press on the progress line: where along it, 0 to 1.
    public event Action<double>? SeekRequested;
    /// A press on the view's own background, which in a window means "move me".
    public event Action<PointerPressedEventArgs>? BackgroundPressed;

    /// Whether the hover controls exist at all. On for the compact window, off
    /// for the front panel, where a remote does the driving.
    public bool Interactive { get; set; } = true;

    /// The title's size. 68 is the front panel's, read from across a room; a
    /// window on a desk wants less ("shrink the title text in the window",
    /// 2026-09-28), so the compact window sets its own.
    public double TitleSize
    {
        get => Title.FontSize;
        set => Title.FontSize = value;
    }

    /// The window buttons, supplied by the window: which ones and in what order
    /// is the desktop's decision (see WindowButtons.Layout), not this view's.
    public Panel WindowButtons => WindowButtonsHost;

    private readonly DispatcherTimer _clock;
    private bool _standbyWanted;
    private bool _hasPlaying;
    private bool _hover;
    private DispatcherTimer? _touchLinger;
    private TimeSpan _duration;

    public NowPlayingPanel()
    {
        InitializeComponent();

        PrevControl.Click += (_, _) => PreviousRequested?.Invoke();
        PlayControl.Click += (_, _) => PlayPauseRequested?.Invoke();
        NextControl.Click += (_, _) => NextRequested?.Invoke();
        ExpandButton.Click += (_, _) => ExpandRequested?.Invoke();

        ProgressTrack.PointerPressed += (_, e) =>
        {
            if (!Interactive || ProgressTrack.Bounds.Width <= 0) return;
            SeekRequested?.Invoke(Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.Bounds.Width, 0, 1));
            e.Handled = true;
        };

        // A press anywhere that no control took is the view's own background.
        Root.PointerPressed += (_, e) =>
        {
            if (e.Pointer.Type == PointerType.Touch) ShowControlsFor(TimeSpan.FromSeconds(4));
            if (e.Handled || e.Source is Button || IsInsideButton(e.Source)) return;
            BackgroundPressed?.Invoke(e);
        };
        Root.PointerEntered += (_, e) => { if (e.Pointer.Type != PointerType.Touch) SetControls(true); };
        Root.PointerExited += (_, e) => { if (e.Pointer.Type != PointerType.Touch) SetControls(false); };

        // The standby clock ticks on its own, and only while it can be seen.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
    }

    private static bool IsInsideButton(object? source)
    {
        for (var v = source as StyledElement; v is not null; v = v.Parent)
            if (v is Button) return true;
        return false;
    }

    private void SetControls(bool on)
    {
        on &= Interactive;
        _hover = on;
        HoverControls.Opacity = on ? 1 : 0;
        HoverControls.IsHitTestVisible = on;
        FormatLine.Opacity = on && WindowButtonsHost.Children.Count > 0 ? 0 : 1;
        ApplyScreen();
    }

    /// Which screen is up: standby when it has been asked for, unless a pointer
    /// is over the view and there is something playing to wake to.
    private void ApplyScreen()
    {
        var standby = _standbyWanted && !(_hover && _hasPlaying);
        Standby.IsVisible = standby;
        Playing.IsVisible = !standby;
        CoverOverlay.IsVisible = !standby;
        Root.Background = Brush.Parse(standby ? "#000000" : "#0A0A09");
        if (standby) { UpdateClock(); _clock.Start(); } else _clock.Stop();
    }

#if DEBUG
    public void ShowControlsForTest() => SetControls(true);
#endif

    private void ShowControlsFor(TimeSpan time)
    {
        SetControls(true);
        _touchLinger?.Stop();
        _touchLinger = new DispatcherTimer { Interval = time };
        _touchLinger.Tick += (_, _) => { _touchLinger?.Stop(); _touchLinger = null; SetControls(false); };
        _touchLinger.Start();
    }

    /// The playing screen. Everything but the position, which moves on its own
    /// and is fed separately (SetPosition).
    public void ShowPlaying(NowPlaying np)
    {
        _standbyWanted = false;
        _hasPlaying = true;

        Cover.Source = np.Cover;
        CoverGround.Background = np.Accent;
        TrackLine.Text = np.TrackLine.ToUpperInvariant();
        FormatLine.Text = np.Format;
        Title.Text = np.Title;

        // "Journey — Frontiers 1983": the dash and the year stay quiet.
        Subtitle.Inlines = new InlineCollection
        {
            new Run(np.Artist),
            new Run(" — ") { Foreground = Brush.Parse("#6E695F") },
            new Run(np.Album),
        };
        if (np.Year > 0) Subtitle.Inlines.Add(new Run($" {np.Year}") { Foreground = Brush.Parse("#9C968C") });

        ProgressFill.Background = np.Accent;
        PlayGlyph.Data = Geometry.Parse(np.IsPlaying
            ? "M 5 1 L 5 29 M 21 1 L 21 29"         // pause
            : "M 3 1 L 25 15 L 3 29 Z");            // play
        PlayGlyph.StrokeThickness = np.IsPlaying ? 6 : 2.4;
        ApplyScreen();
    }

    public void SetPosition(TimeSpan position, TimeSpan duration)
    {
        _duration = duration;
        Elapsed.Text = Clock(position);
        Remaining.Text = duration > TimeSpan.Zero ? "\u2212" + Clock(duration - position) : "";
        var track = ProgressTrack.Bounds.Width;
        ProgressFill.Width = duration > TimeSpan.Zero && track > 0
            ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1) * track
            : 0;
    }

    /// The standby screen: a clock, and what is paused if anything is. With
    /// nothing at all to show, only the clock.
    public void ShowStandby(IImage? cover, string? title, string? subtitle, TimeSpan pausedAt)
    {
        _standbyWanted = true;
        var hasCard = title is not null;
        if (!hasCard) _hasPlaying = false;
        PausedCard.IsVisible = hasCard;
        Standby.ColumnDefinitions[1].Width = new GridLength(hasCard ? 2 : 0);
        if (hasCard)
        {
            PausedCover.Source = cover;
            PausedAt.Text = $"PAUSED AT {Clock(pausedAt)}";
            PausedTitle.Text = title;
            PausedSubtitle.Text = subtitle ?? "";
        }
        ApplyScreen();
    }

    public bool IsStandby => Standby.IsVisible;

    /// Whether the meters are there at all: only when the player can measure.
    public bool ShowMeters
    {
        get => Meters.IsVisible;
        set => Meters.IsVisible = value;
    }

    /// The latest levels, in dBFS of RMS. The needles do their own moving.
    public void SetLevels(double left, double right)
    {
        LeftMeter.SetLevel(left);
        RightMeter.SetLevel(right);
    }

    private void UpdateClock()
    {
        // The desktop's own choice of 12 or 24 hours, not ours.
        var now = DateTime.Now;
        var twelve = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h');
        ClockText.Text = now.ToString(twelve ? "h:mm" : "H:mm", CultureInfo.CurrentCulture);
        ClockSuffix.Text = twelve ? now.ToString("tt", CultureInfo.CurrentCulture) : "";
        ClockSuffix.IsVisible = twelve;
    }

    private static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _clock.Stop();
        _touchLinger?.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}

/// Everything the playing screen shows, as one value.
public sealed record NowPlaying(
    IImage? Cover,
    string TrackLine,       // "Track 12 of 14"
    string Format,          // "FLAC · 16 / 44.1"
    string Title,
    string Artist,
    string Album,
    int Year,
    IBrush Accent,          // the playing album's light tone
    bool IsPlaying);
