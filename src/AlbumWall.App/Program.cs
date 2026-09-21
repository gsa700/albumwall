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
        // Avalonia, because the quiet forms must work with no display at all: a
        // tool that removes programs silently (winget, a management agent) runs
        // the registered QuietUninstallString, `--uninstall --quiet`, and a script
        // setting up a jukebox runs `--install --quiet`.
        //
        // NOT the Uninstall button in Settings > Installed apps, as this comment
        // used to say. Both strings are registered, and that button runs the
        // other one: UninstallString, plain `--uninstall`, which comes up far
        // enough to ask about the settings. Read back out of the registry on
        // Hambench, 2026-09-20, when both were run for real.
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

    /// <summary>
    /// Running on Avalonia's own Wayland backend rather than through XWayland. Since 2026-09-21
    /// that is the default wherever a Wayland session exists: Fedora 45's mutter 51 stopped giving
    /// clicks to borderless X11 windows that are not GTK's, so under XWayland every click on the
    /// wall went to the window behind it. Native Wayland never meets that code.
    ///
    /// The price is the window's position. A Wayland client cannot place its window or learn where
    /// it is (Position always reads 0,0), so the compositor decides where it opens, and the saved
    /// position is left alone rather than overwritten, ready for when GNOME can honour it.
    /// <c>ALBUMWALL_X11=1</c> goes back through XWayland, for testing whether mutter is fixed.
    /// </summary>
    public static readonly bool NativeWayland =
        OperatingSystem.IsLinux()
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && Environment.GetEnvironmentVariable("ALBUMWALL_X11") != "1";

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => WithBackend(AppBuilder.Configure<App>()
            .UsePlatformDetect())
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .AfterSetup(_ => Avalonia.Logging.Logger.Sink =
                new ConsoleLogSink(Avalonia.Logging.LogEventLevel.Warning));

    /// UseWayland after UsePlatformDetect replaces only the windowing platform;
    /// rendering and the rest are still what detection chose.
    private static AppBuilder WithBackend(AppBuilder builder)
    {
        if (!NativeWayland) return builder;
        Console.WriteLine("[wall] backend: native Wayland");
        return builder.UseWayland();
    }
}
