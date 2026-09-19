// AlbumWall — image dimensions from the header, without decoding.
//
// The wall needs to know how big a cover REALLY is, for two reasons that both
// came out of the MP3/M4A library on Hambench, where 88 of 1,034 covers are
// under 300 px and the smallest is 70x70:
//
//   * the art cache decodes to a fixed width, which silently enlarges a small
//     cover before the view ever sees it — so by then nothing can tell a
//     thumbnail from a proper scan;
//   * finding the albums whose art wants replacing should not mean decoding a
//     thousand images. The header is a few dozen bytes the scanner has in hand
//     anyway.
//
// Returns null for anything it does not recognise, and callers treat null as
// "unknown, behave as before" — never as "small".

namespace AlbumWall.Domain;

public static class ImageSize
{
    public static (int Width, int Height)? Read(ReadOnlySpan<byte> d)
    {
        // PNG: signature, then IHDR with big-endian width and height.
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
            return Sane(Be32(d, 16), Be32(d, 20));

        // GIF: "GIF8", little-endian logical screen size.
        if (d.Length >= 10 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46)
            return Sane(d[6] | (d[7] << 8), d[8] | (d[9] << 8));

        // BMP: "BM", little-endian width and (possibly negative) height.
        if (d.Length >= 26 && d[0] == 0x42 && d[1] == 0x4D)
            return Sane(Le32(d, 18), Math.Abs(Le32(d, 22)));

        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
            return Jpeg(d);

        return null;
    }

    /// Walks the segments to the first start-of-frame. It can be a long way in:
    /// a cover exported from a photo tool carries an EXIF block with its own
    /// embedded thumbnail ahead of the frame header, so the first few hundred
    /// bytes are not enough and callers should hand over what they have.
    private static (int, int)? Jpeg(ReadOnlySpan<byte> d)
    {
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }

            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }                        // padding

            // SOF0..SOF15, less the three in that range that are not frames.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                return Sane((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);

            // Markers with no length: restarts, SOI, TEM.
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }

            i += 2 + ((d[i + 2] << 8) | d[i + 3]);
        }
        return null;
    }

    private static (int, int)? Sane(int w, int h) =>
        w is > 0 and < 65536 && h is > 0 and < 65536 ? (w, h) : null;

    private static int Be32(ReadOnlySpan<byte> d, int i) =>
        (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];

    private static int Le32(ReadOnlySpan<byte> d, int i) =>
        d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24);
}
