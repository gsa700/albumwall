using Avalonia;
using System;

namespace AlbumWall.App;

public class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    /// Set when this run exists only to uninstall: ask, act, and go.
    public static bool PendingUninstall { get; private set; }

    /// <c>--front-panel</c>: open the now-playing view in a second window, for a
    /// dedicated player's second screen (see FrontPanelWindow). Only a kiosk's
    /// launcher passes it; the desktop app never opens that window.
    public static bool FrontPanel { get; private set; }

    /// A dedicated player rather than a desktop: <c>--kiosk</c>, which the kiosk's
    /// launcher passes, or ALBUMWALL_SUPERVISED=1, which it has always set (so a
    /// kiosk installed before the flag existed counts too). The app may then use
    /// the machine as its own: see MainWindow.WarmCovers.
    public static bool Kiosk { get; } =
        Environment.GetCommandLineArgs().Any(a => a.Equals("--kiosk", StringComparison.OrdinalIgnoreCase))
        || Environment.GetEnvironmentVariable("ALBUMWALL_SUPERVISED") == "1";

    /// Which app this is, for Spindle's install and update code: everything
    /// else (folders, files, repository, bundle id, ALBUMWALL_SUPERVISED,
    /// ALBUMWALL_UPDATE_FEED) follows from the name.
    public static AppInfo Identity => new()
    {
        Name = App.DisplayName,
        Description = "A player for a music library you own",
        DataDir = Path.GetDirectoryName(Settings.Path)!,
        AppAssembly = typeof(Program).Assembly,
        DesktopCategories = ["AudioVideo", "Audio", "Player"],
        NativeLibrariesWindows = ["libSkiaSharp.dll", "libHarfBuzzSharp.dll", "e_sqlite3.dll", "av_libglesv2.dll"],
        NativeLibrariesUnix = ["libSkiaSharp.so", "libHarfBuzzSharp.so", "libe_sqlite3.so"],
        MoreDataFiles = ["session.json", "index.db", "index.db-journal"],
        CommandLink = true,         // `albumwall` from a terminal, on Linux
    };

    [STAThread]
    public static void Main(string[] args)
    {
        AppInfo.Use(Identity);
        // System libraries from the system's places, before anything loads one
        // (Spindle's NativeLoading; AlbumWall.Domain has its own resolver).
        NativeLoading.Install([.. Identity.NativeLibrariesUnix, .. Identity.NativeLibrariesWindows], typeof(Program).Assembly.GetName().Name!);
        AlbumWall.Domain.FlacIntegrity.InstallResolver();
        WindowSystem.NativeWayland = NativeWayland;     // for Spindle.Ui (WaylandShell, WindowPlacement, DrawnCaptions)

        // Before the log: a second launch must not rotate the running copy's log away.
        if (CommandLine.AsksHelp(args) || CommandLine.AsksVersion(args))
        {
            CommandLine.AttachConsole();
            Console.WriteLine(CommandLine.AsksHelp(args) ? CommandLine.Help() : InstallService.CurrentVersion);
            return;
        }
        if (InstallCommandLine.Parse(args).Action == InstallAction.None && !RunsHere(args))
            return;

        LogFile.Start(Path.GetDirectoryName(Settings.Path)!, "albumwall.log");
        Diagnostics.CatchCrashes(App.DisplayName, InstallService.CurrentVersion,
                                 () => NativeWayland ? "native Wayland" : "X11");
        FrontPanel = args.Any(a => a.Equals("--front-panel", StringComparison.OrdinalIgnoreCase));

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
        var request = InstallCommandLine.Parse(args);
        try
        {
            switch (request.Action)
            {
                case InstallAction.Install:
                    var installed = InstallService.Install();
                    Console.WriteLine($"Installed to {installed.ExePath}");
                    if (!request.Quiet) InstallService.LaunchDetached(installed.ExePath);
                    return;

                case InstallAction.Uninstall when request.Quiet:
                    // Quiet keeps the settings: only a person answering a question
                    // gets to throw their preferences away.
                    InstallService.Uninstall(new UninstallOptions(RemoveSettings: false));
                    return;

                case InstallAction.Uninstall:
                    // Interactive: the window comes up just far enough to ask.
                    PendingUninstall = true;
                    break;
            }
        }
        catch (InstallBlockedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
            return;
        }

        WindowsShell.ClaimIdentity();       // before any window exists, or it does not take
        if (NativeWayland) WaylandScreens.WaitForOne();     // Avalonia aborts with no screen at all
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// Whether this launch is the copy that runs (SingleInstance). A command is
    /// handed to the running copy and its answer printed; a plain launch while
    /// one runs brings that one forward. Either way this process then goes.
    private static bool RunsHere(string[] args)
    {
        if (CommandLine.Parse(args) is not null)
        {
            var reply = SingleInstance.Send(args, TimeSpan.FromSeconds(1));
            CommandLine.AttachConsole();
            // "! " starts an answer that says it could not be done.
            var failed = reply is null || reply.StartsWith("! ", StringComparison.Ordinal);
            var text = reply is null ? $"{App.DisplayName} is not running." : failed ? reply[2..] : reply;
            if (text.Length > 0) (failed ? Console.Error : Console.Out).WriteLine(text);
            Environment.ExitCode = failed ? 1 : 0;
            return false;
        }

        if (SingleInstance.TryBecomeFirst()) return true;
        if (SingleInstance.Send(args, TimeSpan.FromSeconds(3)) is not null) return false;

        // Nobody answered: the copy that has the lock is closing (or hung).
        // Closing lets go of it, so wait a little and run in its place.
        for (var i = 0; i < 25; i++)
        {
            Thread.Sleep(200);
            if (SingleInstance.TryBecomeFirst()) return true;
        }
        CommandLine.AttachConsole();
        Console.Error.WriteLine($"{App.DisplayName} is already running, but did not answer.");
        Environment.ExitCode = 1;
        return false;
    }

    /// <summary>
    /// Running on Avalonia's own Wayland backend rather than through XWayland. Since 2026-09-21
    /// that is the default wherever a Wayland session exists: Fedora 45's mutter 51 stopped giving
    /// clicks to borderless X11 windows that are not GTK's, so under XWayland every click on the
    /// wall went to the window behind it. Native Wayland never meets that code.
    ///
    /// The price is the window's position. A Wayland client cannot place its window or learn where
    /// it is (Position always reads 0,0), so the compositor decides where it opens, and the saved
    /// position is left alone rather than overwritten.
    ///
    /// DROPPED 2026-11-11 (Dave): the "Backend": "x11" setting (2026-09-28, GNOME only, so
    /// the wall reopened where it was left under XWayland) and ALBUMWALL_X11=1. They were a
    /// workaround for window memory on GNOME; Vergence, Windows and the Mac never needed it.
    /// X11 is now only the fallback: no Wayland session, or a screen reader running (below).
    /// Not FreeBSD yet: Avalonia 12.1's Wayland backend fails there at start (WakeupFd, pipe2 with
    /// Linux flag values; tried on kepler under Vergence, 2026-11-11), so FreeBSD stays on X11.
    /// </summary>
    public static readonly bool NativeWayland =
        OperatingSystem.IsLinux()
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && !ScreenReaderRunning;

    /// A screen reader (Orca) is running as the app starts. Avalonia 12 speaks to
    /// Linux screen readers (AT-SPI) only from its X11 backend: on native
    /// Wayland Orca could not see AlbumWall at all (found 2026-10-21, over SSH
    /// on Techbench). So with one running the app goes through XWayland, where
    /// it is fully named, and everyone else keeps native Wayland. The XWayland
    /// click trouble that made Wayland the default (see above) matters least to
    /// someone working by keyboard. The check is Spindle's (ScreenReader, from
    /// here, 2026-11-03), so Deadwax and Marginal ask the same way.
    private static bool ScreenReaderRunning => ScreenReader.Running;

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => WithBackend(AppBuilder.Configure<App>()
            .UseSystemPlatform())          // Spindle.Ui: UsePlatformDetect, plus FreeBSD (X11, Skia, HarfBuzz)
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // macOS: Avalonia's own application-menu items include "About
            // Avalonia"; MainWindow.MacMenu.cs supplies the whole menu instead.
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true })
            .AfterSetup(_ => Avalonia.Logging.Logger.Sink =
                new ConsoleLogSink(Avalonia.Logging.LogEventLevel.Warning));

    /// UseWayland after UsePlatformDetect replaces only the windowing platform;
    /// rendering and the rest are still what detection chose.
    ///
    /// ForceDrawnDecorations: every window draws its own title bar and never asks the compositor
    /// for one. Without it Avalonia asks for server-side decorations on every toplevel
    /// (xdg-decoration set_mode server_side) and, for the borderless main window, withdraws the
    /// request a moment later by destroying the decoration object. The protocol says that means
    /// "back to client-side at the next commit"; mutter never offers server-side at all and KWin
    /// honours the withdrawal, but COSMIC (Pop!_OS 24.04, 2026-09-26) keeps its title bar, so the
    /// wall showed two sets of window buttons. Secondary windows already draw theirs on GNOME
    /// (see DrawnCaptions), so this makes every desktop look like GNOME does.
    private static AppBuilder WithBackend(AppBuilder builder)
    {
        if (!NativeWayland)
        {
            if (ScreenReaderRunning)
                Console.WriteLine("[wall] backend: X11 through XWayland (a screen reader is running)");
            return builder;
        }
        Console.WriteLine("[wall] backend: native Wayland");
        // Marked experimental ("used mostly for testing"). If a future Avalonia drops it, this
        // stops compiling rather than silently regressing, and COSMIC is the desktop to recheck.
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
        var options = new WaylandPlatformOptions { ForceDrawnDecorations = true };
        // A Pi 5 (V3D, Pi OS trixie + labwc, 2026-09-26) fails the first profile Avalonia tries,
        // desktop OpenGL 4.0, with "eglCreateContext failed with error EGL_SUCCESS" and never
        // falls through to the GLES entries further down its own list: the render loop threw
        // ~14,000 times a second and drew nothing. Offering only GLES renders cleanly. Kept to
        // arm64 so the x64 desktops stay on the profiles they were tested with.
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64)
            options.GlProfiles =
            [
                new Avalonia.OpenGL.GlVersion(Avalonia.OpenGL.GlProfileType.OpenGLES, 3, 0),
                new Avalonia.OpenGL.GlVersion(Avalonia.OpenGL.GlProfileType.OpenGLES, 2, 0),
            ];
        return builder.UseWayland().With(options);
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
    }
}
