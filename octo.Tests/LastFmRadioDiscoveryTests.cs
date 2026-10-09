using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Radio;
using Octo.Models.Settings;
using Octo.Services.LastFm;
using Octo.Services.Local;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>Discovery Mix finds artists the listener does not know, through the ones they play.</summary>
public sealed class LastFmRadioDiscoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "octo-discovery-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    /// <summary>Seed A and Seed B are played; Owned Artist is in the library. Shared Y is a
    /// neighbour of both seeds, Fresh X of one, and Hop Z only of Fresh X.</summary>
    private static readonly Dictionary<string, (string Name, double Match)[]> Graph = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Seed A"] = [("Owned Artist", .9), ("Seed B", .8), ("Fresh X", .7), ("Shared Y", .5)],
        ["Seed B"] = [("Shared Y", .6), ("Seed A", .9)],
        ["Fresh X"] = [("Hop Z", .9), ("Seed A", .5)],
        ["Shared Y"] = [("Fresh X", .4)],
    };

    [Fact]
    public async Task DiscoveryMix_IsArtistsTheListenerDoesNotKnow_TwoSongsEachAtMost()
    {
        var library = new Mock<ILocalLibraryService>();
        library.Setup(l => l.GetLibraryArtistNamesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["Owned Artist"]);

        var discovery = Assert.Single(await Build(new GraphHandler(Graph), library.Object),
            station => station.Kind == LastFmRadioStationKind.Discovery);

        var artists = discovery.Tracks.Select(track => track.Artist).ToList();
        Assert.DoesNotContain(artists, artist => artist is "Seed A" or "Seed B" or "Owned Artist");
        // Hop Z is two steps out: a neighbour of a new artist, not of one the listener plays.
        Assert.Contains("Shared Y", artists);
        Assert.Contains("Fresh X", artists);
        Assert.Contains("Hop Z", artists);
        Assert.All(artists.GroupBy(artist => artist), group =>
            Assert.True(group.Count() <= LastFmRadioRecommendationService.FrontierArtistCap, group.Key));
        Assert.Contains("Seed A", discovery.Seeds);
    }

    [Fact]
    public async Task DiscoveryMix_FallsBackToTheTagChart_WhenTheGraphFindsNobodyNew()
    {
        var discovery = Assert.Single(await Build(new GraphHandler(new(StringComparer.OrdinalIgnoreCase)), null),
            station => station.Kind == LastFmRadioStationKind.Discovery);

        Assert.All(discovery.Tracks, track => Assert.StartsWith("indie Artist", track.Artist));
        Assert.Contains("indie", discovery.Seeds);
    }

    private async Task<IReadOnlyList<LastFmRadioStation>> Build(HttpMessageHandler handler, ILocalLibraryService? library)
    {
        Directory.CreateDirectory(_directory);
        var settings = TestOptions.Monitor(new LastFmSettings
        {
            ApiKey = "test-key", EnablePersonalizedStations = true, EnableDiscoveryStations = false, MinimumPlays = 3,
        });
        var state = new LastFmRadioStateStore(Path.Combine(_directory, "state.json"), settings,
            new ExternalIdRegistry(), new Mock<ILogger<LastFmRadioStateStore>>().Object);
        for (var index = 0; index < 4; index++)
            foreach (var artist in new[] { "Seed A", "Seed B" })
                state.RecordPlay("alice", new LastFmRadioPlay
                {
                    Artist = artist, Title = $"{artist} song {index}", Genre = "Indie",
                    PlayedAtUtc = DateTime.UtcNow.AddHours(-index),
                });
        var lastFm = new LastFmService(new HttpClient(handler), settings,
            Options.Create(new MetadataSettings { Language = "en" }), new Mock<ILogger<LastFmService>>().Object);
        var service = new LastFmRadioRecommendationService(lastFm, state, settings,
            new Mock<ILogger<LastFmRadioRecommendationService>>().Object, library: library);
        return await service.BuildAsync("alice");
    }

    private sealed class GraphHandler(Dictionary<string, (string Name, double Match)[]> graph) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var artist = query["artist"] ?? "";
            object body = query["method"] switch
            {
                "artist.getsimilar" => new { similarartists = new { artist = graph.GetValueOrDefault(artist, [])
                    .Select(item => new { name = item.Name, match = item.Match }).ToArray() } },
                "artist.gettoptracks" => new { toptracks = new { track = Enumerable.Range(0, 3)
                    .Select(index => new { name = $"{artist}-top-{index}", artist = new { name = artist } }).ToArray() } },
                "tag.gettoptracks" => new { tracks = new { track = Enumerable.Range(0, 20)
                    .Select(index => new { name = $"{query["tag"]}-{index}", duration = 180000,
                        artist = new { name = $"{query["tag"]} Artist {index}" } }).ToArray() } },
                _ => new { },
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }
    }
}
