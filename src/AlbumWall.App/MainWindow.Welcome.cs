// AlbumWall - the first run: a welcome that asks where the music is.
//
// 2026-10-04: a machine with no library at all used to be given one, of its
// Music folder, and on a new machine that is usually empty - so the first thing
// anyone saw was "No music here yet", worded as a fault. Now there is no
// library until one is chosen (Settings.NeedsLibrary), and the window asks:
// a folder on this computer, or a server. Nothing on the disk is read until a
// button says so; "Look in the usual places" counts from directory listings
// and opens no file (Domain.MusicFinder).
//
// Everything chosen here goes through FirstLibraries, which is the one place
// a library-less app becomes one with a wall.

using Avalonia.Controls;
using Avalonia.Threading;

namespace AlbumWall.App;

public partial class MainWindow
{
    private CancellationTokenSource? _look;

    private void SetUpWelcome()
    {
        App.DrawWordmark(WelcomeWordmark);
        WelcomeServer.ShowHeading = false;
        WelcomeServer.ShowCancel = false;
        WelcomeServer.Finished += chosen => { if (chosen is { Count: > 0 }) FirstLibraries(chosen); };

        WelcomeChoose.Click += async (_, _) =>
        {
            if (await PickFolder(this, "Where is your music?") is { } path)
                FirstLibraries([FolderLibrary(path, null)]);
        };
        WelcomeLook.Click += async (_, _) => await LookInTheUsualPlaces();
        WelcomeUseFound.Click += (_, _) =>
        {
            var chosen = WelcomeFound.Children.OfType<CheckBox>()
                .Where(box => box.IsChecked == true && box.Tag is Domain.MusicFinder.Found)
                .Select(box => (Domain.MusicFinder.Found)box.Tag!)
                .Select(found => FolderLibrary(found.Path, found.Label))
                .ToList();
            if (chosen.Count > 0) FirstLibraries(chosen);
        };
    }

    /// The window with no library: the welcome, and nothing else on the wall.
    private void ShowWelcome()
    {
        StopWatching();
        _scan?.Cancel();
        _scanning = false;
        EmptyState.IsVisible = false;
        ScanState.IsVisible = false;
        WelcomeState.IsVisible = true;
        _counts = "";
        CountsText.Text = "";
        CountsDot.IsVisible = false;
        StatusText.Text = "";
        ShowLibraryName();
        Console.WriteLine("[welcome] no library yet: asking where the music is");
    }

    /// The first library or libraries, from whichever of the welcome's ways.
    /// The first one chosen goes on the wall; the rest are in the picker.
    private void FirstLibraries(IReadOnlyList<Library> chosen)
    {
        _look?.Cancel();
        var all = _settings.AllLibraries();
        foreach (var library in chosen)
        {
            all.Add(library);
            Console.WriteLine($"[library] added {library.Name} ({library.Root})");
        }
        _settings.CurrentLibrary = chosen[0].Id;
        _settings.NeedsLibrary = null;
        _settings.Save();
        WelcomeState.IsVisible = false;
        Console.WriteLine($"[welcome] answered: {chosen.Count} {(chosen.Count == 1 ? "library" : "libraries")}");
        ScanLibrary();
        _prefs?.Fill();
    }

    /// A folder library, its path left null when it is the platform's Music
    /// folder - which is what null has always meant - and named for what the
    /// welcome called it where it called it something.
    private static Library FolderLibrary(string path, string? label)
    {
        var isDefault = string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                                      Path.GetFullPath(Library.DefaultRoot).TrimEnd(Path.DirectorySeparatorChar),
                                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var library = Library.Folder(isDefault ? null : path);
        if (label is { Length: > 0 }) library.Name = label;
        return library;
    }

    /// Pressed, never unasked. Counts what is in each usual place, side by
    /// side, and offers what holds music, every one ticked.
    private async Task LookInTheUsualPlaces()
    {
        _look?.Cancel();
        var ct = (_look = new CancellationTokenSource()).Token;
        WelcomeLook.IsEnabled = false;
        WelcomeFound.Children.Clear();
        WelcomeFound.IsVisible = false;
        WelcomeUseFound.IsVisible = false;
        WelcomeLookStatus.Text = "Looking…";
        WelcomeLookStatus.IsVisible = true;

        IReadOnlyList<Domain.MusicFinder.Found> found;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { found = await Domain.MusicFinder.LookAsync(ct); }
        catch (OperationCanceledException) { return; }
        finally { WelcomeLook.IsEnabled = true; }
        Console.WriteLine($"[welcome] looked in the usual places in {sw.ElapsedMilliseconds} ms: "
                        + (found.Count == 0 ? "nothing" : string.Join(", ", found.Select(f => $"{f.Label} {f.Albums}/{f.Tracks}"))));

        if (found.Count == 0)
        {
            WelcomeLookStatus.Text = "No music in the usual places. Choose the folder it is in, or sign in to a server.";
            return;
        }

        WelcomeLookStatus.Text = found.Count == 1 ? "Found music in:" : "Found music in these. Each becomes a library of its own:";
        foreach (var f in found)
        {
            WelcomeFound.Children.Add(new CheckBox
            {
                IsChecked = true,
                Tag = f,
                FontSize = 13,
                Content = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock { Text = $"{f.Label}   about {Count(f.Albums, "album")}", FontSize = 13 },
                        new TextBlock { Text = f.Path, FontSize = 12, Classes = { "dim", "mono" },
                                        TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                                        TextTrimming = Avalonia.Media.TextTrimming.PathSegmentEllipsis },
                    },
                },
            });
        }
        WelcomeFound.IsVisible = true;
        WelcomeUseFound.IsVisible = true;
    }
}
