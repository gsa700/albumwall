// AlbumWall — playback.
//
// All the policy lives here; Mpv.cs is only the wire. The requirements this is
// built to meet, from the spec:
//
//   * GAPLESS. Non-negotiable, and the reason the backend was chosen first.
//   * NATIVE SAMPLE RATE, no unnecessary resampling. This library is 100%
//     16-bit/44.1 kHz, and a player that hands it to a 48 kHz graph has thrown
//     away the point of ripping to FLAC.
//   * REPLAYGAIN from tags: album gain for album playback, track gain for
//     shuffle, never silently applied without being visible.
//
// THE QUEUE IS MPV'S PLAYLIST, and the app reads its position back rather than
// tracking one of its own. The first version did the opposite — kept the queue
// here and fed mpv one file at a time — on the reasoning that owning it was less
// fragile. That was wrong twice over. mpv cannot decode the next track ahead of
// time if it does not know what the next track is, so gapless was impossible by
// construction; and the hand-off between tracks depended on an event that
// `keep-open=yes` prevents from ever arriving, so an album simply stopped after
// its first track.
//
// Reading `playlist-pos` back means the app's idea of the current track cannot
// drift from what is actually coming out of the speakers.

using System.Runtime.InteropServices;

namespace AlbumWall.Playback;

public enum GainMode { Off, Track, Album }

public sealed class TrackChangedEventArgs(int index) : EventArgs
{
    public int Index { get; } = index;
}

public sealed class Player : IDisposable
{
    private IntPtr _ctx;
    private Thread? _pump;
    private volatile bool _running;

    private readonly List<string> _queue = [];
    private int _index = -1;
    private readonly Lock _gate = new();

    /// Whether playback is possible at all on this machine.
    public static bool IsAvailable => Mpv.IsAvailable;

    public bool IsPlaying { get; private set; }
    public int Index => _index;
    public int Count { get { lock (_gate) return _queue.Count; } }

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? Finished;

    public Player(GainMode gain = GainMode.Album)
    {
        if (!Mpv.IsAvailable)
            throw new InvalidOperationException(
                "libmpv is not installed. On Fedora: sudo dnf install mpv-libs");

        _ctx = Mpv.mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create failed");

        // Options must be set BEFORE mpv_initialize; several of these are
        // read-only afterwards.
        Option("vid", "no");                 // audio only; there is no video here
        Option("audio-display", "no");       // do not treat cover art as a video track
        Option("gapless-audio", "yes");      // the hard requirement
        Option("prefetch-playlist", "yes");  // decode the next file before it is needed

        // Let the output run at the FILE's rate rather than resampling to a fixed
        // one. On Linux this asks PipeWire to switch the graph; on Windows it is
        // what makes WASAPI exclusive mode meaningful.
        Option("audio-samplerate", "0");
        Option("audio-exclusive", "no");     // opt-in later; it can block other apps

        // keep-open MUST be off. With it on, mpv pauses at the end of a file
        // instead of unloading it, the playlist never advances, and playback
        // simply stops after track one — which is exactly what it did.
        Option("keep-open", "no");
        Option("idle", "yes");   // do not exit when the album finishes

        ApplyGain(gain);

        var rc = Mpv.mpv_initialize(_ctx);
        if (rc < 0)
        {
            Mpv.mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
            throw new InvalidOperationException($"mpv_initialize: {Mpv.ErrorText(rc)}");
        }

        Mpv.mpv_observe_property(_ctx, 2, "pause", Mpv.Format.Flag);
        Mpv.mpv_observe_property(_ctx, 4, "playlist-pos", Mpv.Format.Int64);
        Mpv.mpv_observe_property(_ctx, 5, "idle-active", Mpv.Format.Flag);

        _running = true;
        _pump = new Thread(Pump) { IsBackground = true, Name = "mpv events" };
        _pump.Start();
    }

    private GainMode _gain;

    /// ReplayGain. Album gain preserves the relative loudness the record was
    /// mastered with, which is what album playback wants; track gain flattens
    /// that out, which is what shuffle wants.
    public GainMode Gain
    {
        get => _gain;
        set { _gain = value; ApplyGain(value); }
    }

    private void ApplyGain(GainMode mode)
    {
        _gain = mode;
        var value = mode switch
        {
            GainMode.Album => "album",
            GainMode.Track => "track",
            _ => "no"
        };
        if (_ctx != IntPtr.Zero) Mpv.mpv_set_property_string(_ctx, "replaygain", value);
        else Option("replaygain", value);
    }

    private void Option(string name, string value) => Mpv.mpv_set_option_string(_ctx, name, value);

    /// Loads the whole album as an mpv PLAYLIST and starts at `start`.
    ///
    /// The entire album goes to mpv at once, rather than being fed one file at a
    /// time. That is what makes gapless real: mpv can only decode the next track
    /// ahead of time if it knows what the next track IS. Driving it with
    /// `loadfile ... replace` per track meant every boundary was a fresh open,
    /// which is the opposite of gapless.
    ///
    /// Position is then read back from mpv's `playlist-pos` rather than tracked
    /// here, so the app's idea of the current track cannot drift from what is
    /// actually coming out of the speakers.
    public void Play(IEnumerable<string> paths, int start = 0)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;

        lock (_gate)
        {
            _queue.Clear();
            _queue.AddRange(list);
            _index = -1;
        }

        // NEVER USE `loadfile ... replace` HERE. It returns immediately but starts
        // playing entry 0 asynchronously, and that deferred start RESETS
        // playlist-pos — clobbering the position set straight after it. The log
        // shows it exactly: `playlist-pos -> 15` (ours) followed by
        // `playlist-pos -> 0` (mpv's). That is why clicking a track sometimes
        // played the first one, and why clicking again always worked: by then the
        // playlist was loaded and there was no pending start to override us.
        //
        // `stop` clears the playlist outright, and `append` onto an idle player
        // starts nothing. Setting playlist-pos is then the ONLY thing that begins
        // playback, so there is no race to lose.
        var hold = 1;
        Mpv.mpv_set_property(_ctx, "pause", Mpv.Format.Flag, ref hold);

        Mpv.Command(_ctx, "stop");
        foreach (var path in list)
            Mpv.Command(_ctx, "loadfile", path, "append");

        var wanted = Math.Clamp(start, 0, list.Count - 1);
        var rc = Mpv.mpv_set_property_string(_ctx, "playlist-pos", wanted.ToString());

        Mpv.mpv_get_property(_ctx, "playlist-count", Mpv.Format.Int64, out long count);
        Console.WriteLine($"[mpv] queued {list.Count} (mpv says {count}), "
                        + $"asked for index {start} -> {wanted}"
                        + (rc < 0 ? $"  FAILED: {Mpv.ErrorText(rc)}" : "")
                        + $"  want: {System.IO.Path.GetFileName(list[wanted])}");

        SetPaused(false);
    }

    public void PlayIndex(int index)
    {
        lock (_gate)
            if (index < 0 || index >= _queue.Count) return;

        Mpv.mpv_set_property_string(_ctx, "playlist-pos", index.ToString());
        SetPaused(false);
    }

    /// `weak` means "do nothing at the end of the playlist" rather than wrapping
    /// or erroring — the album ending is handled by idle-active instead.
    public void Next() => Mpv.Command(_ctx, "playlist-next", "weak");

    public void Previous()
    {
        // Below three seconds, go to the previous track; past that, restart the
        // current one. This is what every physical player does and what the hand
        // expects.
        if (Position > TimeSpan.FromSeconds(3)) { Seek(TimeSpan.Zero); return; }
        Mpv.Command(_ctx, "playlist-prev", "weak");
    }

    public void TogglePause() => SetPaused(IsPlaying);

    public void SetPaused(bool paused)
    {
        var flag = paused ? 1 : 0;
        Mpv.mpv_set_property(_ctx, "pause", Mpv.Format.Flag, ref flag);
        IsPlaying = !paused;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        Mpv.Command(_ctx, "stop");
        IsPlaying = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan to) =>
        Mpv.Command(_ctx, "seek", to.TotalSeconds.ToString("0.###"), "absolute");

    public TimeSpan Position =>
        Mpv.mpv_get_property(_ctx, "time-pos", Mpv.Format.Double, out double v) == 0 && v > 0
            ? TimeSpan.FromSeconds(v)
            : TimeSpan.Zero;

    public TimeSpan Duration =>
        Mpv.mpv_get_property(_ctx, "duration", Mpv.Format.Double, out double v) == 0 && v > 0
            ? TimeSpan.FromSeconds(v)
            : TimeSpan.Zero;

    /// Drains mpv's event queue on its own thread.
    ///
    /// mpv_wait_event must be called from a single thread and its returned event
    /// is only valid until the next call, so nothing here may be handed out
    /// without being copied first.
    private void Pump()
    {
        while (_running)
        {
            var ptr = Mpv.mpv_wait_event(_ctx, 0.2);
            if (ptr == IntPtr.Zero) continue;

            var ev = Marshal.PtrToStructure<Mpv.Event>(ptr);
            switch (ev.Id)
            {
                case Mpv.EventId.Shutdown:
                    _running = false;
                    break;

                case Mpv.EventId.PropertyChange:
                    OnPropertyChanged(ev);
                    break;
            }
        }
    }

    /// mpv advances the playlist itself; this is how the app finds out.
    private void OnPropertyChanged(Mpv.Event ev)
    {
        if (ev.Data == IntPtr.Zero) return;
        var prop = Marshal.PtrToStructure<Mpv.EventProperty>(ev.Data);
        if (prop.Data == IntPtr.Zero) return;

        switch (ev.ReplyUserdata)
        {
            case 2:     // pause
                IsPlaying = Marshal.ReadInt32(prop.Data) == 0;
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;

            case 4:     // playlist-pos
                var pos = (int)Marshal.ReadInt64(prop.Data);
                if (pos < 0 || pos == _index) return;
                _index = pos;
                lock (_gate)
                    if (pos < _queue.Count)
                        Console.WriteLine($"[mpv] playlist-pos -> {pos}  "
                                        + $"{System.IO.Path.GetFileName(_queue[pos])}");
                TrackChanged?.Invoke(this, new TrackChangedEventArgs(pos));
                break;

            case 5:     // idle-active — the playlist ran out
                if (Marshal.ReadInt32(prop.Data) == 0) return;
                IsPlaying = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
                Finished?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_ctx != IntPtr.Zero)
        {
            Mpv.mpv_wakeup(_ctx);
            _pump?.Join(500);
            Mpv.mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
