// AlbumWall — the Preferences window. See the note in the markup for the
// format, which the file-properties window is meant to follow.

using Avalonia;
using Avalonia.Controls;

namespace AlbumWall.App;

public partial class PrefsWindow : Window
{
    /// In the order they appear. About stays last.
    public enum Tab { Library, Statistics, Startup, Appearance, Help, About }

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

        // A TAB IS CALLED WHAT ITS HEADER SAYS. With the family's tab template the
        // automation name falls through to the CONTENT, so every tab here announced
        // itself to a screen reader as "Avalonia.Controls.StackPanel" - five of them
        // in a row. Found by the updater's test, which could not find "About" to
        // press. Whatever reads the screen for someone gets the header.
        foreach (var item in Tabs.Items.OfType<TabItem>())
            if (item.Header is string header)
                Avalonia.Automation.AutomationProperties.SetName(item, header);
        WhenItOpens.Text = $"When {App.DisplayName} opens";

        ChooseFolder.Click += async (_, _) => { await host.ChooseLibraryFolder(this); Fill(); };
        UseDefault.Click += (_, _) => { host.UseDefaultLibrary(); Fill(); };
        AddFolderLibrary.Click += async (_, _) => { await host.AddFolderLibrary(this); Fill(); };
        AddNavidromeLibrary.Click += async (_, _) => { await host.AddNavidromeLibrary(this); Fill(); };

        // A rename lands when he leaves the box or presses Enter, not on every
        // keystroke: the picker would otherwise show each letter as it came.
        LibraryName.LostFocus += (_, _) => RenameCurrent();
        LibraryName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) RenameCurrent(); };

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
        CheckUpdates.IsCheckedChanged += (_, _) =>
        {
            if (_filling) return;
            host.AppSettings.CheckForUpdates = CheckUpdates.IsChecked == true;
            host.AppSettings.Save();
        };

        UpdateButton.Click += async (_, _) => await OnUpdateButton();
        UpdateNotes.Click += async (_, _) =>
        {
            var url = App.LatestUpdate?.ReleaseUrl ?? App.ProjectUrl + "/releases/latest";
            try { await Launcher.LaunchUriAsync(new Uri(url)); }
            catch (Exception ex) { Console.WriteLine($"[about] could not open {url}: {ex.Message}"); }
        };

        // Applies as it moves, like the rest of this tab: the point of the option
        // is to see which way round you prefer, and that cannot be done from a
        // checkbox that only takes effect next launch.
        TransportTop.IsCheckedChanged += (_, _) =>
        {
            if (_filling) return;
            host.TransportAtTop = TransportTop.IsChecked == true;
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
        // Everything the tab controls, not just the colors: once the layout
        // option moved in here, a "defaults" button that quietly skipped it would
        // be lying about its own tab. His ask: "can we fold that into the window
        // appearance defaults?"
        ResetColors.Click += (_, _) =>
        {
            host.ColorLightness = Ground.DefaultLightness;
            host.ColorTint = Ground.DefaultTint;
            host.ColorChrome = Ground.DefaultChrome;
            host.TransportAtTop = false;
            Fill();
        };

        InstallButton.Click += async (_, _) =>
        {
            if (_host is null) return;
            if (Install.InstallService.Mode == Install.InstallMode.Installed)
                await App.RunUninstallAsync(_host, asked: true);
            else
                await App.OfferInstallAsync(_host);
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
        Opened += (_, _) => FitToTallestTab();
    }

    public void Select(Tab tab) => Tabs.SelectedIndex = (int)tab;

    /// ONE HEIGHT FOR EVERY TAB, the tallest one's, worked out as the window
    /// opens: "the Help page is scrolling. Can we have the window auto size
    /// length to the longest content automagically?" (2026-09-28). It was a
    /// fixed 700, chosen by eye for tabs that have grown since, and Help and
    /// Statistics depend on what is in them - the library's formats, the keys.
    ///
    /// Still one size for all of them, so nothing jumps between tabs. Only a
    /// screen too short for the tallest tab leaves anything to scroll.
    ///
    /// Only the selected tab is in the visual tree, and a control outside it has
    /// no styles or templates to measure with, so each tab is selected in turn
    /// and its content host measured with unlimited height. All of it happens
    /// inside one dispatcher job, and the renderer only ever sees the tab that
    /// was selected before. Done once, as it opens: doing it again while it is
    /// in use would take the focus out of whatever box he was typing in.
    private void FitToTallestTab()
    {
        var selected = Tabs.SelectedIndex;
        double tallest = 0, chrome = -1;
        var tabs = Tabs.Items.OfType<TabItem>().ToList();
        for (var i = 0; i < tabs.Count; i++)
        {
            Tabs.SelectedIndex = i;
            UpdateLayout();
            if (tabs[i].Content is not Control content || Avalonia.VisualTree.VisualExtensions.GetVisualParent(content) is not Control host)
                continue;
            if (chrome < 0) chrome = ClientSize.Height - host.Bounds.Height;
            host.Measure(new Size(host.Bounds.Width, double.PositiveInfinity));
            tallest = Math.Max(tallest, host.DesiredSize.Height);
            host.InvalidateMeasure();
        }
        Tabs.SelectedIndex = selected;
        if (chrome < 0 || tallest <= 0) return;

        var want = Math.Ceiling(tallest + chrome);
        if (Screens.ScreenFromWindow(this) is { } screen)
            want = Math.Min(want, screen.WorkingArea.Height / screen.Scaling - 48);
        Console.WriteLine($"[prefs] tallest tab {tallest:0} + {chrome:0} around it -> {want:0} high (was {Height:0})");
        Height = want;
        UpdateLayout();
    }

    /// What is in a tab can grow while the window is open - update notes on
    /// About, a scan's progress under the library list - and a tab that is not
    /// a scroller would be cut off at the foot. So after anything is filled in,
    /// the tab on screen is measured again and the window GROWS if it must.
    /// Never shrinks (a window jumping smaller as a line disappears is worse
    /// than some spare room), and never selects another tab, so the focus stays
    /// where it was.
    private void GrowForSelectedTab()
    {
        if (Tabs.SelectedItem is not TabItem { Content: Control content }
            || Avalonia.VisualTree.VisualExtensions.GetVisualParent(content) is not Control host
            || host.Bounds.Height <= 0) return;
        host.Measure(new Size(host.Bounds.Width, double.PositiveInfinity));
        var want = Math.Ceiling(host.DesiredSize.Height + ClientSize.Height - host.Bounds.Height);
        host.InvalidateMeasure();
        if (Screens.ScreenFromWindow(this) is { } screen)
            want = Math.Min(want, screen.WorkingArea.Height / screen.Scaling - 48);
        if (want <= Height + 0.5) return;
        Console.WriteLine($"[prefs] {((TabItem)Tabs.SelectedItem).Header} grew: {Height:0} -> {want:0} high");
        Height = want;
    }

    // ------------------------------------------------------------------ updates

    private bool _updating;
    private string? _updateNote;

    /// Where things stand, from whatever is known: the launch-time check may have
    /// answered already, and the last update may have failed to apply.
    private void FillUpdate()
    {
        UpdateSection.IsVisible = Install.UpdateService.CanUpdate;
        if (!UpdateSection.IsVisible || _updating) return;

        var have = Install.UpdateService.CurrentVersion;
        var info = App.LatestUpdate;

        UpdateNotes.IsVisible = info is { UpdateAvailable: true };
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = info is { UpdateAvailable: true, AssetUrl: not null }
            ? $"Update to {info.LatestTag.TrimStart('v', 'V')} and restart"
            : "Check for updates";

        UpdateStatus.Text = _updateNote
            ?? (App.LastUpdateFailed ? $"The last update could not be put in place, so this is still {have}. Try it again."
              : info is null ? $"This is version {have}."
              : info.Error is { } error ? error
              : info.NothingPublished ? $"No release has been published yet. This is version {have}."
              : !info.UpdateAvailable ? $"This is the latest version, {have}."
              : info.AssetUrl is null ? $"{info.LatestTag} is out, but it has no build for this kind of computer."
              : $"Version {info.LatestTag.TrimStart('v', 'V')} is available. This is {have}.");
    }

    private async Task OnUpdateButton()
    {
        if (_updating || _host is null) return;
        _updateNote = null;

        // Nothing newer known: this press is a check.
        if (App.LatestUpdate is not { UpdateAvailable: true, AssetUrl: not null })
        {
            UpdateButton.IsEnabled = false;
            UpdateStatus.Text = "Looking…";
            App.LatestUpdate = await Install.UpdateService.CheckAsync();
            _host.ShowUpdateDot(App.LatestUpdate.UpdateAvailable);
            FillUpdate();
            return;
        }

        // Something newer known: this press fetches it, checks it, and restarts into it.
        _updating = true;
        UpdateButton.IsEnabled = false;
        UpdateNotes.IsVisible = false;
        UpdateBar.IsVisible = true;
        try
        {
            var progress = new Progress<double>(f =>
            {
                UpdateFill.Width = f * UpdateBar.Bounds.Width;
                UpdateStatus.Text = $"Downloading {App.LatestUpdate.LatestTag.TrimStart('v', 'V')}… {f:P0}";
            });
            var staged = await Install.UpdateService.DownloadAndStageAsync(App.LatestUpdate, progress);

            UpdateStatus.Text = "Checked. Restarting into the new version…";
            Install.UpdateService.ApplyAndRestart(staged);
            App.ExitForUpdate(_host);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[update] failed: {ex.Message}");
            _updating = false;
            UpdateBar.IsVisible = false;
            UpdateFill.Width = 0;
            _updateNote = ex.Message;
            FillUpdate();
        }
    }

    /// The About tab's links: what each is called, what is at the other end in
    /// plain words, and where it goes. A null address opens the notices window,
    /// which is inside the program and needs no network.
    private void FillAboutLinks()
    {
        (string Name, string WhatFor, string? Url)[] links =
        [
            ("Source code",
             $"Everything {App.DisplayName} is made from, and its history. On GitHub.",
             App.ProjectUrl),
            ("The license",
             "GNU GPL version 3 or later: use it, share it, change it, and pass the same freedom on.",
             "https://www.gnu.org/licenses/gpl-3.0.html"),
            ("What's inside",
             "Every library built into this program: what it does here, its license, where its source is.",
             null),
            ("The audio engine",
             "The libmpv this plays through, built by this project: its releases, and the source they were built from.",
             App.ProjectUrl + "/releases?q=libmpv"),
            ("Report a problem",
             "Something wrong, or something missing. Opens the project's issue list.",
             App.ProjectUrl + "/issues")
        ];

        foreach (var (name, whatFor, url) in links)
        {
            var link = new Button
            {
                Classes = { "link" },
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Content = new TextBlock
                {
                    Text = name, FontSize = 13,
                    TextDecorations = Avalonia.Media.TextDecorations.Underline
                }
            };
            ToolTip.SetTip(link, url ?? "Opens here, in a window of its own");
            link.Click += async (_, _) =>
            {
                if (url is null) { NoticesWindow.ShowFrom(this); return; }
                try { await Launcher.LaunchUriAsync(new Uri(url)); }
                catch (Exception ex) { Console.WriteLine($"[about] could not open {url}: {ex.Message}"); }
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("132,*") };
            row.Children.Add(link);
            var about = new TextBlock
            {
                Text = whatFor, FontSize = 13, LineHeight = 19, Classes = { "dim" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            Grid.SetColumn(about, 1);
            row.Children.Add(about);
            AboutLinks.Children.Add(row);
        }
    }

    /// A scan is running and has something to show. `mine` is the one started
    /// from the button here, which is the one the button can stop.
    public void ShowScan(double fraction, string detail, bool mine)
    {
        ScanRow.IsVisible = true;
        ScanFill.Width = fraction * ScanRow.Bounds.Width;
        ScanDetail.Text = detail;
        if (_rescan is not null)
        {
            _rescan.Content = mine ? "Stop" : "Rescan";
            _rescan.IsEnabled = mine;       // not while another scan has the floor
        }
    }

    public void ScanEnded()
    {
        ScanRow.IsVisible = false;
        ScanFill.Width = 0;
        if (_rescan is not null)
        {
            _rescan.Content = "Rescan";
            _rescan.IsEnabled = true;
        }
    }

    public void ShowRescanResult(string text)
    {
        RescanResult.Text = text;
        RescanResult.IsVisible = text.Length > 0;
    }

    /// The Rescan button on the row of the library on the wall. Rebuilt with
    /// the rows, so whatever a scan last said about it is said again then.
    private Button? _rescan;

    /// What Rescan does, in the terms he will experience it: when to press it,
    /// what it costs, and that it can be stopped. Three stories: a server, a
    /// folder on Windows, where it is minutes, and a folder anywhere else.
    private string RescanTip()
    {
        var n = _host?.LibraryTrackCount ?? 0;
        var every = n > 0 ? $"all {n:N0} tracks" : "every track";
        if (_host?.CurrentLibrary.IsNavidrome == true)
            return "Asks the server for its albums again. Rarely needed: it is asked every time this "
                 + "library is opened. If something is missing, the server may not have scanned it yet.";
        if (OperatingSystem.IsWindows())
            return $"Reads {every} again. Only needed if a tag edit hasn't shown up. It can take several "
                 + "minutes while Windows Security checks each file. Keep listening; stop it any time.";
        return $"Reads {every} again. Only needed if a tag edit hasn't shown up: files that look "
             + "unchanged are not read again. Keep listening; stop it any time.";
    }

    private void RenameCurrent()
    {
        if (_host is null || _filling) return;
        _host.RenameLibrary(_host.CurrentLibrary.Id, LibraryName.Text ?? "");
        FillLibraries();
    }

    /// One row per library: its name and where it is; Rescan on the one on the
    /// wall, Switch to on the others, and Forget on all of them but the last,
    /// since the wall has to show something. Rescan reads the library ON THE
    /// WALL, so that is the only row it goes on: another is switched to first.
    ///
    /// "Switch to" was "Show" until 2026-09-28: "Clicking isn't going to show
    /// the library it's going to Load it and switch to it." It is the words the
    /// tab already uses ("Switch between them with the name in the bottom
    /// bar"), and it promises nothing heavy, because on Windows the index
    /// brings a known library up in about a second.
    private static readonly Avalonia.Media.IBrush Damaged = Avalonia.Media.Brush.Parse("#E5534B");

    private void FillLibraries()
    {
        if (_host is null) return;
        var current = _host.CurrentLibrary;
        var all = _host.Libraries;

        ThisLibrary.Text = all.Count > 1 ? $"This library: {current.Name}" : "This library";
        if (!LibraryName.IsFocused) LibraryName.Text = current.Name;

        LibraryList.Children.Clear();
        _rescan = null;
        ThisLibraryPanel.IsVisible = all.Count > 0;
        if (all.Count == 0)
        {
            var none = new TextBlock
            {
                Text = "No library yet. Add a folder or a Navidrome server, or answer the welcome in the main window.",
                FontSize = 13, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            none.Classes.Add("dim");
            LibraryList.Children.Add(none);
        }
        foreach (var library in all)
        {
            var id = library.Id;
            var shown = id == current.Id;

            var words = new StackPanel { Spacing = 2 };
            words.Children.Add(new TextBlock
            {
                Text = shown ? $"{library.Name}  \u2014 on the wall" : library.Name,
                FontSize = 13,
                FontWeight = shown ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
            });
            var where = new TextBlock
            {
                Text = library.Root,
                FontSize = 12,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            where.Classes.Add("dim");
            where.Classes.Add("mono");
            words.Children.Add(where);
            if (library.IsFolder)
            {
                var editable = new CheckBox
                {
                    Content = "Allow editing (lyrics) in this library", FontSize = 12, IsChecked = library.Editable,
                    Margin = new Avalonia.Thickness(0, 2, 0, 0),
                };
                Avalonia.Controls.ToolTip.SetTip(editable,
                    "Lets Properties change the files here. Leave it off for a copy that is replaced from "
                    + "somewhere else, such as a nightly mirror: an edit made there would be lost.");
                editable.IsCheckedChanged += (_, _) => _host.SetLibraryEditable(id, editable.IsChecked == true);
                words.Children.Add(editable);

                // The integrity check (MainWindow.Integrity.cs). Greyed with the
                // reason where there is no libFLAC, not left off.
                var available = Domain.FlacIntegrity.Available;
                var integrity = new CheckBox
                {
                    Content = "Check the audio for damage now and then", FontSize = 12,
                    IsChecked = library.CheckIntegrity, IsEnabled = available,
                };
                Avalonia.Controls.ToolTip.SetTip(integrity, available
                    ? "Decodes each FLAC file in the background and compares it with the checksum stored "
                      + "inside it, as `flac -t` does: new and changed files soon, every file again once a "
                      + "month, one at a time at low priority, and only while AlbumWall is open. It only "
                      + "reads; it never changes a file. MP3 and AAC carry no checksum, so they are not checked."
                    : "Needs libFLAC, which this system does not have.");
                integrity.IsCheckedChanged += (_, _) => { _host.SetLibraryIntegrity(id, integrity.IsChecked == true); Fill(); };
                words.Children.Add(integrity);
                if (library.CheckIntegrity && available)
                {
                    var line = new TextBlock
                    {
                        Text = _host.IntegrityLine(library), FontSize = 12,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(28, 0, 0, 0),
                    };
                    line.Classes.Add("dim");
                    words.Children.Add(line);

                    var problems = _host.IntegrityProblems(library);
                    foreach (var (path, problem) in problems.Take(8))
                    {
                        var bad = new TextBlock
                        {
                            Text = $"{path}: {problem}", FontSize = 12, Foreground = Damaged,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(28, 0, 0, 0),
                        };
                        bad.Classes.Add("mono");
                        words.Children.Add(bad);
                    }
                    if (problems.Count > 0)
                    {
                        var more = new TextBlock
                        {
                            Text = (problems.Count > 8 ? $"and {problems.Count - 8} more. " : "")
                                 + $"Every one is in {MainWindow.IntegrityLogPath}",
                            FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Margin = new Avalonia.Thickness(28, 0, 0, 0),
                        };
                        more.Classes.Add("dim");
                        words.Children.Add(more);
                    }
                }
            }

            var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 7,
                                           VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            if (shown)
            {
                var running = _host.RescanRunning;
                var rescan = new Button { Content = running ? "Stop" : "Rescan" };
                rescan.Classes.Add("panel");
                Avalonia.Controls.ToolTip.SetTip(rescan, RescanTip());
                rescan.Click += (_, _) =>
                {
                    if (_host.RescanRunning) _host.StopRescan();
                    else { ShowRescanResult(""); _host.Rescan(); }
                    Fill();
                };
                buttons.Children.Add(rescan);
                _rescan = rescan;
            }
            else
            {
                var switchTo = new Button { Content = "Switch to" };
                switchTo.Classes.Add("panel");
                Avalonia.Controls.ToolTip.SetTip(switchTo, "Puts this library on the wall. The music keeps playing.");
                switchTo.Click += (_, _) => { _host.SwitchLibrary(id); Fill(); };
                buttons.Children.Add(switchTo);
            }
            if (all.Count > 1)
            {
                var forget = new Button { Content = "Forget" };
                forget.Classes.Add("panel");
                Avalonia.Controls.ToolTip.SetTip(forget, library.IsNavidrome
                    ? "Removes it from this list, with its sign-in and the covers kept here. Nothing on the server is touched."
                    : "Removes it from this list. Nothing on disk is touched.");
                forget.Click += (_, _) => { _host.RemoveLibrary(id); Fill(); };
                buttons.Children.Add(forget);
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            row.Children.Add(words);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);

            var border = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(13, 9),
                Child = row,
            };
            border[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetBg");
            LibraryList.Children.Add(border);
        }
    }

    /// Re-reads everything shown. Called when it opens, after anything here
    /// changes the library, and by the main window when a scan finishes — the
    /// counts on the About tab are only as good as the last scan.
    public void Fill()
    {
        if (_host is null) return;
        if (IsVisible) Avalonia.Threading.Dispatcher.UIThread.Post(GrowForSelectedTab, Avalonia.Threading.DispatcherPriority.Background);

        FillStatistics();

        _filling = true;
        FillLibraries();
        LibraryPath.Text = _host.LibraryRootPath;
        var server = _host.CurrentLibrary.IsNavidrome;
        FolderButtons.IsVisible = !server;
        LibraryAbout.Text = server
            ? "The albums are the server's, and so is the art. The files are played as they are, and nothing on the server is changed. Its album list and covers are kept on this computer so the wall opens at once."
            : "Everything under this folder is scanned. Album art is taken from the tags, or from a cover file beside the tracks if there is none.";

        // Resume is on unless turned off; auto-play is off unless turned on.
        Resume.IsChecked = _host.AppSettings.ResumeSession != false;
        AutoPlay.IsChecked = _host.AppSettings.AutoPlay == true;
        AutoPlay.IsEnabled = Resume.IsChecked == true;
        CheckUpdates.IsChecked = _host.AppSettings.CheckForUpdates != false;
        TransportTop.IsChecked = _host.TransportAtTop;
        Light.Value = _host.ColorLightness;
        Tint.Value = _host.ColorTint;
        Bars.SelectedIndex = _host.ColorChrome;
        _filling = false;

        // The color it found, shown at a strength nobody would want as a
        // background — this is the hue itself, so he can see what the sliders
        // are working with.
        var hue = _host.ColorHue;
        HueSwatch.Background = new Avalonia.Media.SolidColorBrush(
            new Avalonia.Media.HslColor(1, hue, 0.60, 0.52).ToRgb());
        if (_host.ColorIsFromLibrary)
        {
            HueLine.Text = $"Your collection leans {HueName(hue)}";
            HueAbout.Text = $"{App.DisplayName} has no color scheme of its own. It looks at the covers in your "
                          + "library, finds the color they lean toward, and builds the window from that — so it "
                          + "is different for every collection, and changes if yours does. The controls below "
                          + "do not pick a color. They decide what is done with yours.";
        }
        else
        {
            HueLine.Text = "Your covers do not lean any one way";
            HueAbout.Text = $"{App.DisplayName} builds its colors from the covers in your library. Yours are spread "
                          + "evenly around the color wheel, or there are none yet, so it is using a neutral warm "
                          + "amber instead. That will change by itself as the collection does. The controls below "
                          + "decide what is done with the color.";
        }

        LightValue.Text = _host.ColorLightness.ToString();
        TintValue.Text = _host.ColorTint.ToString();
        BarsAbout.Text = AboutBars(_host.ColorChrome);

        FillInstall();
        FillUpdate();

        var installed = Install.InstallService.Mode == Install.InstallMode.Installed;
        ShortcutSection.IsVisible = OperatingSystem.IsWindows() && !installed;
        if (ShortcutSection.IsVisible)
        {
            var has = WindowsShell.HasShortcut;
            var current = WindowsShell.ShortcutIsCurrent;

            // The shortcut is the INSTALLED copy's, and this is some other copy:
            // say so, and offer nothing. Until 2026-09-20 this case got the
            // wording for a stray shortcut — "points at a different copy ... one
            // that was moved or rebuilt somewhere else" — with buttons to re-point
            // it here or remove it, which was true of nothing: the copy it points
            // at is exactly where it should be. Seen in his screenshot of the
            // development build's About tab an hour after the first real install.
            var theirs = !current && WindowsShell.ShortcutBelongsToInstalledCopy;
            ShortcutAdd.IsVisible = !theirs;
            if (ShortcutAdd.Parent is Control buttons) buttons.IsVisible = !theirs;    // or the empty row keeps its gap
            ShortcutAdd.Content = !has ? "Add to the Start Menu" : current ? "Re-create the shortcut" : "Point the shortcut here";
            ShortcutRemove.IsVisible = has && !theirs;
            ShortcutStatus.Text = _shortcutNote
                ?? (!has ? "No shortcut yet. With one, Start search finds it and you can pin it to the taskbar."
                    : current ? "In the Start Menu, and pointing at this copy."
                    : theirs ? $"The Start Menu shortcut belongs to the installed copy, in {Install.InstallService.InstallDirectory}. "
                             + "This copy leaves it alone."
                    : "There is a shortcut, but it points at a different copy of the program — one that was moved or rebuilt somewhere else.");
        }

        var v = typeof(PrefsWindow).Assembly.GetName().Version;
        if (AboutWordmark.Inlines is not { Count: > 0 }) App.DrawWordmark(AboutWordmark);
        if (AboutLinks.Children.Count == 0) FillAboutLinks();
        if (HelpBody.Children.Count == 0) FillHelp();
        AboutVersion.Text = $"Version {v?.Major}.{v?.Minor}.{v?.Build}";
        AboutLibrary.Text = $"{_host.LibraryCounts}\n{_host.LibraryRootPath}";
    }

    /// A plain word for a hue, in degrees. Only ever shown beside the swatch, so
    /// it has to be right, not precise.
    private static string HueName(double hue) => (((hue % 360) + 360) % 360) switch
    {
        < 15 => "red",
        < 45 => "orange",
        < 70 => "yellow",
        < 160 => "green",
        < 200 => "teal",
        < 255 => "blue",
        < 290 => "violet",
        < 335 => "pink",
        _ => "red"
    };

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

    /// Says how this copy is running, in words, and offers the one action that
    /// fits. Four cases, because there are four: installed; a release sitting
    /// loose wherever it was unzipped; one pinned as portable; and a development
    /// build, which is many files and cannot be installed by copying one.
    private void FillInstall()
    {
        var dir = Install.InstallService.ExeDirectory;

        switch (Install.InstallService.Mode)
        {
            case Install.InstallMode.Installed:
                InstallStatus.Text = $"Installed in {dir}.";
                InstallButton.Content = "Uninstall\u2026";
                InstallButton.IsVisible = true;
                break;

            case Install.InstallMode.Portable:
                InstallStatus.Text = $"Portable: runs from {dir} and registers nothing with this computer "
                                   + $"({Install.InstallLayout.PortableMarker} is beside the program).";
                InstallButton.IsVisible = false;
                break;

            default:
                if (Install.InstallService.IsSingleFile)
                {
                    InstallStatus.Text = $"Running from {dir}, not installed. Installing copies it to "
                                       + $"{Install.InstallService.InstallDirectory} and adds it to your menus.";
                    InstallButton.Content = "Install\u2026";
                    InstallButton.IsVisible = true;
                }
                else
                {
                    InstallStatus.Text = "A development build, running from its build folder. "
                                       + "Only a published release can install itself.";
                    InstallButton.IsVisible = false;
                }
                break;
        }
    }
}
