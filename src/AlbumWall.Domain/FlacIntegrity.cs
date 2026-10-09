// AlbumWall — is the audio still what was ripped?
//
// Every FLAC carries an MD5 of its own decoded audio in its STREAMINFO block.
// Decoding the whole file and comparing is the one check that says, for any
// library and by nobody's conventions, that a file has not rotted, been cut
// short in a copy, or picked up a bad sector since it was written. It is what
// `flac -t` does, through the same library.
//
// libFLAC is the system's, loaded when first asked for (libFLAC.so.14 on
// current Linux, .so.12 on Debian 12 and Ubuntu 24.04). Where there is none,
// Available is false and the option says why instead of doing nothing.
//
// His rules for it, 2026-10-08: off by default ("we don't want some major
// background process going on without the users knowledge"), read only, and
// "keep it simple: an integrity check. more in depth is for a dedicated tool."
// So: FLAC only. MP3 and AAC carry no checksum of their audio to compare with.

using System.Runtime.InteropServices;

namespace AlbumWall.Domain;

public static class FlacIntegrity
{
    public enum Outcome { Ok, NoChecksum, Damaged }

    public sealed record Result(Outcome Outcome, string? Problem);

    private const string Lib = "FLAC";

    static FlacIntegrity()
    {
        NativeLibrary.SetDllImportResolver(typeof(FlacIntegrity).Assembly, (name, assembly, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            foreach (var candidate in Candidates())
                if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
            return IntPtr.Zero;
        });
    }

    private static IEnumerable<string> Candidates() => OperatingSystem.IsWindows()
        ? ["libFLAC.dll", "FLAC.dll", "libFLAC-8.dll"]
        : OperatingSystem.IsMacOS()
            ? ["libFLAC.14.dylib", "libFLAC.12.dylib", "libFLAC.dylib"]
            : ["libFLAC.so.14", "libFLAC.so.12", "libFLAC.so.8", "libFLAC.so"];

    private static bool? _available;

    /// Whether this machine has a libFLAC to decode with. Asked once.
    public static bool Available => _available ??= Probe();

    private static bool Probe()
    {
        try
        {
            var decoder = FLAC__stream_decoder_new();
            if (decoder == IntPtr.Zero) return false;
            FLAC__stream_decoder_delete(decoder);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// Decodes the whole file with MD5 checking on. Throws nothing: a file that
    /// cannot be opened or decoded IS the answer. Not thread safe; the checker
    /// runs one file at a time.
    public static Result Check(string path)
    {
        if (!HasChecksum(path, out var headerProblem))
            return headerProblem is null ? Verify(path, noChecksum: true) : new(Outcome.Damaged, headerProblem);
        return Verify(path, noChecksum: false);
    }

    private static Result Verify(string path, bool noChecksum)
    {
        var decoder = FLAC__stream_decoder_new();
        if (decoder == IntPtr.Zero) return new(Outcome.Damaged, "libFLAC could not start a decoder");
        _errors = 0;
        _firstError = null;
        try
        {
            FLAC__stream_decoder_set_md5_checking(decoder, 1);
            var init = FLAC__stream_decoder_init_file(decoder, path, Write, IntPtr.Zero, Error, IntPtr.Zero);
            if (init != 0) return new(Outcome.Damaged, $"could not be opened (libFLAC init status {init})");

            var decoded = FLAC__stream_decoder_process_until_end_of_stream(decoder) != 0;
            var state = FLAC__stream_decoder_get_state(decoder);
            // finish() is where libFLAC compares the MD5: false = the audio
            // decoded is not the audio that was written.
            var md5Ok = FLAC__stream_decoder_finish(decoder) != 0;

            if (_errors > 0) return new(Outcome.Damaged, $"{_errors} decoding error(s), first: {_firstError}");
            if (!decoded || state > EndOfStream) return new(Outcome.Damaged, $"decoding stopped early (state {state})");
            if (!md5Ok) return new(Outcome.Damaged, "the audio does not match its checksum");
            return new(noChecksum ? Outcome.NoChecksum : Outcome.Ok, null);
        }
        finally
        {
            FLAC__stream_decoder_delete(decoder);
        }
    }

    /// STREAMINFO is the first metadata block, right after "fLaC", and ends
    /// with the 16-byte MD5. All zeros means the encoder never wrote one, so
    /// a clean decode is all that can be said.
    private static bool HasChecksum(string path, out string? problem)
    {
        problem = null;
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[42];
            if (f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
            {
                problem = "too short to be a FLAC file";
                return false;
            }
            if (head[0] != (byte)'f' || head[1] != (byte)'L' || head[2] != (byte)'a' || head[3] != (byte)'C')
            {
                problem = "not a FLAC file (no fLaC marker)";
                return false;
            }
            foreach (var b in head[26..42]) if (b != 0) return true;
            return false;
        }
        catch (Exception e)
        {
            problem = "could not be read: " + e.Message;
            return false;
        }
    }

    // ---- libFLAC (stream_decoder.h)

    private const int EndOfStream = 4;   // FLAC__STREAM_DECODER_END_OF_STREAM; above it = aborted/error

    [ThreadStatic] private static int _errors;
    [ThreadStatic] private static string? _firstError;

    private delegate int WriteCallback(IntPtr decoder, IntPtr frame, IntPtr buffer, IntPtr client);
    private delegate void ErrorCallback(IntPtr decoder, int status, IntPtr client);

    // Kept in static fields so the collector never frees what libFLAC calls.
    private static readonly WriteCallback Write = (_, _, _, _) => 0;   // CONTINUE: the samples are not needed
    private static readonly ErrorCallback Error = (_, status, _) =>
    {
        _errors++;
        _firstError ??= status switch
        {
            0 => "lost sync",
            1 => "bad frame header",
            2 => "frame CRC mismatch",
            3 => "unparseable stream",
            4 => "bad metadata",
            _ => $"error {status}",
        };
    };

    [DllImport(Lib)] private static extern IntPtr FLAC__stream_decoder_new();
    [DllImport(Lib)] private static extern void FLAC__stream_decoder_delete(IntPtr decoder);
    [DllImport(Lib)] private static extern int FLAC__stream_decoder_set_md5_checking(IntPtr decoder, int value);
    [DllImport(Lib)] private static extern int FLAC__stream_decoder_init_file(
        IntPtr decoder, [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        WriteCallback write, IntPtr metadata, ErrorCallback error, IntPtr client);
    [DllImport(Lib)] private static extern int FLAC__stream_decoder_process_until_end_of_stream(IntPtr decoder);
    [DllImport(Lib)] private static extern int FLAC__stream_decoder_get_state(IntPtr decoder);
    [DllImport(Lib)] private static extern int FLAC__stream_decoder_finish(IntPtr decoder);
}
