// AlbumWall — the one place that WRITES to a music file.
//
// Everything else in this program reads. This changes a file he owns, so it
// is done the slow, careful way, and only where he has said a library may be
// edited (Library.Editable):
//
//   * The old value is written to a backup file first. A lyric typed over by
//     mistake is a lyric that can be put back.
//   * The edit is made to a COPY beside the file. The original is not opened
//     for writing at any point.
//   * The copy is read back and must say what was asked, with the same audio
//     and every other tag as before. For a FLAC that includes the checksum of
//     the audio the file carries in its own header.
//   * Only then does the copy take the original's place, in one rename, so
//     there is no moment at which the file is half written. A power cut leaves
//     either the old file or the new one, and at worst a stray copy.
//
// THE MODIFIED TIME MOVES, on purpose. It is how the nightly copy to the NAS
// and the library index both learn the file changed; an edit that kept the
// old time would reach neither (see LibraryIndex.cs, and the day that cost).
//
// Lyrics only, so far: the first thing asked for, and one field is the right
// size for the first write.

namespace AlbumWall.Domain;

public static class TagWriter
{
    /// How it went: `Problem` is null when the file now says what was asked.
    /// `Backup` is where the old lyrics were kept, when there was a write.
    public sealed record Result(string? Problem, string? Backup)
    {
        public bool Ok => Problem is null;
    }

    private static readonly string[] LyricFields = ["LYRICS", "UNSYNCEDLYRICS"];

    /// As lyrics are kept: one kind of line ending, nothing trailing.
    public static string Tidy(string? lyrics) =>
        (lyrics ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// The lyrics in a file that is already open, or "".
    public static string ReadLyrics(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            return Tidy(LyricFields.Select(n => xiph.GetFirstField(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)));
        return Tidy(file.Tag.Lyrics);
    }

    /// Sets the file's embedded lyrics, or removes them if `lyrics` is empty.
    /// Never throws.
    public static Result SetLyrics(string path, string lyrics, string backupDir)
    {
        var wanted = Tidy(lyrics);
        var temp = path + ".albumwall-tmp";
        // The copy does not end in the file's own extension, so nothing that
        // scans the folder mistakes it for a song; TagLib is told what it is.
        var kind = "taglib/" + Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        try
        {
            string old, before;
            using (var file = TagLib.File.Create(path, kind, TagLib.ReadStyle.Average))
            {
                old = ReadLyrics(file);
                before = Everything(file, path);
            }
            if (old == wanted) return new Result(null, null);

            Directory.CreateDirectory(backupDir);
            // Never over an earlier backup: two edits in one second are two files.
            var stem = Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(path)}");
            var backup = stem + ".lyrics.txt";
            for (var n = 2; File.Exists(backup); n++) backup = $"{stem}-{n}.lyrics.txt";
            File.WriteAllText(backup, $"{path}\nThe lyrics as they were before the edit{(old.Length == 0 ? ": none." : ":")}\n\n{old}\n");

            File.Copy(path, temp, overwrite: true);
            using (var file = TagLib.File.Create(temp, kind, TagLib.ReadStyle.Average))
            {
                if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                {
                    foreach (var name in LyricFields) xiph.RemoveField(name);
                    if (wanted.Length > 0) xiph.SetField("LYRICS", wanted);
                }
                else
                    file.Tag.Lyrics = wanted.Length > 0 ? wanted : null;
                file.Save();
            }

            using (var file = TagLib.File.Create(temp, kind, TagLib.ReadStyle.Average))
            {
                if (ReadLyrics(file) != wanted)
                    return Fail(temp, "the lyrics did not read back as written", backup);
                if (Everything(file, temp) != before)
                    return Fail(temp, "something other than the lyrics changed in the copy", backup);
            }

            File.Move(temp, path, overwrite: true);
            return new Result(null, backup);
        }
        catch (Exception ex)
        {
            return Fail(temp, ex.Message, null);
        }
    }

    private static Result Fail(string temp, string problem, string? backup)
    {
        try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
        return new Result($"{problem.TrimEnd('.')}. The file was not changed.", backup);
    }

    /// Everything about a file that an edit to its lyrics must leave alone, as
    /// one string to compare: the audio and every tag but the lyrics.
    private static string Everything(TagLib.File file, string path)
    {
        var parts = new List<string>
        {
            file.Properties?.Duration.Ticks.ToString() ?? "",
            file.Properties?.Description ?? "",
            file.Properties?.AudioSampleRate.ToString() ?? "",
            FlacAudioMd5(path),
            (file.Tag.Pictures?.Length ?? 0).ToString(),
        };
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            foreach (var name in xiph.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (LyricFields.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                parts.Add($"{name}={string.Join("\u001f", xiph.GetField(name))}");
            }
        else
        {
            var t = file.Tag;
            parts.AddRange(
            [
                t.Title ?? "", string.Join("\u001f", t.Performers ?? []), string.Join("\u001f", t.AlbumArtists ?? []),
                t.Album ?? "", t.Year.ToString(), $"{t.Track}/{t.TrackCount}", $"{t.Disc}/{t.DiscCount}",
                string.Join("\u001f", t.Genres ?? []), string.Join("\u001f", t.Composers ?? []), t.Comment ?? "",
                t.Copyright ?? "", t.MusicBrainzReleaseId ?? "", t.MusicBrainzTrackId ?? "",
            ]);
        }
        return string.Join("\u001e", parts);
    }

    /// The checksum of the decoded audio that a FLAC keeps in its first block,
    /// or "" for anything else. It is the file's own word on what it holds.
    private static string FlacAudioMd5(string path)
    {
        try
        {
            using var from = File.OpenRead(path);
            var head = new byte[42];
            if (from.Read(head, 0, head.Length) != head.Length) return "";
            if (head[0] != 'f' || head[1] != 'L' || head[2] != 'a' || head[3] != 'C' || (head[4] & 0x7f) != 0) return "";
            return Convert.ToHexString(head, 26, 16);
        }
        catch (Exception)
        {
            return "";
        }
    }
}
