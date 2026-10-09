// AlbumWall — the integrity check's background pass over one library.
//
// One file at a time, on its own thread at below-normal priority, and after
// each file it rests for as long as the file took: at most half of one core,
// and less whenever anything else wants it. A library of ~4,000 FLACs is
// about ten minutes of decoding, so a first pass spread out like this is a
// background half hour; after that only new, changed and month-old files are
// read (IntegrityRecord).

namespace AlbumWall.Domain;

public static class IntegrityChecker
{
    /// How long a file must have sat unchanged before it is checked.
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(10);

    public sealed record Status(
        bool Running, int Files, int Checked, int Due, int Done,
        int Problems, int NoChecksum, DateTime? LastPass);

    /// What the record says, with no pass running.
    public static Status Summary(string recordPath)
    {
        var record = IntegrityRecord.Load(recordPath);
        return Summarize(record, running: false, due: 0, done: 0);
    }

    private static Status Summarize(IntegrityRecord record, bool running, int due, int done) => new(
        running, record.Files.Count, record.Files.Count(f => f.Value.Checked != default),
        due, done, record.Files.Count(f => f.Value.Problem is not null),
        record.Files.Count(f => f.Value.NoChecksum), record.LastPass);

    public static Task RunAsync(string root, string recordPath, string logPath,
                                Action<Status> progress, CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try { Run(root, recordPath, logPath, progress, ct); done.TrySetResult(); }
            catch (OperationCanceledException) { done.TrySetCanceled(ct); }
            catch (Exception e) { done.TrySetException(e); }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "integrity",
        };
        thread.Start();
        return done.Task;
    }

    private static void Run(string root, string recordPath, string logPath, Action<Status> progress, CancellationToken ct)
    {
        var record = IntegrityRecord.Load(recordPath);
        var now = DateTime.UtcNow;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var files = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*.flac", options))
        {
            ct.ThrowIfCancellationRequested();
            files[Path.GetRelativePath(root, path)] = new FileInfo(path);
        }

        // A file that has gone is no longer this library's business.
        foreach (var gone in record.Files.Keys.Where(k => !files.ContainsKey(k)).ToList())
            record.Files.Remove(gone);

        // New and changed files first, then the longest unchecked.
        var due = files
            .Where(f => record.IsDue(f.Key, f.Value, now))
            .OrderBy(f => record.Files.TryGetValue(f.Key, out var e) && e.Size == f.Value.Length
                          && e.Modified == f.Value.LastWriteTimeUtc.Ticks ? 1 : 0)
            .ThenBy(f => record.Files.TryGetValue(f.Key, out var e) ? e.Checked : DateTime.MinValue)
            .ToList();

        Console.WriteLine($"[integrity] {root}: {files.Count} FLAC files, {due.Count} due");
        progress(Summarize(record, running: true, due.Count, 0));

        var clock = new System.Diagnostics.Stopwatch();
        try
        {
        for (var i = 0; i < due.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (relative, file) = due[i];
            TimeSpan took;
            try
            {

            // A file written in the last few minutes may still BE being
            // written: a ripper encodes each track straight into its final
            // name, and half a file reads as damaged. It waits for a later
            // pass. (Found 2026-10-08, the first pass ran while he was ripping.)
            file.Refresh();
            if (!file.Exists || DateTime.UtcNow - file.LastWriteTimeUtc < Settle) continue;
            var before = (file.Length, file.LastWriteTimeUtc);

            clock.Restart();
            var result = FlacIntegrity.Check(file.FullName);
            took = clock.Elapsed;

            // Changed while it was being read: the answer is about a file that
            // no longer exists. Leave it due.
            file.Refresh();
            if (!file.Exists || (file.Length, file.LastWriteTimeUtc) != before) continue;

            var had = record.Files.GetValueOrDefault(relative)?.Problem;
            var problem = result.Outcome == FlacIntegrity.Outcome.Damaged ? result.Problem : null;
            record.Files[relative] = new IntegrityRecord.Entry
            {
                Size = file.Length,
                Modified = file.LastWriteTimeUtc.Ticks,
                Checked = DateTime.UtcNow,
                Problem = problem,
                NoChecksum = result.Outcome == FlacIntegrity.Outcome.NoChecksum,
            };
            if (problem is not null && problem != had)
            {
                Console.WriteLine($"[integrity] DAMAGED {relative}: {problem}");
                Log(logPath, $"DAMAGED  {Path.Combine(root, relative)}: {problem}");
            }
            else if (problem is null && had is not null)
            {
                Log(logPath, $"fine now {Path.Combine(root, relative)} (was: {had})");
            }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // One file that moved or vanished mid-pass (a ripper renaming
                // an album folder, 2026-10-08: "Could not find file" ended the
                // whole pass at 3,950 of 3,968) is skipped, never the pass.
                Console.WriteLine($"[integrity] skipped {relative}: {e.Message}");
                continue;
            }

            if ((i + 1) % 25 == 0)
            {
                record.Save(recordPath);
                progress(Summarize(record, running: true, due.Count, i + 1));
            }

            // Rest as long as the file took: half a core at most.
            if (ct.WaitHandle.WaitOne(took < TimeSpan.FromSeconds(2) ? took : TimeSpan.FromSeconds(2)))
                ct.ThrowIfCancellationRequested();
        }
        }
        catch (OperationCanceledException)
        {
            record.Save(recordPath);   // what was checked before the stop stays checked
            throw;
        }

        record.LastPass = DateTime.UtcNow;
        record.Save(recordPath);
        progress(Summarize(record, running: false, 0, 0));
        Console.WriteLine($"[integrity] {root}: pass done, {record.Files.Count(f => f.Value.Problem is not null)} problem(s)");
    }

    private static void Log(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm}  {line}{Environment.NewLine}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[integrity] could not write {path}: {e.Message}");
        }
    }
}
