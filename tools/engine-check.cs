#:project ../src/AlbumWall.Playback/AlbumWall.Playback.csproj
#:property AllowUnsafeBlocks=true

// AlbumWall - the own audio engine (OwnEngine) and its decoders, checked
// without the app.
//
//   dotnet run tools/engine-check.cs -- decode <file.flac>...   every sample, against the file's own MD5
//   dotnet run tools/engine-check.cs -- compare <file>...       every sample, against ffmpeg's decode of the file
//   dotnet run tools/engine-check.cs -- seek <file>             seeks land on the exact sample
//   dotnet run tools/engine-check.cs -- play <file>...          plays MUTED, and says what the engine reports
//
// decode: FLAC stores an MD5 of its audio. Every sample is decoded the way
// the engine decodes it, to floats, turned back into integers and hashed, so
// a match proves both the decoder and that the float conversion loses nothing.
// compare: for formats with no MD5 (MP3), ffmpeg (on the PATH) is the
// reference: it is what mpv plays through, so the same number of samples
// proves the gapless trimming is the same, and the differences show the
// decoding is. Lossy decoders differ in their last bits, so the measure is
// ISO 11172-4's for full accuracy: RMS under 2^-15/sqrt(12), every sample
// within 2^-14.
// seek: twenty places in the file, each read after a seek and compared with
// the same samples from a straight decode.
// play: the engine at volume 0 (the output still runs, so its timeline and
// the levels are real), with a jump near the end of each track to watch the
// boundary pass.

using System.Diagnostics;
using System.Security.Cryptography;
using AlbumWall.Codecs;
using AlbumWall.Playback;

var mode = args.Length > 0 ? args[0] : "";
var files = args.Skip(1).ToList();
switch (mode)
{
    case "decode": return Decode(files);
    case "compare": return Compare(files);
    case "seek": return SeekCheck(files[0]);
    case "play": return PlayCheck(files);
    default: Console.WriteLine("usage: engine-check.cs decode|compare|seek|play <file>..."); return 2;
}

/// Everything the track plays, straight through.
static float[] All(IAudioTrack t)
{
    var all = new List<float>((int)Math.Min(int.MaxValue / 2, (t.TotalFrames + 4096) * t.Channels));
    var buf = new float[4096 * t.Channels];
    int n;
    while ((n = t.Read(buf)) > 0) all.AddRange(buf.AsSpan(0, n * t.Channels));
    return [.. all];
}

static int Decode(List<string> files)
{
    var bad = 0;
    foreach (var f in files)
    {
        var sw = Stopwatch.StartNew();
        var t = FlacTrack.Open(f);
        var all = All(t);
        if (all.Length != t.Info.TotalSamples * t.Info.Channels) throw new Exception($"read {all.Length / t.Info.Channels} frames, STREAMINFO says {t.Info.TotalSamples}");
        var bytes = (t.Info.BitsPerSample + 7) / 8;
        var full = 1L << (t.Info.BitsPerSample - 1);
        var pcm = new byte[all.Length * bytes];
        for (var i = 0; i < all.Length; i++)
        {
            var v = (long)Math.Round(all[i] * (double)full);
            for (var b = 0; b < bytes; b++) pcm[i * bytes + b] = (byte)(v >> (8 * b));
        }
        var ok = MD5.HashData(pcm).AsSpan().SequenceEqual(t.Info.Md5);
        if (!ok) bad++;
        var secs = (double)t.Info.TotalSamples / t.Info.SampleRate;
        Console.WriteLine($"{(ok ? "MD5 ok " : "MD5 BAD")}  {secs,6:0.0} s in {sw.ElapsedMilliseconds,4} ms ({secs * 1000 / sw.ElapsedMilliseconds,4:0}x)  "
                        + $"gain x{t.Gain.Multiplier(GainMode.Album):0.###}/{t.Gain.Multiplier(GainMode.Track):0.###}  {Path.GetFileName(f)}");
    }
    Console.WriteLine(bad == 0 ? $"all {files.Count} match" : $"{bad} of {files.Count} DO NOT match");
    return bad == 0 ? 0 : 1;
}

static int Compare(List<string> files)
{
    var rmsLimit = Math.Pow(2, -15) / Math.Sqrt(12);
    var maxLimit = Math.Pow(2, -14);
    var bad = 0;
    foreach (var f in files)
    {
        var sw = Stopwatch.StartNew();
        var t = AudioTracks.Open(f, f);
        var ours = All(t);
        var ms = sw.ElapsedMilliseconds;
        var reference = Ffmpeg(f, t.Channels);
        var ch = t.Channels;
        var frames = Math.Min(ours.Length, reference.Length) / ch;
        double sum = 0, max = 0;
        for (var i = 0; i < frames * ch; i++)
        {
            var d = Math.Abs((double)ours[i] - reference[i]);
            sum += d * d;
            max = Math.Max(max, d);
        }
        var rms = Math.Sqrt(sum / Math.Max(1, frames * ch));
        var sameLength = ours.Length == reference.Length;
        var ok = sameLength && rms < rmsLimit && max <= maxLimit;
        if (!ok) bad++;
        var secs = (double)ours.Length / ch / t.SampleRate;
        Console.WriteLine($"{(ok ? "ok " : "BAD")}  {ours.Length / ch,9} frames{(sameLength ? "" : $" (ffmpeg {reference.Length / ch}, {ours.Length / ch - reference.Length / ch:+0;-0})")}  "
                        + $"rms {rms:0.0e0} max {max:0.0e0}  {secs,6:0.0} s in {ms,4} ms ({secs * 1000 / Math.Max(1, ms),4:0}x)  "
                        + $"{(t is Mp3Track m ? $"pad {m.StartPad}/{m.EndPad} " : t is Mp4Track a ? $"skip {a.Skip} end {a.End} " : "")}gain x{t.Gain.Multiplier(GainMode.Album):0.###}/{t.Gain.Multiplier(GainMode.Track):0.###}  {Path.GetFileName(f)}");
        if (!sameLength) Console.WriteLine($"       best alignment: ours is {Offset(ours, reference, ch):+0;-0} frames from ffmpeg's");
    }
    Console.WriteLine(bad == 0 ? $"all {files.Count} within ISO full accuracy of ffmpeg" : $"{bad} of {files.Count} NOT");
    return bad == 0 ? 0 : 1;
}

/// ffmpeg's decode of the file, as floats, with its own gapless trimming.
static float[] Ffmpeg(string file, int channels)
{
    var psi = new ProcessStartInfo("ffmpeg", ["-v", "error", "-i", file, "-f", "f32le", "-ac", channels.ToString(), "-"])
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    using var p = Process.Start(psi)!;
    using var ms = new MemoryStream();
    p.StandardOutput.BaseStream.CopyTo(ms);
    p.WaitForExit();
    var bytes = ms.ToArray();
    var floats = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * 4);
    return floats;
}

/// Where ours lines up best with the reference, within 4096 frames: for a
/// length that differs, whether the start is off and by how much.
static int Offset(float[] ours, float[] reference, int ch)
{
    const int span = 4096, window = 44100;
    var best = 0;
    var bestErr = double.MaxValue;
    for (var off = -span; off <= span; off++)
    {
        double err = 0;
        for (var i = span; i < span + window && (i + off + 1) * ch <= ours.Length && (i + 1) * ch <= reference.Length; i++)
        {
            var d = ours[(i + off) * ch] - reference[i * ch];
            err += d * d;
        }
        if (err < bestErr) { bestErr = err; best = off; }
    }
    return best;
}

static int SeekCheck(string file)
{
    var t = AudioTracks.Open(file, file);
    var all = All(t);
    var ch = t.Channels;
    var total = all.Length / ch;
    var rng = new Random(7);
    var places = new List<long> { 0, 1, 1151, 1152, 4095, 4096, total - 100 };
    while (places.Count < 20) places.Add(rng.NextInt64(total - 5000));
    var bad = 0;
    foreach (var p in places)
    {
        var sw = Stopwatch.StartNew();
        t.Seek(p);
        var buf = new float[Math.Min(5000, total - p) * ch];
        var n = t.Read(buf);
        var same = n * ch == buf.Length && buf.AsSpan().SequenceEqual(all.AsSpan((int)(p * ch), buf.Length));
        if (!same) bad++;
        Console.WriteLine($"{(same ? "ok " : "BAD")} sample {p,9}  {sw.Elapsed.TotalMilliseconds,6:0.0} ms");
    }
    Console.WriteLine(bad == 0 ? "every seek exact" : $"{bad} seeks WRONG");
    return bad == 0 ? 0 : 1;
}

static int PlayCheck(List<string> files)
{
    using var e = new OwnEngine(GainMode.Album) { Volume = 0 };
    var sw = Stopwatch.StartNew();
    string T() => $"{sw.Elapsed.TotalSeconds,6:0.00}";
    var finished = false;
    e.TrackChanged += (_, a) => Console.WriteLine($"{T()} TrackChanged {a.Index}");
    e.DurationChanged += (_, _) => Console.WriteLine($"{T()} Duration {e.Duration:m\\:ss\\.fff}");
    e.StateChanged += (_, _) => Console.WriteLine($"{T()} State playing={e.IsPlaying}");
    e.Seeked += (_, at) => Console.WriteLine($"{T()} Seeked {at:m\\:ss}");
    e.Finished += (_, _) => { Console.WriteLine($"{T()} Finished (index {e.Index})"); finished = true; };

    e.Play(files, 0, null, false);
    Thread.Sleep(1500);
    Console.WriteLine($"{T()} pos {e.Position:m\\:ss\\.fff}  levels {e.Levels().Left:0.0}/{e.Levels().Right:0.0} dB");
    for (var i = 0; i < files.Count && !finished; i++)
    {
        while (e.Index != i && !finished) Thread.Sleep(20);
        var near = e.Duration - TimeSpan.FromSeconds(2);
        Console.WriteLine($"{T()} seek to {near:m\\:ss\\.fff} of track {i}");
        e.Seek(near);
        Thread.Sleep(1000);
        Console.WriteLine($"{T()} pos {e.Position:m\\:ss\\.fff} index {e.Index}");
        var until = DateTime.UtcNow.AddSeconds(4);
        while (e.Index == i && !finished && DateTime.UtcNow < until) Thread.Sleep(10);
        Console.WriteLine($"{T()} pos {e.Position:m\\:ss\\.fff} index {e.Index}  levels {e.Levels().Left:0.0}/{e.Levels().Right:0.0} dB");
    }
    var stop = DateTime.UtcNow.AddSeconds(5);
    while (!finished && DateTime.UtcNow < stop) Thread.Sleep(20);
    Console.WriteLine(finished ? "played to the end" : "DID NOT FINISH");
    return finished ? 0 : 1;
}
