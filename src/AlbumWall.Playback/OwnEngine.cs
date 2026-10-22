// AlbumWall — its own audio engine: FLAC, MP3 and AAC, on the Mac, Linux and
// Windows (roadmap, "Our own audio engine: mpv out, in steps", steps 1 to 3b,
// started 2026-10-21). Called FlacEngine until it played more than FLAC. The
// decoders are AlbumWall.Codecs'; the output is the platform's (Output.cs);
// everything here is plain .NET.
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
// Levels and gain match mpv's (Codecs' ReplayGain, Volume below), so the
// same record plays at the same loudness whichever engine plays it.

using AlbumWall.Codecs;

namespace AlbumWall.Playback;

internal sealed class OwnEngine : IEngine
{
    private readonly Lock _lock = new();
    private List<string> _queue = [];           // where each track is: a file, or a server's address
    private List<string> _names = [];           // what each is called in the log: never an address

    // The output, and the decoding that feeds it (on the output's thread,
    // under _lock). _generation tells a callback from an output already
    // thrown away that it has nothing more to say.
    private IOutput? _out;
    private int _outChannels;
    private int _generation;
    private (int Index, IAudioTrack Track, long Frame)? _pending;    // a seek or jump the output has yet to restart for
    private volatile IAudioTrack? _decoding;      // read outside the lock too, to wait for its bytes there
    private int _decodingIndex = -1;
    private float _decodingGain = 1f;
    private long _handed;                       // frames given to _out so far
    private readonly List<Mark> _marks = [];
    private IAudioTrack? _afterBreak;             // the next track, when its format needs a new output

    private Task<IAudioTrack?>? _prefetch;
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
    private readonly record struct Mark(long At, int Index, long TrackFrame, IAudioTrack Track);

    public OwnEngine(GainMode gain)
    {
        _gain = gain;
        Array.Fill(_levelBlock, -1);
        _watch = new Thread(Watch) { IsBackground = true, Name = "engine watch" };
        _watch.Start();
    }

    /// Whether this engine can play a queue: FLAC, every track, each a file
    /// here or a server's address. Chosen by the names (a server's track's
    /// library path ends in its own extension; its address does not).
    /// Anything else stays with mpv.
    public static bool CanPlay(IReadOnlyList<string> sources, IReadOnlyList<string> names) =>
        sources.Count > 0 && sources.Count == names.Count
        && Enumerable.Range(0, sources.Count).All(i =>
            AudioTracks.CanDecode(names[i])
            && (AudioTracks.IsAddress(sources[i]) || (!sources[i].Contains("://") && File.Exists(sources[i]))));

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
                // Where it was asked to go, until the output is there.
                if (_pending is { } p) return TimeSpan.FromSeconds((double)p.Frame / p.Track.SampleRate);
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

    public void Play(IReadOnlyList<string> paths, int start, TimeSpan? at, bool paused) => Play(paths, paths, start, at, paused);

    /// `sources` are where the tracks are, `names` what they are called in
    /// the log, one for one: the same list for files, the library paths for a
    /// server's tracks, whose addresses carry the sign-in.
    public void Play(IReadOnlyList<string> sources, IReadOnlyList<string> names, int start, TimeSpan? at, bool paused)
    {
        if (sources.Count == 0) return;
        var paths = sources;
        Discard();
        lock (_lock)
        {
            _names = [.. names];
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
            frame = (long)(a.TotalSeconds * track.SampleRate);
            track.Seek(frame);
        }
        Console.WriteLine($"[engine] queued {paths.Count}, from {index}{(frame > 0 ? $" at {at!.Value:m\\:ss}" : "")}"
                        + $"{(paused ? ", held" : "")}: {System.IO.Path.GetFileName(track.Name)} "
                        + $"({track.SampleRate} Hz, {(track.BitsPerSample > 0 ? $"{track.BitsPerSample}-bit" : "lossy")}, {track.Channels} ch, "
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

    /// The track playing on meanwhile: opening the next one is not silence.
    private void JumpTo(int index, bool play)
    {
        if (index < 0 || index >= Count) return;
        var track = OpenFrom(ref index);
        if (track is null) return;
        if (!Restart(index, track, 0, play))
        {
            Discard();
            Begin(index, track, 0, play);
        }
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
        int index;
        IAudioTrack track;
        lock (_lock)
        {
            // A seek on a seek not yet done is on the same track.
            if (_pending is { } p) (index, track) = (p.Index, p.Track);
            else if (_out is not null && Current(_out.Played) is { } m) (index, track) = (m.Index, m.Track);
            else return;
        }

        var frame = Math.Clamp((long)(to.TotalSeconds * track.SampleRate), 0, Math.Max(0, track.TotalFrames - 1));
        if (!Restart(index, track, frame, _playing))
        {
            Discard();          // before the track's cursor moves under the decoding
            track.Seek(frame);
            Begin(index, track, frame, _playing);
        }
        Seeked?.Invoke(this, to);
    }

    // ---------------------------------------------------------------- output

    /// The output kept, where it can be (IOutput.Restart): what it holds for
    /// the old place is thrown away, and `track` is decoded from `frame` once
    /// the output's own thread is ready for it, so its cursor never moves
    /// under the decoding. False when it cannot be kept: a different format,
    /// or an output that cannot.
    private bool Restart(int index, IAudioTrack track, long frame, bool play)
    {
        IOutput? output;
        lock (_lock)
        {
            output = _out;
            if (output is null || output.SampleRate != track.SampleRate || _outChannels != track.Channels) return false;
            _pending = (index, track, frame);
        }
        // A wait for a server's bytes lets go, so that the output gets to it.
        _decoding?.Interrupt();
        if (!output.Restart(at => Restarted(output, at)))
        {
            lock (_lock) _pending = null;
            return false;
        }
        if (play) output.Start();
        else output.Pause();
        return true;
    }

    /// On the output's thread, with nothing old left in it: the decoding
    /// starts again where it was asked to, at frame `at` of the timeline.
    private void Restarted(IOutput output, long at)
    {
        lock (_lock)
        {
            if (_out != output || _pending is not { } p) return;
            _pending = null;
            p.Track.Seek(p.Frame);
            _decoding = p.Track;
            _decodingIndex = p.Index;
            _decodingGain = p.Track.Gain.Multiplier(_gain);
            _handed = at;
            _marks.Clear();
            _marks.Add(new Mark(at, p.Index, p.Frame, p.Track));
            _afterBreak = null;
            Array.Fill(_levelBlock, -1);
            _levelAt = at;
            _levelL = _levelR = 0;
            Prefetch(p.Index + 1);
        }
    }

    /// A new output, starting with `track` at `frame`.
    private void Begin(int index, IAudioTrack track, long frame, bool play)
    {
        Discard();
        int generation;
        lock (_lock)
        {
            generation = _generation;
            _outChannels = track.Channels;
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

        var output = Output.Open(track.SampleRate, track.Channels, into => Source(into, generation));
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
        // First, and without the lock: the output's thread may hold it while
        // waiting for a server's bytes, and is let go of here.
        _decoding?.Interrupt();
        IOutput? old;
        lock (_lock)
        {
            old = _out;
            _out = null;
            _generation++;
            _decoding = null;
            _pending = null;
        }
        old?.Dispose();
    }

    /// The output asks for what plays next.
    private int Source(Span<float> into, int generation)
    {
        // A server's track may still be arriving: what this asks for is
        // waited for here, before the lock, so that nothing else waits with it.
        // Only the output's thread reads a track, so this is safe unlocked.
        if (_decoding is { } track) track.Prepare(into.Length / track.Channels);

        lock (_lock)
        {
            if (generation != _generation || _decoding is null) return 0;
            var channels = _decoding.Channels;
            var want = into.Length / channels;
            var done = 0;
            while (done < want && _decoding is not null)
            {
                int n;
                try { n = _decoding.Read(into[(done * channels)..(want * channels)]); }
                catch (FlacDecodeException ex)
                {
                    // mpv goes on to the next track after a broken one; so does this.
                    Console.WriteLine($"[engine] {System.IO.Path.GetFileName(_decoding.Name)}: {ex.Message}");
                    n = 0;
                }
                var chunk = into.Slice(done * channels, n * channels);
                Measure(chunk, channels);
                if (_decodingGain != 1f)
                    foreach (ref var s in chunk) s *= _decodingGain;
                done += n;
                _handed += n;
                // Short because the output is being thrown away is not the end
                // of the track.
                if (done < want && (_decoding.Interrupted || !NextTrack())) break;
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
        IAudioTrack? next = null;
        while (index < _queue.Count && next is null)
        {
            next = index == _prefetchIndex && _prefetch is not null ? _prefetch.Result : TryOpen(_names[index], _queue[index]);
            if (next is null) index++;
        }
        _decoding = null;
        if (next is null) return false;

        var current = _marks[^1].Track;
        if (next.SampleRate != current.SampleRate || next.Channels != current.Channels)
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
        var (name, source) = (_names[index], _queue[index]);
        _prefetchIndex = index;
        _prefetch = Task.Run(() => TryOpen(name, source));
    }

    /// The first track from `index` on that opens, with `index` moved to it.
    private IAudioTrack? OpenFrom(ref int index)
    {
        for (; index < Count; index++)
        {
            Task<IAudioTrack?>? ready;
            string name, source;
            lock (_lock)
            {
                ready = index == _prefetchIndex ? _prefetch : null;
                (name, source) = (_names[index], _queue[index]);
            }
            var track = ready?.Result;
            // Not the one being decoded now (the last track's, opened ahead,
            // stays the prefetch): its cursor is the output's. A fresh copy.
            if (track is not null && track == _decoding) { track = null; ready = null; }
            track ??= TryOpen(name, source);
            if (track is null) continue;
            // A track that was half played before is opened at its start.
            if (ready is not null) track.Seek(0);
            return track;
        }
        return null;
    }

    /// Logged by name only: a server's source carries the sign-in.
    private static IAudioTrack? TryOpen(string name, string source)
    {
        try { return AudioTracks.Open(name, source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException
                                      or OperationCanceledException or FlacDecodeException)
        {
            var why = ex is OperationCanceledException ? "the server did not answer in time" : ex.Message;
            Console.WriteLine($"[engine] cannot play {System.IO.Path.GetFileName(name)}: {why}");
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
            IAudioTrack? afterBreak = null;
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
                        Console.WriteLine($"[engine] playing {mark.Index}  {System.IO.Path.GetFileName(_names[mark.Index])}");
                TrackChanged?.Invoke(this, new TrackChangedEventArgs(mark.Index));
                DurationChanged?.Invoke(this, EventArgs.Empty);
            }

            if (afterBreak is not null)
            {
                Console.WriteLine($"[engine] new output for {breakIndex}: {afterBreak.SampleRate} Hz, {afterBreak.Channels} ch");
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
