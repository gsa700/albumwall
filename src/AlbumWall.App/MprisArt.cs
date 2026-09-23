// AlbumWall — a file for the desktop's media controls to show, when the cover has none.
//
// MPRIS carries a cover as a URL (mpris:artUrl), and GNOME's flyout and Plasma's
// media widget both open that file themselves. A cover.jpg beside the tracks is
// such a file already. A picture embedded in the tracks is not, and the iTunes-era
// mp3/m4a collection has almost nothing else: played on KDE on 2026-09-22, every
// album from the NAS showed a blank square in the media widget while the wall
// showed its cover.
//
// So the embedded picture is written out once, to the cache, and that file's URL
// is what goes out. Only the playing album's picture is kept: the widget needs
// one file, and a cache that grows with every album played is a cache nobody
// cleans. Windows does not come here; SMTC takes the bytes (see Smtc).

using System.Text;
using Avalonia.Threading;

namespace AlbumWall.App;

public static class MprisArt
{
    private static string? _extracting;

    private static string Dir
    {
        get
        {
            var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(cache))
                cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(cache, "albumwall", "now-playing");
        }
    }

    /// The file to name as the album's art, or null. Null for an embedded cover
    /// that has not been written out yet: that starts, and `ready` is called on
    /// the UI thread once there is a file, to ask again.
    public static string? For(Domain.Album album, Action ready)
    {
        if (album.ArtEmbeddedIn is not { } track)
            return album.ArtPath;
        if (!OperatingSystem.IsLinux())
            return null;

        // The track's size and time are in the name, so a re-tagged track with
        // a new picture is a new file rather than the old one served stale.
        var key = Key($"{track}\n{album.ArtSize}\n{album.ArtModified}");
        foreach (var ext in (string[])[".jpg", ".png"])
        {
            var existing = Path.Combine(Dir, key + ext);
            if (File.Exists(existing)) return existing;
        }

        if (_extracting == key) return null;
        _extracting = key;

        Task.Run(() => Extract(track, key)).ContinueWith(t =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_extracting == key) _extracting = null;
                if (t.Result) ready();
            }));
        return null;
    }

    private static bool Extract(string track, string key)
    {
        try
        {
            byte[]? cover;
            using (var tf = TagLib.File.Create(track))
                cover = Domain.ImageSize.Cover(tf.Tag.Pictures);
            if (cover is null) return false;

            // PNG says so in its first four bytes; everything else goes out as
            // .jpg, which is what embedded art all but always is.
            var png = cover.Length > 4 && cover[0] == 0x89 && cover[1] == 'P' && cover[2] == 'N' && cover[3] == 'G';
            Directory.CreateDirectory(Dir);
            foreach (var old in Directory.GetFiles(Dir))
                File.Delete(old);

            // Written whole, then renamed, so the shell never opens half a file.
            var path = Path.Combine(Dir, key + (png ? ".png" : ".jpg"));
            File.WriteAllBytes(path + ".part", cover);
            File.Move(path + ".part", path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mpris] cover not written: {ex.Message}");
            return false;
        }
    }

    /// FNV-1a, 64 bits. A file name, not a secret: nothing here needs the
    /// system crypto library, and on Fedora 45 that library has already once
    /// been the thing that broke (OpenSSL 4 against .NET).
    private static string Key(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h.ToString("x16");
    }
}
