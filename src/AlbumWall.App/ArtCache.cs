// AlbumWall — cover art loading.
//
// Art is decoded ONCE per size bucket and cached, downscaled during decode so the
// full-size bitmap never exists in memory. DecodeToWidth does that work.
//
// Two source paths, because real libraries use both: a sidecar file beside the
// tracks (this FLAC library, cover.jpg only) or a picture block embedded in the
// file itself (the mp3/aac collection).
//
// THE CACHE IS BOUNDED. It was not, originally, and that was fine for as long as
// the numbers stayed small: 194 albums, one bucket each. It stopped being fine the
// moment the expansion panel arrived and started asking for a bigger bucket than
// the tiles, because every opened album then held TWO bitmaps and the count went
// past the album count — 206 entries for 194 albums — with resident memory
// climbing as fast as albums were opened. At the ~1,100 albums this library is
// heading for, unbounded meant over a gigabyte of retained bitmaps.
//
// Eviction drops the cache's reference and does NOT dispose. A bitmap may still be
// on screen, and disposing one out from under a live Image is a crash; letting the
// GC collect it once nothing refers to it is both safe and sufficient.

using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace AlbumWall.App;

public static class ArtCache
{
    /// Roughly how much decoded art to keep. Chosen to hold a comfortable working
    /// set — a few screens of tiles plus the open album — without tracking the
    /// library's size, which is the property that made the old cache unbounded.
    private const long Budget = 160L * 1024 * 1024;

    private sealed class Entry
    {
        public required Task<Bitmap?> Task { get; init; }
        public long Bytes;
    }

    /// What a decoded bitmap is filed under.
    ///
    /// Source and Bucket alone are NOT enough. A cover corrected in place — the
    /// ordinary way a wrong sleeve gets fixed — keeps its path, and a 500 px
    /// replacement for a 500 px original keeps its dimensions too, so nothing in
    /// the old key moved and the app went on showing the sleeve it had already
    /// decoded. Not until a restart: this cache is static and a rescan does not
    /// touch it. Size and Modified come from the scanner (Album.ArtSize /
    /// ArtModified), so a swapped file simply lands under a different key and the
    /// stale entry ages out of the LRU on its own.
    private readonly record struct Key(string Source, long Size, long Modified, int Bucket);

    private static readonly object Gate = new();
    private static readonly Dictionary<Key, Entry> Cache = [];
    private static readonly LinkedList<Key> Lru = [];
    private static readonly Dictionary<Key, LinkedListNode<Key>> Nodes = [];
    private static long _bytes;

    /// How many covers are currently decoded and held. Surfaced in the status bar:
    /// it is the honest way to see both that the wall is virtualizing and that the
    /// cache is staying inside its budget.
    public static int Decoded { get { lock (Gate) return Cache.Count; } }

    public static long HeldBytes { get { lock (Gate) return _bytes; } }

    /// Decoding at exactly the display size would re-decode on every slider nudge.
    /// Two buckets cover the whole range with at most one re-decode, and the GPU
    /// scales the rest.
    public static int BucketFor(int displayPx) => displayPx <= 256 ? 256 : 512;

    public static Task<Bitmap?> GetAsync(Domain.Album album, int displayPx)
    {
        var source = album.ArtPath ?? album.ArtEmbeddedIn;
        if (source is null) return Task.FromResult<Bitmap?>(null);

        var id = new Key(source, album.ArtSize, album.ArtModified, BucketFor(displayPx));

        lock (Gate)
        {
            if (Cache.TryGetValue(id, out var hit))
            {
                Touch(id);
                return hit.Task;
            }

            var entry = new Entry { Task = Task.Run(() => Decode(id.Source, id.Bucket)) };
            Cache[id] = entry;
            Nodes[id] = Lru.AddLast(id);

            // Charge the cache once the decode lands, then evict down to budget.
            _ = entry.Task.ContinueWith(t =>
            {
                lock (Gate)
                {
                    if (t.Result is { } bmp)
                    {
                        entry.Bytes = (long)bmp.PixelSize.Width * bmp.PixelSize.Height * 4;
                        _bytes += entry.Bytes;
                    }
                    Trim();
                }
            }, TaskScheduler.Default);

            return entry.Task;
        }
    }

    /// Marks an entry as most recently used. Caller holds the lock.
    private static void Touch(Key id)
    {
        if (!Nodes.TryGetValue(id, out var node)) return;
        Lru.Remove(node);
        Nodes[id] = Lru.AddLast(id);
    }

    /// Drops least-recently-used entries until the budget is met. Caller holds the
    /// lock. Never disposes: see the note at the top of this file.
    private static void Trim()
    {
        while (_bytes > Budget && Lru.First is { } oldest)
        {
            var id = oldest.Value;
            if (Cache.Remove(id, out var gone)) _bytes -= gone.Bytes;
            Nodes.Remove(id);
            Lru.RemoveFirst();
        }
    }

    private static Bitmap? Decode(string key, int bucket)
    {
        try
        {
            // A sidecar is a plain image file; anything else is a track we must
            // pull the embedded picture out of.
            if (IsImageFile(key))
                return DecodeNoLarger(File.ReadAllBytes(key), bucket);

            using var tf = TagLib.File.Create(key);
            return Domain.ImageSize.Cover(tf.Tag.Pictures) is { } cover
                ? DecodeNoLarger(cover, bucket)
                : null;
        }
        catch
        {
            // A bad cover must never take the wall down — the tile just shows
            // its placeholder.
            return null;
        }
    }

    /// Decodes DOWN to the bucket and never up.
    ///
    /// DecodeToWidth does exactly what it says in both directions: handed a
    /// 150 px thumbnail and asked for 256 it returns 256 px of interpolation,
    /// and from then on nothing downstream can tell that cover from a real one.
    /// It looked soft on the wall and there was no way to do anything about it,
    /// because the evidence had been destroyed at the door. A cover smaller
    /// than the bucket is now kept at its own size — which is also less memory —
    /// and the view decides how far it is prepared to stretch it.
    ///
    /// An unrecognized header falls through to the old behavior.
    private static Bitmap DecodeNoLarger(byte[] data, int bucket)
    {
        using var ms = new MemoryStream(data);
        return Domain.ImageSize.Read(data) is var (w, _) && w <= bucket
            ? new Bitmap(ms)
            : Bitmap.DecodeToWidth(ms, bucket);
    }

    /// A one-off tiny decode that deliberately BYPASSES the cache: it is used to
    /// derive the ground color at startup, and a 16 px thumbnail has no business
    /// occupying a cache slot that a display-size cover will want.
    public static Bitmap? DecodeTiny(string source, int px) => Decode(source, px);

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif";
}
