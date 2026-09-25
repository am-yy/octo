namespace Octo.Models.Settings;

public class MetadataSettings
{
    /// <summary>
    /// Language code sent as Accept-Language to the external metadata APIs
    /// (Deezer, Last.fm). Deezer localizes album genre names by caller IP
    /// unless told otherwise, so a server hosted in a non-English country
    /// writes localized genre tags into downloaded files. Empty lets the
    /// provider decide from the server's IP.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// A download that still has no album after Deezer and its own tags is filed as a single
    /// under its title, instead of joining every other album-less track in Navidrome's one
    /// "[Unknown Album]" (#50). A single filed under its title is a real release, and it gives
    /// Navidrome something to group. Never applied to a compilation, where a hundred one-track
    /// albums would be worse than the bucket.
    /// Environment variable: ALBUM_FROM_TITLE
    /// </summary>
    public bool AlbumFromTitle { get; set; } = true;

    /// <summary>
    /// Ask the Cover Art Archive first when a fingerprint named the MusicBrainz release the album
    /// tag describes: the right pressing, no guessing by name (#51).
    /// Environment variable: COVER_ART_ARCHIVE
    /// </summary>
    public bool UseCoverArtArchive { get; set; } = true;

    /// <summary>
    /// Treat a cover that is not square as missing. A 16:9 cover is a video thumbnail; when
    /// nothing better turns up its centre square is used, which for a YouTube "Topic" upload is
    /// the real cover inside the letterbox. Off keeps whatever the source embedded.
    /// Environment variable: REPLACE_VIDEO_COVERS
    /// </summary>
    public bool ReplaceVideoCovers { get; set; } = true;

    /// <summary>
    /// Also write cover.jpg beside a download, which Navidrome reads, which survives a retag, and
    /// which covers a file the embed did not stick to. Only in the Organized layout and only in a
    /// folder the download created: Navidrome ranks cover.* above embedded art, so in a shared
    /// folder, or an album folder that was already there, one file would change every album's
    /// cover. Never replaces an existing cover.* or folder.*.
    /// Environment variable: COVER_FILE
    /// </summary>
    public bool WriteCoverFile { get; set; } = true;

    /// <summary>
    /// Fetch lyrics: a sidecar beside each download, and live for any song as it plays when the
    /// library has none (#52). Off by default like the rest; there is no destructive path, since
    /// an unmatched track simply has no lyrics file and a wrong one is a text file to delete.
    /// Environment variable: LYRICS_FETCH
    /// </summary>
    public bool FetchLyrics { get; set; } = false;

    /// <summary>
    /// Lyrics sources, in order: lrclib (open, synced), netease (synced and deep on non-Western
    /// and older music, but an unofficial API, so it only runs when listed), lyricsovh (plain
    /// text). Synced beats plain, so a later source is only asked while nothing earlier had timing.
    /// Environment variable: LYRICS_SOURCES
    /// </summary>
    public string LyricsSources { get; set; } = "lrclib,lyricsovh";

    public static readonly string[] KnownLyricsSources = ["lrclib", "netease", "lyricsovh"];

    public IReadOnlyList<string> EffectiveLyricsSources =>
        (LyricsSources ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(source => source.ToLowerInvariant())
            .Where(source => KnownLyricsSources.Contains(source))
            .Distinct()
            .ToList();
}
