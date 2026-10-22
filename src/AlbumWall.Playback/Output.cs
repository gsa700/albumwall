// AlbumWall — where the own engine's samples go: one output per platform
// (roadmap, "Our own audio engine", step 2). FlacEngine asks only this of it.

namespace AlbumWall.Playback;

internal interface IOutput : IDisposable
{
    /// Fills the span (interleaved floats, a whole number of frames) with what
    /// plays next. Returns the frames written: fewer than asked for means the
    /// end, and zero means nothing more will come.
    delegate int Source(Span<float> into);

    int SampleRate { get; }

    void Start();
    void Pause();

    /// Linear, 0-1.
    void SetVolume(float gain);

    /// Frames played since the output was made: what has reached the device,
    /// not what has been handed over. Stands still while paused.
    long Played { get; }

    /// True once the source has ended and everything handed over has played.
    bool Drained { get; }
}

internal static class Output
{
    /// Whether the own engine has an output on this machine.
    public static bool IsAvailable => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
                                      || (OperatingSystem.IsLinux() && PulseOutput.IsAvailable);

    public static IOutput Open(int sampleRate, int channels, IOutput.Source source) =>
        OperatingSystem.IsMacOS() ? new AudioQueueOutput(sampleRate, channels, source)
        : OperatingSystem.IsWindows() ? new WasapiOutput(sampleRate, channels, source)
        : new PulseOutput(sampleRate, channels, source);
}
