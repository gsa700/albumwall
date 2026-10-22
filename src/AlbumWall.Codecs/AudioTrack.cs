// AlbumWall — one track open for playing, whatever its format: what the own
// engine (OwnEngine) asks of a decoder, and nothing more.
//
// Each format is a small codec core on the shared parts of this project: the
// bytes as they arrive (TrackBytes), the tags (ReplayGain), seeking to an
// exact sample. FLAC first; MP3 and AAC next, his call 2026-10-22 ("replace
// the top 3 decoders with native C# versions"), so that mpv can go.

namespace AlbumWall.Codecs;

public interface IAudioTrack
{
    /// The track's name: its file, or its library path for a server's track.
    /// Never the address it is fetched from, which carries the sign-in.
    string Name { get; }

    int SampleRate { get; }
    int Channels { get; }
    int BitsPerSample { get; }

    /// Frames from the first to the last, after anything the format says to
    /// drop (an encoder's padding): what is played, gapless.
    long TotalFrames { get; }

    TimeSpan Duration { get; }
    ReplayGain Gain { get; }

    /// The next frames, interleaved, as floats from -1 to 1, into `into`
    /// (whose length is a whole number of frames). Returns the frames written;
    /// fewer than asked for only at the end of the track.
    int Read(Span<float> into);

    /// Puts the cursor on frame `frame`, exactly, at the next Read.
    void Seek(long frame);

    /// Outside the engine's lock, before a Read of `frames`: whatever waiting
    /// that Read would do (for bytes still arriving, a seek) done here.
    void Prepare(int frames);

    /// Stops a wait for bytes that have not arrived; the next Seek undoes it.
    void Interrupt();
    bool Interrupted { get; }
}

public static class AudioTracks
{
    /// Whether a track by this name (its file or library path) has a decoder
    /// here.
    public static bool CanDecode(string name) =>
        name.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);

    /// A track by name, from where it is: a file, or a server's address
    /// (http or https), which is fetched and not kept. The decoder is chosen
    /// by the name's extension, as the scanner chose the file by it.
    public static IAudioTrack Open(string name, string source) =>
        name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? Mp3Track.Open(name, source)
        : name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ? Mp4Track.Open(name, source)
        : FlacTrack.Open(name, source);

    public static bool IsAddress(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
