// AlbumWall — is this libmpv actually gapless? Measured, not listened to.
//
//   dotnet run tools/gapless-check.cs -- <path to libmpv> <track 1> <track 2> [more tracks...]
//
// Decodes the tracks back to back through the given libmpv, with the same
// gapless options the player uses, into one WAV at full speed (mpv's `pcm`
// output is untimed, so ten minutes of music takes half a second). Then it
// counts the samples that came out and compares them with the number the FILES
// say they contain.
//
// WHY THIS EXISTS. Gapless is this project's hard requirement, and until
// 2026-09-20 the only test was somebody's ears. That day a new audio-only
// libmpv passed by ear on Linux — on FLAC — and on Windows produced "a barely
// audible burp right after the track change" on AAC. This tool found the cause
// in under a second: the output was 1,012 samples longer than the upstream
// library's, and 676 + 336 are exactly the END PADDING the two files declare.
// The build trimmed each track's encoder delay and left its padding in.
//
// WHY FLAC PROVES NOTHING. A lossy encoder pads every track: some samples of
// priming at the front (2,112 for Apple's AAC) and enough at the back to fill
// the last frame. A player is only gapless if it trims BOTH, using numbers the
// encoder wrote down. FLAC has no padding, so a FLAC album is gapless in any
// player that simply does not stop between files. Test with AAC (.m4a) from
// iTunes — those carry the numbers in an `iTunSMPB` tag and usually have no MP4
// edit list, which is the case that separates one ffmpeg from another — and
// with MP3.
//
// What it can check exactly: .m4a files with an iTunSMPB tag, because that tag
// states the true sample count. For anything else it still reports what came
// out, which is enough to compare two libraries against each other: the right
// answer is the same number from both.

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: dotnet run tools/gapless-check.cs -- <libmpv> <track> <track> [...]");
    return 2;
}

var lib = NativeLibrary.Load(Path.GetFullPath(args[0]));
var files = args[1..].Select(Path.GetFullPath).ToArray();

// Said here, because otherwise it is said sixty lines down as "could not find
// gapless-check-1234.wav", which reads like a libmpv that cannot decode. Git
// Bash on Windows rewrites backslashed arguments; PowerShell does not.
if (files.Where(f => !File.Exists(f)).ToList() is { Count: > 0 } missing)
{
    foreach (var f in missing) Console.Error.WriteLine($"no such track: {f}");
    return 2;
}
var wav = Path.Combine(Path.GetTempPath(), $"gapless-check-{Environment.ProcessId}.wav");

T Fn<T>(string name) where T : Delegate =>
    Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
var create = Fn<Create>("mpv_create");
var initialize = Fn<Init>("mpv_initialize");
var setOption = Fn<SetOpt>("mpv_set_option_string");
var getString = Fn<GetStr>("mpv_get_property_string");
var command = Fn<Command>("mpv_command");
var waitEvent = Fn<Wait>("mpv_wait_event");
var destroy = Fn<Destroy>("mpv_terminate_destroy");

var ctx = create();
void Option(string k, string v) { if (setOption(ctx, k, v) < 0) Console.WriteLine($"  (option {k}={v} refused)"); }

// The player's own gapless settings — see Player.cs — so this tests what ships.
Option("gapless-audio", "yes");
Option("prefetch-playlist", "yes");
Option("audio-display", "no");
Option("audio-samplerate", "0");
// And the measuring rig.
Option("vid", "no"); Option("terminal", "no"); Option("idle", "yes");
Option("ao", "pcm"); Option("ao-pcm-file", wav); Option("ao-pcm-waveheader", "yes");
Option("audio-format", "s16");

if (initialize(ctx) < 0) { Console.Error.WriteLine("mpv would not initialize"); return 2; }
Console.WriteLine($"{Marshal.PtrToStringUTF8(getString(ctx, "mpv-version"))}, ffmpeg {Marshal.PtrToStringUTF8(getString(ctx, "ffmpeg-version"))}");

void Run(params string[] a)
{
    var ptrs = a.Select(Marshal.StringToCoTaskMemUTF8).Append(IntPtr.Zero).ToArray();
    var block = Marshal.AllocCoTaskMem(IntPtr.Size * ptrs.Length);
    Marshal.Copy(ptrs, 0, block, ptrs.Length);
    if (command(ctx, block) < 0) Console.WriteLine($"  ({a[0]} failed)");
}
Run("loadfile", files[0], "replace");
foreach (var f in files[1..]) Run("loadfile", f, "append");

const int EndFile = 7, Shutdown = 1;
var ended = 0;
var deadline = DateTime.UtcNow.AddMinutes(5);
while (ended < files.Length && DateTime.UtcNow < deadline)
{
    var id = Marshal.ReadInt32(waitEvent(ctx, 1.0));
    if (id == EndFile) ended++;
    if (id == Shutdown) break;
}
destroy(ctx);

// 44-byte header, 16-bit stereo unless the source says otherwise; read it.
long decoded;
using (var r = new BinaryReader(File.OpenRead(wav)))
{
    r.BaseStream.Seek(22, SeekOrigin.Begin); var channels = r.ReadInt16();
    r.BaseStream.Seek(34, SeekOrigin.Begin); var bits = r.ReadInt16();
    decoded = (r.BaseStream.Length - 44) / (channels * bits / 8);
}
File.Delete(wav);

// What the files say. iTunSMPB: " 00000000 <delay> <padding> <samples> ..."
long expected = 0; var known = true;
foreach (var f in files)
{
    var head = Latin1(f);
    var m = Regex.Match(head, "iTunSMPB.{0,120}? 0{8} ([0-9A-Fa-f]{8}) ([0-9A-Fa-f]{8}) ([0-9A-Fa-f]{16})", RegexOptions.Singleline);
    if (!m.Success) { known = false; Console.WriteLine($"  {Path.GetFileName(f)}: no iTunSMPB tag, length not checkable"); continue; }
    var delay = Convert.ToInt64(m.Groups[1].Value, 16);
    var padding = Convert.ToInt64(m.Groups[2].Value, 16);
    var samples = Convert.ToInt64(m.Groups[3].Value, 16);
    expected += samples;
    Console.WriteLine($"  {Path.GetFileName(f)}: {samples} samples, encoder delay {delay}, end padding {padding}");
}

Console.WriteLine($"decoded {decoded} samples from {ended} of {files.Length} tracks");
if (!known) { Console.WriteLine("NOT CHECKED: compare this number against another libmpv on the same files."); return 0; }

var extra = decoded - expected;

// A few samples either way are not a failure. The known-good upstream build
// comes out 6 samples over on three Dark Side tracks — a tenth of a millisecond,
// at the very end of the stream — while a build that fails to trim is over by
// the files' padding, which is hundreds. Sixteen per track separates them with
// room to spare and is far below anything audible.
var tolerance = 16L * files.Length;
if (Math.Abs(extra) <= tolerance)
{
    Console.WriteLine($"PASS: within {Math.Abs(extra)} samples of what the files contain. Gapless.");
    return 0;
}
Console.WriteLine($"FAIL: {extra:+#;-#} samples ({extra * 1000.0 / 44100:0.0} ms at 44.1 kHz) against the {expected} the files contain.");
Console.WriteLine("      Extra samples are padding or priming that was played instead of trimmed: a click or a burp at every track change.");
return 1;

static string Latin1(string path)
{
    // The tag lives in the metadata, which is at the front or the back of the file.
    using var fs = File.OpenRead(path);
    var take = (int)Math.Min(fs.Length, 2_000_000);
    var a = new byte[take]; fs.ReadExactly(a);
    var b = new byte[take]; fs.Seek(-take, SeekOrigin.End); fs.ReadExactly(b);
    return System.Text.Encoding.Latin1.GetString(a) + System.Text.Encoding.Latin1.GetString(b);
}

delegate IntPtr Create(); delegate int Init(IntPtr c); delegate void Destroy(IntPtr c);
delegate int SetOpt(IntPtr c, [MarshalAs(UnmanagedType.LPUTF8Str)] string k, [MarshalAs(UnmanagedType.LPUTF8Str)] string v);
delegate IntPtr GetStr(IntPtr c, [MarshalAs(UnmanagedType.LPUTF8Str)] string n);
delegate int Command(IntPtr c, IntPtr a); delegate IntPtr Wait(IntPtr c, double timeout);
