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

    /// The index Play was asked for, held until mpv is observed to be ON it.
    ///
    /// Ordering games with mpv have now failed twice — first `loadfile replace`
    /// started entry 0 asynchronously and overwrote the position, then `stop` plus
    /// appends did something equivalent. Rather than keep guessing at the command
    /// sequence that has no race in it, the intent is remembered and reasserted
    /// if mpv is seen landing anywhere else. Self-correcting beats correct-by-
    /// argument when the argument keeps losing.
    private int _pending = -1;
    private int _pendingTries;

    /// How long the intent is DEFENDED for.
    ///
    /// Disarming the moment mpv is first seen on the right track was not enough:
    /// it lands correctly, then drifts to entry 0 a moment later, and by then
    /// nothing is watching. So the intent is held for a window rather than
    /// cleared on first sight. Three seconds is longer than any of the observed
    /// drifts and far shorter than any track, so a natural advance to the next
    /// track cannot be mistaken for drift.
    private DateTime _pendingUntil = DateTime.MinValue;
    private bool _pendingStarted;

    /// Set when a queue is being RESTORED rather than played: arrive on the
    /// track, at the position, and stay silent. See Play's `paused`.
    private bool _pendingHold;

    /// True while mpv's `start` option holds a resume position that has to be
    /// taken away again once the file it was meant for has loaded.
    private bool _startArmed;

    /// Whether playback is possible at all on this machine.
    public static bool IsAvailable => Mpv.IsAvailable;

    public bool IsPlaying { get; private set; }
    public int Index => _index;
    public int Count { get { lock (_gate) return _queue.Count; } }

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? Finished;

    /// The track's length has become known, or changed. mpv learns it only once
    /// a file has loaded, AFTER TrackChanged has gone out, so anything that
    /// captured Duration at the change captured zero.
    public event EventHandler? DurationChanged;

    /// Playback jumped, by anyone's hand: the app's bar, the desktop's, or
    /// Previous restarting a track. Carries where it was asked to go.
    public event EventHandler<TimeSpan>? Seeked;

    public Player(GainMode gain = GainMode.Album)
    {
        if (!Mpv.IsAvailable)
            throw new InvalidOperationException(
                "libmpv is not installed. On Fedora: sudo dnf install mpv-libs");

        _ctx = Mpv.mpv_create();
        if (Mpv.LoadedFrom() is { } lib) Console.WriteLine($"[mpv] library: {lib}");
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create failed");

        // Options must be set BEFORE mpv_initialize; several of these are
        // read-only afterwards.
        // What the system mixer calls this stream. Without it every mpv-based
        // app is "mpv", and the one slider you are looking for is anonymous.
        Option("audio-client-name", "AlbumWall");

        // ...which is only half of it. audio-client-name is what PulseAudio,
        // PipeWire and JACK read. WASAPI has no client name; mpv names the
        // Windows session from its WINDOW TITLE option instead, whose default is
        // "<media title> - mpv" — and that is what his volume mixer said:
        // "mixer has song title - mpv". There is no window here, so the option
        // has no other job. Windows only: on Linux the same string becomes the
        // stream's media name, where the song title is the useful thing to show.
        if (OperatingSystem.IsWindows()) Option("title", "AlbumWall");

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
        Mpv.mpv_observe_property(_ctx, 6, "duration", Mpv.Format.Double);

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

    private int _volume = 100;

    /// The app's own volume, 0-100, independent of the system mixer.
    ///
    /// mpv's software volume, so it attenuates only this app's stream — which is
    /// the point: turning the music down so a notification can be heard over it
    /// should not touch anything else that is making noise.
    ///
    /// NOT the same lever as ReplayGain. That normalizes between records; this is
    /// how loud the app is playing right now.
    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            var v = _volume.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_ctx == IntPtr.Zero) { Option("volume", v); return; }

            // mpv reports refusals rather than throwing, and a volume that
            // silently did not apply is the kind of thing you only notice later.
            var rc = Mpv.mpv_set_property_string(_ctx, "volume", v);
            if (rc < 0) Console.WriteLine($"[mpv] volume {v} refused: {rc}");
        }
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
    ///
    /// `at` and `paused` are for picking up where the last run left off: land on
    /// the track at that position and wait. It goes through exactly the same
    /// path as a real play — the same stop/append/play-index, the same
    /// reassertion if mpv drifts — because that path exists to defeat a race
    /// that only shows on the FIRST play after launch, which is precisely when
    /// a restore happens. The one difference is the last step: on arriving,
    /// hold instead of releasing.
    ///
    /// The position is given to mpv as its `start` option rather than as a seek
    /// afterwards. A seek needs a loaded file and there is no good moment to
    /// send one from here; `start` is read by the loader itself, so the file
    /// opens already at the right place, paused, with nothing heard from the
    /// beginning of the track. It is a global option, so it is cleared again
    /// the moment that file has loaded — left set, every later track would
    /// start part-way through.
    public void Play(IEnumerable<string> paths, int start = 0,
                     TimeSpan? at = null, bool paused = false)
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

        // Armed BEFORE the stop, not after it. The stop makes mpv idle for a
        // moment, and the event pump tells that idle from a record ending by
        // whether a request is in flight (see idle-active below). Armed
        // afterwards, there was a window in which it could not tell.
        var wanted = Math.Clamp(start, 0, list.Count - 1);
        _pending = wanted;
        _pendingTries = 0;
        _pendingStarted = false;
        _pendingHold = paused;
        _pendingUntil = DateTime.UtcNow.AddSeconds(3);

        Mpv.Command(_ctx, "stop");
        foreach (var path in list)
            Mpv.Command(_ctx, "loadfile", path, "append");

        _startArmed = at is { TotalSeconds: > 0.5 };
        Mpv.mpv_set_property_string(_ctx, "start",
            _startArmed ? at!.Value.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                        : "none");

        // `playlist-play-index` is the purpose-built command for "play this entry",
        // rather than setting a property and hoping nothing else writes it.
        var rc = Mpv.Command(_ctx, "playlist-play-index", wanted.ToString());

        Mpv.mpv_get_property(_ctx, "playlist-count", Mpv.Format.Int64, out long count);
        Console.WriteLine($"[mpv] queued {list.Count} (mpv says {count}), "
                        + $"asked for index {start} -> {wanted}"
                        + (rc < 0 ? $"  FAILED: {Mpv.ErrorText(rc)}" : "")
                        + $"  want: {System.IO.Path.GetFileName(list[wanted])}");

        // DELIBERATELY STILL PAUSED. When mpv is idle and the playlist is empty,
        // appending the first file makes it start playing entry 0 on its own —
        // which is why this only ever went wrong on the FIRST play after launch,
        // and was fine every time after. Sound is released below, from the event
        // pump, once mpv is observed to actually be on the requested track.
    }

    public void PlayIndex(int index)
    {
        lock (_gate)
            if (index < 0 || index >= _queue.Count) return;

        _pending = index;
        _pendingTries = 0;
        _pendingStarted = false;
        _pendingHold = false;
        _pendingUntil = DateTime.UtcNow.AddSeconds(3);
        Mpv.Command(_ctx, "playlist-play-index", index.ToString());
    }

    /// `weak` means "do nothing at the end of the playlist" rather than wrapping
    /// or erroring — the album ending is handled by idle-active instead.
    public void Next()
    {
        _pending = -1;
        _pendingUntil = DateTime.MinValue;
        Mpv.Command(_ctx, "playlist-next", "weak");
    }

    public void Previous()
    {
        // Below three seconds, go to the previous track; past that, restart the
        // current one. This is what every physical player does and what the hand
        // expects.
        if (Position > TimeSpan.FromSeconds(3)) { Seek(TimeSpan.Zero); return; }
        _pending = -1;
        _pendingUntil = DateTime.MinValue;
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
        // No request is in flight any more, so the idle this causes is a real
        // ending and must be reported as one (see idle-active in the pump) —
        // even when Stop comes within the three seconds a Play() stays armed.
        _pending = -1;
        Mpv.Command(_ctx, "stop");
        IsPlaying = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan to)
    {
        Mpv.Command(_ctx, "seek", to.TotalSeconds.ToString("0.###"), "absolute");
        Seeked?.Invoke(this, to);
    }

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
            // The pump wakes at least every 200 ms, which makes it the right
            // place to enforce the deadline: if mpv never reaches the requested
            // track, playback must not sit silently paused forever.
            if (_pending >= 0 && DateTime.UtcNow >= _pendingUntil)
            {
                if (!_pendingStarted)
                {
                    Console.WriteLine($"[mpv] never reached {_pending}; "
                                    + (_pendingHold ? "holding anyway" : "playing anyway"));
                    _pendingStarted = true;
                    SetPaused(_pendingHold);
                }
                _pending = -1;
            }

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

                case Mpv.EventId.FileLoaded:
                    // The loader has read `start`; take it away before the next
                    // track can inherit it.
                    if (_startArmed)
                    {
                        _startArmed = false;
                        Mpv.mpv_set_property_string(_ctx, "start", "none");
                    }

                    // AND ASK WHERE IT IS, because the property observer will not
                    // always say. mpv reports a property only when its value
                    // differs from the last one it REPORTED, and Play() takes
                    // playlist-pos from N to -1 to N faster than that is looked
                    // at. So starting track 1 of one album while track 1 of
                    // another was playing — pressing two sleeves in a row, the
                    // most ordinary thing there is — produced no event at all:
                    // three seconds of silence until the deadline above gave up
                    // ("never reached 0; playing anyway"), then music with Index
                    // still -1, so no play controls and no track marked. Measured
                    // on Hambench 2026-09-20, on the build before this one. A file
                    // having loaded is the one thing that always happens.
                    if (Mpv.mpv_get_property(_ctx, "playlist-pos", Mpv.Format.Int64, out long at) == 0)
                        OnPosition((int)at);
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
                OnPosition((int)Marshal.ReadInt64(prop.Data));
                break;

            case 5:     // idle-active — the playlist ran out
                if (Marshal.ReadInt32(prop.Data) == 0) return;

                // Or Play() has just said `stop`, which is idle too, for an
                // instant. A request in flight means this is that, whatever
                // Index says by the time the event is looked at.
                if (_pending >= 0) return;

                IsPlaying = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
                Finished?.Invoke(this, EventArgs.Empty);

                // AND THEN IT IS ON NO TRACK, which has to be said here because
                // nothing else will say it. mpv does report playlist-pos -1 when
                // the queue runs out, and OnPosition throws every negative
                // position away — rightly, they also arrive in the middle of
                // Play() — so Index went on naming the last track of a record
                // that had finished, and the app, asking "is it on a track?",
                // kept the play controls up over nothing: last title, 0:00, a
                // Play button that played nothing. His report, 2026-09-20: "the
                // control bar should disappear when there is no album playing".
                //
                // After Finished, not before: a listener can still see which
                // track it ended on.
                _index = -1;
                break;

            case 6:     // duration
                DurationChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// Where mpv says it is. Reached from the playlist-pos observer and from
    /// every file load, since the observer alone can miss one (see FileLoaded).
    /// Safe to call twice with the same answer.
    private void OnPosition(int pos)
    {
        if (pos < 0) return;

        if (_pending >= 0)
        {
            if (DateTime.UtcNow >= _pendingUntil)
            {
                _pending = -1;              // window closed; whatever it is now, it is
            }
            else if (pos != _pending)
            {
                // Landed somewhere we did not ask for: put it back.
                if (_pendingTries++ < 5)
                {
                    Console.WriteLine($"[mpv] drifted to {pos}, reasserting {_pending} "
                                    + $"(attempt {_pendingTries})");
                    Mpv.Command(_ctx, "playlist-play-index", _pending.ToString());
                    return;
                }
                Console.WriteLine($"[mpv] gave up reasserting {_pending}; sitting at {pos}");
                _pending = -1;
            }
            else if (!_pendingStarted)
            {
                // On the requested track at last — let it be heard,
                // unless this is a restore, which arrives and waits.
                _pendingStarted = true;
                SetPaused(_pendingHold);
            }
            // A match does NOT disarm: it may still drift away afterwards.
        }

        if (pos == _index) return;
        _index = pos;
        lock (_gate)
            if (pos < _queue.Count)
                Console.WriteLine($"[mpv] playlist-pos -> {pos}  "
                                + $"{System.IO.Path.GetFileName(_queue[pos])}");
        TrackChanged?.Invoke(this, new TrackChangedEventArgs(pos));
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
