// AlbumWall — cover art loading.
//
// Art is decoded ONCE per size bucket and cached. This matters more than it
// looks: 194 covers at 1000x1000 RGBA would be ~780 MB resident if decoded at
// full size, and the library is heading for 1100+ albums. DecodeToWidth does
// the downscale during decode, so the full-size bitmap never exists in memory.
//
// Two source paths, because real libraries use both: a sidecar file beside the
// tracks (this FLAC library, cover.jpg only) or a picture block embedded in the
// file itself (the mp3/aac collection).

using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace AlbumWall.App;

public static class ArtCache
{
    private static readonly ConcurrentDictionary<(string Key, int Bucket), Task<Bitmap?>> Cache = new();

    /// How many covers have actually been decoded. Surfaced in the status bar
    /// because it is the only honest way to see whether the wall is really
    /// virtualising: on a 194-album library this should read well under 100
    /// after a cold open, not 194.
    public static int Decoded => Cache.Count;

    /// Decoding at exactly the display size would re-decode every album on each
    /// slider nudge. Two buckets cover 48-320 px with at most one re-decode,
    /// and the GPU scales the rest.
    public static int BucketFor(int displayPx) => displayPx <= 256 ? 256 : 512;

    public static Task<Bitmap?> GetAsync(Domain.Album album, int displayPx)
    {
        var key = album.ArtPath ?? album.ArtEmbeddedIn;
        if (key is null) return Task.FromResult<Bitmap?>(null);

        var bucket = BucketFor(displayPx);
        return Cache.GetOrAdd((key, bucket), static k => Task.Run(() => Decode(k.Key, k.Bucket)));
    }

    private static Bitmap? Decode(string key, int bucket)
    {
        try
        {
            // A sidecar is a plain image file; anything else is a track we must
            // pull the embedded picture out of.
            if (IsImageFile(key))
            {
                using var fs = File.OpenRead(key);
                return Bitmap.DecodeToWidth(fs, bucket);
            }

            using var tf = TagLib.File.Create(key);
            var pics = tf.Tag.Pictures;
            if (pics is not { Length: > 0 }) return null;
            var front = pics.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pics[0];
            if (front.Data.Count == 0) return null;

            using var ms = new MemoryStream(front.Data.Data);
            return Bitmap.DecodeToWidth(ms, bucket);
        }
        catch
        {
            // A bad cover must never take the wall down — the tile just shows
            // its placeholder.
            return null;
        }
    }

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif";
}
