// AlbumWall — one library's settings, on a window of its own.
//
// Everything about ONE library: its name, where it is, reading it again,
// whether its files may be edited, the check for damage and what it found,
// and forgetting it. Opened by the Settings button on the library's line in
// Preferences > Library, which is only the list now ("move all the data after
// the separator to a per Library setting page including rescan and forget",
// 2026-10-10). One window, shown for one library at a time; Settings on
// another line refills it.
//
// The Properties format: light, a fixed width, the system's own title bar
// and no Close button, settings written straight through with no OK. Its
// height is its content's, worked out once per library, since a server's
// page is half a folder's.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace AlbumWall.App;

public sealed class LibrarySettingsWindow : Window
{
    private readonly MainWindow _host;
    private readonly PrefsWindow _prefs;
    private string _id = "";

    private static readonly IBrush Ink = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#1D1D20"));
    private static readonly IBrush DimInk = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#62626A"));
    private static readonly IBrush Damaged = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#B3261E"));

    /// The choices for how often every file is read again, in days; null is
    /// the check's own month and 0 is never (new and changed files only).
    private static readonly (string Label, int? Days)[] Intervals =
    [
        ("Every week", 7),
        ("Every month", null),
        ("Every three months", 90),
        ("Only new and changed files", 0),
    ];

    public LibrarySettingsWindow(MainWindow host, PrefsWindow prefs)
    {
        _host = host;
        _prefs = prefs;

        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AlbumWall/Assets/app.ico")));
        // A FIXED size, set from here, never SizeToContent: see StatisticsWindow
        // for the Wayland half-width window that ruled it out. The height is
        // the page's, measured after it is in the tree (FitHeight).
        Width = 560;
        Height = 420;
        CanResize = false;
        ShowInTaskbar = false;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Foreground = Ink;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // The light sheet, as Properties and Preferences have it.
        Resources["SheetBg"] = SolidColorBrush.Parse("#E4E4E2");
        Resources["SheetField"] = SolidColorBrush.Parse("#F6F6F4");
        Resources["SheetEdge"] = SolidColorBrush.Parse("#C2C2C6");
        Avalonia.Styling.Style Restyle<T>(Func<Avalonia.Styling.Selector?, Avalonia.Styling.Selector> which,
                                          params (AvaloniaProperty Property, object Value)[] set) where T : Control
        {
            var style = new Avalonia.Styling.Style(which);
            foreach (var (property, value) in set) style.Setters.Add(new Avalonia.Styling.Setter(property, value));
            return style;
        }
        Styles.Add(Restyle<TextBlock>(x => x.OfType<TextBlock>().Class("dim"), (TextBlock.ForegroundProperty, DimInk)));
        Styles.Add(Restyle<Button>(x => x.OfType<Button>().Class("panel"),
            (Button.BackgroundProperty, SolidColorBrush.Parse("#12000000")),
            (Button.ForegroundProperty, Ink),
            (Button.BorderBrushProperty, SolidColorBrush.Parse("#38000000"))));
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetBg");

        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        RememberPlace(host.AppSettings);
        Opened += (_, _) => FitHeight();
    }

    private Control? _page;

    private void FitHeight()
    {
        if (_page is null) return;
        _page.Measure(new Size(Width, double.PositiveInfinity));
        var want = Math.Ceiling(_page.DesiredSize.Height);
        if (Screens.ScreenFromWindow(this) is { } screen)
            want = Math.Min(want, screen.WorkingArea.Height / screen.Scaling - 48);
        if (want > 0 && Math.Abs(Height - want) > 0.5) Height = want;
    }

    /// Opens where it was last closed, if that place is still on a screen, and
    /// otherwise centred on Preferences. Not on native Wayland, which neither
    /// tells an app where a window is nor lets it choose.
    private void RememberPlace(Settings settings)
    {
        if (Program.NativeWayland) return;
        if (settings is { LibraryX: { } x, LibraryY: { } y }
            && Screens.ScreenFromPoint(new PixelPoint(x + 40, y + 20)) is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        Closing += (_, _) =>
        {
            settings.LibraryX = Position.X;
            settings.LibraryY = Position.Y;
            settings.Save();
        };
    }

    /// Shows the window for a library, refilling it if it is already open.
    public void ShowFor(string id)
    {
        _id = id;
        _rescanResult = "";
        Fill();
        if (!IsVisible) Show(_prefs);
        else Activate();
    }

    private Library? Library => _host.Libraries.FirstOrDefault(l => l.Id == _id);

    /// Rebuilt whole, from what is true now. Called by Preferences after
    /// anything changes a library, as its own tab is.
    public void Fill()
    {
        if (Library is not { } library) { if (IsVisible) Close(); return; }
        Title = $"{App.DisplayName} — {library.Name}: settings";
        _page = Build(library);
        Content = _page;
        // The height is the content's; a change in what is on the page (the
        // damaged list arriving) is measured again rather than clipped.
        Avalonia.Threading.Dispatcher.UIThread.Post(FitHeight, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ---- what a scan has to say, kept across rebuilds --------------------

    private Border? _scanRow, _scanFill;
    private TextBlock? _scanDetail, _rescanResultBlock;
    private Button? _rescan;
    private (double Fraction, string Detail, bool Mine)? _scan;
    private string _rescanResult = "";

    public void ShowScan(double fraction, string detail, bool mine)
    {
        _scan = (fraction, detail, mine);
        ApplyScan();
    }

    public void ScanEnded()
    {
        _scan = null;
        ApplyScan();
    }

    public void ShowRescanResult(string text)
    {
        _rescanResult = text;
        ApplyScan();
    }

    private void ApplyScan()
    {
        if (_scanRow is null || _scanFill is null || _scanDetail is null) return;
        _scanRow.IsVisible = _scan is not null;
        if (_scan is { } scan)
        {
            _scanFill.Width = scan.Fraction * _scanRow.Bounds.Width;
            _scanDetail.Text = scan.Detail;
        }
        else _scanFill.Width = 0;
        if (_rescan is not null)
        {
            _rescan.Content = _scan is { Mine: true } ? "Stop" : "Rescan";
            _rescan.IsEnabled = _scan is null || _scan.Value.Mine;    // not while another scan has the floor
        }
        if (_rescanResultBlock is not null)
        {
            _rescanResultBlock.Text = _rescanResult;
            _rescanResultBlock.IsVisible = _rescanResult.Length > 0;
        }
    }

    // ---- the page --------------------------------------------------------

    private Control Build(Library library)
    {
        var id = library.Id;
        var onWall = id == _host.CurrentLibrary.Id;
        var server = library.IsNavidrome;
        var page = new StackPanel { Spacing = 12, Margin = new Thickness(18, 14, 18, 18) };

        page.Children.Add(new TextBlock
        {
            Text = onWall ? $"{library.Name}  — on the wall" : library.Name,
            FontSize = 15, FontWeight = FontWeight.SemiBold,
        });

        // The name. A rename lands when he leaves the box or presses Enter,
        // not on every keystroke: the picker would otherwise show each letter.
        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        nameRow.Children.Add(new TextBlock { Text = "Name", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBox { Text = library.Name, FontSize = 13, MaxLength = 60 };
        void Rename()
        {
            var wanted = (name.Text ?? "").Trim();
            if (wanted.Length == 0 || wanted == library.Name) return;
            _host.RenameLibrary(id, wanted);
            _prefs.Fill();
        }
        name.LostFocus += (_, _) => Rename();
        name.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Rename(); };
        Grid.SetColumn(name, 1);
        nameRow.Children.Add(name);
        page.Children.Add(nameRow);

        var where = new TextBlock { Text = library.Root, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        where.Classes.Add("mono");
        var whereBox = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(13, 11), Child = where };
        whereBox[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetField");
        page.Children.Add(whereBox);

        page.Children.Add(Dim(server
            ? "The albums are the server's, and so is the art. The files are played as they are, and nothing on the server is changed. Its album list and covers are kept on this computer so the wall opens at once."
            : "Everything under this folder is scanned. Album art is taken from the tags, or from a cover file beside the tracks if there is none."));

        if (server)
        {
            if (library.Server is { } address && Domain.Navidrome.IsPlainHttp(address))
            {
                var plain = Dim(Domain.Navidrome.PlainHttpShort);
                ToolTip.SetTip(plain, Domain.Navidrome.PlainHttpWarning);
                page.Children.Add(plain);
            }
        }
        else if (onWall)
        {
            // The folder and the rescan act on the library ON THE WALL, because
            // a scan only ever reads that one.
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
            var choose = Panel("Choose folder…");
            choose.Click += async (_, _) => { await _host.ChooseLibraryFolder(this); _prefs.Fill(); };
            var mine = Panel("Use my Music folder");
            mine.Click += (_, _) => { _host.UseDefaultLibrary(); _prefs.Fill(); };
            _rescan = Panel(_host.RescanRunning ? "Stop" : "Rescan");
            ToolTip.SetTip(_rescan, RescanTip(library));
            _rescan.Click += (_, _) =>
            {
                if (_host.RescanRunning) _host.StopRescan();
                else { _rescanResult = ""; _host.Rescan(); }
                _prefs.Fill();
            };
            buttons.Children.Add(choose);
            buttons.Children.Add(mine);
            buttons.Children.Add(_rescan);
            page.Children.Add(buttons);

            // Its progress and result, under the button that started them.
            _scanFill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Width = 0, Opacity = 0.72,
                                     HorizontalAlignment = HorizontalAlignment.Left, Background = Ink };
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Child = _scanFill };
            track[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetEdge");
            _scanDetail = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            _scanDetail.Classes.Add("dim"); _scanDetail.Classes.Add("mono");
            var scanRow = new StackPanel { Spacing = 7, IsVisible = false };
            scanRow.Children.Add(track);
            scanRow.Children.Add(_scanDetail);
            _scanRow = new Border { Child = scanRow, IsVisible = false };
            page.Children.Add(_scanRow);
            _rescanResultBlock = Dim("");
            _rescanResultBlock.IsVisible = false;
            page.Children.Add(_rescanResultBlock);
        }
        else
        {
            _rescan = null; _scanRow = null; _scanFill = null; _scanDetail = null; _rescanResultBlock = null;
            page.Children.Add(Dim("Its folder can be changed, and it can be read again, once it is on the wall: Switch to, in Preferences."));
        }

        if (!server)
        {
            page.Children.Add(Rule());

            var editable = new CheckBox { Content = "Allow editing (lyrics) in this library", FontSize = 13, IsChecked = library.Editable };
            ToolTip.SetTip(editable,
                "Lets Properties change the files here. Leave it off for a copy that is replaced from "
                + "somewhere else, such as a nightly mirror: an edit made there would be lost.");
            editable.IsCheckedChanged += (_, _) => _host.SetLibraryEditable(id, editable.IsChecked == true);
            page.Children.Add(editable);

            // The integrity check (MainWindow.Integrity.cs), offered where the
            // last scan found FLAC files, and before the first scan has said.
            var hasFlac = _host.StoredStatistics(library)?.HasFlac ?? true;
            if (hasFlac)
            {
                var available = Domain.FlacIntegrity.Available;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
                var integrity = new CheckBox
                {
                    Content = "Check the audio for damage", FontSize = 13,
                    IsChecked = library.CheckIntegrity, IsEnabled = available, VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(integrity, available
                    ? "Decodes each FLAC file in the background and compares it with the checksum stored "
                      + "inside it, as `flac -t` does: new and changed files soon, and every file again as "
                      + "often as you choose, one at a time at low priority, and only while AlbumWall is open. "
                      + "It only reads; it never changes a file. MP3 and AAC carry no checksum, so they are not checked."
                    : "Needs libFLAC, which this system does not have.");
                row.Children.Add(integrity);

                var every = new ComboBox
                {
                    ItemsSource = Intervals.Select(i => i.Label).ToList(),
                    SelectedIndex = Math.Max(0, Array.FindIndex(Intervals, i => i.Days == library.IntegrityDays)),
                    IsEnabled = available && library.CheckIntegrity, FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200,
                };
                ToolTip.SetTip(every, "How often every file is read again. New and changed files are always read soon.");
                every.SelectionChanged += (_, _) =>
                {
                    if (every.SelectedIndex >= 0) _host.SetLibraryIntegrityDays(id, Intervals[every.SelectedIndex].Days);
                };
                Grid.SetColumn(every, 1);
                row.Children.Add(every);
                page.Children.Add(row);

                integrity.IsCheckedChanged += (_, _) =>
                {
                    _host.SetLibraryIntegrity(id, integrity.IsChecked == true);
                    every.IsEnabled = available && integrity.IsChecked == true;
                    _prefs.Fill();
                };

                if (library.CheckIntegrity && available)
                {
                    var line = Dim(_host.IntegrityLine(library), 12);
                    line.Margin = new Thickness(28, 0, 0, 0);
                    page.Children.Add(line);

                    // What it found, in a box of its own height so a bad batch
                    // cannot stretch the window: the list scrolls, the page does not.
                    var problems = _host.IntegrityProblems(library);
                    if (problems.Count > 0)
                    {
                        var found = new StackPanel { Spacing = 2 };
                        foreach (var (path, problem) in problems.Take(8))
                        {
                            var bad = new TextBlock { Text = $"{path}: {problem}", FontSize = 12, Foreground = Damaged, TextWrapping = TextWrapping.Wrap };
                            bad.Classes.Add("mono");
                            found.Children.Add(bad);
                        }
                        found.Children.Add(Dim((problems.Count > 8 ? $"and {problems.Count - 8} more. " : "")
                                             + $"Every one is in {MainWindow.IntegrityLogPath}", 12));
                        page.Children.Add(new ScrollViewer
                        {
                            Content = found, MaxHeight = 120, Margin = new Thickness(28, 0, 0, 0),
                            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        });
                    }
                }
            }
            else
                page.Children.Add(Dim("No FLAC files here as of the last scan, so there is nothing for the check for damage to read.", 12));
        }

        // Forgetting, last: the one thing here that removes the library. Not
        // for the only one, since the wall has to show something.
        if (_host.Libraries.Count > 1)
        {
            page.Children.Add(Rule());
            var forget = Panel("Forget this library");
            ToolTip.SetTip(forget, server
                ? "Removes it from the list, with its sign-in and the covers kept here. Nothing on the server is touched."
                : "Removes it from the list. Nothing on disk is touched.");
            forget.HorizontalAlignment = HorizontalAlignment.Left;
            forget.Click += (_, _) => { _host.RemoveLibrary(id); _prefs.Fill(); Close(); };
            page.Children.Add(forget);
        }

        ApplyScan();
        return page;
    }

    private static string RescanTip(Library library)
    {
        var every = library.Tracks is { } n ? $"every one of its {n:N0} tracks" : "every track";
        if (OperatingSystem.IsWindows())
            return $"Reads {every} again. Only needed if a tag edit hasn't shown up. It can take several "
                 + "minutes while Windows Security checks each file. Keep listening; stop it any time.";
        return $"Reads {every} again. Only needed if a tag edit hasn't shown up: files that look "
             + "unchanged are not read again. Keep listening; stop it any time.";
    }

    private static Button Panel(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add("panel");
        return button;
    }

    private static TextBlock Dim(string text, double size = 13)
    {
        var block = new TextBlock { Text = text, FontSize = size, LineHeight = size + 7, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("dim");
        return block;
    }

    private static Border Rule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 0) };
        rule[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetEdge");
        return rule;
    }
}
