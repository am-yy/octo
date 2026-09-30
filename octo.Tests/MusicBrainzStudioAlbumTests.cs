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
            {"date":"2022-10-20","release-group":{"id":"single","primary-type":"Single"}},
            {"date":"2023-02-10","release-group":{"id":"bravo","primary-type":"Album","secondary-types":["Compilation"]}}]},
          {"title":"Zukunft Pink","releases":[
            {"date":"2024-06-21","release-group":{"id":"love-songs","primary-type":"Album"}},
            {"date":"","release-group":{"id":"empty-date","primary-type":"Album","secondary-types":[]}},
            {"release-group":{"id":"undated","primary-type":"Album","secondary-types":[]}}]},
          {"title":"Zukunft Pink (Remix)","releases":[
            {"date":"1999-01-01","release-group":{"id":"remix-album","primary-type":"Album"}}]}
        ]}
        """;

        Assert.Equal("love-songs", Pick(search, "Zukunft Pink"));
    }

    [Fact]
    public void MatchesTitle_IgnoringPunctuationAndCase()
    {
        const string search = """
        {"recordings":[{"title":"They Don't Care About Us","releases":[
          {"date":"1995-06-20","release-group":{"id":"history","primary-type":"Album"}}]}]}
        """;

        Assert.Equal("history", Pick(search, "They Dont Care About Us"));
    }

    [Fact]
    public void FallsBackToSoundtrack_OnlyWithoutStudioAlbum()
    {
        const string soundtrackOnly = """
        {"recordings":[{"title":"Blub","releases":[
          {"date":"2021-09-17","release-group":{"id":"toem","primary-type":"Album","secondary-types":["Soundtrack"]}},
          {"date":"2020-01-01","release-group":{"id":"best-of","primary-type":"Album","secondary-types":["Compilation"]}}]}]}
        """;
        const string both = """
        {"recordings":[{"title":"Song","releases":[
          {"date":"1990-01-01","release-group":{"id":"film","primary-type":"Album","secondary-types":["Soundtrack"]}},
          {"date":"1995-01-01","release-group":{"id":"studio","primary-type":"Album"}}]}]}
        """;

        Assert.Equal("toem", Pick(soundtrackOnly, "Blub"));
        Assert.Equal("studio", Pick(both, "Song"));
    }

    [Fact]
    public void ReturnsNull_WhenOnlySinglesAndCompilations()
    {
        const string search = """
        {"recordings":[{"title":"Zukunft Pink","releases":[
          {"date":"2022-10-20","release-group":{"id":"single","primary-type":"Single"}},
          {"date":"2023-02-10","release-group":{"id":"bravo","primary-type":"Album","secondary-types":["Compilation"]}}]}]}
        """;

        Assert.Null(Pick(search, "Zukunft Pink"));
    }
}
