// AlbumWall — Now Playing and the media keys on macOS.
//
// The Mac's counterpart of Smtc (Windows) and Mpris (Linux): MPNowPlayingInfoCenter
// is what Control Center's Now Playing and the menu bar's Now Playing item show,
// with the cover; MPRemoteCommandCenter is where the keyboard's play/pause,
// next and previous keys, headphone buttons and those Now Playing controls
// arrive. Like Smtc it is PUSHED: nothing shows until Update() says so. It
// takes the same State record and hands back the same command strings, so
// MainWindow needs no third vocabulary: a key on any platform ends up in
// MprisCommand. And until something here says "I am playing", the media keys
// open Music.app, which is what Dave found on 2026-10-14.
//
// MediaPlayer is an Objective-C framework, reached through the Objective-C
// runtime from C# the way MacWindow and Mpv's dladdr are: nothing to bundle.
// Two things the runtime makes awkward:
// - The command center calls a TARGET and ACTION. So a small class is made at
//   run time, AlbumWallRemoteTarget, whose methods are C# functions.
// - The cover is asked for through a BLOCK (requestHandler). A block that
//   captures nothing is a fixed struct, made here once by hand; its function
//   hands back the current cover.
//
// Everything runs on the UI thread, which on macOS is the main thread, where
// MediaPlayer wants to be called and where it calls back.

using System.Runtime.InteropServices;

namespace AlbumWall.App;

public sealed class MacNowPlaying
{
    private const string MediaPlayerPath = "/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // MPNowPlayingPlaybackState
    private const long StatePlaying = 1, StatePaused = 2, StateStopped = 3;

    private static Action<string>? _command;
    private static Action<TimeSpan>? _seek;

    /// The cover the artwork block hands back: an NSImage, retained, or nothing.
    private static IntPtr _image;
    private static IntPtr _artBlock;

    private readonly IntPtr _mediaPlayer;
    private readonly IntPtr _center;
    private Mpris.State _state = new(false, false, "", "", "", "", 0, 0, 1.0);
    private TimeSpan _position;
    private TimeSpan _duration;
    private IntPtr _artwork;          // MPMediaItemArtwork, retained
    private string? _artFor;

    private MacNowPlaying(IntPtr mediaPlayer)
    {
        _mediaPlayer = mediaPlayer;
        _center = Send(Class("MPNowPlayingInfoCenter"), Sel("defaultCenter"));
        RegisterCommands();
    }

    /// Returns null rather than throwing, as Smtc does: no media keys is an
    /// inconvenience, and must never be the reason the player does not start.
    public static MacNowPlaying? Start(Action<string> command, Action<TimeSpan> seek)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var mp = NativeLibrary.Load(MediaPlayerPath);
            _command = command;
            _seek = seek;
            var me = new MacNowPlaying(mp);
            Console.WriteLine("[nowplaying] registered with the Mac's Now Playing and media keys");
            return me;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[nowplaying] unavailable: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------- commands

    private static readonly (string Command, string Selector)[] Commands =
    [
        ("playCommand", "awPlay:"),
        ("pauseCommand", "awPause:"),
        ("togglePlayPauseCommand", "awToggle:"),
        ("nextTrackCommand", "awNext:"),
        ("previousTrackCommand", "awPrevious:"),
        ("stopCommand", "awStop:"),
        ("changePlaybackPositionCommand", "awSeek:"),
    ];

    private static unsafe void RegisterCommands()
    {
        var cls = objc_getClass("AlbumWallRemoteTarget");
        if (cls == IntPtr.Zero)
        {
            cls = objc_allocateClassPair(Class("NSObject"), "AlbumWallRemoteTarget", 0);
            // "q@:@": returns NSInteger (MPRemoteCommandHandlerStatus), takes the event.
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint> play = &OnPlay, pause = &OnPause,
                toggle = &OnToggle, next = &OnNext, previous = &OnPrevious, stop = &OnStop, seek = &OnSeek;
            class_addMethod(cls, Sel("awPlay:"), (IntPtr)play, "q@:@");
            class_addMethod(cls, Sel("awPause:"), (IntPtr)pause, "q@:@");
            class_addMethod(cls, Sel("awToggle:"), (IntPtr)toggle, "q@:@");
            class_addMethod(cls, Sel("awNext:"), (IntPtr)next, "q@:@");
            class_addMethod(cls, Sel("awPrevious:"), (IntPtr)previous, "q@:@");
            class_addMethod(cls, Sel("awStop:"), (IntPtr)stop, "q@:@");
            class_addMethod(cls, Sel("awSeek:"), (IntPtr)seek, "q@:@");
            objc_registerClassPair(cls);
        }
        var target = Send(Send(cls, Sel("alloc")), Sel("init"));      // kept for the life of the process

        var center = Send(Class("MPRemoteCommandCenter"), Sel("sharedCommandCenter"));
        foreach (var (command, selector) in Commands)
        {
            var c = Send(center, Sel(command));
            SendPtrPtr(c, Sel("addTarget:action:"), target, Sel(selector));
            SendBool(c, Sel("setEnabled:"), true);
        }
    }

    // MPRemoteCommandHandlerStatus: 0 success, 100 commandFailed.
    [UnmanagedCallersOnly] private static nint OnPlay(IntPtr self, IntPtr sel, IntPtr e) => Do("Play");
    [UnmanagedCallersOnly] private static nint OnPause(IntPtr self, IntPtr sel, IntPtr e) => Do("Pause");
    [UnmanagedCallersOnly] private static nint OnToggle(IntPtr self, IntPtr sel, IntPtr e) => Do("PlayPause");
    [UnmanagedCallersOnly] private static nint OnNext(IntPtr self, IntPtr sel, IntPtr e) => Do("Next");
    [UnmanagedCallersOnly] private static nint OnPrevious(IntPtr self, IntPtr sel, IntPtr e) => Do("Previous");
    [UnmanagedCallersOnly] private static nint OnStop(IntPtr self, IntPtr sel, IntPtr e) => Do("Stop");

    /// The bar in Now Playing was dragged: the event carries where to.
    [UnmanagedCallersOnly]
    private static nint OnSeek(IntPtr self, IntPtr sel, IntPtr e)
    {
        try
        {
            var seconds = SendDouble(e, Sel("positionTime"));
            _seek?.Invoke(TimeSpan.FromSeconds(seconds));
            return 0;
        }
        catch { return 100; }
    }

    private static nint Do(string command)
    {
        try { _command?.Invoke(command); return 0; }
        catch { return 100; }
    }

    // ---------------------------------------------------------- what is shown

    /// Tells the system what is playing. `album` is where the cover comes from,
    /// read as bytes like Smtc's, because most of this library's art is embedded
    /// in the tracks and has no file to point at.
    public void Update(Mpris.State state, Domain.Album? album)
    {
        try
        {
            if (state.TrackKey != _state.TrackKey) { _position = TimeSpan.Zero; _duration = TimeSpan.Zero; }
            _state = state;
            if (!state.HasTrack)
            {
                SendPtr(_center, Sel("setNowPlayingInfo:"), IntPtr.Zero);
                SendLong(_center, Sel("setPlaybackState:"), StateStopped);
                _artFor = null;
                return;
            }

            var key = album is null ? null : album.ArtPath ?? album.ArtEmbeddedIn;
            if (key != _artFor)
            {
                _artFor = key;
                SetArtwork(IntPtr.Zero);
                _ = LoadCoverAsync(album, key);
            }
            Publish();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[nowplaying] update failed: {ex.Message}");
        }
    }

    /// Where the track is, and how long. macOS moves the bar itself between calls
    /// from the elapsed time and the rate, so this is sent on a change and every
    /// couple of seconds, as Smtc's is.
    ///
    /// The LENGTH is taken from here too. A track's State is made the moment it
    /// starts, usually before mpv knows how long it is, so its length is 0, and
    /// with no length Now Playing draws no bar at all (2026-10-14, first try:
    /// "no progress bar in control center").
    public void UpdateTimeline(TimeSpan position, TimeSpan duration)
    {
        if (!_state.HasTrack) return;
        _position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (duration > TimeSpan.Zero) _duration = duration;
        try { Publish(); }
        catch (Exception ex) { Console.WriteLine($"[nowplaying] timeline failed: {ex.Message}"); }
    }

    private void Publish()
    {
        var info = Send(Class("NSMutableDictionary"), Sel("dictionary"));
        void Put(string key, IntPtr value)
        {
            if (value != IntPtr.Zero) SendPtrPtr(info, Sel("setObject:forKey:"), value, Constant(key));
        }

        Put("MPMediaItemPropertyTitle", NSString(_state.Title));
        Put("MPMediaItemPropertyArtist", NSString(_state.Artist));
        Put("MPMediaItemPropertyAlbumTitle", NSString(_state.Album));
        Put("MPNowPlayingInfoPropertyMediaType", Number(1));               // MPNowPlayingInfoMediaTypeAudio
        var length = _duration > TimeSpan.Zero ? _duration.TotalSeconds : _state.LengthMicros / 1e6;
        if (length > 0)
            Put("MPMediaItemPropertyPlaybackDuration", Number(length));
        Put("MPNowPlayingInfoPropertyElapsedPlaybackTime", Number(_position.TotalSeconds));
        Put("MPNowPlayingInfoPropertyPlaybackRate", Number(_state.Playing ? 1.0 : 0.0));
        Put("MPMediaItemPropertyArtwork", _artwork);

        SendPtr(_center, Sel("setNowPlayingInfo:"), info);
        SendLong(_center, Sel("setPlaybackState:"), _state.Playing ? StatePlaying : StatePaused);
    }

    private async Task LoadCoverAsync(Domain.Album? album, string? key)
    {
        try
        {
            var bytes = await Task.Run(() => ReadCover(album));
            if (key != _artFor || bytes is null) return;      // the record changed while we read

            var data = SendBytes(Class("NSData"), Sel("dataWithBytes:length:"), bytes, (nuint)bytes.Length);
            var image = Send(Send(Class("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
            if (image == IntPtr.Zero) return;
            SetArtwork(image);
            Publish();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[nowplaying] cover failed: {ex.Message}");
        }
    }

    private static byte[]? ReadCover(Domain.Album? album)
    {
        if (album is null) return null;
        if (album.ArtEmbeddedIn is { } track)
        {
            using var file = TagLib.File.Create(track);
            return Domain.ImageSize.Cover(file.Tag.Pictures);
        }
        return album.ArtPath is { } path && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// Takes ownership of `image` (retained, or zero for none) and wraps it in the
    /// MPMediaItemArtwork that Publish hands over.
    private unsafe void SetArtwork(IntPtr image)
    {
        if (_artwork != IntPtr.Zero) { Send(_artwork, Sel("release")); _artwork = IntPtr.Zero; }
        if (_image != IntPtr.Zero) Send(_image, Sel("release"));
        _image = image;
        if (image == IntPtr.Zero) return;

        var size = SendSize(image, Sel("size"));
        _artwork = SendSizeBlock(Send(Class("MPMediaItemArtwork"), Sel("alloc")),
                                 Sel("initWithBoundsSize:requestHandler:"), size, ArtBlock());
    }

    /// The requestHandler: a global block, which captures nothing and so needs
    /// no copying or freeing, whose function returns the current cover at
    /// whatever size is asked for (an NSImage scales itself when drawn).
    private static unsafe IntPtr ArtBlock()
    {
        if (_artBlock != IntPtr.Zero) return _artBlock;
        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        descriptor->Size = (ulong)sizeof(BlockLiteral);
        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        block->Isa = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");
        block->Flags = 1 << 28;                                  // BLOCK_IS_GLOBAL
        delegate* unmanaged<IntPtr, CGSize, IntPtr> invoke = &ArtFor;
        block->Invoke = (IntPtr)invoke;
        block->Descriptor = (IntPtr)descriptor;
        return _artBlock = (IntPtr)block;
    }

    [UnmanagedCallersOnly]
    private static IntPtr ArtFor(IntPtr block, CGSize size) => _image;

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public ulong Reserved;
        public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    // ---------------------------------------------------------------- runtime

    private IntPtr Constant(string name) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(_mediaPlayer, name));

    private static IntPtr NSString(string s)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(s);
        try { return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static IntPtr Number(double d) => SendDoubleArg(Class("NSNumber"), Sel("numberWithDouble:"), d);

    private static IntPtr Class(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendPtr(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendPtrPtr(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendLong(IntPtr r, IntPtr s, long a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr r, IntPtr s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDoubleArg(IntPtr r, IntPtr s, double a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendBytes(IntPtr r, IntPtr s, byte[] bytes, nuint length);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGSize SendSize(IntPtr r, IntPtr s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendSizeBlock(IntPtr r, IntPtr s, CGSize size, IntPtr block);
}
