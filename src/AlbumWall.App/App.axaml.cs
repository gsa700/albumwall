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
            var window = new MainWindow();
            desktop.MainWindow = window;

            window.Opened += async (_, _) =>
            {
                // This run exists only to uninstall: ask, act, and go.
                if (Program.PendingUninstall)
                {
                    await RunUninstallAsync(window);
                    return;
                }

                // Re-asserted at EVERY start, never check-and-skip — the family's
                // rule, learned when a lost registration stayed lost. A no-op
                // unless this copy is the installed one.
                Install.InstallService.EnsureRegistered();

                // A release sitting wherever it was unzipped offers to install
                // itself. Never a development build (see InstallService), and
                // never a test run with its settings redirected.
                var testRun = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALBUMWALL_CONFIG_DIR"));
                if (Install.InstallService.ShouldOfferInstall && !testRun)
                    await OfferInstallAsync(window);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// Offer to install a loose copy. If accepted, hands over to the installed
    /// copy and closes this one.
    public static async Task OfferInstallAsync(Avalonia.Controls.Window owner)
    {
        var where = Install.InstallService.InstallDirectory;
        var lists = OperatingSystem.IsWindows()
            ? "lists it in Settings \u2192 Apps \u2192 Installed apps, with a Start Menu shortcut"
            : "adds it to your applications menu";

        var accepted = await new ConfirmWindow(
            $"Install {DisplayName}",
            $"Install {DisplayName} on this computer?",
            affirmative: "Install", negative: "Not now",
            detail: $"Copies the program to {where} and {lists}. Your music, your settings and "
                  + "everything else are untouched either way.\n\n"
                  + "To run from here permanently without being asked again, put a file named "
                  + $"{Install.InstallLayout.PortableMarker} beside the program.")
            .ShowDialog<bool>(owner);
        if (!accepted) return;

        try
        {
            var installed = Install.InstallService.Install();

            // Installed but not listed is a real outcome, not a detail: the program
            // works, yet the usual way to remove it is missing. Say so here rather
            // than leave it to be discovered.
            if (!installed.Registered)
            {
                await new ConfirmWindow("Installed, with one problem",
                    $"{DisplayName} is installed in {where} and will run normally, but it could "
                    + "not register itself with the desktop.",
                    affirmative: "OK", negative: null,
                    detail: "Starting the installed copy again usually fixes it. Failing that, run "
                          + "it once with --install from a terminal.")
                    .ShowDialog<bool>(owner);
            }

            Install.InstallService.LaunchDetached(installed.ExePath);

            // Closing runs the normal save path on purpose, so the session carries
            // over to the installed copy, which reads the same data directory.
            owner.Close();
        }
        catch (Exception ex)
        {
            await new ConfirmWindow("Could not install", ex.Message,
                affirmative: "OK", negative: null).ShowDialog<bool>(owner);
        }
    }

    /// The interactive uninstall: say what goes, ask about the settings, then go.
    /// <param name="asked">
    /// True when a person chose Uninstall from inside the running app, false when
    /// the whole run was started only to uninstall. The difference is what a
    /// "Cancel" means: from the app it means carry on; from --uninstall the run
    /// has nothing else to do and ends.
    /// </param>
    public static async Task RunUninstallAsync(Avalonia.Controls.Window owner, bool asked = false)
    {
        var dialog = new ConfirmWindow(
            $"Uninstall {DisplayName}",
            $"Remove {DisplayName} from this computer?",
            affirmative: "Uninstall", negative: "Cancel",
            detail: "Removes the program and its menu entries. Your music is never touched.",
            option: "Also remove my settings and the saved session");

        var accepted = await dialog.ShowDialog<bool>(owner);
        if (accepted)
        {
            Install.InstallService.Uninstall(new Install.UninstallOptions(dialog.OptionChecked));

            // A backstop, from LP-100A: the helper is now waiting on this process id, and nothing
            // in this process is worth preserving. If closing the window below does not end it —
            // a close made from inside a dialog's click has left a windowless process running
            // before — this does, and the uninstall completes either way.
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Environment.Exit(0));
        }

        if (accepted || !asked) owner.Close();
    }
}