namespace AlbumWall.Playback;

/// What plays a queue: mpv (MpvEngine), or for FLAC on the Mac AlbumWall's own
/// (OwnEngine). Player chooses between them for each queue and is the only
/// thing the app sees; this is the surface it relies on.
internal interface IEngine : IDisposable
{
    bool IsPlaying { get; }
    int Index { get; }
    int Count { get; }
    GainMode Gain { get; set; }
    int Volume { get; set; }
    bool Metering { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }

    event EventHandler<TrackChangedEventArgs>? TrackChanged;
    event EventHandler? StateChanged;
    event EventHandler? Finished;
    event EventHandler? DurationChanged;
    event EventHandler<TimeSpan>? Seeked;

    void Play(IReadOnlyList<string> paths, int start, TimeSpan? at, bool paused);
    void PlayIndex(int index);
    void Next();
    void Previous();
    void SetPaused(bool paused);
    void Stop();
    void Seek(TimeSpan to);
    (double Left, double Right) Levels();

    /// How far Levels runs ahead of the speakers.
    TimeSpan LevelsLead { get; }
}
