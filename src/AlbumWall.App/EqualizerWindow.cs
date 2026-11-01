// AlbumWall - the equalizer's window: four bands to fit the music to the room.
//
// "Equalizer…" in the menu (MainWindow.PlayMode.cs) and on Preferences'
// Playback page. A parametric equalizer, his choice, for the sound system
// rather than the song: each band's frequency is swept by ear to the place
// that needs a boost or a cut (Playback/Equalizer.cs has the bands and how
// they are applied). Everything applies as it moves, and is kept.
//
// On top, the curve the four bands make, redrawn as they move, from 20 Hz to
// 20 kHz and +-12 dB, after the headroom that keeps it from clipping: what
// leaves the app. Under it a column per band: its gain, its frequency, and for
// the two middle bands how wide it is.
//
// In the light windows' colors, as Preferences and Properties. One at a time.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using AlbumWall.Playback;

namespace AlbumWall.App;

public sealed class EqualizerWindow : Window
{
    private static EqualizerWindow? _open;
    private readonly MainWindow _host;
    private EqSettings _eq;
    private readonly EqCurve _curve = new();
    private readonly ToggleSwitch _on;
    private readonly List<(Slider Gain, Slider Freq, Slider? Width, TextBlock GainText, TextBlock FreqText, TextBlock? WidthText)> _bands = [];
    private bool _filling;

    public static void ShowFrom(MainWindow owner)
    {
        if (_open is null)
        {
            _open = new EqualizerWindow(owner);
            _open.Closed += (_, _) => _open = null;
            _open.Show(owner);
        }
        _open.Activate();
    }

    /// The one open, if any (the snapshot rig photographs it).
    public static EqualizerWindow? Current => _open;

    /// For Preferences: the switch there and the one here are the same setting.
    public static void Refresh() => _open?.Fill();

    private EqualizerWindow(MainWindow host)
    {
        _host = host;
        _eq = host.EqualizerSettings;
        Title = $"{App.DisplayName} — Equalizer";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AlbumWall/Assets/app.ico")));
        Width = 760;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Foreground = SolidColorBrush.Parse("#1D1D20");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources["SheetBg"] = SolidColorBrush.Parse("#E4E4E2");
        Resources["SheetField"] = SolidColorBrush.Parse("#F6F6F4");
        Resources["SheetEdge"] = SolidColorBrush.Parse("#C2C2C6");
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetBg");

        _on = new ToggleSwitch { OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center };
        _on.IsCheckedChanged += (_, _) => { if (!_filling) Change(e => e.On = _on.IsChecked == true); };
        // The light theme's own button: Button.panel is drawn for the dark wall.
        var flat = new Button { Content = "Flat", VerticalAlignment = VerticalAlignment.Center, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(flat, "Every band back to 0 dB, at its usual frequency");
        flat.Click += (_, _) => Change(e => e.Bands = EqSettings.Default());

        var top = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        buttons.Children.Add(flat);
        buttons.Children.Add(_on);
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        top.Children.Add(new TextBlock
        {
            Text = "For the room and its speakers: sweep a band to where the sound needs help, then lift or cut it. Boosts lower everything else, so nothing clips.",
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 20, 0),
        });

        var curveBox = new Border
        {
            Height = 170, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            ClipToBounds = true, Child = _curve, Margin = new Thickness(0, 14, 0, 14),
        };
        curveBox[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetField");
        curveBox[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetEdge");

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 18 };
        for (var i = 0; i < 4; i++)
        {
            var column = Band(i);
            Grid.SetColumn(column, i);
            columns.Children.Add(column);
        }

        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 22) };
        body.Children.Add(top);
        body.Children.Add(curveBox);
        body.Children.Add(columns);
        Content = body;
        Fill();
        TextSize.Follow(this);
    }

    private Control Band(int i)
    {
        var (min, max) = EqSettings.Ranges[i];
        var peak = EqSettings.Kinds[i] == EqKind.Peak;
        var column = new StackPanel { Spacing = 4 };
        column.Children.Add(new TextBlock
        {
            Text = EqSettings.Names[i] + (peak ? "" : " shelf"), FontSize = 14, FontWeight = FontWeight.SemiBold,
        });

        TextBlock Value() => new() { FontSize = 12, Opacity = 0.7 };
        Slider Line(double lo, double hi, string name)
        {
            var s = new Slider { Minimum = lo, Maximum = hi };
            Avalonia.Automation.AutomationProperties.SetName(s, $"{EqSettings.Names[i]} {name}");
            return s;
        }

        var gainText = Value();
        var gain = Line(-EqSettings.MaxGain, EqSettings.MaxGain, "gain");
        gain.SmallChange = 0.5; gain.LargeChange = 3; gain.TickFrequency = 0.5; gain.IsSnapToTickEnabled = true;
        column.Children.Add(Label("Gain", gainText));
        column.Children.Add(gain);

        // Frequency on a log scale, so a sweep moves as evenly through the bass
        // as through the treble: the slider holds log10(Hz).
        var freqText = Value();
        var freq = Line(Math.Log10(min), Math.Log10(max), "frequency");
        freq.SmallChange = 0.01; freq.LargeChange = 0.1;
        column.Children.Add(Label("Frequency", freqText));
        column.Children.Add(freq);

        Slider? width = null;
        TextBlock? widthText = null;
        if (peak)
        {
            widthText = Value();
            // Wide on the left, narrow on the right: Q on a log scale.
            width = Line(Math.Log10(EqSettings.MinQ), Math.Log10(EqSettings.MaxQ), "width");
            width.SmallChange = 0.02; width.LargeChange = 0.1;
            column.Children.Add(Label("Width", widthText));
            column.Children.Add(width);
        }

        gain.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_filling) Change(q => q.Bands[i].Gain = Math.Round(gain.Value * 2) / 2); };
        freq.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_filling) Change(q => q.Bands[i].Frequency = Math.Round(Math.Pow(10, freq.Value))); };
        if (width is not null)
            width.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_filling) Change(q => q.Bands[i].Q = Math.Round(Math.Pow(10, width.Value), 2)); };

        _bands.Add((gain, freq, width, gainText, freqText, widthText));
        return column;

        static Control Label(string name, TextBlock value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = name, FontSize = 12 });
            return row;
        }
    }

    /// The window from the settings.
    private void Fill()
    {
        _eq = _host.EqualizerSettings;
        _filling = true;
        _on.IsChecked = _eq.On;
        for (var i = 0; i < 4; i++)
        {
            var b = _eq.Bands[i];
            var (gain, freq, width, _, _, _) = _bands[i];
            gain.Value = b.Gain;
            freq.Value = Math.Log10(b.Frequency);
            if (width is not null) width.Value = Math.Log10(b.Q);
        }
        _filling = false;
        Say();
    }

    /// Each change makes a new settings object, which the sound picks up as the
    /// next block goes out, and is kept.
    private void Change(Action<EqSettings> change)
    {
        var next = _eq.Copy();
        change(next);
        _eq = next.Checked();
        _host.EqualizerSettings = _eq;
        if (_eq.Bands.Count == 4)
        {
            _filling = true;
            // Flat puts the sliders back.
            for (var i = 0; i < 4; i++)
            {
                var (gain, freq, width, _, _, _) = _bands[i];
                if (Math.Abs(gain.Value - _eq.Bands[i].Gain) > 0.01) gain.Value = _eq.Bands[i].Gain;
                if (Math.Abs(Math.Pow(10, freq.Value) - _eq.Bands[i].Frequency) > 1) freq.Value = Math.Log10(_eq.Bands[i].Frequency);
                if (width is not null && Math.Abs(Math.Pow(10, width.Value) - _eq.Bands[i].Q) > 0.01) width.Value = Math.Log10(_eq.Bands[i].Q);
            }
            _filling = false;
        }
        Say();
    }

    private void Say()
    {
        for (var i = 0; i < 4; i++)
        {
            var b = _eq.Bands[i];
            var (_, _, _, gainText, freqText, widthText) = _bands[i];
            gainText.Text = $"{(b.Gain > 0 ? "+" : "")}{b.Gain:0.0} dB";
            freqText.Text = b.Frequency >= 1000 ? $"{b.Frequency / 1000:0.##} kHz" : $"{b.Frequency:0} Hz";
            if (widthText is not null) widthText.Text = $"Q {b.Q:0.0#}";
        }
        _curve.Eq = _eq;
    }
}

/// The curve the four bands make, after the headroom, on a log frequency axis.
internal sealed class EqCurve : Control
{
    private EqSettings? _eq;
    public EqSettings? Eq { get => _eq; set { _eq = value; InvalidateVisual(); } }

    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#D8D8DA")), 1);
    private static readonly IPen Zero = new Pen(new SolidColorBrush(Color.Parse("#B4B4B8")), 1);
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8A8A90"));
    private static readonly IPen Line = new Pen(new SolidColorBrush(Color.Parse("#2B6CB0")), 2.2);
    private static readonly IPen LineOff = new Pen(new SolidColorBrush(Color.Parse("#A9A9AE")), 1.6, new DashStyle([3, 3], 0));

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        double X(double f) => (Math.Log10(f) - Math.Log10(20)) / (Math.Log10(20000) - Math.Log10(20)) * w;
        double Y(double db) => h / 2 - db / (EqSettings.MaxGain + 2) * (h / 2);

        var face = new Typeface(Avalonia.Controls.Documents.TextElement.GetFontFamily(this));
        void Text(string s, double x, double y) => context.DrawText(
            new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 10.5, Label), new Point(x, y));

        foreach (var f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            context.DrawLine(Grid, new Point(X(f), 0), new Point(X(f), h));
            Text(f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}", X(f) + 3, h - 15);
        }
        foreach (var db in new double[] { -12, -6, 6, 12 })
        {
            context.DrawLine(Grid, new Point(0, Y(db)), new Point(w, Y(db)));
            Text($"{(db > 0 ? "+" : "")}{db:0}", 4, Y(db) - 14);
        }
        context.DrawLine(Zero, new Point(0, Y(0)), new Point(w, Y(0)));

        if (_eq is not { } eq) return;
        var headroom = Equalizer.HeadroomDb(eq);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var first = true;
            for (var x = 0.0; x <= w; x += 2)
            {
                var f = Math.Pow(10, Math.Log10(20) + x / w * (Math.Log10(20000) - Math.Log10(20)));
                var y = Y(Equalizer.ResponseDb(eq, f) - headroom);
                if (first) { g.BeginFigure(new Point(x, y), false); first = false; }
                else g.LineTo(new Point(x, y));
            }
            g.EndFigure(false);
        }
        context.DrawGeometry(null, eq.On ? Line : LineOff, geometry);
    }
}
