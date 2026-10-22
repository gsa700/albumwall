// AlbumWall - keeping the display awake while it is being watched, and the
// machine awake while it is playing.
//
// His call, 2026-10-22: "if in full screen and playing I think it should
// prevent sleeping the display ... when the album ends or paused then the
// normal setting rules." Full screen is the one state that says someone is
// looking at it: on a smaller screen, or a Pi on the stereo, the wall IS the
// display. A window on a desktop, compact included, leaves the desktop's power
// settings alone. On a Mac that locks when the display sleeps, the lock waits
// too, for as long as the album plays full screen.
//
// And on Linux, the machine itself while anything plays, full screen or not:
// macOS and Windows keep a machine with an audio stream open awake by
// themselves, and GNOME does not, so with the screen off it could suspend in
// the middle of a record. mpv used to ask for that; Hymie asks here.
//
// Each platform's own request, nothing bundled:
//   * the Mac: an IOKit power assertion, PreventUserIdleDisplaySleep;
//   * Windows: SetThreadExecutionState, from the UI thread, which owns it;
//   * Linux: the desktop portal's Inhibit (GNOME and KDE both serve it), with
//     org.freedesktop.ScreenSaver as the fallback for the display alone.
// Every failure is logged and otherwise ignored: a player that will not play
// because the display cannot be held on is the worse bargain.

using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace AlbumWall.App;

internal sealed class KeepAwake
{
    private bool _display, _system;

    /// What should be held now. Called on the UI thread whenever playing or
    /// the window's state changes; does nothing when nothing has.
    public void Set(bool display, bool system)
    {
        // Only Linux holds the machine on its own account (see above).
        system &= OperatingSystem.IsLinux();
        if (display == _display && system == _system) return;
        _display = display;
        _system = system;
        Console.WriteLine($"[awake] display {(display ? "held" : "free")}, machine {(system ? "held" : "free")}");

        if (OperatingSystem.IsMacOS()) Mac(display);
        else if (OperatingSystem.IsWindows()) Windows(display);
        else if (OperatingSystem.IsLinux()) _linux = _linux.ContinueWith(_ => Linux(display, system)).Unwrap();
    }

    public void Release() => Set(false, false);

    // ------------------------------------------------------------------ Mac

    private uint _assertion;

    private void Mac(bool display)
    {
        if (display == (_assertion != 0)) return;
        if (!display)
        {
            IOPMAssertionRelease(_assertion);
            _assertion = 0;
            return;
        }
        var type = CFString("PreventUserIdleDisplaySleep");
        var name = CFString("AlbumWall is playing full screen");
        var status = IOPMAssertionCreateWithName(type, 255 /* kIOPMAssertionLevelOn */, name, out _assertion);
        CFRelease(type);
        CFRelease(name);
        if (status != 0)
        {
            _assertion = 0;
            Console.WriteLine($"[awake] the Mac refused the assertion: {status}");
        }
    }

    private static IntPtr CFString(string s) => CFStringCreateWithCString(IntPtr.Zero, s, 0x08000100 /* UTF-8 */);

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(IOKit)] private static extern int IOPMAssertionCreateWithName(IntPtr type, uint level, IntPtr name, out uint id);
    [DllImport(IOKit)] private static extern int IOPMAssertionRelease(uint id);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string s, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);

    // ------------------------------------------------------------------ Windows

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;

    private static void Windows(bool display)
    {
        // Continuous: the state stays until it is set again, from this thread.
        if (SetThreadExecutionState(display ? EsContinuous | EsDisplayRequired | EsSystemRequired : EsContinuous) == 0)
            Console.WriteLine("[awake] Windows refused SetThreadExecutionState");
    }

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    // ------------------------------------------------------------------ Linux

    // One request at a time, in order: each change waits for the one before.
    private Task _linux = Task.CompletedTask;
    private string? _portalHandle;      // the portal's request, to close
    private uint _screenSaverCookie;    // the fallback's, to uninhibit

    private const string Portal = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";

    private async Task Linux(bool display, bool system)
    {
        try
        {
            var connection = DBusConnection.Session;

            // A change is the old request closed and a new one made: the portal
            // has no way to change what an inhibition covers.
            if (_portalHandle is { } old)
            {
                _portalHandle = null;
                await Call(connection, Portal, old, "org.freedesktop.portal.Request", "Close", "", static (ref MessageWriter _) => { });
            }
            if (_screenSaverCookie != 0)
            {
                var cookie = _screenSaverCookie;
                _screenSaverCookie = 0;
                await Call(connection, "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver",
                           "org.freedesktop.ScreenSaver", "UnInhibit", "u", (ref MessageWriter w) => w.WriteUInt32(cookie));
            }
            if (!display && !system) return;

            // Flags: 4 suspend, 8 idle (the display blanking and locking).
            var flags = (display ? 8u : 0) | (system ? 4u : 0);
            var reason = display ? "Playing full screen" : "Playing music";
            try
            {
                _portalHandle = await connection.CallMethodAsync(
                    Message(connection, Portal, PortalPath, "org.freedesktop.portal.Inhibit", "Inhibit", "sua{sv}",
                        (ref MessageWriter w) =>
                        {
                            w.WriteString("");
                            w.WriteUInt32(flags);
                            var dict = w.WriteDictionaryStart();
                            w.WriteDictionaryEntryStart();
                            w.WriteString("reason");
                            w.WriteVariantString(reason);
                            w.WriteDictionaryEnd(dict);
                        }),
                    (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null);
                return;
            }
            catch (DBusErrorReplyException ex) when (display)
            {
                Console.WriteLine($"[awake] no portal inhibit ({ex.ErrorName}); trying org.freedesktop.ScreenSaver");
            }
            _screenSaverCookie = await connection.CallMethodAsync(
                Message(connection, "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver",
                        "org.freedesktop.ScreenSaver", "Inhibit", "ss",
                        (ref MessageWriter w) => { w.WriteString("AlbumWall"); w.WriteString(reason); }),
                (m, _) => m.GetBodyReader().ReadUInt32(), null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[awake] could not {(display || system ? "hold" : "free")} it: {ex.Message}");
        }
    }

    private delegate void Body(ref MessageWriter writer);

    private static MessageBuffer Message(DBusConnection connection, string destination, string path,
                                         string @interface, string member, string signature, Body body)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature.Length > 0 ? signature : null);
            body(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    private static Task Call(DBusConnection connection, string destination, string path,
                             string @interface, string member, string signature, Body body) =>
        connection.CallMethodAsync(Message(connection, destination, path, @interface, member, signature, body));
}
