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

    [Fact]
    public async Task GenreRadio_LeansOnArtistsTheListenerDoesNotKnow()
    {
        // The tag chart alternates ten library artists with new ones. The same builds with a
        // library Octo cannot read, where those ten are strangers, are the comparison.
        var owned = Enumerable.Range(0, 10).Select(index => $"Owned {index}").ToArray();
        var chart = Enumerable.Range(0, 40).Select(index => index % 2 == 0 ? owned[index / 2 % 10] : $"New {index}").ToArray();
        async Task<int> Taken(bool libraryKnown)
        {
            var library = new Mock<ILocalLibraryService>();
            library.Setup(l => l.GetLibraryArtistNamesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(libraryKnown ? owned : []);
            var total = 0;
            for (var seed = 0; seed < 20; seed++)
            {
                var genre = Assert.Single(await Build(new GraphHandler(new(StringComparer.OrdinalIgnoreCase), chart),
                    library.Object, trackCount: 10, seed: seed), station => station.Kind == LastFmRadioStationKind.Genre);
                total += genre.Tracks.Count(track => owned.Contains(track.Artist));
            }
            return total;
        }

        var known = await Taken(libraryKnown: true);
        var strangers = await Taken(libraryKnown: false);
        Assert.True(known < strangers * 0.7, $"known {known}, strangers {strangers}");
    }

    [Fact]
    public async Task DiscoveryMix_ReachesOutFromTheLinkedAccountsLongTermArtists()
    {
        // Long Love is a long-term favourite on Last.fm that Octo has never heard played here.
        var graph = new Dictionary<string, (string Name, double Match)[]>(Graph, StringComparer.OrdinalIgnoreCase)
        {
            ["Long Love"] = [("Far W", .9)],
        };

        var linked = Assert.Single(await Build(new GraphHandler(graph, topArtists: ["Long Love"]), null, linkedAccount: true),
            station => station.Kind == LastFmRadioStationKind.Discovery);
        var unlinked = Assert.Single(await Build(new GraphHandler(graph, topArtists: ["Long Love"]), null),
            station => station.Kind == LastFmRadioStationKind.Discovery);

        Assert.Contains("Long Love", linked.Seeds);
        Assert.Contains(linked.Tracks, track => track.Artist == "Far W");
        // A long-term favourite is known, not a discovery.
        Assert.DoesNotContain(linked.Tracks, track => track.Artist == "Long Love");
        Assert.DoesNotContain("Long Love", unlinked.Seeds);
    }

    [Fact]
    public void ScoreArtists_SteadyFavouriteOutranksOneAfternoon_AndLongTermArtistsCount()
    {
        var now = DateTime.UtcNow;
        var plays = Enumerable.Range(0, 15).Select(index => new LastFmRadioPlay
                { Artist = "Steady", Title = "S" + index, PlayedAtUtc = now.AddDays(-index * 4) })
            .Concat(Enumerable.Range(0, 3).Select(index => new LastFmRadioPlay
                { Artist = "Binge", Title = "B" + index, PlayedAtUtc = now.AddHours(-index) }))
            .Append(new LastFmRadioPlay { Artist = "Once", Title = "O", PlayedAtUtc = now })
            .OrderByDescending(play => play.PlayedAtUtc).ToList();

        var scores = LastFmRadioRecommendationService.ScoreArtists(plays, ["Long Love", "Once"]);

        Assert.Equal(["Steady", "Once", "Binge", "Long Love"], scores.Keys);
        Assert.True(scores["Long Love"] > Math.Log(2), "a top long-term artist outweighs a single play");
    }

    private async Task<IReadOnlyList<LastFmRadioStation>> Build(HttpMessageHandler handler, ILocalLibraryService? library,
        int trackCount = 50, int seed = 7, bool linkedAccount = false)
    {
        Directory.CreateDirectory(_directory);
        var settings = TestOptions.Monitor(new LastFmSettings
        {
            ApiKey = "test-key", EnablePersonalizedStations = true, EnableDiscoveryStations = false, MinimumPlays = 3,
            RadioTrackCount = trackCount,
            UserSessions = linkedAccount
                ? new(StringComparer.OrdinalIgnoreCase) { ["alice"] = new() { SessionKey = "sk", LastFmUser = "alicefm" } }
                : new(StringComparer.OrdinalIgnoreCase),
        });
        var state = new LastFmRadioStateStore(Path.Combine(_directory, Guid.NewGuid() + ".json"), settings,
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
            new Mock<ILogger<LastFmRadioRecommendationService>>().Object, library: library)
        {
            Randomizer = () => new Random(seed),
        };
        return await service.BuildAsync("alice");
    }

    private sealed class GraphHandler(Dictionary<string, (string Name, double Match)[]> graph, string[]? chart = null,
        string[]? topArtists = null) : HttpMessageHandler
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
                "user.gettopartists" when query["user"] == "alicefm" => new { topartists = new { artist =
                    (topArtists ?? []).Select(name => new { name }).ToArray() } },
                "tag.gettoptracks" => new { tracks = new { track = Enumerable.Range(0, chart?.Length ?? 20)
                    .Select(index => new { name = $"{query["tag"]}-{index}", duration = 180000,
                        artist = new { name = chart?[index] ?? $"{query["tag"]} Artist {index}" } }).ToArray() } },
                _ => new { },
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }
    }
}
