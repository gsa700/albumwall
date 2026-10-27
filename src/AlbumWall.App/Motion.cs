// AlbumWall - "reduce motion": the system's own setting, followed.
//
// Roadmap, "Arrow-key navigation ... and the rest of keyboard and screen-reader
// access": the sliding play controls and the unfolding album ignored the setting
// people turn on because movement on screen makes them unwell. With it on, every
// slide, unfold, scroll and flip here takes no time (Ms, Of, MainWindow.Animate),
// and the playing track's marker holds still instead of breathing.
//
// Read once, as the app starts: the Mac's Accessibility > Display > Reduce motion,
// Windows' Accessibility > Visual effects > Animation effects, GNOME's
// enable-animations (other Linux desktops have no setting it can read, and get
// motion). ALBUMWALL_REDUCE_MOTION=1 or 0 overrides it, for testing either way.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AlbumWall.App;

public static class Motion
{
    public static bool Reduced { get; } = Read();

    /// A duration, or none at all when motion is reduced.
    public static int Ms(int ms) => Reduced ? 0 : ms;
    public static TimeSpan Of(TimeSpan duration) => Reduced ? TimeSpan.Zero : duration;

    private static bool Read()
    {
        switch (Environment.GetEnvironmentVariable("ALBUMWALL_REDUCE_MOTION"))
        {
            case "1": return Say(true, "ALBUMWALL_REDUCE_MOTION=1");
            case "0": return Say(false, "ALBUMWALL_REDUCE_MOTION=0");
        }
        try
        {
            if (OperatingSystem.IsMacOS()) return Say(MacReduced(), "the Mac's setting");
            if (OperatingSystem.IsWindows()) return Say(WindowsReduced(), "Windows' setting");
            if (OperatingSystem.IsLinux()) return Say(GnomeReduced(), "GNOME's setting");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[motion] could not read the system's setting ({ex.Message}); full motion");
        }
        return false;
    }

    private static bool Say(bool reduced, string from)
    {
        Console.WriteLine($"[motion] {(reduced ? "reduced" : "full")} ({from})");
        return reduced;
    }

    // [[NSWorkspace sharedWorkspace] accessibilityDisplayShouldReduceMotion]
    private static bool MacReduced()
    {
        var workspace = objc_msgSend(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
        return workspace != IntPtr.Zero
            && objc_msgSend_bool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion"));
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool objc_msgSend_bool(IntPtr receiver, IntPtr selector);

    // SPI_GETCLIENTAREAANIMATION: false is "Animation effects" turned off.
    private static bool WindowsReduced()
    {
        var on = true;
        return SystemParametersInfo(0x1042, 0, ref on, 0) && !on;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    private static bool GnomeReduced()
    {
        if (Domain.ToolPath.Find("gsettings") is not { } gsettings) return false;
        using var p = Process.Start(new ProcessStartInfo(gsettings, "get org.gnome.desktop.interface enable-animations")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        });
        if (p is null || !p.WaitForExit(1000)) { try { p?.Kill(); } catch { } return false; }
        return p.StandardOutput.ReadToEnd().Trim() == "false";
    }
}
