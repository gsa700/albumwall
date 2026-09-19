// AlbumWall — the Preferences window. See the note in the markup for the
// format, which the file-properties window is meant to follow.

using Avalonia;
using Avalonia.Controls;

namespace AlbumWall.App;

public partial class PrefsWindow : Window
{
    /// In the order they appear. About stays last.
    public enum Tab { Library, Startup, About }

    private readonly MainWindow? _host;

    /// True while the controls are being filled from the settings, which is not
    /// him changing them: without it, merely opening the window would write the
    /// defaults into his settings file.
    private bool _filling;

    /// For the XAML previewer only.
    public PrefsWindow() => InitializeComponent();

    public PrefsWindow(MainWindow host) : this()
    {
        _host = host;

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
        _filling = false;

        var v = typeof(PrefsWindow).Assembly.GetName().Version;
        AboutVersion.Text = $"AlbumWall {v?.Major}.{v?.Minor}.{v?.Build}";
        AboutLibrary.Text = $"{_host.LibraryCounts}\n{_host.LibraryRootPath}";
    }

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
