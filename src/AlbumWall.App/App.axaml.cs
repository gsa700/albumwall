using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AlbumWall.App;

public partial class App : Application
{
    /// The name the app goes by ON SCREEN, in one place: the header in the top
    /// bar, both window titles, the About tab, and what it tells the desktop's
    /// media controls it is called.
    ///
    /// SETTLED 2026-09-19: "let's mark this settled: AlbumWall is the name". It
    /// had been the working name from the start and the front-runner for a
    /// while. What decided it was seeing the rivals — Shieldwall, Songhoard,
    /// Discotheca, Musivum, all of them good — and finding it still held up: it
    /// says what the thing is, it sorts near the top of a list, and it asks
    /// nobody to know Norse or Latin. (It is also better than it looks: an
    /// *album* was a whitened board the Romans hung on a public wall for
    /// everyone to read at once.)
    ///
    /// This constant exists because for an afternoon the header said Shieldwall,
    /// to see how a candidate looked above a real wall of covers. Trying one on
    /// cost a single string, because nothing else hangs off this — the folders,
    /// assemblies, namespaces, settings folder and bus name say albumwall on
    /// their own account, and now always will.
    public const string DisplayName = "AlbumWall";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}