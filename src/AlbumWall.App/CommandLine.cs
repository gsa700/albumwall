// AlbumWall - what the command line can ask of the running copy, and --help.
//
// Linux has had most of this through MPRIS and playerctl; Windows had nothing,
// and a script on any of them can now say `AlbumWall --next`. The commands are
// carried to the copy that is running (SingleInstance) and done there by
// MainWindow.RunCommand; they never start one. --help is their list; the Help
// tab only points to it.

namespace AlbumWall.App;

public static class CommandLine
{
    public sealed record Command(string Name, string? Text);

    /// Flag, what it does: the list --help prints.
    public static readonly (string Flag, string Does)[] Commands =
    [
        ("--play-pause", "Play or pause."),
        ("--play", "Play, after a pause."),
        ("--play \"dark side\"", "Play the first album whose title, or else whose artist, contains the words, and open it on the wall."),
        ("--pause", "Pause."),
        ("--next", "Next track."),
        ("--previous", "Previous track."),
        ("--stop", "Stop."),
        ("--show", "Bring the window forward. Starting the app again while it runs does the same."),
        ("--quit", "Close the app."),
    ];

    private static readonly HashSet<string> Names =
        ["--play-pause", "--play", "--pause", "--next", "--previous", "--stop", "--show", "--quit"];

    /// The command in `args`, or null for a plain launch. `--play` takes the
    /// words after it, quoted or not, up to the next flag.
    public static Command? Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i].ToLowerInvariant();
            if (!Names.Contains(flag)) continue;
            var words = args.Skip(i + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal));
            var text = flag == "--play" ? string.Join(' ', words).Trim() : "";
            return new Command(flag[2..], text.Length > 0 ? text : null);
        }
        return null;
    }

    public static bool AsksHelp(IEnumerable<string> args) =>
        args.Any(a => a is "--help" or "-h" or "-?" or "/?");

    public static bool AsksVersion(IEnumerable<string> args) => args.Any(a => a is "--version" or "-v");

    public static string Help()
    {
        var width = Commands.Max(c => c.Flag.Length) + 3;
        var lines = new List<string>
        {
            $"{App.DisplayName} {Install.InstallService.CurrentVersion}",
            "",
            "Run with nothing to open the wall, or to bring it forward if it is already open.",
            "Given to the copy that is running (they never start one):",
            "",
        };
        lines.AddRange(Commands.Select(c => "  " + c.Flag.PadRight(width) + c.Does));
        lines.Add("");
        lines.Add("  --help".PadRight(width + 2) + "This list.");
        lines.Add("  --version".PadRight(width + 2) + "The version.");
        return string.Join(Environment.NewLine, lines);
    }

    /// A Windows GUI program has no console to print to; borrow the one it was
    /// started from, if any. Elsewhere the console is already there.
    public static void AttachConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (AttachConsole(-1))
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
        catch { /* nowhere to say it, then */ }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
