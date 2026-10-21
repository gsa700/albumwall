// AlbumWall - what a second launch asks of this window (SingleInstance, CommandLine).
//
// Called on a worker thread with the second launch's arguments; does the thing on
// the UI thread and answers with one line, which the second launch prints. An
// answer starting "! " says it could not be done (exit code 1). Success is quiet,
// as command-line tools are, except --play "words", which says what it found.

using Avalonia.Controls;
using Avalonia.Threading;

namespace AlbumWall.App;

public partial class MainWindow
{
    private void ListenForSecondLaunches() =>
        SingleInstance.Listen(args => Dispatcher.UIThread.InvokeAsync(() => RunCommand(args)).GetTask());

    private string RunCommand(string[] args)
    {
        var command = CommandLine.Parse(args);
        switch (command?.Name)
        {
            case null:
            case "show":
                // A plain second launch: the window it wanted is this one.
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Show();
                Activate();
                return "";

            case "quit":
                // After the answer has gone, or the second launch hears nothing and
                // says the app is not running.
                DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(300));
                return "";

            case "play" when command.Text is { } words:
                return PlayFirstMatch(words);
        }

        if (_player is null)
            return command.Name is "play" or "play-pause"
                ? "! Nothing to play yet: pick an album, or --play \"words\"."
                : "! Nothing is playing.";

        switch (command.Name)
        {
            case "play-pause": _player.TogglePause(); break;
            case "play": _player.SetPaused(false); break;
            case "pause": _player.SetPaused(true); break;
            case "next": _player.Next(); break;
            case "previous": _player.Previous(); break;
            case "stop": _player.Stop(); break;
        }
        return "";
    }

    /// The album the words name: a title that IS them, then a title that
    /// contains them, then an artist that does. In wall order, so the first is
    /// the one that sits first on the wall. The whole library, not what a
    /// search has narrowed the wall to.
    private string PlayFirstMatch(string words)
    {
        if (!Playback.Player.IsAvailable || _playerFailed)
            return "! The player is not available on this computer.";
        if (_all.Count == 0)
            return _scanning ? "! The library is still being read; try again in a moment." : "! The library is empty.";

        bool Has(string s) => s.Contains(words, StringComparison.CurrentCultureIgnoreCase);
        var hit = _all.FirstOrDefault(a => string.Equals(a.Title, words, StringComparison.CurrentCultureIgnoreCase))
               ?? _all.FirstOrDefault(a => Has(a.Title))
               ?? _all.FirstOrDefault(a => Has(a.Artist));
        if (hit is null) return $"! No album's title or artist contains \"{words}\".";

        StartPlayback(hit, 0, shuffle: false);
        return _playerFailed ? "! It could not be played." : $"Playing {hit.Artist} - {hit.Title}";
    }
}
