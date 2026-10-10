// The same two channels as Deadwax (Deadwax.Core/ReleaseFeed.cs), so the family's apps release
// alike. Checked by tools/update-check.cs.
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlbumWall.Domain;

/// <summary>
/// The two release channels (2026-10-10). Every build is published as a GitHub pre-release: that is
/// Edge. A build that has run on Edge without trouble is promoted, the same binary with its
/// pre-release flag cleared: that is Stable. GitHub's /releases/latest never returns a pre-release,
/// so Stable reads that; Edge reads the release list and takes the newest APP release in it.
///
/// THIS REPOSITORY ALSO HOLDS THE AUDIO ENGINE: libmpv-X.Y.Z-N releases, published as pre-releases.
/// They carry no app and must never be offered, so Edge takes only tags shaped exactly vX.Y.Z (the
/// shape scripts/release.sh insists on).
/// </summary>
public static partial class ReleaseFeed
{
    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")]
    private static partial Regex AppTag();

    public static bool IsAppTag(string? tag) => tag is not null && AppTag().IsMatch(tag);

    /// <summary>The newest app release in a /releases list, never a draft; null when it holds none.</summary>
    public static JsonElement? Newest(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array) return null;
        JsonElement? best = null;
        string? bestTag = null;
        foreach (var r in releases.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("tag_name", out var t) || t.GetString() is not { } tag || !IsAppTag(tag)) continue;
            if (bestTag is null || VersionOrder.IsNewer(tag, bestTag)) { best = r; bestTag = tag; }
        }
        return best;
    }
}
