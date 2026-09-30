using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// Where each later page of a search's songs comes from. Page one shows the library's best
/// matches and then outside songs; these pin that every page after it carries on from
/// there, so a client scrolling the list sees each row once and none go missing.
/// </summary>
public class SearchSongPagePlannerTests
{
    /// <summary>
    /// One search, described the way page one left it: a library of <paramref name="library"/>
    /// matches, a build of <paramref name="built"/> outside songs, and page one asked for
    /// <paramref name="pageOneCount"/> rows, with the local/outside split the search uses.
    /// </summary>
    private sealed record Search(int PageOneCount, int Library, int Built)
    {
        public (int Local, int External) Budget => SearchBudget.Compute(PageOneCount);
        public int PrefixCount => Math.Min(Budget.Local, Library);
        public int Shown => SearchSongOrder.PageOneExternalCount(Built, Budget.Local, Budget.External, PrefixCount);
        public int Later => Built - Shown;
        public bool LibraryContinues => PrefixCount >= Budget.Local && Budget.Local > 0;

        public List<string> LibraryRows => Enumerable.Range(0, Library).Select(i => $"l{i}").ToList();
        public List<string> BuiltRows => Enumerable.Range(0, Built).Select(i => $"e{i}").ToList();

        /// <summary>Every row of the search once, in order, as if it had no pages.</summary>
        public List<string> Whole =>
            LibraryRows.Take(PrefixCount)
                .Concat(BuiltRows)
                .Concat(LibraryContinues ? LibraryRows.Skip(PrefixCount) : [])
                .ToList();

        public SearchSongPage Plan(int offset, int count) =>
            SearchSongPagePlanner.Plan(offset, count, PrefixCount, Shown, PageOneCount, Later, LibraryContinues);

        /// <summary>The rows a page renders, with Navidrome answering the library part.</summary>
        public List<string> Render(SearchSongPage page)
        {
            var locals = LibraryRows.Skip(page.LocalOffset).Take(page.LeadingLocals + page.TrailingLocals).ToList();
            return locals.Take(page.LeadingLocals)
                .Concat(BuiltRows.Take(Shown).Skip(page.PageOneExternalSkip).Take(page.PageOneExternalTake))
                .Concat(BuiltRows.Skip(Shown).Skip(page.LaterExternalSkip).Take(page.LaterExternalTake))
                .Concat(locals.Skip(page.LeadingLocals))
                .ToList();
        }

        /// <summary>
        /// Page one exactly as the search builds it today: the library's prefix, then the
        /// outside rows its budget allows.
        /// </summary>
        public List<string> PageOne =>
            LibraryRows.Take(PrefixCount).Concat(BuiltRows.Take(Shown)).ToList();

        /// <summary>The last place anything can be, so a walk knows it has passed the end.</summary>
        public int End => PageOneCount + Later + (LibraryContinues ? Library - PrefixCount : 0);
    }

    public static IEnumerable<object[]> Searches()
    {
        // Page sizes around the budget's edges: the smallest request that earns discovery,
        // the spec default, the last one below the outside ceiling, and big radio-style ones.
        foreach (var pageOne in new[] { 13, 20, 40, 79, 80, 81, 200 })
        foreach (var library in new[] { 0, 5, 12, 13, 30, 100, 400 })
        foreach (var built in new[] { 0, 3, 8, 25, 60 })
            yield return [pageOne, library, built];
    }

    [Theory]
    [MemberData(nameof(Searches))]
    public void OffsetZero_IsExactlyPageOne(int pageOne, int library, int built)
    {
        var search = new Search(pageOne, library, built);

        Assert.Equal(search.PageOne, search.Render(search.Plan(0, pageOne)));
    }

    [Theory]
    [MemberData(nameof(Searches))]
    public void SteppingByThePageSize_ShowsEveryRowOnce(int pageOne, int library, int built)
    {
        var search = new Search(pageOne, library, built);

        var seen = search.PageOne;
        for (var offset = pageOne; offset <= search.End; offset += pageOne)
            seen.AddRange(search.Render(search.Plan(offset, pageOne)));

        Assert.Equal(search.Whole, seen);
    }

    [Theory]
    [MemberData(nameof(Searches))]
    public void ChangingThePageSizeMidway_StillShowsEveryRowOnce(int pageOne, int library, int built)
    {
        var search = new Search(pageOne, library, built);
        int[] sizes = [7, 1, 33, 20, 12, 50, 3, 100];

        var seen = search.PageOne;
        var offset = pageOne;
        for (var step = 0; offset <= search.End; step++)
        {
            var size = sizes[step % sizes.Length];
            seen.AddRange(search.Render(search.Plan(offset, size)));
            offset += size;
        }

        Assert.Equal(search.Whole, seen);
    }

    [Theory]
    // The worked example: a 20-row page, 30 library matches, 25 outside songs. Page one is
    // l0-l11 then e0-e7. The whole list is l0-l11, e0-e24, l12-l29.
    //       offset count  localOffset leading shownSkip shownTake laterSkip laterTake trailing
    [InlineData(20, 20, 12, 0, 0, 0, 0, 17, 3)]    // e8-e24, then l12-l14
    [InlineData(40, 20, 15, 0, 0, 0, 0, 0, 20)]    // l15 onward; Navidrome has only 15 left to give
    [InlineData(60, 20, 35, 0, 0, 0, 0, 0, 20)]    // past the end: Navidrome answers nothing
    [InlineData(30, 5, 0, 0, 0, 0, 10, 5, 0)]      // a smaller page inside the outside rows
    [InlineData(35, 5, 12, 0, 0, 0, 15, 2, 3)]     // across the join into the library
    [InlineData(10, 20, 10, 2, 0, 8, 0, 10, 0)]    // an offset inside page one: its own rows again
    [InlineData(20, 0, 0, 0, 0, 0, 0, 0, 0)]       // a page of nothing asks Navidrome for nothing
    public void WorkedExample(int offset, int count, int localOffset, int leading,
        int shownSkip, int shownTake, int laterSkip, int laterTake, int trailing)
    {
        var page = SearchSongPagePlanner.Plan(offset, count, prefixCount: 12, pageOneExternals: 8,
            pageOneCount: 20, laterExternals: 17, libraryContinues: true);

        Assert.Equal(new SearchSongPage(localOffset, leading, shownSkip, shownTake, laterSkip, laterTake, trailing), page);
    }

    [Fact]
    public void AShortPageOne_LeavesItsEmptyPlacesBehind_SoTheNextPageStartsOnTheLibrary()
    {
        // 20 asked for, 12 from the library and only 3 outside songs built: page one showed
        // 15 rows. A client stepping by 20 must get l12 next, not l17.
        var page = SearchSongPagePlanner.Plan(20, 20, prefixCount: 12, pageOneExternals: 3,
            pageOneCount: 20, laterExternals: 0, libraryContinues: true);

        Assert.Equal(new SearchSongPage(12, 0, 0, 0, 0, 0, 20), page);
    }

    [Fact]
    public void ALibraryThatRanOutOnPageOne_IsNotAskedAgain()
    {
        var page = SearchSongPagePlanner.Plan(20, 20, prefixCount: 5, pageOneExternals: 15,
            pageOneCount: 20, laterExternals: 45, libraryContinues: false);

        Assert.Equal(new SearchSongPage(0, 0, 0, 0, 0, 20, 0), page);
        Assert.Equal(new SearchSongPage(0, 0, 0, 0, 0, 0, 0),
            SearchSongPagePlanner.Plan(80, 20, 5, 15, 20, 45, false));
    }

    [Theory]
    [InlineData(-5, 20)]
    [InlineData(20, -1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void NonsenseNumbers_NeitherThrowNorWrapRound(int offset, int count)
    {
        var page = SearchSongPagePlanner.Plan(offset, count, 12, 8, 20, 17, true);

        Assert.True(page.LocalOffset >= 0 && page.LeadingLocals >= 0 && page.TrailingLocals >= 0);
        Assert.True(page.PageOneExternalSkip >= 0 && page.LaterExternalSkip >= 0);
    }
}
