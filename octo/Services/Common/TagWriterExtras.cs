namespace Octo.Services.Common;

/// <summary>
/// The tag frames TagLib's generic Tag does not expose, written in the frame each container's
/// readers look for. Navidrome's mappings.yaml maps TXXX:ARTISTS (ID3v2), ARTISTS (Vorbis) and
/// ----:com.apple.iTunes:ARTISTS (MP4) to its artists tag, and reads the recording id from
/// UFID:http://musicbrainz.org, MUSICBRAINZ_TRACKID and "MusicBrainz Track Id".
/// </summary>
internal static class TagWriterExtras
{
    /// <summary>Picard's and Navidrome's owner string for the recording id on ID3.</summary>
    internal const string MusicBrainzUfidOwner = "http://musicbrainz.org";

    /// <summary>
    /// The container's own tag, created when the format has an obvious one. For anything else
    /// only a tag that already exists is used, so a WAV never grows a Vorbis comment it cannot hold.
    /// </summary>
    private static (TagLib.Id3v2.Tag? Id3, TagLib.Ogg.XiphComment? Xiph, TagLib.Mpeg4.AppleTag? Apple) NativeTags(
        TagLib.File file) => file switch
    {
        TagLib.Mpeg.AudioFile => (file.GetTag(TagLib.TagTypes.Id3v2, true) as TagLib.Id3v2.Tag, null, null),
        TagLib.Mpeg4.File => (null, null, file.GetTag(TagLib.TagTypes.Apple, true) as TagLib.Mpeg4.AppleTag),
        TagLib.Flac.File or TagLib.Ogg.File =>
            (null, file.GetTag(TagLib.TagTypes.Xiph, true) as TagLib.Ogg.XiphComment, null),
        _ => (file.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag,
            file.GetTag(TagLib.TagTypes.Xiph, false) as TagLib.Ogg.XiphComment,
            file.GetTag(TagLib.TagTypes.Apple, false) as TagLib.Mpeg4.AppleTag),
    };

    /// <summary>
    /// One field holding several values, each its own value rather than one joined string.
    /// TagLib writes a multi-value TXXX null-separated even in ID3v2.3, which is what keeps a
    /// credit of two artists from being read back as one artist named after both.
    /// </summary>
    public static void SetMultiValue(TagLib.File file, string field, IReadOnlyList<string> values)
    {
        var array = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray();
        if (array.Length == 0) return;

        var (id3, xiph, apple) = NativeTags(file);
        if (id3 is not null) TagLib.Id3v2.UserTextInformationFrame.Get(id3, field, true).Text = array;
        xiph?.SetField(field, array);
        apple?.SetDashBoxes("com.apple.iTunes", field, array);
    }

    /// <summary>
    /// The RECORDING id, in the frame Picard and Navidrome both read it from. Written frame by
    /// frame rather than through Tag.MusicBrainzTrackId: TagLib# after 2.3.0 repurposes that
    /// property for the release TRACK id on ID3 (UFID owner "MusicBrainz Release Track Id"),
    /// which Navidrome would not read as a recording, and a package bump must not change what
    /// lands on disk.
    /// </summary>
    public static void SetRecordingId(TagLib.File file, string recordingId)
    {
        if (string.IsNullOrWhiteSpace(recordingId)) return;

        var (id3, xiph, apple) = NativeTags(file);
        if (id3 is not null)
            TagLib.Id3v2.UniqueFileIdentifierFrame.Get(id3, MusicBrainzUfidOwner, true).Identifier =
                TagLib.ByteVector.FromString(recordingId, TagLib.StringType.UTF8);
        xiph?.SetField("MUSICBRAINZ_TRACKID", recordingId);
        apple?.SetDashBox("com.apple.iTunes", "MusicBrainz Track Id", recordingId);
    }

    /// <summary>The recording id a file already carries, from whichever frame holds it.</summary>
    public static string? ReadRecordingId(TagLib.File file)
    {
        var (id3, xiph, apple) = NativeTags(file);
        var fromId3 = id3 is null ? null
            : TagLib.Id3v2.UniqueFileIdentifierFrame.Get(id3, MusicBrainzUfidOwner, false)?.Identifier?.ToString();
        var value = fromId3
            ?? xiph?.GetFirstField("MUSICBRAINZ_TRACKID")
            ?? apple?.GetDashBox("com.apple.iTunes", "MusicBrainz Track Id");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static void SetCompilation(TagLib.File file, bool value)
    {
        var (id3, xiph, apple) = NativeTags(file);
        if (id3 is not null) id3.IsCompilation = value;
        if (xiph is not null) xiph.IsCompilation = value;
        if (apple is not null) apple.IsCompilation = value;
    }

    public static bool IsCompilation(TagLib.File file) =>
        (file.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag)?.IsCompilation == true
        || (file.GetTag(TagLib.TagTypes.Xiph, false) as TagLib.Ogg.XiphComment)?.IsCompilation == true
        || (file.GetTag(TagLib.TagTypes.Apple, false) as TagLib.Mpeg4.AppleTag)?.IsCompilation == true;

    /// <summary>The album a file already names, for a download whose source named none.</summary>
    public static (string? Album, string? AlbumArtist, bool IsCompilation) ReadAlbum(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return (file.Tag.Album, file.Tag.FirstAlbumArtist, IsCompilation(file));
        }
        catch
        {
            return (null, null, false);
        }
    }

    /// <summary>The recording id and length of a file on disk, for deciding whether two files
    /// are the same recording. Nulls when the file cannot be read.</summary>
    public static (string? RecordingId, int Seconds) ReadIdentity(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return (ReadRecordingId(file), (int)Math.Round(file.Properties.Duration.TotalSeconds));
        }
        catch
        {
            return (null, 0);
        }
    }
}
