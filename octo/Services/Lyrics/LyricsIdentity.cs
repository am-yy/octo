using Octo.Services.Common;

namespace Octo.Services.Lyrics;

/// <summary>
/// Whether a lyrics entry is the song asked for. Strict on purpose: a lyrics search is loose and
/// returns the artist's other songs, and a length alone once let one of those stand in
/// ("Ultimate $uicide" for "$UICIDE", both about 170 s). So, by <see cref="SongIdentity"/>:
///
/// 1. The same title, whole: no prefix or containment rule, which is how "Ultimate $uicide"
///    would pass. Case, accents, punctuation, stylized characters, guests and upload noise such
///    as "(Explicit)" or "(Official Video)" are ignored.
/// 2. The same kind of recording: a remix, a live take, a sped-up upload never stands in for
///    the original, nor the original for them. A remaster is the same recording.
/// 3. The same artist: a primary artist of one side is credited on the other, and the two do
///    not name different guests. KuGou's "、" and its "Ye (侃爷)" read as they should.
///
/// The length is checked separately (<see cref="LengthFits"/>), because not every source knows it.
/// </summary>
internal static class LyricsIdentity
{
    /// <summary>How far apart two lengths may be and still be one recording.</summary>
    public const int LengthToleranceSeconds = SongIdentity.LengthToleranceSeconds;

    /// <summary>Lengths are compared by <see cref="LengthFits"/>, where a source knows one.</summary>
    private static readonly SongMatchOptions Titles = new() { LengthToleranceSeconds = null };

    public static bool SameSong(string wantTitle, string wantArtist, string? gotTitle, string? gotArtist,
        IEnumerable<string>? gotCredits = null)
    {
        if (string.IsNullOrWhiteSpace(gotTitle) || string.IsNullOrWhiteSpace(wantArtist)) return false;
        return SongIdentity.Same(wantTitle, wantArtist, gotTitle, Credit(gotArtist, gotCredits), Titles).IsSame;
    }

    public static bool SameTitle(string want, string? got) =>
        !string.IsNullOrWhiteSpace(got) && SongIdentity.SameTitle(want, got, Titles).IsSame;

    public static bool SameArtist(string want, string? got, IEnumerable<string>? credits = null) =>
        SongIdentity.ArtistsAgree(want, got, credits);

    /// <summary>A source that lists its artists one by one, as one credit.</summary>
    private static string? Credit(string? artist, IEnumerable<string>? credits)
    {
        var listed = credits?.Where(credit => !string.IsNullOrWhiteSpace(credit)).ToList();
        return string.IsNullOrWhiteSpace(artist) && listed is { Count: > 0 } ? string.Join("、", listed) : artist;
    }

    /// <summary>Within a few seconds, or unknown on either side.</summary>
    public static bool LengthFits(int? want, double? got) => SongIdentity.LengthFits(want, got);

    /// <summary>Why a match that passed is still worth a look, or null.</summary>
    public static string? Doubt(int? want, double? got)
    {
        if (want is not > 0) return "the song's length is unknown";
        if (got is not > 0) return "the lyrics carry no length";
        return Math.Abs(got.Value - want.Value) > 1.5 ? $"lengths differ by {Math.Abs(got.Value - want.Value):0.#} s" : null;
    }

    /// <summary>The title as compared: see <see cref="SongIdentity.ParseTitle"/>.</summary>
    internal static string TitleKey(string title) => SongIdentity.ParseTitle(title).Key;

    /// <summary>The first-named artist, so "Drake feat. Rihanna" and "Drake" agree, as a key.</summary>
    internal static string LeadArtist(string artist) => SongIdentity.Key(SongIdentity.PrimaryArtist(artist));
}
