// AlbumWall — the Preferences window. See the note in the markup for the
// format, which the file-properties window is meant to follow.

using Avalonia;
using Avalonia.Controls;

namespace AlbumWall.App;

public partial class PrefsWindow : Window
{
    /// In the order they appear. About stays last.
    public enum Tab { Library, Statistics, Startup, Appearance, About }

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
        Rescan.Click += (_, _) =>
        {
            if (host.RescanRunning) host.StopRescan();
            else { RescanResult.Text = ""; host.Rescan(); }
            Fill();
        };

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
    }

    public void Select(Tab tab) => Tabs.SelectedIndex = (int)tab;

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
        Rescan.Content = mine ? "Stop" : "Read everything again";
        Rescan.IsEnabled = mine;        // not while another scan has the floor
    }

    public void ScanEnded()
    {
        ScanRow.IsVisible = false;
        ScanFill.Width = 0;
        Rescan.Content = "Read everything again";
        Rescan.IsEnabled = true;
    }

    public void ShowRescanResult(string text) => RescanResult.Text = text;

    /// Re-reads everything shown. Called when it opens, after anything here
    /// changes the library, and by the main window when a scan finishes — the
    /// counts on the About tab are only as good as the last scan.
    public void Fill()
    {
        if (_host is null) return;

        FillStatistics();

        _filling = true;
        LibraryPath.Text = _host.LibraryRootPath;

        // What the button does, in the terms he will experience it. The count is
        // the honest size of the job; the reason it is slow is Windows' and is
        // only claimed on Windows.
        var n = _host.LibraryTrackCount;
        var every = n > 0 ? $"all {n:N0} tracks" : "every track";
        // Two different stories, because the app genuinely behaves differently on
        // the two platforms — see MainWindow.TrustsIndex. Promising a Linux user
        // that this is how a missed tag edit gets picked up would be describing a
        // problem he cannot have.
        RescanAbout.Text = OperatingSystem.IsWindows()
            ? $"{App.DisplayName} notices music that is added, changed or removed by itself, and remembers what it "
            + $"has read so that it opens quickly. This sets that memory aside and opens {every} again. "
            + "Windows Security checks each file as it is opened, so it can take several minutes. "
            + "You can keep listening while it runs, and stop it whenever you like.\n\n"
            + "You should only need it when a tag edit has not shown up. That can happen if a tool changed a file "
            + $"while keeping its size and date the same, and {App.DisplayName} was closed at the time."

            : $"{App.DisplayName} notices music that is added, changed or removed by itself, and opens {every} "
            + "every time it scans — so a tag edit always shows up, whatever tool made it and whether or not "
            + $"{App.DisplayName} was running at the time. This scans again now, which you should rarely need.\n\n"
            + "On a big library, or one on a network drive, it can take a while. You can keep listening while it "
            + "runs, and stop it whenever you like.";

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
