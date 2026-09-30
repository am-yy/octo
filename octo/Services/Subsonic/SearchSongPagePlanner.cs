namespace Octo.Services.Subsonic;

/// <summary>
/// Where one later page of a search3 / search2 song list comes from.
///
/// A search with discovery answers page one with the library's best matches and then
/// outside songs. Before this, a client that asked for page two was sent straight to
/// Navidrome at the same offset, so every outside song past the first page was
/// unreachable and the library rows page one had held back were skipped as well. This
/// lays the whole answer out as one list, in the order page one started it, so any
/// songOffset lands on the right rows:
///
///   1. the library rows page one showed (its local prefix)
///   2. the outside rows page one showed
///   3. empty places, when page one came back shorter than it was asked for
///   4. the outside rows page one had no room for
///   5. the rest of the library, from where the prefix stopped
///
/// Places 0 up to page one's song count are page one, whatever it managed to fill, so a
/// client stepping by its page size lands exactly after it. Past that there are no gaps,
/// so a client that stops at the first short page is not stopped early.
/// </summary>
internal static class SearchSongPagePlanner
{
    /// <summary>
    /// Plan the page at <paramref name="songOffset"/> of <paramref name="songCount"/> rows.
    /// </summary>
    /// <param name="order">What page one showed; see <see cref="SearchSongOrder"/>.</param>
    public static SearchSongPage Plan(int songOffset, int songCount, SearchSongOrder order) =>
        Plan(songOffset, songCount, order.PrefixCount, order.PageOneExternals, order.PageOneCount,
            order.LaterExternals.Count, order.LibraryContinues);

    /// <summary>The same plan from the bare numbers, which is what the tests drive.</summary>
    /// <param name="prefixCount">Library rows page one showed.</param>
    /// <param name="pageOneExternals">Outside rows page one took from the build.</param>
    /// <param name="pageOneCount">The songCount page one was asked for.</param>
    /// <param name="laterExternals">Outside rows left for the pages after it.</param>
    /// <param name="libraryContinues">
    /// Whether the library may have more rows past the prefix. Only when page one got a
    /// full prefix: a short one means Navidrome had nothing more to give.
    /// </param>
    public static SearchSongPage Plan(int songOffset, int songCount, int prefixCount,
        int pageOneExternals, int pageOneCount, int laterExternals, bool libraryContinues)
    {
        // long throughout, so an absurd offset or count cannot wrap round into a real row.
        long start = Math.Max(0, songOffset);
        long end = start + Math.Max(0, songCount);
        long prefix = Math.Max(0, prefixCount);
        long shown = Math.Max(0, pageOneExternals);
        long pageOne = Math.Max(prefix + shown, pageOneCount);
        long later = Math.Max(0, laterExternals);

        var (leadingFrom, leadingTo) = Overlap(start, end, 0, prefix);
        var (shownFrom, shownTo) = Overlap(start, end, prefix, prefix + shown);
        var (laterFrom, laterTo) = Overlap(start, end, pageOne, pageOne + later);
        var (tailFrom, tailTo) = libraryContinues
            ? Overlap(start, end, pageOne + later, long.MaxValue)
            : (0, 0);

        // The library side of a page is always one run of Navidrome's own list: rows from
        // the prefix and rows from the rest can only share a page when the page reaches
        // right across the outside rows, and then the rest starts where the prefix stopped.
        var leading = leadingTo - leadingFrom;
        var trailing = tailTo - tailFrom;
        var localOffset = leading > 0 ? leadingFrom
            : trailing > 0 ? prefix + (tailFrom - pageOne - later)
            : 0;

        return new SearchSongPage(
            LocalOffset: Clamp(localOffset),
            LeadingLocals: Clamp(leading),
            PageOneExternalSkip: Clamp(shownFrom - prefix),
            PageOneExternalTake: Clamp(shownTo - shownFrom),
            LaterExternalSkip: Clamp(laterFrom - pageOne),
            LaterExternalTake: Clamp(laterTo - laterFrom),
            TrailingLocals: Clamp(trailing));
    }

    /// <summary>The part of the page that falls inside one stretch of the list.</summary>
    private static (long From, long To) Overlap(long start, long end, long from, long to)
    {
        var a = Math.Max(start, from);
        var b = Math.Min(end, to);
        return a < b ? (a, b) : (0, 0);
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
}

/// <summary>
/// One page, in the order it is rendered: library rows, outside rows page one also showed,
/// outside rows it had no room for, then library rows again. The library rows come from a
/// single Navidrome call at <see cref="LocalOffset"/> for
/// <see cref="LeadingLocals"/> + <see cref="TrailingLocals"/> rows.
/// </summary>
internal readonly record struct SearchSongPage(
    int LocalOffset,
    int LeadingLocals,
    int PageOneExternalSkip,
    int PageOneExternalTake,
    int LaterExternalSkip,
    int LaterExternalTake,
    int TrailingLocals);
