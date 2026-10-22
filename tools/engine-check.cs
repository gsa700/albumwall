#:project ../src/AlbumWall.Playback/AlbumWall.Playback.csproj
#:property AllowUnsafeBlocks=true

// AlbumWall - the own audio engine (FlacEngine), checked without the app.
//
//   dotnet run tools/engine-check.cs -- decode <file.flac>...   every sample, against the file's own MD5
//   dotnet run tools/engine-check.cs -- seek <file.flac>        seeks land on the exact sample
//   dotnet run tools/engine-check.cs -- play <file.flac>...     plays MUTED, and says what the engine reports
//
// decode: FLAC stores an MD5 of its audio. Every sample is decoded the way
// the engine decodes it, to floats, turned back into integers and hashed, so
// a match proves both the decoder and that the float conversion loses nothing.
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
    case "seek": return SeekCheck(files[0]);
    case "play": return PlayCheck(files);
    default: Console.WriteLine("usage: engine-check.cs decode|seek|play <file.flac>..."); return 2;
}

static float[] All(FlacTrack t)
{
    var all = new float[t.Info.TotalSamples * t.Info.Channels];
    var n = t.Read(all);
    if (n != t.Info.TotalSamples) throw new Exception($"read {n} frames, STREAMINFO says {t.Info.TotalSamples}");
    if (t.Read(new float[t.Info.Channels * 16]) != 0) throw new Exception("samples after the end");
    return all;
}

static int Decode(List<string> files)
{
    var bad = 0;
    foreach (var f in files)
    {
        var sw = Stopwatch.StartNew();
        var t = FlacTrack.Open(f);
        var all = All(t);
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

static int SeekCheck(string file)
{
    var t = FlacTrack.Open(file);
    var all = All(t);
    var ch = t.Info.Channels;
    var rng = new Random(7);
    var places = new List<long> { 0, 1, t.Info.MaxBlockSize - 1, t.Info.MaxBlockSize, t.Info.TotalSamples - 100 };
    while (places.Count < 20) places.Add(rng.NextInt64(t.Info.TotalSamples - 5000));
    var bad = 0;
    foreach (var p in places)
    {
        var sw = Stopwatch.StartNew();
        t.Seek(p);
        var buf = new float[Math.Min(5000, t.Info.TotalSamples - p) * ch];
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
    using var e = new FlacEngine(GainMode.Album) { Volume = 0 };
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
