using Avalonia;
using System;

namespace AlbumWall.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    /// Set when this run exists only to uninstall: ask, act, and go.
    public static bool PendingUninstall { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        LogFile.Start();

        // --install and --uninstall, the family's switches. Handled here, before
        // Avalonia, because the quiet forms must work with no display at all: the
        // Windows "Installed apps" entry runs `--uninstall --quiet`, and a script
        // setting up a jukebox runs `--install --quiet`.
        var request = Install.InstallCommandLine.Parse(args);
        try
        {
            switch (request.Action)
            {
                case Install.InstallAction.Install:
                    var installed = Install.InstallService.Install();
                    Console.WriteLine($"Installed to {installed.ExePath}");
                    if (!request.Quiet) Install.InstallService.LaunchDetached(installed.ExePath);
                    return;

                case Install.InstallAction.Uninstall when request.Quiet:
                    // Quiet keeps the settings: only a person answering a question
                    // gets to throw their preferences away.
                    Install.InstallService.Uninstall(new Install.UninstallOptions(RemoveSettings: false));
                    return;

                case Install.InstallAction.Uninstall:
                    // Interactive: the window comes up just far enough to ask.
                    PendingUninstall = true;
                    break;
            }
        }
        catch (Install.InstallBlockedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
            return;
        }

        WindowsShell.ClaimIdentity();       // before any window exists, or it does not take
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .AfterSetup(_ => Avalonia.Logging.Logger.Sink =
                new ConsoleLogSink(Avalonia.Logging.LogEventLevel.Warning));
}
