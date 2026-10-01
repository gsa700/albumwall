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

    /// <c>--front-panel</c>: open the now-playing view in a second window, for a
    /// dedicated player's second screen (see FrontPanelWindow). Only a kiosk's
    /// launcher passes it; the desktop app never opens that window.
    public static bool FrontPanel { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        LogFile.Start();
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
    ///
    /// <c>"Backend": "x11"</c> in settings.json does the same for good, on one machine. His call,
    /// 2026-09-28, for positions: under XWayland the app can place its window and learn where it
    /// is, so the wall reopens where it was left and the compact strip keeps its own spot. "until
    /// they figure out window management": native Wayland stays the default for everyone else,
    /// and deleting the line goes back. Not in Preferences: it is a workaround, not a choice
    /// anyone should have to understand.
    ///
    /// The setting is honoured on GNOME only, because mutter's window placement is the whole
    /// reason for it. COSMIC (1.8, Fedora 45, 2026-09-30) does not scale XWayland clients and
    /// sets no Xft.dpi, so through XWayland on the 6K at 200% the app saw scaling=1 and drew at
    /// half size. Native Wayland gets the output scale. ALBUMWALL_X11=1 still applies everywhere.
    /// </summary>
    public static readonly bool NativeWayland =
        OperatingSystem.IsLinux()
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && Environment.GetEnvironmentVariable("ALBUMWALL_X11") != "1"
        && !(OnGnome && SettingsAskX11);

    private static bool SettingsAskX11 =>
        string.Equals(Settings.Load().Backend, "x11", StringComparison.OrdinalIgnoreCase);

    /// XDG_CURRENT_DESKTOP is a colon-separated list ("GNOME", "ubuntu:GNOME").
    private static bool OnGnome =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
            .Split(':').Contains("GNOME", StringComparer.OrdinalIgnoreCase);

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
            if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                Console.WriteLine("[wall] backend: X11 through XWayland ("
                                + (Environment.GetEnvironmentVariable("ALBUMWALL_X11") == "1"
                                    ? "ALBUMWALL_X11=1" : "settings \"Backend\": \"x11\"") + ")");
            return builder;
        }
        Console.WriteLine("[wall] backend: native Wayland"
                        + (SettingsAskX11 ? " (settings \"Backend\": \"x11\" is for GNOME only)" : ""));
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
