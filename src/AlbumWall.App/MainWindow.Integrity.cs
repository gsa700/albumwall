// AlbumWall — running the integrity check (Domain/FlacIntegrity.cs) for the
// libraries it is turned on in, and saying what it found.
//
// Off everywhere until he ticks it, per library, in the library's settings
// (Preferences > Library > Settings), where he also picks how often every file
// is read again (Library.IntegrityDays; a month unless he says). It is only
// offered for a library whose last scan found FLAC files. When on, it starts a
// few minutes after launch, so it never competes with the wall coming up, and
// looks again every six hours while the app is open, for what is due. It
// does not run with the app closed: no service, no timer, nothing he did not
// start. One library at a time, one file at a time (IntegrityChecker).
//
// What it found is in Preferences under the library, every damaged file is
// written to integrity.log beside the settings, and while any library has a
// damaged file the dot on the gear says so.

using Avalonia.Controls;
using Avalonia.Threading;

namespace AlbumWall.App;

public partial class MainWindow
{
    private static readonly TimeSpan IntegrityFirstDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan IntegrityEvery = TimeSpan.FromHours(6);

    private CancellationTokenSource? _integrityStop;
    private string? _integrityLibrary;        // the one being checked now
    private DispatcherTimer? _integrityTimer;
    private readonly Dictionary<string, Domain.IntegrityChecker.Status> _integrity = [];
    private bool _updateDotWanted;

    internal static string IntegrityLogPath =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Settings.Path)!, "integrity.log");

    private static string IntegrityRecordPath(Library library) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Settings.Path)!, "integrity", library.Id + ".json");

    private void StartIntegrityChecks()
    {
        foreach (var library in Libraries.Where(l => l.IsFolder && l.CheckIntegrity))
            _integrity[library.Id] = Domain.IntegrityChecker.Summary(IntegrityRecordPath(library));
        ShowIntegrityAttention();

        _integrityTimer = new DispatcherTimer { Interval = IntegrityFirstDelay };
        _integrityTimer.Tick += (_, _) =>
        {
            _integrityTimer.Interval = IntegrityEvery;
            _ = RunIntegrityAsync();
        };
        _integrityTimer.Start();
        Closing += (_, _) => _integrityStop?.Cancel();
    }

    private async Task RunIntegrityAsync()
    {
        if (_integrityStop is not null || !Domain.FlacIntegrity.Available) return;
        _integrityStop = new CancellationTokenSource();
        var stop = _integrityStop.Token;
        try
        {
            foreach (var library in Libraries.Where(l => l.IsFolder && l.CheckIntegrity).ToList())
            {
                if (stop.IsCancellationRequested) break;
                // An unplugged drive or an unmounted share is not a library
                // whose files have all vanished.
                if (!Directory.Exists(library.Root)) continue;
                _integrityLibrary = library.Id;
                var id = library.Id;
                try
                {
                    await Domain.IntegrityChecker.RunAsync(library.Root, IntegrityRecordPath(library), IntegrityLogPath,
                        library.IntegrityEvery,
                        status => Dispatcher.UIThread.Post(() =>
                        {
                            _integrity[id] = status;
                            ShowIntegrityAttention();
                            _prefs?.Fill();
                        }), stop);
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { Console.WriteLine($"[integrity] {library.Name}: {e.Message}"); }
            }
        }
        finally
        {
            _integrityLibrary = null;
            _integrityStop = null;
            foreach (var library in Libraries.Where(l => l.IsFolder && l.CheckIntegrity))
                _integrity[library.Id] = Domain.IntegrityChecker.Summary(IntegrityRecordPath(library));
            ShowIntegrityAttention();
            _prefs?.Fill();
        }
    }

    internal void SetLibraryIntegrity(string id, bool on)
    {
        if (Libraries.FirstOrDefault(l => l.Id == id) is not { IsFolder: true } library || library.CheckIntegrity == on) return;
        library.CheckIntegrity = on;
        _settings.Save();
        Console.WriteLine($"[library] {library.Name}: integrity check {(on ? "on" : "off")}");
        if (on)
        {
            _integrity[id] = Domain.IntegrityChecker.Summary(IntegrityRecordPath(library));
            // Turned on by hand: start now rather than at the next tick.
            if (_integrityStop is null) _ = RunIntegrityAsync();
        }
        else
        {
            _integrity.Remove(id);
            if (_integrityLibrary == id) _integrityStop?.Cancel();
        }
        ShowIntegrityAttention();
    }

    /// How often every file is read again. Takes effect at the next look; a
    /// library already being checked finishes what it found due.
    internal void SetLibraryIntegrityDays(string id, int? days)
    {
        if (Libraries.FirstOrDefault(l => l.Id == id) is not { IsFolder: true } library || library.IntegrityDays == days) return;
        library.IntegrityDays = days;
        _settings.Save();
        Console.WriteLine($"[library] {library.Name}: every file checked again "
                        + (days is null ? "monthly" : days == 0 ? "never (new and changed files only)" : $"every {days} days"));
    }

    /// One line for the library's settings, under its tick box.
    internal string IntegrityLine(Library library)
    {
        if (!_integrity.TryGetValue(library.Id, out var s))
            return "Starts within a few minutes.";
        if (s.Running)
            return $"Checking: {s.Done:N0} of {s.Due:N0} files due"
                 + (s.Problems > 0 ? $"  ·  {s.Problems} damaged" : "");
        if (s.LastPass is null)
            return _integrityStop is null ? "Waiting to start." : "Waiting for another library to finish.";

        var parts = new List<string>
        {
            $"{s.Files:N0} FLAC files, last checked {Ago(s.LastPass.Value)}",
            s.Problems == 0 ? "no damage found" : $"{s.Problems} damaged",
        };
        if (s.NoChecksum > 0) parts.Add($"{s.NoChecksum} without a checksum (decoded cleanly)");
        return string.Join("  ·  ", parts);
    }

    internal IReadOnlyList<(string Path, string Problem)> IntegrityProblems(Library library) =>
        _integrity.TryGetValue(library.Id, out var s) && s.Problems > 0
            ? Domain.IntegrityRecord.Load(IntegrityRecordPath(library)).Problems.ToList()
            : [];

    private static string Ago(DateTime utc)
    {
        var t = DateTime.UtcNow - utc;
        return t.TotalMinutes < 2 ? "just now"
             : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} minutes ago"
             : t.TotalDays < 1 ? $"{(int)t.TotalHours} h ago"
             : t.TotalDays < 2 ? "yesterday"
             : $"{(int)t.TotalDays} days ago";
    }

    /// The gear's dot: an update, or damage found in any library.
    private void ShowIntegrityAttention()
    {
        var damaged = _integrity.Values.Sum(s => s.Problems);
        UpdateDot.IsVisible = _updateDotWanted || damaged > 0;
        ToolTip.SetTip(SettingsButton,
            damaged > 0 ? $"Preferences — {damaged} damaged file{(damaged == 1 ? "" : "s")} found (Library tab)"
            : _updateDotWanted ? "Preferences — a newer version is available"
            : "Preferences");
    }
}
