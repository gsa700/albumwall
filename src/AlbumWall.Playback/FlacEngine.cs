// AlbumWall — its own audio engine: FLAC, on the Mac and Linux (roadmap, "Our
// own audio engine: mpv out, in steps", steps 1 and 2a, started 2026-10-21).
// The output is the platform's (Output.cs); everything here is plain .NET.
//
// One continuous stream of samples. The album's tracks are decoded one after
// another into the same output, so nothing happens at a track boundary: the
// last sample of one track is followed by the first of the next, which is
// gapless by construction rather than by prefetching and splicing. Where each
// track begins on the output's timeline is written down as it is decoded (a
// Mark), and the track playing NOW is read from how far the output has
// actually played, so the app's idea of the current track follows the
// speakers, not the decoder, which runs about 0.4 s ahead.
//
// Only when two tracks differ in sample rate or channels does the stream
// break: the first is let play out and a new output is made for the second.
// A CD rip never does that.
//
// Levels and gain match mpv's (FlacTrack.ReplayGain, Volume below), so the
// same record plays at the same loudness whichever engine plays it.

namespace AlbumWall.Playback;

internal sealed class FlacEngine : IEngine
{
    private readonly Lock _lock = new();
    private List<string> _queue = [];

    // The output, and the decoding that feeds it (on the output's thread,
    // under _lock). _generation tells a callback from an output already
    // thrown away that it has nothing more to say.
    private IOutput? _out;
    private int _generation;
    private FlacTrack? _decoding;
    private int _decodingIndex = -1;
    private float _decodingGain = 1f;
    private long _handed;                       // frames given to _out so far
    private readonly List<Mark> _marks = [];
    private FlacTrack? _afterBreak;             // the next track, when its format needs a new output

    private Task<FlacTrack?>? _prefetch;
    private int _prefetchIndex = -1;

    private volatile int _index = -1;
    private volatile bool _playing;
    private TimeSpan _duration;
    private GainMode _gain;
    private int _volume = 100;

    private readonly Thread _watch;
    private volatile bool _running = true;

    /// Where a track starts on the output's timeline: frame At of the output is
    /// frame TrackFrame of track Index (not 0 after a seek).
    private readonly record struct Mark(long At, int Index, long TrackFrame, FlacTrack Track);

    public FlacEngine(GainMode gain)
    {
        _gain = gain;
        Array.Fill(_levelBlock, -1);
        _watch = new Thread(Watch) { IsBackground = true, Name = "engine watch" };
        _watch.Start();
    }

    /// Whether this engine can play a queue: local FLAC files, every one.
    /// Anything else, a stream from a server included, stays with mpv.
    public static bool CanPlay(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(p => !p.Contains("://")
                                          && p.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
                                          && File.Exists(p));

    public bool IsPlaying => _playing;
    public int Index => _index;
    public int Count { get { lock (_lock) return _queue.Count; } }
    public bool Metering => true;

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? Finished;
    public event EventHandler? DurationChanged;
    public event EventHandler<TimeSpan>? Seeked;

    public GainMode Gain
    {
        get => _gain;
        set
        {
            _gain = value;
            // Heard from the next samples decoded, which is a buffer's length
            // (0.4 s) from now: what is already waiting keeps the gain it had.
            lock (_lock)
                if (_decoding is not null) _decodingGain = _decoding.Gain.Multiplier(value);
        }
    }

    /// 0-100 on mpv's curve, the cube of the fraction (mpv's audio_get_gain),
    /// so the slider sounds the same whichever engine is playing. Set on the
    /// output's own volume, which is heard at once.
    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            lock (_lock) _out?.SetVolume(Cube(_volume));
        }
    }

    private static float Cube(int volume) => MathF.Pow(volume / 100f, 3);

    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                if (_out is null || Current(_out.Played) is not { } m) return TimeSpan.Zero;
                return TimeSpan.FromSeconds((double)(_out.Played - m.At + m.TrackFrame) / _out.SampleRate);
            }
        }
    }

    public TimeSpan Duration => _duration;

    /// The mark for what the output is playing now.
    private Mark? Current(long played)
    {
        Mark? found = null;
        foreach (var m in _marks)
        {
            if (m.At > played) break;
            found = m;
        }
        return found;
    }

    public void Play(IReadOnlyList<string> paths, int start, TimeSpan? at, bool paused)
    {
        if (paths.Count == 0) return;
        Discard();
        lock (_lock)
        {
            _queue = [.. paths];
            _prefetch = null;
            _prefetchIndex = -1;
        }
        _index = -1;

        var index = Math.Clamp(start, 0, paths.Count - 1);
        var track = OpenFrom(ref index);
        if (track is null) { Console.WriteLine("[engine] nothing in the queue would open"); return; }

        long frame = 0;
        if (at is { TotalSeconds: > 0.5 } a && index == Math.Clamp(start, 0, paths.Count - 1))
        {
            frame = (long)(a.TotalSeconds * track.Info.SampleRate);
            track.Seek(frame);
        }
        Console.WriteLine($"[engine] queued {paths.Count}, from {index}{(frame > 0 ? $" at {at!.Value:m\\:ss}" : "")}"
                        + $"{(paused ? ", held" : "")}: {System.IO.Path.GetFileName(track.Path)} "
                        + $"({track.Info.SampleRate} Hz, {track.Info.BitsPerSample}-bit, {track.Info.Channels} ch, "
                        + $"gain x{track.Gain.Multiplier(_gain):0.###})");
        Begin(index, track, frame, play: !paused);
        SetState(!paused);
    }

    public void PlayIndex(int index) => JumpTo(index, play: true);

    /// At the end of the queue, nothing: mpv's `playlist-next weak`.
    public void Next()
    {
        if (_index >= 0 && _index + 1 < Count) JumpTo(_index + 1, _playing);
    }

    /// Past three seconds in, back to the start of the track; before that, the
    /// track before, and on the first track nothing (mpv's `playlist-prev weak`).
    public void Previous()
    {
        if (Position > TimeSpan.FromSeconds(3)) { Seek(TimeSpan.Zero); return; }
        if (_index > 0) JumpTo(_index - 1, _playing);
    }

    private void JumpTo(int index, bool play)
    {
        if (index < 0 || index >= Count) return;
        // Stopped before anything is opened: the track asked for may be the
        // one the output is decoding right now, and it is about to be rewound.
        Discard();
        var track = OpenFrom(ref index);
        if (track is null) return;
        Begin(index, track, 0, play);
        SetState(play);
    }

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            if (paused) _out?.Pause();
            else _out?.Start();
        }
        SetState(!paused);
    }

    public void Stop()
    {
        var was = _index;
        Discard();
        SetState(false);
        if (was >= 0) Finished?.Invoke(this, EventArgs.Empty);
        _index = -1;
    }

    public void Seek(TimeSpan to)
    {
        Mark? m;
        lock (_lock) m = _out is null ? null : Current(_out.Played);
        if (m is not { } mark) return;

        var track = mark.Track;
        Discard();          // before the track's cursor moves under the decoding
        var frame = Math.Clamp((long)(to.TotalSeconds * track.Info.SampleRate), 0, Math.Max(0, track.Info.TotalSamples - 1));
        track.Seek(frame);
        Begin(mark.Index, track, frame, _playing);
        Seeked?.Invoke(this, to);
    }

    // ---------------------------------------------------------------- output

    /// A new output, starting with `track` at `frame`.
    private void Begin(int index, FlacTrack track, long frame, bool play)
    {
        Discard();
        int generation;
        lock (_lock)
        {
            generation = _generation;
            _decoding = track;
            _decodingIndex = index;
            _decodingGain = track.Gain.Multiplier(_gain);
            _handed = 0;
            _marks.Clear();
            _marks.Add(new Mark(0, index, frame, track));
            _afterBreak = null;
            Array.Fill(_levelBlock, -1);
            _levelAt = 0;
            _levelL = _levelR = 0;
            Prefetch(index + 1);
        }

        var output = Output.Open(track.Info.SampleRate, track.Info.Channels, into => Source(into, generation));
        output.SetVolume(Cube(_volume));
        lock (_lock)
        {
            if (generation != _generation) { output.Dispose(); return; }
            _out = output;
        }
        if (play) output.Start();
    }

    /// The output thrown away, immediately. Outside the lock: disposing waits
    /// for a callback in progress, which may itself be waiting for the lock.
    private void Discard()
    {
        IOutput? old;
        lock (_lock)
        {
            old = _out;
            _out = null;
            _generation++;
            _decoding = null;
        }
        old?.Dispose();
    }

    /// The output asks for what plays next.
    private int Source(Span<float> into, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation || _decoding is null) return 0;
            var channels = _decoding.Info.Channels;
            var want = into.Length / channels;
            var done = 0;
            while (done < want && _decoding is not null)
            {
                int n;
                try { n = _decoding.Read(into[(done * channels)..(want * channels)]); }
                catch (FlacDecodeException ex)
                {
                    // mpv goes on to the next track after a broken one; so does this.
                    Console.WriteLine($"[engine] {System.IO.Path.GetFileName(_decoding.Path)}: {ex.Message}");
                    n = 0;
                }
                var chunk = into.Slice(done * channels, n * channels);
                Measure(chunk, channels);
                if (_decodingGain != 1f)
                    foreach (ref var s in chunk) s *= _decodingGain;
                done += n;
                _handed += n;
                if (done < want && !NextTrack()) break;
            }
            return done;
        }
    }

    /// On to the next track in the same stream. False at the end of the queue,
    /// or when the next track needs an output of its own (a different rate or
    /// channel count): then this one plays out first and Watch makes it.
    private bool NextTrack()
    {
        var index = _decodingIndex + 1;
        FlacTrack? next = null;
        while (index < _queue.Count && next is null)
        {
            next = index == _prefetchIndex && _prefetch is not null ? _prefetch.Result : TryOpen(_queue[index]);
            if (next is null) index++;
        }
        _decoding = null;
        if (next is null) return false;

        var current = _marks[^1].Track.Info;
        if (next.Info.SampleRate != current.SampleRate || next.Info.Channels != current.Channels)
        {
            _afterBreak = next;
            _decodingIndex = index;
            return false;
        }
        _decoding = next;
        _decodingIndex = index;
        _decodingGain = next.Gain.Multiplier(_gain);
        _marks.Add(new Mark(_handed, index, 0, next));
        Prefetch(index + 1);
        return true;
    }

    /// Opens the track after the one now being decoded, on another thread, so
    /// that it is in memory before the boundary arrives. Under _lock.
    private void Prefetch(int index)
    {
        if (index >= _queue.Count || index == _prefetchIndex) return;
        var path = _queue[index];
        _prefetchIndex = index;
        _prefetch = Task.Run(() => TryOpen(path));
    }

    /// The first track from `index` on that opens, with `index` moved to it.
    private FlacTrack? OpenFrom(ref int index)
    {
        for (; index < Count; index++)
        {
            Task<FlacTrack?>? ready;
            string path;
            lock (_lock)
            {
                ready = index == _prefetchIndex ? _prefetch : null;
                path = _queue[index];
            }
            var track = ready?.Result ?? TryOpen(path);
            if (track is null) continue;
            // A track that was half played before is opened at its start.
            if (ready is not null) track.Seek(0);
            return track;
        }
        return null;
    }

    private static FlacTrack? TryOpen(string path)
    {
        try { return FlacTrack.Open(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[engine] cannot play {System.IO.Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------- levels

    // RMS per block of 1024 frames of the output's timeline, measured BEFORE
    // the gain as mpv's astats is (it sits ahead of the volume), and kept for a
    // while so Levels can read the block that is sounding now, not the one
    // being decoded. 512 blocks is 12 s at 44.1 kHz, far more than is buffered.
    private const int LevelShift = 10;
    private const int LevelBlocks = 512;
    private readonly long[] _levelBlock = new long[LevelBlocks];
    private readonly float[] _levelLeft = new float[LevelBlocks];
    private readonly float[] _levelRight = new float[LevelBlocks];
    private long _levelAt;          // the next frame to measure
    private double _levelL, _levelR;

    private void Measure(ReadOnlySpan<float> chunk, int channels)
    {
        var right = channels > 1 ? 1 : 0;
        for (var i = 0; i < chunk.Length; i += channels)
        {
            double l = chunk[i], r = chunk[i + right];
            _levelL += l * l;
            _levelR += r * r;
            if ((++_levelAt & ((1 << LevelShift) - 1)) != 0) continue;
            var block = (_levelAt >> LevelShift) - 1;
            var slot = (int)(block % LevelBlocks);
            _levelLeft[slot] = (float)(_levelL / (1 << LevelShift));
            _levelRight[slot] = (float)(_levelR / (1 << LevelShift));
            _levelBlock[slot] = block;
            _levelL = _levelR = 0;
        }
    }

    /// None: Levels reads the block sounding now.
    public TimeSpan LevelsLead => TimeSpan.Zero;

    public (double Left, double Right) Levels()
    {
        lock (_lock)
        {
            if (_out is null) return (double.NegativeInfinity, double.NegativeInfinity);
            var block = _out.Played >> LevelShift;
            var slot = (int)(block % LevelBlocks);
            if (_levelBlock[slot] != block) return (double.NegativeInfinity, double.NegativeInfinity);
            return (Db(_levelLeft[slot]), Db(_levelRight[slot]));
        }

        static double Db(float meanSquare) => meanSquare > 0 ? 10 * Math.Log10(meanSquare) : double.NegativeInfinity;
    }

    // ---------------------------------------------------------------- watching

    /// Follows the output: which track is sounding, and when the end of the
    /// queue (or a change of format) has played out.
    private void Watch()
    {
        while (_running)
        {
            Thread.Sleep(40);
            Mark? now = null;
            bool drained = false;
            FlacTrack? afterBreak = null;
            int breakIndex = -1;
            lock (_lock)
            {
                if (_out is null) continue;
                now = Current(_out.Played);
                drained = _out.Drained;
                if (drained && _afterBreak is not null)
                {
                    afterBreak = _afterBreak;
                    breakIndex = _decodingIndex;
                    _afterBreak = null;
                }
                // What has played is let go of, all but the track sounding now.
                if (now is { } n) _marks.RemoveAll(m => m.At < n.At);
            }

            if (now is { } mark && mark.Index != _index)
            {
                _index = mark.Index;
                _duration = mark.Track.Duration;
                lock (_lock)
                    if (mark.Index < _queue.Count)
                        Console.WriteLine($"[engine] playing {mark.Index}  {System.IO.Path.GetFileName(_queue[mark.Index])}");
                TrackChanged?.Invoke(this, new TrackChangedEventArgs(mark.Index));
                DurationChanged?.Invoke(this, EventArgs.Empty);
            }

            if (afterBreak is not null)
            {
                Console.WriteLine($"[engine] new output for {breakIndex}: {afterBreak.Info.SampleRate} Hz, {afterBreak.Info.Channels} ch");
                Begin(breakIndex, afterBreak, 0, _playing);
            }
            else if (drained)
            {
                // The record has played out. Finished goes while Index still
                // names the last track, as the app expects (it asks whether the
                // player was on one); then it is on none.
                Discard();
                SetState(false);
                Finished?.Invoke(this, EventArgs.Empty);
                _index = -1;
            }
        }
    }

    private void SetState(bool playing)
    {
        _playing = playing;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _running = false;
        Discard();
        _watch.Join(500);
    }
}
