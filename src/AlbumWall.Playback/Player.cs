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
// The queue is OURS, not mpv's playlist. mpv is asked to play one file at a time
// with the next one preloaded, because the app needs to know precisely which
// track is playing to drive the UI, and reading that back out of mpv's playlist
// state is more fragile than simply owning it.

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

        // Keep the last file loaded when it ends, so the end of a track is an
        // event we handle rather than mpv going idle and tearing down the output.
        Option("keep-open", "yes");
        Option("idle", "yes");

        ApplyGain(gain);

        var rc = Mpv.mpv_initialize(_ctx);
        if (rc < 0)
        {
            Mpv.mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
            throw new InvalidOperationException($"mpv_initialize: {Mpv.ErrorText(rc)}");
        }

        Mpv.mpv_observe_property(_ctx, 1, "time-pos", Mpv.Format.Double);
        Mpv.mpv_observe_property(_ctx, 2, "pause", Mpv.Format.Flag);
        Mpv.mpv_observe_property(_ctx, 3, "eof-reached", Mpv.Format.Flag);

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

    /// Replaces the queue and starts at `start`.
    public void Play(IEnumerable<string> paths, int start = 0)
    {
        lock (_gate)
        {
            _queue.Clear();
            _queue.AddRange(paths);
            _index = -1;
        }
        PlayIndex(start);
    }

    public void PlayIndex(int index)
    {
        string path;
        lock (_gate)
        {
            if (index < 0 || index >= _queue.Count) return;
            _index = index;
            path = _queue[index];
        }

        Mpv.Command(_ctx, "loadfile", path, "replace");
        SetPaused(false);
        TrackChanged?.Invoke(this, new TrackChangedEventArgs(index));
    }

    public void Next()
    {
        int next;
        lock (_gate) next = _index + 1 < _queue.Count ? _index + 1 : -1;

        if (next < 0) { Stop(); Finished?.Invoke(this, EventArgs.Empty); return; }
        PlayIndex(next);
    }

    public void Previous()
    {
        // Below three seconds, go to the previous track; past that, restart the
        // current one. This is what every physical player does and what the hand
        // expects.
        if (Position > TimeSpan.FromSeconds(3)) { Seek(TimeSpan.Zero); return; }

        int prev;
        lock (_gate) prev = _index > 0 ? _index - 1 : 0;
        PlayIndex(prev);
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

                case Mpv.EventId.EndFile:
                    // A file ending is how we advance. With gapless the next file
                    // is already decoded, so this is bookkeeping rather than the
                    // thing that causes the sound to continue.
                    if (IsPlaying) Next();
                    break;

                case Mpv.EventId.PropertyChange:
                    if (ev.ReplyUserdata == 2) StateChanged?.Invoke(this, EventArgs.Empty);
                    break;
            }
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
