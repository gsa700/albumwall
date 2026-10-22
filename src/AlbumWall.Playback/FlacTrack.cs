// AlbumWall — one FLAC file open for playing.
//
// The whole file is read into memory when it is opened: 20-40 MB for a CD
// track, gone again when the track is. That makes the decoder's input a plain
// array, which is what Deadwax's decoder takes (Flac.cs), and it means a
// track boundary never waits on the disk or the network: the next track is
// opened ahead of time (FlacEngine).

namespace AlbumWall.Playback;

internal sealed class FlacTrack
{
    public string Path { get; }
    public FlacStreamInfo Info { get; }
    public ReplayGain Gain { get; }

    private readonly byte[] _data;
    private readonly int _framesStart;
    private readonly float _scale;

    private int _at;                    // the next frame to decode
    private long[][] _channels;         // the last frame decoded
    private int _frameLength;           // its length in samples
    private int _frameOffset;           // how much of it has been handed out

    private FlacTrack(string path, byte[] data)
    {
        Path = path;
        _data = data;
        Info = FlacDecoder.ReadMetadata(data, out _framesStart, out var tags);
        Gain = ReplayGain.From(tags);
        if (Info.Channels > 8) throw new FlacDecodeException("More channels than FLAC has.");
        _scale = 1f / (1L << (Info.BitsPerSample - 1));
        _channels = new long[Info.Channels][];
        for (var c = 0; c < Info.Channels; c++) _channels[c] = new long[Math.Max(Info.MaxBlockSize, 1)];
        _at = _framesStart;
    }

    public static FlacTrack Open(string path) => new(path, File.ReadAllBytes(path));

    public TimeSpan Duration => Info.TotalSamples > 0 && Info.SampleRate > 0
        ? TimeSpan.FromSeconds((double)Info.TotalSamples / Info.SampleRate)
        : TimeSpan.Zero;

    /// The next samples, interleaved, as floats from -1 to 1, into `into`
    /// (whose length is a whole number of frames). Returns the frames written;
    /// fewer than asked for only at the end of the file.
    public int Read(Span<float> into)
    {
        var channels = Info.Channels;
        var want = into.Length / channels;
        var done = 0;
        while (done < want)
        {
            if (_frameOffset >= _frameLength)
            {
                if (_at >= _data.Length || !DecodeNext()) break;
                continue;
            }
            var n = Math.Min(want - done, _frameLength - _frameOffset);
            for (var i = 0; i < n; i++)
            {
                var o = (done + i) * channels;
                for (var c = 0; c < channels; c++) into[o + c] = _channels[c][_frameOffset + i] * _scale;
            }
            _frameOffset += n;
            done += n;
        }
        return done;
    }

    /// False at the end: what follows the last frame is not another frame (an
    /// ID3v1 tag left by a tagger, or padding).
    private bool DecodeNext()
    {
        if (!FlacDecoder.TryReadHeader(_data, _at, Info, out _, out _)) { _at = _data.Length; return false; }
        _at += FlacDecoder.DecodeFrame(_data, _at, _data.Length, Info, ref _channels, out _frameLength);
        _frameOffset = 0;
        return true;
    }

    /// Puts the cursor on sample `to`, exactly: the frame holding it is found
    /// by its header, without decoding what comes before (a binary search over
    /// the bytes, each guess checked by the header that follows it), then
    /// decoded, and what precedes the sample is skipped.
    public void Seek(long to)
    {
        _frameLength = _frameOffset = 0;
        _at = _framesStart;
        if (to <= 0) return;

        var total = Info.TotalSamples;
        long lo = _framesStart, hi = _data.Length;
        var from = _framesStart;
        var fromSample = 0L;
        for (var tries = 0; tries < 48 && hi - lo > 16 && total > 0; tries++)
        {
            var mid = (int)((lo + hi) / 2);
            if (FindFrame(mid) is not var (at, first, size)) { hi = mid; continue; }
            if (first > to) { hi = mid; continue; }
            from = at;
            fromSample = first;
            if (first + size > to) break;       // this frame holds it
            lo = at + 1;
        }

        _at = from;
        var skip = to - fromSample;
        while (skip > 0 && DecodeNext())
        {
            if (skip < _frameLength) { _frameOffset = (int)skip; return; }
            skip -= _frameLength;
        }
    }

    /// The first frame header at or after `from` that the next header agrees
    /// with (its first sample is this one's first plus its size), so that a
    /// sync pattern inside a frame's audio is not taken for a frame.
    private (int At, long First, int Size)? FindFrame(int from)
    {
        var limit = Info.MaxFrameSize > 0 ? Info.MaxFrameSize + 64 : 1 << 20;
        for (var i = from; i < _data.Length - 1; i++)
        {
            if (_data[i] != 0xFF || !FlacDecoder.TryReadHeader(_data, i, Info, out var first, out var size)) continue;
            if (Info.TotalSamples > 0 && first + size >= Info.TotalSamples) return (i, first, size);
            var end = (int)Math.Min(_data.Length - 1L, (long)i + limit);
            for (var j = i + 2; j < end; j++)
                if (_data[j] == 0xFF && FlacDecoder.TryReadHeader(_data, j, Info, out var next, out _) && next == first + size)
                    return (i, first, size);
        }
        return null;
    }
}

/// ReplayGain from a file's tags, read the way mpv reads it, so that the same
/// record plays at the same level whichever engine plays it. mpv plays FLAC
/// through FFmpeg's demuxer (MpvEngine's `demuxer=lavf`), where FFmpeg parses the
/// four tags (libavformat/replaygain.c) and mpv takes them from there
/// (demux_lavf.c, export_replaygain): a gain counts only with a peak above
/// zero beside it, an album without its own gain takes the track's, and with
/// neither there is no ReplayGain at all.
internal readonly record struct ReplayGain(bool Present, float TrackGain, float TrackPeak, float AlbumGain, float AlbumPeak)
{
    public static ReplayGain From(IReadOnlyDictionary<string, string> tags)
    {
        var tg = Parse(tags, "REPLAYGAIN_TRACK_GAIN", int.MinValue);
        var tp = Parse(tags, "REPLAYGAIN_TRACK_PEAK", 0);
        var ag = Parse(tags, "REPLAYGAIN_ALBUM_GAIN", int.MinValue);
        var ap = Parse(tags, "REPLAYGAIN_ALBUM_PEAK", 0);
        var track = tg != int.MinValue && tp != 0;
        var album = ag != int.MinValue && ap != 0;
        if (!track && !album) return default;

        float trackGain = 0, trackPeak = 1, albumGain = 0, albumPeak = 1;
        if (track) { trackGain = albumGain = tg / 100000f; trackPeak = albumPeak = tp / 100000f; }
        if (album) { albumGain = ag / 100000f; albumPeak = ap / 100000f; }
        return new ReplayGain(true, trackGain, trackPeak, albumGain, albumPeak);
    }

    /// The multiplier for a mode: mpv's compute_replaygain, clipping
    /// prevention on (its default), no pre-amp, no fallback gain.
    public float Multiplier(GainMode mode)
    {
        if (!Present || mode == GainMode.Off) return 1f;
        var (gain, peak) = mode == GainMode.Track ? (TrackGain, TrackPeak) : (AlbumGain, AlbumPeak);
        return Math.Min(MathF.Pow(10f, gain / 20f), 1f / peak);
    }

    /// FFmpeg's parse_value: a whole number, then up to five decimals, in
    /// hundred-thousandths ("-7.53 dB" is -753000; what follows is ignored).
    private static int Parse(IReadOnlyDictionary<string, string> tags, string key, int missing)
    {
        if (!tags.TryGetValue(key, out var text)) return missing;
        var s = text.AsSpan().TrimStart(" \t");
        var negative = s.Length > 0 && s[0] == '-';
        var i = s.Length > 0 && (s[0] == '-' || s[0] == '+') ? 1 : 0;
        long whole = 0;
        for (; i < s.Length && char.IsAsciiDigit(s[i]); i++) whole = Math.Min(whole * 10 + (s[i] - '0'), int.MaxValue);
        long fraction = 0;
        if (i < s.Length && s[i] == '.')
            for (int scale = 10000, j = i + 1; scale > 0 && j < s.Length && char.IsAsciiDigit(s[j]); j++, scale /= 10)
                fraction += scale * (s[j] - '0');
        if (whole > (int.MaxValue - fraction) / 100000) return missing;
        var value = whole * 100000 + fraction;
        return (int)(negative ? -value : value);
    }
}
