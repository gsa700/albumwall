// AlbumWall — the Preferences window. See the note in the markup for the
// format, which the file-properties window is meant to follow.

using Avalonia;
using Avalonia.Controls;

namespace AlbumWall.App;

public partial class PrefsWindow : Window
{
    /// In the order they appear. About stays last.
    public enum Tab { Library, Startup, Colors, About }

    private readonly MainWindow? _host;

    /// True while the controls are being filled from the settings, which is not
    /// him changing them: without it, merely opening the window would write the
    /// defaults into his settings file.
    private bool _filling;

    /// Set when adding or removing the shortcut fails, and shown in place of the
    /// usual status so the reason is not lost to a log nobody is reading.
    private string? _shortcutNote;

    /// For the XAML previewer only.
    public PrefsWindow() => InitializeComponent();

    public PrefsWindow(MainWindow host) : this()
    {
        _host = host;
        Title = $"{App.DisplayName} \u2014 Preferences";
        WhenItOpens.Text = $"When {App.DisplayName} opens";

        ChooseFolder.Click += async (_, _) => { await host.ChooseLibraryFolder(this); Fill(); };
        UseDefault.Click += (_, _) => { host.UseDefaultLibrary(); Fill(); };
        Rescan.Click += (_, _) => host.Rescan();

        // Written straight through: there is no OK button to forget, and a
        // setting that only takes effect at the next launch has nothing to
        // apply in the meantime.
        Resume.IsCheckedChanged += (_, _) =>
        {
            if (_filling) return;
            host.AppSettings.ResumeSession = Resume.IsChecked == true;
            AutoPlay.IsEnabled = Resume.IsChecked == true;
            host.AppSettings.Save();
        };
        AutoPlay.IsCheckedChanged += (_, _) =>
        {
            if (_filling) return;
            host.AppSettings.AutoPlay = AutoPlay.IsChecked == true;
            host.AppSettings.Save();
        };

        Light.Minimum = Ground.MinLightness;
        Light.Maximum = Ground.MaxLightness;
        Bars.ItemsSource = Ground.Chromes.Select(c => char.ToUpper(c.Name[0]) + c.Name[1..]).ToList();

        Light.PropertyChanged += (_, e) =>
        {
            if (_filling || e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            host.ColorLightness = (int)Math.Round(Light.Value);
            LightValue.Text = host.ColorLightness.ToString();
        };
        Tint.PropertyChanged += (_, e) =>
        {
            if (_filling || e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            host.ColorTint = (int)Math.Round(Tint.Value);
            TintValue.Text = host.ColorTint.ToString();
        };
        Bars.SelectionChanged += (_, _) =>
        {
            if (_filling || Bars.SelectedIndex < 0) return;
            host.ColorChrome = Bars.SelectedIndex;
            BarsAbout.Text = AboutBars(host.ColorChrome);
        };
        ResetColors.Click += (_, _) =>
        {
            host.ColorLightness = Ground.DefaultLightness;
            host.ColorTint = Ground.DefaultTint;
            host.ColorChrome = Ground.DefaultChrome;
            Fill();
        };

        ShortcutAdd.Click += (_, _) => { _shortcutNote = WindowsShell.AddShortcut(); Fill(); };
        ShortcutRemove.Click += (_, _) => { _shortcutNote = WindowsShell.RemoveShortcut(); Fill(); };

        // Where it was, like the family's Setup windows. Saved as it closes
        // rather than as it moves: nothing is lost if this one is wrong.
        Closing += (_, _) =>
        {
            host.AppSettings.PrefsX = Position.X;
            host.AppSettings.PrefsY = Position.Y;
            host.AppSettings.Save();
        };

        Fill();
    }

    public void Select(Tab tab) => Tabs.SelectedIndex = (int)tab;

    /// Re-reads everything shown. Called when it opens, after anything here
    /// changes the library, and by the main window when a scan finishes — the
    /// counts on the About tab are only as good as the last scan.
    public void Fill()
    {
        if (_host is null) return;

        _filling = true;
        LibraryPath.Text = _host.LibraryRootPath;

        // Resume is on unless turned off; auto-play is off unless turned on.
        Resume.IsChecked = _host.AppSettings.ResumeSession != false;
        AutoPlay.IsChecked = _host.AppSettings.AutoPlay == true;
        AutoPlay.IsEnabled = Resume.IsChecked == true;
        Light.Value = _host.ColorLightness;
        Tint.Value = _host.ColorTint;
        Bars.SelectedIndex = _host.ColorChrome;
        _filling = false;

        LightValue.Text = _host.ColorLightness.ToString();
        TintValue.Text = _host.ColorTint.ToString();
        BarsAbout.Text = AboutBars(_host.ColorChrome);

        ShortcutSection.IsVisible = OperatingSystem.IsWindows();
        if (OperatingSystem.IsWindows())
        {
            var has = WindowsShell.HasShortcut;
            var current = WindowsShell.ShortcutIsCurrent;
            ShortcutAdd.Content = !has ? "Add to the Start Menu" : current ? "Re-create the shortcut" : "Point the shortcut here";
            ShortcutRemove.IsVisible = has;
            ShortcutStatus.Text = _shortcutNote
                ?? (!has ? "No shortcut yet. With one, Windows calls this AlbumWall instead of naming it after its exe, and you can pin it."
                    : current ? "In the Start Menu, and pointing at this copy."
                    : "There is a shortcut, but it points at a different copy of the program — one that was moved or rebuilt somewhere else.");
        }

        var v = typeof(PrefsWindow).Assembly.GetName().Version;
        AboutVersion.Text = $"{App.DisplayName} {v?.Major}.{v?.Minor}.{v?.Build}";
        AboutLibrary.Text = $"{_host.LibraryCounts}\n{_host.LibraryRootPath}";
    }

    /// What each way of separating the bars from the wall actually does, in the
    /// words Ground.cs uses to justify them.
    private static string AboutBars(int chrome) => Ground.Chromes[chrome].Name switch
    {
        "recede" => "The bars sink below the wall and lose their color, so the wall is the lit thing in the room.",
        "lift" => "The bars rise as a neutral panel, like a toolbar laid over the wall.",
        "ink" => "Near-black and fully neutral: the most separation, and the wall floats.",
        "warm" => "The bars keep your library's warmth, pushed well apart from the wall in lightness.",
        _ => ""
    };

    /// Puts the window where it was last time, if that place still exists —
    /// a monitor can be unplugged between runs — and otherwise over the main
    /// window, which is where someone who has just chosen Preferences is looking.
    public void Place(Window owner)
    {
        if (_host?.AppSettings is { PrefsX: { } x, PrefsY: { } y }
            && owner.Screens.ScreenFromPoint(new PixelPoint(x + 40, y + 20)) is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
            return;
        }
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}
