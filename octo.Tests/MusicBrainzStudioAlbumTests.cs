using System.Text.Json;
using Octo.Services.Fingerprint;

namespace Octo.Tests;

public class MusicBrainzStudioAlbumTests
{
    private static string? Pick(string json, string title)
    {
        using var doc = JsonDocument.Parse(json);
        return MusicBrainzClient.PickStudioAlbum(doc.RootElement, title);
    }

    [Fact]
    public void PicksEarliestStudioAlbum_OverSinglesCompilationsAndOtherRecordings()
    {
        const string search = """
        {"recordings":[
          {"title":"Zukunft Pink","releases":[
            {"status":"Official","date":"2022-10-20","release-group":{"id":"single","primary-type":"Single"}},
            {"status":"Official","date":"2023-02-10","release-group":{"id":"bravo","primary-type":"Album","secondary-types":["Compilation"]}}]},
          {"title":"Zukunft Pink","releases":[
            {"status":"Official","date":"2024-06-21","release-group":{"id":"love-songs","primary-type":"Album"}},
            {"status":"Official","date":"","release-group":{"id":"empty-date","primary-type":"Album","secondary-types":[]}},
            {"status":"Official","release-group":{"id":"undated","primary-type":"Album","secondary-types":[]}}]},
          {"title":"Zukunft Pink (Remix)","releases":[
            {"status":"Official","date":"1999-01-01","release-group":{"id":"remix-album","primary-type":"Album"}}]}
        ]}
        """;

        Assert.Equal("love-songs", Pick(search, "Zukunft Pink"));
    }

    [Fact]
    public void MatchesTitle_IgnoringPunctuationAndCase()
    {
        const string search = """
        {"recordings":[{"title":"They Don't Care About Us","releases":[
          {"status":"Official","date":"1995-06-20","release-group":{"id":"history","primary-type":"Album"}}]}]}
        """;

        Assert.Equal("history", Pick(search, "They Dont Care About Us"));
    }

    [Fact]
    public void PicksArtistMixtape_WhenStreamingCatalogOnlyHasTheSingle()
    {
        // MusicBrainz's Guillotine response includes Exmilitary as Album + Mixtape/Street.
        const string search = """
        {"recordings":[{"title":"Guillotine","releases":[
          {"status":"Official","date":"2011-08-03","release-group":{"id":"single","primary-type":"Single"}},
          {"status":"Official","date":"2012-01-16","release-group":{"id":"compilation","primary-type":"Album","secondary-types":["Compilation"]}},
          {"status":"Official","date":"2011-04-25","release-group":{"id":"f1f6c7e2-7848-4554-b36c-2190e1d6bfb0","primary-type":"Album","secondary-types":["Mixtape/Street"]}}]}]}
        """;

        Assert.Equal("f1f6c7e2-7848-4554-b36c-2190e1d6bfb0", Pick(search, "Guillotine"));
    }

    [Fact]
    public void PrefersStudioAlbumOverEarlierMixtape()
    {
        const string search = """
        {"recordings":[{"title":"Song","releases":[
          {"status":"Official","date":"2010-01-01","release-group":{"id":"film","primary-type":"Album","secondary-types":["Soundtrack"]}},
          {"status":"Official","date":"2012-01-01","release-group":{"id":"studio","primary-type":"Album"}},
          {"status":"Official","date":"2011-01-01","release-group":{"id":"mixtape","primary-type":"Album","secondary-types":["Mixtape/Street"]}}]}]}
        """;

        Assert.Equal("studio", Pick(search, "Song"));
    }

    [Fact]
    public void PrefersArtistMixtapeOverSoundtrack()
    {
        const string search = """
        {"recordings":[{"title":"Song","releases":[
          {"status":"Official","date":"2010-01-01","release-group":{"id":"film","primary-type":"Album","secondary-types":["Soundtrack"]}},
          {"status":"Official","date":"2011-01-01","release-group":{"id":"mixtape","primary-type":"Album","secondary-types":["Mixtape/Street"]}}]}]}
        """;

        Assert.Equal("mixtape", Pick(search, "Song"));
    }

    [Theory]
    [InlineData("Compilation")]
    [InlineData("Live")]
    [InlineData("Remix")]
    public void RejectsMixtapesThatAreAlsoCompilationsLiveOrRemixes(string secondaryType)
    {
        var search = $$$"""
        {"recordings":[{"title":"Song","releases":[
          {"status":"Official","release-group":{"id":"wrong-version","primary-type":"Album","secondary-types":["Mixtape/Street","{{{secondaryType}}}"]}}]}]}
        """;

        Assert.Null(Pick(search, "Song"));
    }

    [Fact]
    public void FallsBackToSoundtrack_OnlyWithoutStudioAlbum()
    {
        const string soundtrackOnly = """
        {"recordings":[{"title":"Blub","releases":[
          {"status":"Official","date":"2021-09-17","release-group":{"id":"toem","primary-type":"Album","secondary-types":["Soundtrack"]}},
          {"status":"Official","date":"2020-01-01","release-group":{"id":"best-of","primary-type":"Album","secondary-types":["Compilation"]}}]}]}
        """;
        const string both = """
        {"recordings":[{"title":"Song","releases":[
          {"status":"Official","date":"1990-01-01","release-group":{"id":"film","primary-type":"Album","secondary-types":["Soundtrack"]}},
          {"status":"Official","date":"1995-01-01","release-group":{"id":"studio","primary-type":"Album"}}]}]}
        """;

        Assert.Equal("toem", Pick(soundtrackOnly, "Blub"));
        Assert.Equal("studio", Pick(both, "Song"));
    }

    [Fact]
    public void SkipsPromoAndStatuslessAlbums_EvenWhenDatedEarlier()
    {
        // Grandaddy "Now It's On": the search also returns a promo-only tour sampler.
        const string search = """
        {"recordings":[{"title":"Now It's On","releases":[
          {"status":"Promotion","date":"2003","release-group":{"id":"tour-sampler","primary-type":"Album","secondary-types":[]}},
          {"date":"2003","release-group":{"id":"label-sampler","primary-type":"Album"}},
          {"status":"Official","date":"2003-06-09","release-group":{"id":"sumday","primary-type":"Album"}}]}]}
        """;

        Assert.Equal("sumday", Pick(search, "Now It's On"));
    }

    [Fact]
    public void YearOnlyDate_DoesNotBeatFullDateInSameYear()
    {
        const string search = """
        {"recordings":[{"title":"Song","releases":[
          {"status":"Official","date":"2003-06-09","release-group":{"id":"precise","primary-type":"Album"}},
          {"status":"Official","date":"2003","release-group":{"id":"year-only","primary-type":"Album"}},
          {"status":"Official","date":"2004","release-group":{"id":"later","primary-type":"Album"}}]}]}
        """;

        Assert.Equal("precise", Pick(search, "Song"));
    }

    [Fact]
    public void ReturnsNull_WhenOnlySinglesAndCompilations()
    {
        const string search = """
        {"recordings":[{"title":"Zukunft Pink","releases":[
          {"status":"Official","date":"2022-10-20","release-group":{"id":"single","primary-type":"Single"}},
          {"status":"Official","date":"2023-02-10","release-group":{"id":"bravo","primary-type":"Album","secondary-types":["Compilation"]}}]}]}
        """;

        Assert.Null(Pick(search, "Zukunft Pink"));
    }
}
