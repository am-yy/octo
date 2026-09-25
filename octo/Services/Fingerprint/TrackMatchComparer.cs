using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Octo.Services.Soulseek;

namespace Octo.Services.Fingerprint;

/// <summary>
/// Decides whether the recording AcoustID identified is the recording that was asked for.
///
/// Exact string equality would reject constantly on real data. The requested side comes
/// from Last.fm and Deezer ("Teardrop - Remastered 2011", "Bjork" for "Björk"); the matched
/// side is MusicBrainz's canonical credit, which adds featured artists the request never
/// mentioned.
///
/// Biased toward "yes" on purpose. A false NO discards a good file AND writes a deny-list
/// entry that stands for thirty days; a false YES costs a wrongly-tagged track that the
/// duration check and the ranking heuristics have already had two chances to catch.
/// </summary>
internal static class TrackMatchComparer
{
    /// <summary>
    /// The shortest core allowed to satisfy the prefix rule. Without a floor, "Go" matches
    /// "Gold" and a correct file is discarded for being a different song.
    /// </summary>
    private const int MinPrefixCore = 6;

    /// <summary>
    /// Casefold, strip diacritics, spell out "&amp;", then drop every non-alphanumeric
    /// character. Compacting to letters and digits is what makes "Don't Stop Me Now" and
    /// "Dont Stop Me Now" one string without a special case for apostrophes.
    ///
    /// Invariant culture throughout: under a Turkish locale ToLower turns "I" into a dotless
    /// "ı" and every comparison involving an I silently stops matching.
    /// </summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var folded = value.Replace("&", " and ")
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Normalize with bracketed and parenthesized annotations removed first, so
    /// "Teardrop (Mad Professor mix)" and "Teardrop" share a core.
    /// </summary>
    internal static string Core(string? value) =>
        Normalize(Regex.Replace(value ?? "", @"[\(\[][^\)\]]*[\]\)]", " "));

    /// <summary>
    /// The prefix rule absorbs Last.fm's "- Remastered 2011" and "(Official Video)" tails
    /// without needing a vocabulary of remaster words. The variant-marker rule is what stops
    /// it absorbing "(Mad Professor mix)" too.
    /// </summary>
    internal static bool TitleMatches(string? requested, string? matched)
    {
        var a = Core(requested);
        var b = Core(matched);
        // Nothing to judge on. Absence of evidence is not a mismatch.
        if (a.Length == 0 || b.Length == 0) return true;

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        var sameCore = a == b
            || (shorter.Length >= MinPrefixCore && longer.StartsWith(shorter, StringComparison.Ordinal));
        if (!sameCore) return false;

        return !HasUnrequestedVariantMarker(requested, matched);
    }

    /// <summary>
    /// Word-boundary matched on the raw text, and one-directional, exactly as
    /// SoulseekDownloadService.VariantPenalty does it. A match carrying "dub" the request
    /// never asked for is a different take; a request that says "live" against a MusicBrainz
    /// title that does not is just MusicBrainz being tidy.
    /// </summary>
    internal static bool HasUnrequestedVariantMarker(string? requested, string? matched)
    {
        var want = (requested ?? "").ToLowerInvariant();
        var got = (matched ?? "").ToLowerInvariant();
        return SoulseekDownloadService.VariantMarkers.Any(marker =>
            Regex.IsMatch(got, $@"\b{marker}\b") && !Regex.IsMatch(want, $@"\b{marker}\b"));
    }

    /// <summary>
    /// Artist credits legitimately nest. MusicBrainz writes "Massive Attack feat. Elizabeth
    /// Fraser" where Last.fm says "Massive Attack", and sometimes the reverse, so containment
    /// in EITHER direction is a match here, unlike for titles.
    /// </summary>
    internal static bool ArtistMatches(string? requested, string? creditedJoined, IEnumerable<string>? credits)
    {
        var a = Normalize(requested);
        var b = Normalize(creditedJoined);
        if (a.Length == 0 || b.Length == 0) return true;

        if (a == b
            || a.Contains(b, StringComparison.Ordinal)
            || b.Contains(a, StringComparison.Ordinal)) return true;

        // A collaboration credits several artists; matching any one of them is enough.
        if (credits?.Any(credit => Normalize(credit) == a) == true) return true;

        // A request naming every credited artist is the same credit whatever joins them. Last.fm
        // and Deezer write "Bizarrap, Rauw Alejandro" where MusicBrainz writes "Bizarrap & Rauw
        // Alejandro"; without this a confident, correct match read as a different recording, and
        // a mismatch deletes the file and blacklists the peer.
        var named = credits?.Select(Normalize).Where(credit => credit.Length > 0).ToList();
        if (named is { Count: > 1 } && named.All(credit => a.Contains(credit, StringComparison.Ordinal))) return true;

        return StripLeadingThe(a) == StripLeadingThe(b);
    }

    private static string StripLeadingThe(string normalized) =>
        normalized.StartsWith("the", StringComparison.Ordinal) && normalized.Length > 3
            ? normalized[3..]
            : normalized;

    /// <summary>
    /// Version words that make a different recording. Unlike TitleMatches, "radio edit" is NOT
    /// neutral here: someone who keeps the album cut and the radio edit keeps both on purpose.
    /// </summary>
    internal static readonly string[] VersionMarkers =
    [
        "remix", "live", "acoustic", "karaoke", "instrumental", "radio edit", "extended", "edit",
        "demo", "session", "dub", "reprise", "cover", "unplugged", "acapella", "a cappella",
        "slowed", "sped up", "nightcore", "mix", "version",
    ];

    private static readonly Regex FeatureSegment = new(
        @"[\(\[]\s*(feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]|\s(feat\.?|ft\.?|featuring)\s.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A remaster is the same recording, so its annotation is neutral.</summary>
    private static readonly Regex Remaster = new(
        @"(\b\d{4}\s+)?\bremaster(ed)?\b(\s+\d{4})?(\s+version)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Same recording AND same version, for telling duplicates apart (#53) and for choosing the
    /// one MusicBrainz recording a kept fingerprint belongs to (#47). Stricter than TitleMatches in
    /// three ways, each of which cost someone a real deletion: the letters must be equal, not a
    /// prefix; version markers must agree both ways, and a marker never matches its absence;
    /// numbers compare as sets, so "Vol. 53" matches "Vol. 53/66" but "Shotta Flow" never
    /// matches "Shotta Flow 4".
    /// </summary>
    internal static bool SameVersion(string? a, string? b)
    {
        var left = Remaster.Replace(FeatureSegment.Replace(a ?? "", ""), " ");
        var right = Remaster.Replace(FeatureSegment.Replace(b ?? "", ""), " ");

        if (!Markers(left).SetEquals(Markers(right))) return false;

        var leftNumbers = Numbers(left);
        var rightNumbers = Numbers(right);
        if (leftNumbers.Count == 0 != (rightNumbers.Count == 0)) return false;
        if (leftNumbers.Count > 0 && !leftNumbers.IsSubsetOf(rightNumbers) && !rightNumbers.IsSubsetOf(leftNumbers))
            return false;

        var leftLetters = new string(Normalize(StripMarkers(left)).Where(char.IsLetter).ToArray());
        var rightLetters = new string(Normalize(StripMarkers(right)).Where(char.IsLetter).ToArray());
        return leftLetters.Length > 0 && leftLetters == rightLetters;
    }

    private static HashSet<string> Markers(string title)
    {
        var lower = title.ToLowerInvariant();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var marker in VersionMarkers)
            if (Regex.IsMatch(lower, $@"\b{Regex.Escape(marker)}\b")) found.Add(marker);
        // "radio edit" contains "edit"; keep the specific one so both sides read alike.
        if (found.Contains("radio edit")) found.Remove("edit");
        return found;
    }

    private static string StripMarkers(string title)
    {
        var result = title;
        foreach (var marker in VersionMarkers.OrderByDescending(marker => marker.Length))
            result = Regex.Replace(result, $@"\b{Regex.Escape(marker)}\b", " ", RegexOptions.IgnoreCase);
        return result;
    }

    private static HashSet<int> Numbers(string title) =>
        Regex.Matches(title, @"\d+")
            .Select(match => int.TryParse(match.Value, out var number) ? number : -1)
            .Where(number => number >= 0)
            .ToHashSet();
}
