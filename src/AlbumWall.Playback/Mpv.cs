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

            // Ordered by how specific they are. A bare "libmpv.so" is usually a
            // development symlink and is the least likely to be present on a
            // machine that only has the runtime package installed.
            foreach (var candidate in Candidates())
                if (NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;

            return IntPtr.Zero;
        });
    }

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
            yield return "libmpv.2.dylib";
            yield return "libmpv.dylib";
            yield break;
        }

        yield return "libmpv.so.2";
        yield return "libmpv.so.1";
        yield return "libmpv.so";
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
