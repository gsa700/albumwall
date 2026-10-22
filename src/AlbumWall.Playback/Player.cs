// AlbumWall — playback, as the app sees it.
//
// Two engines behind one face (roadmap, "Our own audio engine: mpv out, in
// steps"). mpv (MpvEngine) plays everything, as it always has. AlbumWall's own
// (FlacEngine) plays FLAC, local or from a server, on the Mac, Linux and
// Windows when Preferences > Playback says so, which is off unless turned on
// (his call, 2026-10-21: a
// Preferences switch). The choice is made for each queue as it starts, never
// in the middle of one, so a record plays through on one engine.
//
// The requirements both are held to, from the spec:
//
//   * GAPLESS. Non-negotiable.
//   * NATIVE SAMPLE RATE, no unnecessary resampling.
//   * REPLAYGAIN from tags: album gain for album playback, track gain for
//     shuffle, never silently applied without being visible.
//
// Events reach the app only from the engine playing: the other one, stopped
// as the switch happens, goes idle, and mpv calls going idle Finished.

namespace AlbumWall.Playback;

public enum GainMode { Off, Track, Album }

public sealed class TrackChangedEventArgs(int index) : EventArgs
{
    public int Index { get; } = index;
}

public sealed class Player : IDisposable
{
    private readonly MpvEngine _mpv;
    private readonly FlacEngine? _own;
    private IEngine _active;

    /// Whether playback is possible at all on this machine. mpv still plays
    /// everything the own engine does not, so it is still required.
    public static bool IsAvailable => MpvEngine.IsAvailable;

    /// Whether the own engine can run here at all: the Mac, and Linux with
    /// PulseAudio's client library (PipeWire serves it too), for now.
    public static bool OwnEngineAvailable => Output.IsAvailable;

    /// The Preferences switch. Takes effect at the next queue.
    public bool UseOwnEngine { get; set; }

    public Player(GainMode gain = GainMode.Album)
    {
        _mpv = new MpvEngine(gain);
        _active = _mpv;
        if (OwnEngineAvailable) _own = new FlacEngine(gain);
        foreach (var engine in Engines())
        {
            engine.TrackChanged += (s, e) => { if (s == _active) TrackChanged?.Invoke(this, e); };
            engine.StateChanged += (s, e) => { if (s == _active) StateChanged?.Invoke(this, e); };
            engine.Finished += (s, e) => { if (s == _active) Finished?.Invoke(this, e); };
            engine.DurationChanged += (s, e) => { if (s == _active) DurationChanged?.Invoke(this, e); };
            engine.Seeked += (s, e) => { if (s == _active) Seeked?.Invoke(this, e); };
        }
    }

    private IEnumerable<IEngine> Engines() => _own is null ? [_mpv] : [_mpv, _own];

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

    public bool IsPlaying => _active.IsPlaying;
    public int Index => _active.Index;
    public int Count => _active.Count;
    public TimeSpan Position => _active.Position;
    public TimeSpan Duration => _active.Duration;
    public bool Metering => _active.Metering;
    public (double Left, double Right) Levels() => _active.Levels();

    /// How far Levels runs ahead of the speakers, for the meters to hold the
    /// needles back by: mpv measures where it decodes, the own engine where
    /// the output is playing.
    public TimeSpan LevelsLead => _active.LevelsLead;

    /// ReplayGain. Album gain preserves the relative loudness the record was
    /// mastered with, which is what album playback wants; track gain flattens
    /// that out, which is what shuffle wants. Both engines keep it, so the next
    /// queue plays with it whichever takes it.
    public GainMode Gain
    {
        get => _active.Gain;
        set { foreach (var e in Engines()) e.Gain = value; }
    }

    /// The app's own volume, 0-100, independent of the system mixer: it
    /// attenuates only this app's stream. NOT the same lever as ReplayGain.
    public int Volume
    {
        get => _active.Volume;
        set { foreach (var e in Engines()) e.Volume = value; }
    }

    /// Plays `paths` from `start`; `at` and `paused` pick up where the last run
    /// left off (see MpvEngine.Play). `names` are the tracks' own paths, one
    /// for each, when `paths` are not: a server's tracks are played from
    /// addresses that carry the sign-in, and named by their library paths.
    public void Play(IEnumerable<string> paths, int start = 0, TimeSpan? at = null, bool paused = false,
                     IReadOnlyList<string>? names = null)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        IReadOnlyList<string> called = names is not null && names.Count == list.Count ? names : list;

        IEngine engine = UseOwnEngine && _own is not null && FlacEngine.CanPlay(list, called) ? _own : _mpv;
        if (engine != _active)
        {
            var old = _active;
            _active = engine;
            old.Stop();
            Console.WriteLine($"[player] engine: {(engine == _own ? "AlbumWall's own (FLAC)" : "mpv")}");
        }
        if (engine == _own) _own.Play(list, called, start, at, paused);
        else engine.Play(list, start, at, paused);
    }

    public void PlayIndex(int index) => _active.PlayIndex(index);
    public void Next() => _active.Next();
    public void Previous() => _active.Previous();
    public void TogglePause() => SetPaused(IsPlaying);
    public void SetPaused(bool paused) => _active.SetPaused(paused);
    public void Stop() => _active.Stop();
    public void Seek(TimeSpan to) => _active.Seek(to);

    public void Dispose()
    {
        foreach (var e in Engines()) e.Dispose();
    }
}
