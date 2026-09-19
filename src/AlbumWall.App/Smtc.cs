// AlbumWall — media keys on Windows.
//
// On Linux the desktop finds a player through MPRIS, which is D-Bus, which
// Windows does not have. The Windows equivalent is the System Media Transport
// Controls: register with them and the media keys, a headset's buttons and the
// card in the volume flyout all arrive here, and the flyout shows what is
// playing with its cover.
//
// The two are opposites in one respect. MPRIS is PULLED — the shell asks and
// the app answers — so Mpris takes a Func<State>. The transport controls are
// PUSHED: nothing is shown until the app says so and calls Update(). So this
// takes the same State record, handed over whenever it changes, and the same
// command strings come back out, which is why MainWindow needs no second
// vocabulary: a key on either platform ends up in MprisCommand.
//
// Compiled only for the Windows target (see the project file). The types below
// come from the Windows SDK projection and do not exist anywhere else.

#if WINDOWS
using Windows.Media;
using Windows.Storage.Streams;

namespace AlbumWall.App;

public sealed class Smtc
{
    private readonly SystemMediaTransportControls _controls;
    private readonly Action<string> _command;

    /// The cover last handed over, by album, so a track change within a record
    /// does not re-read and re-upload the same picture.
    private string? _artFor;

    private Smtc(SystemMediaTransportControls controls, Action<string> command)
    {
        _controls = controls;
        _command = command;

        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.IsStopEnabled = true;
        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;

        // Raised on a thread of the system's choosing. MprisCommand posts to
        // the UI thread itself, so nothing here needs to.
        _controls.ButtonPressed += (_, e) =>
        {
            var command = e.Button switch
            {
                SystemMediaTransportControlsButton.Play => "Play",
                SystemMediaTransportControlsButton.Pause => "Pause",
                SystemMediaTransportControlsButton.Next => "Next",
                SystemMediaTransportControlsButton.Previous => "Previous",
                SystemMediaTransportControlsButton.Stop => "Stop",
                _ => null
            };
            if (command is not null) _command(command);
        };
    }

    /// A desktop program is not handed its transport controls the way a store
    /// app is; it has to ask for the set belonging to one of its windows.
    /// Returns null rather than throwing: no media keys is an inconvenience,
    /// and must never be the reason the player does not start.
    public static Smtc? Start(IntPtr window, Action<string> command)
    {
        try
        {
            if (window == IntPtr.Zero) return null;
            var controls = SystemMediaTransportControlsInterop.GetForWindow(window);
            Console.WriteLine("[smtc] registered with the system media controls");
            return new Smtc(controls, command);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[smtc] unavailable: {ex.Message}");
            return null;
        }
    }

    /// Tells the system what is playing. `album` is where the cover comes from.
    public void Update(Mpris.State state, Domain.Album? album)
    {
        try
        {
            if (!state.HasTrack)
            {
                _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
                _controls.DisplayUpdater.ClearAll();
                _controls.DisplayUpdater.Update();
                _artFor = null;
                return;
            }

            _controls.PlaybackStatus = state.Playing
                ? MediaPlaybackStatus.Playing
                : MediaPlaybackStatus.Paused;

            var show = _controls.DisplayUpdater;
            show.Type = MediaPlaybackType.Music;
            show.MusicProperties.Title = state.Title;
            show.MusicProperties.Artist = state.Artist;
            show.MusicProperties.AlbumTitle = state.Album;
            show.Update();

            var key = album is null ? null : album.ArtPath ?? album.ArtEmbeddedIn;
            if (key == _artFor) return;
            _artFor = key;
            _ = SetCoverAsync(album, key);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[smtc] update failed: {ex.Message}");
        }
    }

    /// The cover goes across as bytes in memory, not as a file, because most of
    /// this library's art is embedded in the tracks and has no file to point at.
    /// It uses the same ImageSize.Cover the wall does, so a cover the wall can
    /// show — repaired header and all — is one the flyout can show.
    private async Task SetCoverAsync(Domain.Album? album, string? key)
    {
        try
        {
            var bytes = await Task.Run(() => ReadCover(album));
            if (key != _artFor) return;                 // the record changed while we read

            var show = _controls.DisplayUpdater;
            if (bytes is null)
            {
                show.Thumbnail = null;
            }
            else
            {
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                show.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            }
            show.Update();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[smtc] cover failed: {ex.Message}");
        }
    }

    private static byte[]? ReadCover(Domain.Album? album)
    {
        if (album is null) return null;
        if (album.ArtEmbeddedIn is { } track)
        {
            using var file = TagLib.File.Create(track);
            return Domain.ImageSize.Cover(file.Tag.Pictures);
        }
        return album.ArtPath is { } path && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
#endif
