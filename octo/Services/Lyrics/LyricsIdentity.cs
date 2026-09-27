using System.Text.RegularExpressions;
using Octo.Services.Fingerprint;

namespace Octo.Services.Lyrics;

/// <summary>
/// Whether a lyrics entry is the song asked for, the same rule the Octo app applies to online
/// lyrics. Strict on purpose: a lyrics search is loose and returns the artist's other songs,
/// and a length alone once let one of those stand in ("Ultimate $uicide" for "$UICIDE", both
/// about 170 s). So three things must hold:
///
/// 1. The same title, ignoring case, accents, punctuation and bracketed extras such as
///    "(Explicit)" or "(feat. X)", compared whole. No prefix or containment rule: that is
///    how "Ultimate $uicide" would pass.
/// 2. The same kind of recording: the version words ("remix", "live", "acoustic" ...) in both
///    titles, brackets included, must be the same set, so a remix or a live take never stands
///    in for the original, nor the original for them.
/// 3. The same artist: the lead artist (before "feat.", "&amp;", "x", ",", "、") on both sides
///    matches, or any credited artist on the entry is the one asked for.
///
/// The length is checked separately (<see cref="LengthFits"/>), because not every source knows it.
/// </summary>
internal static class LyricsIdentity
{
    /// <summary>How far apart two lengths may be and still be one recording.</summary>
    public const int LengthToleranceSeconds = 3;

    /// <summary>Words that make a title a different recording, the app's list.</summary>
    private static readonly HashSet<string> VersionWords =
    [
        "remix", "mix", "rmx", "live", "edit", "acoustic", "instrumental", "demo", "version", "rework",
        "bootleg", "vip", "cover", "karaoke", "extended", "dub", "slowed", "sped", "reverb", "unplugged",
    ];

    private static readonly Regex Brackets = new(@"\s*[\(\[（【][^\)\]）】]*[\)\]）】]", RegexOptions.Compiled);

    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>A trailing unbracketed guest credit, "Song feat. X".</summary>
    private static readonly Regex TrailingFeature = new(@"\s+(feat\.?|ft\.?|featuring)\s.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A "- Remastered 2011" style tail, which is the same recording. Only these words:
    /// "- Live" or "- Remix" is a different one and stays.</summary>
    private static readonly Regex NeutralTail = new(
        @"\s+-\s+((\d{4}\s+)?(remaster(ed)?|explicit|clean|mono|stereo)(\s+\d{4})?(\s+(version|mix))?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Everything after the lead artist.</summary>
    private static readonly Regex Guests = new(@"\s*(,|&|、|/|;|•|\bfeat\.?|\bft\.?|\bfeaturing|\bx\b|\bwith\b|\band\b)\s*.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool SameSong(string wantTitle, string wantArtist, string? gotTitle, string? gotArtist,
        IEnumerable<string>? gotCredits = null) =>
        SameTitle(wantTitle, gotTitle) && SameArtist(wantArtist, gotArtist, gotCredits);

    public static bool SameTitle(string want, string? got)
    {
        if (string.IsNullOrWhiteSpace(got)) return false;
        var a = TitleKey(want);
        return a.Length > 0 && a == TitleKey(got) && VersionOf(want).SetEquals(VersionOf(got));
    }

    public static bool SameArtist(string want, string? got, IEnumerable<string>? credits = null)
    {
        var lead = LeadArtist(want);
        if (lead.Length == 0) return false;
        if (!string.IsNullOrWhiteSpace(got)
            && (lead == LeadArtist(got) || Key(want) == Key(got) || Key(StripThe(want)) == Key(StripThe(got))))
            return true;
        return credits?.Any(credit => LeadArtist(credit) == lead || Key(credit) == Key(want)) == true;
    }

    /// <summary>Within a few seconds, or unknown on either side.</summary>
    public static bool LengthFits(int? want, double? got) =>
        want is not > 0 || got is not > 0 || Math.Abs(got.Value - want.Value) <= LengthToleranceSeconds;

    /// <summary>Why a match that passed is still worth a look, or null.</summary>
    public static string? Doubt(int? want, double? got)
    {
        if (want is not > 0) return "the song's length is unknown";
        if (got is not > 0) return "the lyrics carry no length";
        return Math.Abs(got.Value - want.Value) > 1.5 ? $"lengths differ by {Math.Abs(got.Value - want.Value):0.#} s" : null;
    }

    internal static string TitleKey(string title) =>
        Key(TrailingFeature.Replace(NeutralTail.Replace(title, ""), ""));

    internal static HashSet<string> VersionOf(string title) =>
        Words.Matches(title.ToLowerInvariant()).Select(match => match.Value).Where(VersionWords.Contains).ToHashSet();

    /// <summary>The first-named artist, so "Drake feat. Rihanna" and "Drake" agree. KuGou adds a
    /// Chinese name in brackets, "Ye (侃爷)", which the brackets rule removes.</summary>
    internal static string LeadArtist(string artist)
    {
        var bare = Brackets.Replace(artist, " ").Trim();
        var lead = Key(Guests.Replace(bare, ""));
        // "X Ambassadors" is all guest by that rule; the whole name is the lead then.
        return lead.Length > 0 ? lead : Key(bare);
    }

    private static string StripThe(string name) =>
        name.TrimStart().StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? name.TrimStart()[4..] : name;

    /// <summary>Case, accents, punctuation, spacing and bracketed extras ignored.</summary>
    private static string Key(string value) => TrackMatchComparer.Normalize(Brackets.Replace(value, " ").Replace("&", " "));
}
