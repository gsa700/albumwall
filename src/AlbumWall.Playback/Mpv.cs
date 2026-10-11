// AlbumWall — libmpv bindings.
//
// Raw P/Invoke only. Nothing in here makes decisions; the policy lives in
// Player.cs, so that the awkward parts of talking to a C library stay in one
// place and do not leak into the player's logic.
//
// WHY libmpv: it is the only backend, chosen because it absorbs the per-platform
// audio difference itself — WASAPI on Windows, PipeWire/ALSA on Linux — behind one
// API, and because gapless playback is a hard requirement and mpv already does it
// properly. Shipping GStreamer on Windows is unpleasant; this is not.
//
// LICENSE: mpv is GPLv2-or-later, so linking it from a GPLv3 application is fine
// via the "or later" clause. Verified from mpv's own Copyright file.

using System.Runtime.InteropServices;

namespace AlbumWall.Playback;

internal static class Mpv
{
    // The soname differs per platform and per distro packaging, so the import
    // name here is a placeholder that the resolver below turns into whatever this
    // machine actually has.
    private const string Lib = "mpv";

    static Mpv()
    {
        NativeLibrary.SetDllImportResolver(typeof(Mpv).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;

            // OURS FIRST, THE SYSTEM'S SECOND.
            //
            // A release carries its own audio-only libmpv, and that is the one
            // gapless was tested against. But the plain TryLoad below is a bare
            // dlopen, and on Linux dlopen NEVER looks beside the executable —
            // only Windows does that. So without this pass a bundled library is
            // silently ignored in favor of whatever the distribution has, or
            // nothing at all. Asking with the assembly makes the runtime probe
            // the app's own directories, including wherever a single-file build
            // unpacked its native libraries.
            foreach (var candidate in Candidates())
                if (NativeLibrary.TryLoad(candidate, asm, DllImportSearchPath.AssemblyDirectory, out var ours))
                {
                    IsBundled = true;
                    return _handle = ours;
                }

            // A development checkout has no bundled copy and lands here, which
            // is right: it uses the distribution's libmpv, as it always did.
            //
            // Ordered by how specific they are. A bare "libmpv.so" is usually a
            // development symlink and is the least likely to be present on a
            // machine that only has the runtime package installed.
            foreach (var candidate in Candidates())
                if (NativeLibrary.TryLoad(candidate, out var handle))
                    return _handle = handle;

            return IntPtr.Zero;
        });
    }

    private static IntPtr _handle;

    /// True when the engine loaded is the one shipped with the app (audio-only,
    /// no scripting, no yt-dlp), false when it is the distribution's.
    public static bool IsBundled { get; private set; }

    /// The file the library was loaded from, on any platform: /proc on Linux,
    /// the module's own name on Windows. Null when it cannot be told.
    public static string? LoadedFile()
    {
        if (OperatingSystem.IsLinux()) return LoadedFrom();
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero) return null;
        var buffer = new char[1024];
        var n = GetModuleFileNameW(_handle, buffer, buffer.Length);
        return n > 0 ? new string(buffer, 0, n) : null;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern int GetModuleFileNameW(IntPtr module, [Out] char[] name, int size);

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "libmpv-2.dll";
            yield return "mpv-2.dll";
            yield return "mpv-1.dll";
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            // Beside the app first (a release carries its own, like everywhere else), then
            // Homebrew. dlopen on macOS searches DYLD paths, ~/lib, /usr/local/lib and
            // /usr/lib, and NOT /opt/homebrew/lib, where Homebrew puts everything on Apple
            // silicon, so a bare name finds nothing on a machine that has `brew install mpv`.
            yield return "libmpv.2.dylib";
            yield return "libmpv.dylib";
            yield return "/opt/homebrew/lib/libmpv.2.dylib";
            yield return "/usr/local/lib/libmpv.2.dylib";
            yield break;
        }

        yield return "libmpv.so.2";
        yield return "libmpv.so.1";
        yield return "libmpv.so";
    }

    /// The file libmpv was actually loaded from, for the log.
    ///
    /// Two copies can exist — the one shipped with the app and the
    /// distribution's — and they differ in exactly the ways that produce
    /// confusing bug reports: codecs, versions, outputs. One line at startup
    /// saying which is running settles it before anyone has to wonder. Linux
    /// only, because /proc makes it free there; elsewhere this returns null.
    public static string? LoadedFrom()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var at = line.IndexOf('/');
                if (at < 0) continue;
                var path = line[at..];
                if (Path.GetFileName(path).StartsWith("libmpv", StringComparison.Ordinal)) return path;
            }
        }
        catch { /* a diagnostic must never be the thing that breaks playback */ }
        return null;
    }

    /// True when libmpv could be loaded at all. Checked before the app offers to
    /// play anything, so a missing library is a clear message rather than a
    /// DllNotFoundException out of a button click.
    public static bool IsAvailable
    {
        get
        {
            try { return mpv_client_api_version() != 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    public enum Format
    {
        None = 0,
        String = 1,
        Flag = 3,
        Int64 = 4,
        Double = 5
    }

    public enum EventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        Idle = 11,
        PlaybackRestart = 21,
        PropertyChange = 22
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public EventId Id;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public IntPtr Name;
        public Format Format;
        public IntPtr Data;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_client_api_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, Format format, out double data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, Format format, out long data);

    /// Returns an mpv-owned string (free it with mpv_free), or null on error.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    /// A property as text, or null when mpv has none to give.
    public static string? GetString(IntPtr ctx, string name)
    {
        var p = mpv_get_property_string(ctx, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { mpv_free(p); }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, Format format, ref int data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, Format format);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, IntPtr args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int error);

    /// Sends a command as an argument list. mpv takes a NULL-terminated array of
    /// UTF-8 strings, which has to be built and freed by hand.
    public static int Command(IntPtr ctx, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            ptrs[^1] = IntPtr.Zero;

            var block = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
            try
            {
                Marshal.Copy(ptrs, 0, block, ptrs.Length);
                return mpv_command(ctx, block);
            }
            finally { Marshal.FreeHGlobal(block); }
        }
        finally
        {
            foreach (var p in ptrs)
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    public static string ErrorText(int code) =>
        Marshal.PtrToStringUTF8(mpv_error_string(code)) ?? $"mpv error {code}";
}
