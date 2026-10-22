// AlbumWall — the one thing about a macOS window that Avalonia does not offer.
//
// The green traffic light is FULL SCREEN on macOS unless the window says
// otherwise, and full screen is switched off for AlbumWall on the Mac
// (MainWindow.UseMacTitleBar says why). Avalonia's only switch for it,
// CanMaximize = false, takes zoom with it and leaves the green button dead. The
// window's collection behavior is the system's own answer: FullScreenNone makes
// the green button zoom, which is what Option-click on it does anyway.
//
// Raw Objective-C runtime calls, through P/Invoke: nothing to bundle, and
// libobjc is on every Mac.

using System.Runtime.InteropServices;

namespace AlbumWall.App;

internal static class MacWindow
{
    private const ulong FullScreenPrimary = 1 << 7;
    private const ulong FullScreenAuxiliary = 1 << 8;
    private const ulong FullScreenNone = 1 << 9;

    /// Makes the green button zoom instead of entering full screen. The handle is
    /// Avalonia's platform handle for the window: an NSWindow, or an NSView whose
    /// window is the one meant. Returns false, and changes nothing, when it is
    /// neither.
    public static bool ZoomInsteadOfFullScreen(IntPtr handle, string? descriptor)
    {
        if (!OperatingSystem.IsMacOS() || handle == IntPtr.Zero) return false;
        var window = descriptor == "NSView" ? Send(handle, Sel("window")) : handle;
        if (window == IntPtr.Zero) return false;

        var behavior = (ulong)Send(window, Sel("collectionBehavior"));
        behavior = behavior & ~(FullScreenPrimary | FullScreenAuxiliary) | FullScreenNone;
        SendULong(window, Sel("setCollectionBehavior:"), behavior);
        return ((ulong)Send(window, Sel("collectionBehavior")) & FullScreenNone) != 0;
    }

    /// The traffic lights' title bar as AppKit first had it, in points: what a
    /// window with no bar of ours (compact) goes back to.
    private static double _standardTitleBar;

    /// Centres the traffic lights on a bar `barHeight` points tall across the
    /// top of the window, or puts them back where AppKit has them when it is
    /// null. Returns where the close button's centre ended up, in points from
    /// the top of the window, or null when the handle is no window.
    ///
    /// Avalonia's ExtendClientAreaTitleBarHeightHint was meant to do this, and
    /// on macOS 27 with Avalonia 12.1.2 it does not: Avalonia reports a 44 pt
    /// title bar while AppKit keeps its standard 28, so the lights sat 14 pt
    /// from the top of a 44 pt bar, 7.5 pt above its middle ("there are too
    /// high", 2026-10-18, measured from a screenshot). The buttons live in
    /// AppKit's title bar container, so that is made the bar's height and each
    /// button is centred in it, the same thing Electron does for its
    /// trafficLightPosition. AppKit lays the title bar out again on a resize, a
    /// zoom and a change of focus, so the window calls this again after each.
    public static double? CenterTrafficLights(IntPtr handle, string? descriptor, double? barHeight)
    {
        if (!OperatingSystem.IsMacOS() || handle == IntPtr.Zero) return null;
        var window = descriptor == "NSView" ? Send(handle, Sel("window")) : handle;
        if (window == IntPtr.Zero) return null;
        var close = SendNInt(window, Sel("standardWindowButton:"), 0);
        var titleBar = close == IntPtr.Zero ? IntPtr.Zero : Send(close, Sel("superview"));
        var container = titleBar == IntPtr.Zero ? IntPtr.Zero : Send(titleBar, Sel("superview"));
        if (container == IntPtr.Zero) return null;

        var bounds = SendRect(container, Sel("frame"));
        if (_standardTitleBar == 0) _standardTitleBar = bounds.H;
        var height = barHeight ?? _standardTitleBar;
        var windowFrame = SendRect(window, Sel("frame"));

        bounds.Y = windowFrame.H - height;
        bounds.H = height;
        SendSetRect(container, Sel("setFrame:"), bounds);
        SendSetRect(titleBar, Sel("setFrame:"), new CGRect { W = bounds.W, H = height });
        for (nint kind = 0; kind <= 2; kind++)     // close, minimize, zoom
        {
            var button = SendNInt(window, Sel("standardWindowButton:"), kind);
            if (button == IntPtr.Zero) continue;
            var r = SendRect(button, Sel("frame"));
            SendSetPoint(button, Sel("setFrameOrigin:"), r.X, Math.Round((height - r.H) / 2));
        }
        return TrafficLightCenter(window, close);
    }

    /// Hides the traffic lights, or shows all three again. Compact has one
    /// bigger size and the wall button is it, so elsewhere its window has no
    /// Maximize button at all; on the Mac the system's green button stayed and
    /// zoomed the compact view to fill the screen (2026-10-18). Greyed out
    /// first, then close alone ("can we just remove the button in compact and
    /// only show close?"), then none (2026-10-22: the red light "sticks out
    /// like a sore thumb ... get rid of the close button totally").
    public static void HideTrafficLights(IntPtr handle, string? descriptor, bool hidden)
    {
        if (!OperatingSystem.IsMacOS() || handle == IntPtr.Zero) return;
        var window = descriptor == "NSView" ? Send(handle, Sel("window")) : handle;
        if (window == IntPtr.Zero) return;
        for (nint kind = 0; kind <= 2; kind++)     // close, minimize, zoom
        {
            var button = SendNInt(window, Sel("standardWindowButton:"), kind);
            if (button != IntPtr.Zero) SendBool(button, Sel("setHidden:"), hidden);
        }
    }

    /// Where the close button's centre is now, without moving anything: for
    /// checking that AppKit has not put the lights back on its own.
    public static double? TrafficLightCenter(IntPtr handle, string? descriptor)
    {
        if (!OperatingSystem.IsMacOS() || handle == IntPtr.Zero) return null;
        var window = descriptor == "NSView" ? Send(handle, Sel("window")) : handle;
        var close = window == IntPtr.Zero ? IntPtr.Zero : SendNInt(window, Sel("standardWindowButton:"), 0);
        return close == IntPtr.Zero ? null : TrafficLightCenter(window, close);
    }

    /// The close button's centre in points from the top of the window: what the
    /// eye judges, read back from AppKit rather than assumed.
    private static double TrafficLightCenter(IntPtr window, IntPtr close)
    {
        var inWindow = SendConvert(close, Sel("convertRect:toView:"), SendRect(close, Sel("bounds")), IntPtr.Zero);
        var content = SendRect(Send(window, Sel("contentView")), Sel("frame"));
        return content.H - (inWindow.Y + inWindow.H / 2);
    }

    /// Sends an NSApplication action with no sender: hide:, hideOtherApplications:,
    /// unhideAllApplications:. For the application menu, whose standard items
    /// Avalonia's defaults would have supplied (MainWindow.MacMenu.cs).
    public static void AppAction(string selector)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
        SendPtr(app, Sel(selector), IntPtr.Zero);
    }

    /// Resizes the window by a point and back, what a drag on its edge does.
    ///
    /// After the display woke on 2026-10-21 (locked from the menu bar's Lock
    /// Display, dark three hours) the window kept its size but drew everything
    /// twice as big, showing only its top-left quarter, until Dave resized it.
    /// Avalonia's drawing surface had kept a wrong scale through the wake, and a
    /// resize makes it take the window's scale again. Done with AppKit's own
    /// setFrame so that it works zoomed as well as not, where setting Width
    /// would un-zoom the window. The second half waits for a frame to be drawn
    /// at the first, as a drag would.
    public static void Nudge(IntPtr handle, string? descriptor)
    {
        if (!OperatingSystem.IsMacOS() || handle == IntPtr.Zero) return;
        var window = descriptor == "NSView" ? Send(handle, Sel("window")) : handle;
        if (window == IntPtr.Zero) return;

        var frame = SendRect(window, Sel("frame"));
        SendSetFrame(window, Sel("setFrame:display:"), frame with { W = frame.W + 1 }, true);
        Avalonia.Threading.DispatcherTimer.RunOnce(
            () => SendSetFrame(window, Sel("setFrame:display:"), frame, true), TimeSpan.FromMilliseconds(100));
    }

    private static Action<string>? _displaysChanged;

    /// Calls `changed`, on the UI thread, with the name of whatever the system
    /// says when the displays may have come back different: they woke, the
    /// screen was unlocked, their arrangement or a window's scale changed. Which
    /// one left the surface wrong on 2026-10-21 is not known, so all of them.
    public static unsafe void WatchDisplays(Action<string> changed)
    {
        if (!OperatingSystem.IsMacOS() || _displaysChanged is not null) return;
        _displaysChanged = changed;

        var cls = objc_getClass("AlbumWallDisplayWatcher");
        if (cls == IntPtr.Zero)
        {
            cls = objc_allocateClassPair(objc_getClass("NSObject"), "AlbumWallDisplayWatcher", 0);
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> note = &OnDisplays;
            class_addMethod(cls, Sel("awDisplays:"), (IntPtr)note, "v@:@");
            objc_registerClassPair(cls);
        }
        var watcher = Send(Send(cls, Sel("alloc")), Sel("init"));     // kept for the life of the process

        var appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        IntPtr AppKitName(string constant) => Marshal.ReadIntPtr(NativeLibrary.GetExport(appKit, constant));

        var local = Send(objc_getClass("NSNotificationCenter"), Sel("defaultCenter"));
        var workspace = Send(Send(objc_getClass("NSWorkspace"), Sel("sharedWorkspace")), Sel("notificationCenter"));
        var distributed = Send(objc_getClass("NSDistributedNotificationCenter"), Sel("defaultCenter"));

        Observe(local, AppKitName("NSApplicationDidChangeScreenParametersNotification"));
        Observe(local, AppKitName("NSWindowDidChangeBackingPropertiesNotification"));
        Observe(workspace, AppKitName("NSWorkspaceScreensDidWakeNotification"));
        // Held back while another app is in front unless asked otherwise, and
        // AlbumWall is seldom in front when the screen is unlocked (tried
        // 2026-10-21: it came only once the window was clicked).
        SendObserveNow(distributed, Sel("addObserver:selector:name:object:suspensionBehavior:"), watcher,
                       Sel("awDisplays:"), NSString("com.apple.screenIsUnlocked"), IntPtr.Zero, DeliverImmediately);

        void Observe(IntPtr center, IntPtr name) =>
            SendObserve(center, Sel("addObserver:selector:name:object:"), watcher, Sel("awDisplays:"), name, IntPtr.Zero);
    }

    [UnmanagedCallersOnly]
    private static void OnDisplays(IntPtr self, IntPtr sel, IntPtr notification)
    {
        try
        {
            var name = Marshal.PtrToStringUTF8(Send(Send(notification, Sel("name")), Sel("UTF8String"))) ?? "?";
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _displaysChanged?.Invoke(name));
        }
        catch
        {
            // Nothing may be thrown back into AppKit; a missed redraw is a resize away.
        }
    }

    private static IntPtr NSString(string s)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(s);
        try { return SendPtrRet(objc_getClass("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtrRet(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendObserve(IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr action, IntPtr name, IntPtr obj);

    private const nuint DeliverImmediately = 4;    // NSNotificationSuspensionBehaviorDeliverImmediately

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendObserveNow(IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr action, IntPtr name, IntPtr obj, nuint behavior);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendSetFrame(IntPtr receiver, IntPtr selector, CGRect frame, [MarshalAs(UnmanagedType.I1)] bool display);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendPtr(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendULong(IntPtr receiver, IntPtr selector, ulong value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendNInt(IntPtr receiver, IntPtr selector, nint value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

    // An NSRect is four doubles, which arm64 passes and returns in registers, so
    // plain objc_msgSend carries it both ways (no _stret variant there).
    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, W, H;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect SendRect(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendSetRect(IntPtr receiver, IntPtr selector, CGRect value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendSetPoint(IntPtr receiver, IntPtr selector, double x, double y);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect SendConvert(IntPtr receiver, IntPtr selector, CGRect rect, IntPtr view);
}
