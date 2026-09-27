using Octo.Services.Common;

namespace Octo.Services.Fingerprint;

/// <summary>
/// Decides whether the recording AcoustID identified is the recording that was asked for.
///
/// Exact string equality would reject constantly on real data. The requested side comes
/// from Last.fm and Deezer ("Teardrop - Remastered 2011", "Bjork" for "Björk"); the matched
/// side is MusicBrainz's canonical credit, which adds featured artists the request never
/// mentioned. Both are read by <see cref="SongIdentity"/>; what is decided here is how much
/// benefit of the doubt a verdict gets.
///
/// Biased toward "yes" on purpose, except about the version. A false NO discards a good file
/// AND writes a deny-list entry that stands for thirty days; a false YES costs a wrongly-tagged
/// track that the duration check and the ranking heuristics have already had two chances to
/// catch. A different version is never a yes: a live take or a remix the request did not ask
/// for is the wrong file however alike the rest reads.
/// </summary>
internal static class TrackMatchComparer
{
    /// <summary>
    /// The shortest core allowed to satisfy the prefix rule. Without a floor, "Go" matches
    /// "Gold" and a correct file is discarded for being a different song.
    /// </summary>
    private const int MinPrefixCore = 6;

    /// <summary>
    /// The same song, by <see cref="SongIdentity"/>'s title keys, or one title the start of the
    /// other: the prefix rule absorbs tails no vocabulary names, such as a subtitle MusicBrainz
    /// leaves off. The version rule is what stops it absorbing "(Mad Professor mix)" too.
    /// </summary>
    internal static bool TitleMatches(string? requested, string? matched)
    {
        var want = SongIdentity.ParseTitle(requested);
        var got = SongIdentity.ParseTitle(matched);
        // Nothing to judge on. Absence of evidence is not a mismatch.
        if (want.Key.Length == 0 || got.Key.Length == 0) return true;

        var sameCore = SongIdentity.SameTitle(requested, matched).Verdict != SongVerdict.Different
            || want.LooseKey == got.LooseKey
            || PrefixOf(want.Key, got.Key);
        if (!sameCore) return false;

        return !HasUnrequestedVariantMarker(requested, matched);
    }

    private static bool PrefixOf(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= MinPrefixCore && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    /// <summary>
    /// One-directional: a match carrying "dub" the request never asked for is a different take;
    /// a request that says "live" against a MusicBrainz title that does not is just MusicBrainz
    /// being tidy, since it writes a live recording's venue and date in the disambiguation and
    /// not the title. The same reading SoulseekDownloadService ranks and filters peers by.
    /// </summary>
    internal static bool HasUnrequestedVariantMarker(string? requested, string? matched) =>
        SongIdentity.AddedVersions(requested, matched).Count > 0;

    /// <summary>
    /// Artist credits legitimately nest. MusicBrainz writes "Massive Attack feat. Elizabeth
    /// Fraser" where Last.fm says "Massive Attack", and sometimes the reverse, so a shared artist
    /// is enough, and so is one key containing the other ("Bjork" in "Björk Guðmundsdóttir").
    /// Two credits that each name a guest the other lacks ("Bizarrap, Duki" against "Bizarrap &amp;
    /// Rauw Alejandro") are a different collaboration.
    /// </summary>
    internal static bool ArtistMatches(string? requested, string? creditedJoined, IEnumerable<string>? credits)
    {
        var a = SongIdentity.Key(requested);
        var b = SongIdentity.Key(creditedJoined);
        var listed = credits?.ToList();
        if (a.Length == 0 || (b.Length == 0 && listed is not { Count: > 0 })) return true;

        switch (SongIdentity.CompareArtists(requested, creditedJoined, listed))
        {
            case ArtistAgreement.Agree or ArtistAgreement.Loose or ArtistAgreement.Unknown:
                return true;
            case ArtistAgreement.Conflict:
                return false;
        }
        return b.Length > 0 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }
}
