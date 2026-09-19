// AlbumWall — a copy of the console in a file.
//
// Everything this app has to say, it says with Console.WriteLine: what the
// window manager granted, what mpv did, why a scan ran. On Linux that is enough,
// because it is started from a terminal and the terminal shows it. On Windows a
// GUI program started from a terminal DETACHES — the prompt comes straight back
// and stdout goes nowhere — so the one run that misbehaves is always the one
// nobody was recording.
//
// Found the hard way: the unfolded album came back on every launch made with
// the output redirected, and not on his, and there was nothing to read to find
// out why. So the console is teed to albumwall.log beside the settings, the
// previous run is kept as albumwall.log.1, and "what did it say?" has an answer
// however it was launched.

using System.Text;

namespace AlbumWall.App;

public static class LogFile
{
    public static string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Settings.Path)!, "albumwall.log");

    /// Best effort. A log that cannot be opened must never stop the app
    /// starting; it just goes back to being console-only.
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path)) File.Move(Path, Path + ".1", overwrite: true);

            var file = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write,
                                                       FileShare.ReadWrite | FileShare.Delete))
            { AutoFlush = true };

            Console.SetOut(new Tee(Console.Out, file));
            Console.WriteLine($"[log] {DateTime.Now:yyyy-MM-dd HH:mm:ss}  pid {Environment.ProcessId}  "
                            + $"{typeof(LogFile).Assembly.GetName().Version}");
        }
        catch { /* console only */ }
    }

    /// Lines are stamped with the time since start, because nearly every
    /// question asked of this log is "in what order, and how far apart?".
    private sealed class Tee(TextWriter console, TextWriter file) : TextWriter
    {
        private readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();
        private readonly object _gate = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                try { console.WriteLine(value); } catch { /* no console: fine */ }
                try { file.WriteLine($"{_since.Elapsed.TotalSeconds,8:0.000}  {value}"); } catch { }
            }
        }

        public override void Write(char value)
        {
            lock (_gate)
            {
                try { console.Write(value); } catch { }
                try { file.Write(value); } catch { }
            }
        }
    }
}
