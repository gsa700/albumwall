#:project ../src/AlbumWall.Playback/AlbumWall.Playback.csproj
#:property AllowUnsafeBlocks=true

// AlbumWall - Hymie, the own audio engine (HymieEngine) and its decoders, checked
// without the app.
//
//   dotnet run tools/engine-check.cs -- decode <file.flac>...   every sample, against the file's own MD5
//   dotnet run tools/engine-check.cs -- compare <file>...       every sample, against ffmpeg's decode of the file
//   dotnet run tools/engine-check.cs -- seek <file>             seeks land on the exact sample
//   dotnet run tools/engine-check.cs -- same <file.m4a> <file.flac>   an ALAC file plays its FLAC source's samples
//   dotnet run tools/engine-check.cs -- play <file>...          plays MUTED, and says what the engine reports
//   dotnet run tools/engine-check.cs -- hostile                 crafted files and server replies fail as broken tracks
//
// decode: FLAC stores an MD5 of its audio. Every sample is decoded the way
// the engine decodes it, to floats, turned back into integers and hashed, so
// a match proves both the decoder and that the float conversion loses nothing.
// compare: for formats with no MD5 (MP3), ffmpeg (on the PATH) is the
// reference: it is what mpv played through until 0.7.0, and the decoders
// were made to match it, so the same number of samples proves the gapless
// trimming is the same, and the differences show the
// decoding is. Lossy decoders differ in their last bits, so the measure is
// ISO 11172-4's for full accuracy: RMS under 2^-15/sqrt(12), every sample
// within 2^-14. A lossless one (ALAC, FLAC) differs in none: every sample
// the same.
// same: ALAC has no MD5 either, so the FLAC it was made from stands in:
// both decoded here, every float the same, and as many.
// seek: twenty places in the file, each read after a seek and compared with
// the same samples from a straight decode.
// play: the engine at volume 0 (the output still runs, so its timeline and
// the levels are real), with a jump near the end of each track to watch the
// boundary pass.
// hostile: the security review of 2026-10-26 (findings 3, 4 and 5), made here
// in memory and on 127.0.0.1: MP4 sample tables claiming more than their box
// holds, a WAV claiming 3,221,225,472 Hz, a server that sends headers and then
// trickles, one that claims a length no array can have. Each must fail as a
// broken track does (an IOException, which the engine passes over) and in
// bounded time; before, they threw IndexOutOfRange, Overflow and OutOfMemory,
// or never returned. About 35 s, most of it the stalled server.

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
    case "same": return Same(files[0], files[1]);
    case "play": return PlayCheck(files);
    case "hostile": return Hostile();
    default: Console.WriteLine("usage: engine-check.cs decode|compare|seek|same|play <file>... | hostile"); return 2;
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
        var ok = sameLength && (t.BitsPerSample > 0 ? max == 0 : rms < rmsLimit && max <= maxLimit);
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

static int Same(string file, string source)
{
    var sw = Stopwatch.StartNew();
    var t = AudioTracks.Open(file, file);
    var ours = All(t);
    var ms = sw.ElapsedMilliseconds;
    var s = FlacTrack.Open(source);
    var reference = All(s);
    var format = t.SampleRate == s.SampleRate && t.Channels == s.Channels && t.BitsPerSample == s.BitsPerSample;
    var differ = 0;
    for (var i = 0; i < Math.Min(ours.Length, reference.Length); i++) if (ours[i] != reference[i]) differ++;
    var ok = format && differ == 0 && ours.Length == reference.Length;
    var secs = (double)ours.Length / t.Channels / t.SampleRate;
    Console.WriteLine($"{(ok ? "ok " : "BAD")}  {t.SampleRate} Hz {t.BitsPerSample}-bit {t.Channels} ch (FLAC {s.SampleRate} Hz {s.BitsPerSample}-bit {s.Channels} ch)  "
                    + $"{ours.Length / t.Channels} frames (FLAC {reference.Length / s.Channels})  {differ} samples differ  "
                    + $"{secs:0.0} s in {ms} ms  {Path.GetFileName(file)}");
    return ok ? 0 : 1;
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
    using var e = new HymieEngine(GainMode.Album) { Volume = 0 };
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

static int Hostile()
{
    var bad = 0;
    var dir = Directory.CreateTempSubdirectory("engine-check-hostile-").FullName;
    try
    {
        // What the review's files were: an ftyp and a moov holding one audio trak whose
        // sample table has one lying box.
        static byte[] Box(string type, params byte[][] body)
        {
            var inner = body.SelectMany(b => b).ToArray();
            var b = new byte[8 + inner.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
            inner.CopyTo(b, 8);
            return b;
        }
        static byte[] U32(params uint[] v) => v.SelectMany(x => { var b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, x); return b; }).ToArray();
        static byte[] Mp4(byte[] table) =>
        [
            .. Box("ftyp", "M4A \0\0\0\0M4A mp42isom"u8.ToArray()),
            .. Box("moov", Box("trak", Box("mdia",
                Box("mdhd", U32(0, 0, 0, 44100, 44100, 0)),
                Box("hdlr", U32(0, 0), "soun"u8.ToArray(), new byte[12]),
                Box("minf", Box("stbl", table))))),
        ];
        var cases = new (string Name, byte[] Bytes)[]
        {
            ("stco count 100000, no entries.m4a", Mp4(Box("stco", U32(0, 100_000)))),
            ("stsc count 100000, no entries.m4a", Mp4(Box("stsc", U32(0, 100_000)))),
            ("stsz count 0x80000000.m4a", Mp4(Box("stsz", U32(0, 0, 0x8000_0000)))),
            ("stsz count 0x7FFFFFF0.m4a", Mp4(Box("stsz", U32(0, 0, 0x7FFF_FFF0)))),
            ("co64 count 100000, no entries.m4a", Mp4(Box("co64", U32(0, 100_000)))),
            ("stsz fixed size, count 0x7FFFFFF0.m4a", Mp4(Box("stsz", U32(0, 1024, 0x7FFF_FFF0)))),
            ("rate 3221225472.wav", Wav(3_221_225_472)),
        };
        static byte[] Wav(uint rate)
        {
            var w = new byte[44 + 4];
            "RIFF"u8.CopyTo(w); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(w.AsSpan(4), (uint)w.Length - 8);
            "WAVEfmt "u8.CopyTo(w.AsSpan(8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(w.AsSpan(16), 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(20), 1);      // PCM
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(22), 2);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(w.AsSpan(24), rate);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(w.AsSpan(28), rate * 4);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(32), 4);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(34), 16);
            "data"u8.CopyTo(w.AsSpan(36));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(w.AsSpan(40), 4);
            return w;
        }
        foreach (var (name, bytes) in cases)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            bad += Refused(name, () => AudioTracks.Open(path, path), TimeSpan.FromSeconds(5));
        }

        // FLAC (the review's L14 and STREAMINFO): two stereo frames of 192 samples,
        // made by hand, under a STREAMINFO that tells the truth or lies. The true one
        // must play, every sample: else the frames are wrong and a refusal proves
        // nothing. The frames are read to the end, which is where a frame is checked.
        var stereo = Flac(channels: 2, maxBlock: 192, infoSize: 34);
        var truth = Path.Combine(dir, "true.flac");
        File.WriteAllBytes(truth, stereo);
        {
            var t = AudioTracks.Open(truth, truth);
            var n = ReadAll(t);
            Console.WriteLine(n == 384 ? "ok    FLAC made here: plays, 384 samples" : $"FAIL  FLAC made here: {n} samples, not 384");
            if (n != 384) bad++;
        }
        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("STREAMINFO mono, frames stereo.flac", Flac(channels: 1, maxBlock: 192, infoSize: 34)),
            ("STREAMINFO largest block 64, frames 192.flac", Flac(channels: 2, maxBlock: 64, infoSize: 34)),
            ("STREAMINFO 20 bytes.flac", Flac(channels: 2, maxBlock: 192, infoSize: 20)),
        })
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            bad += Refused(name, () => { var t = AudioTracks.Open(path, path); ReadAll(t); return t; }, TimeSpan.FromSeconds(5));
        }

        // A server on 127.0.0.1: what it does with each request is the request's path.
        var listener = new System.Net.HttpListener();
        var port = Random.Shared.Next(20000, 60000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        c.Response.ContentType = "audio/flac";
                        if (c.Request.Url!.AbsolutePath == "/trickle")
                        {
                            // Headers, one byte, then nothing: no length (chunked).
                            c.Response.SendChunked = true;
                            await c.Response.OutputStream.WriteAsync("f"u8.ToArray());
                            await c.Response.OutputStream.FlushAsync();
                            await Task.Delay(TimeSpan.FromSeconds(90));
                        }
                        else
                        {
                            c.Response.ContentLength64 = int.MaxValue;     // claimed, never sent
                            await c.Response.OutputStream.FlushAsync();
                            await Task.Delay(TimeSpan.FromSeconds(5));
                        }
                        c.Response.Abort();
                    }
                    catch (Exception) { }
                });
            }
        });
        bad += Refused("server: headers, one byte, then a stall (no length)",
            () => AudioTracks.Open("Trickle.flac", $"http://127.0.0.1:{port}/trickle"), TimeSpan.FromSeconds(40));
        bad += Refused("server: Content-Length 2147483647",
            () => AudioTracks.Open("Huge.flac", $"http://127.0.0.1:{port}/huge"), TimeSpan.FromSeconds(5));
        listener.Stop();
    }
    finally { Directory.Delete(dir, recursive: true); }
    Console.WriteLine(bad == 0 ? "hostile: all refused as broken tracks" : $"hostile: {bad} FAILED");
    return bad == 0 ? 0 : 1;

    // Opening must fail with an IOException (what a broken track throws, and the engine
    // passes over) within `limit`.
    static int ReadAll(IAudioTrack t)
    {
        var buf = new float[1024 * t.Channels];
        int n, total = 0;
        while ((n = t.Read(buf)) > 0) total += n;
        return total;
    }

    // fLaC, a STREAMINFO saying `channels` and `maxBlock` (`infoSize` bytes of it
    // claimed, 34 true), then two frames: stereo, 192 samples of 16 bits, each
    // channel a constant subframe. CRC-8 over the header, CRC-16 over the frame.
    static byte[] Flac(int channels, int maxBlock, int infoSize)
    {
        var f = new List<byte>("fLaC"u8.ToArray()) { 0x80, 0, 0, (byte)infoSize };
        var info = new byte[34];
        info[0] = 0; info[1] = 192; info[2] = (byte)(maxBlock >> 8); info[3] = (byte)maxBlock;
        var packed = (ulong)44100 << 44 | (ulong)(channels - 1) << 41 | (ulong)15 << 36 | 384;
        for (var i = 0; i < 8; i++) info[10 + i] = (byte)(packed >> (56 - 8 * i));
        f.AddRange(info.Take(infoSize));
        for (var frame = 0; frame < 2; frame++)
        {
            // fixed blocking; 192 samples, 44.1 kHz; independent stereo, 16 bits; frame number
            var h = new List<byte> { 0xFF, 0xF8, 0x19, 0x18, (byte)frame };
            h.Add(Crc8(h));
            h.AddRange(new byte[] { 0x00, 0x10, 0x00, 0x00, 0xF0, 0x00 });   // constant 0x1000, then -0x1000
            var crc = Crc16(h);
            h.Add((byte)(crc >> 8)); h.Add((byte)crc);
            f.AddRange(h);
        }
        return f.ToArray();

        static byte Crc8(List<byte> d)
        {
            byte c = 0;
            foreach (var b in d) { c ^= b; for (var i = 0; i < 8; i++) c = (byte)((c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1); }
            return c;
        }
        static ushort Crc16(List<byte> d)
        {
            ushort c = 0;
            foreach (var b in d) { c ^= (ushort)(b << 8); for (var i = 0; i < 8; i++) c = (ushort)((c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1); }
            return c;
        }
    }

    static int Refused(string what, Func<IAudioTrack> open, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        var run = Task.Run(open);
        Task.WhenAny(run, Task.Delay(limit + TimeSpan.FromSeconds(1))).Wait();
        if (!run.IsCompleted)
        {
            Console.WriteLine($"FAIL  {what}: still opening after {sw.Elapsed.TotalSeconds:0} s");
            return 1;
        }
        var ex = run.Exception?.GetBaseException();
        // What the engine passes over as a broken track (HymieEngine.TryOpen): an IOException,
        // or an HttpRequestException where the connection is reset before any headers, as
        // Windows' http.sys does with the claimed 2 GB reply (Mobilenode, 2026-10-27).
        if (ex is IOException or System.Net.Http.HttpRequestException)
        {
            Console.WriteLine($"ok    {what}: {ex.GetType().Name} in {sw.Elapsed.TotalSeconds:0.0} s ({ex.Message})");
            return 0;
        }
        Console.WriteLine($"FAIL  {what}: {(ex is null ? "it opened" : ex.GetType().Name + ": " + ex.Message)}");
        return 1;
    }
}
