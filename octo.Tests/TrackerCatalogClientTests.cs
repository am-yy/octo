using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Octo.Services.Trackers;

namespace Octo.Tests;

public sealed class TrackerCatalogClientTests
{
    [Theory]
    [InlineData("WEB", "CD", "FLAC", "Lossless", TrackerDecision.CandidateExistingGroup)]
    [InlineData("CD", "CD", "MP3", "320", TrackerDecision.CandidateExistingGroup)]
    [InlineData("WEB", "WEB", "FLAC", "24bit Lossless", TrackerDecision.IgnoreSameMediumFlac)]
    [InlineData("CD", "CD", "FLAC", "24bit Lossless", TrackerDecision.IgnoreSameMediumFlac)]
    public async Task ClassifiesCompleteGroupInventoryByMediumAndFormat(string sourceMedium, string medium,
        string format, string encoding, TrackerDecision decision)
    {
        using var fixture = new Fixture(_ => _ switch
        {
            "browse" => BrowseGroup(27, categoryName: "Music", categoryId: 1),
            "torrentgroup" => GroupDetail([Torrent(1, medium, format, encoding, 0)]),
            _ => throw new InvalidOperationException(),
        });

        var finding = await fixture.Catalog.SearchAsync("red", [new ReleaseName("Artist", "Album")],
            sourceMedium, 12, "album", CancellationToken.None);

        Assert.NotNull(finding);
        Assert.Equal(decision, finding.Decision);
        Assert.Equal(sourceMedium, finding.SourceMedium);
        Assert.Equal(12, finding.IdentityVersion);
        Assert.Equal(2, finding.QueryCoverage.Count); // artist+album and album-only
        Assert.All(finding.QueryCoverage, q => Assert.True(q.Complete));
        Assert.Equal(1, fixture.Calls.Count(c => c.Action == "torrentgroup"));
        Assert.Equal(2, fixture.Calls.Count(c => c.Action == "browse"));
        if (decision == TrackerDecision.CandidateExistingGroup)
        {
            Assert.Equal("existing-group", finding.UploadMode);
            Assert.Equal(27, finding.SelectedGroupId);
        }
        if (decision == TrackerDecision.IgnoreSameMediumFlac)
            Assert.Equal("ignored", finding.Status);
    }

    [Fact]
    public async Task CompleteEmptySearchesProduceNewGroupCandidate()
    {
        using var fixture = new Fixture(_ => EmptyBrowse());
        var finding = await fixture.Catalog.SearchAsync("ops", [new ReleaseName("Sun Drug", "Crisps")],
            "WEB", 3, "album", CancellationToken.None);

        Assert.NotNull(finding);
        Assert.True(finding.SearchComplete);
        Assert.Equal(TrackerDecision.CandidateNewGroup, finding.Decision);
        Assert.Equal("new-group", finding.UploadMode);
        Assert.Null(finding.SelectedGroupId);
        Assert.Equal(2, fixture.Calls.Count(c => c.Action == "browse"));
        Assert.Contains(fixture.Calls, c => c.Query == "Sun Drug Crisps");
        Assert.Contains(fixture.Calls, c => c.Query == "Crisps");
    }


    [Fact]
    public async Task SearchesEveryPageAndFetchesEachMatchedGroupOnce()
    {
        var browseCalls = 0;
        using var fixture = new Fixture((action, _, _) => action switch
        {
            "browse" => BrowsePage(browseCalls++ % 2 + 1, 2),
            "torrentgroup" => GroupDetail([Torrent(1, "CD", "FLAC", "Lossless", 5)]),
            _ => throw new InvalidOperationException(),
        });
        var finding = await fixture.Catalog.SearchAsync("red", [new ReleaseName("Artist", "Album")],
            "WEB", 1, "album", CancellationToken.None);

        Assert.NotNull(finding);
        Assert.Equal(TrackerDecision.CandidateExistingGroup, finding.Decision);
        Assert.Equal(4, fixture.Calls.Count(c => c.Action == "browse"));
        Assert.Equal(1, fixture.Calls.Count(c => c.Action == "torrentgroup"));
        Assert.All(finding.QueryCoverage, q => { Assert.Equal(2, q.Pages); Assert.Equal(2, q.Results); });
    }

    [Fact]
    public async Task MoreThanTenMatchedGroupsStayUnknownAfterTenDetails()
    {
        using var fixture = new Fixture((action, _, value) => action switch
        {
            "browse" => BrowseGroups(Enumerable.Range(1, 11)),
            "torrentgroup" => GroupDetail([Torrent(1, "CD", "FLAC", "Lossless", 0)], int.Parse(value)),
            _ => throw new InvalidOperationException(),
        });
        var finding = await fixture.Catalog.SearchAsync("ops", [new ReleaseName("Artist", "Album")],
            "WEB", 1, "album", CancellationToken.None);

        Assert.NotNull(finding);
        Assert.Equal(TrackerDecision.Unknown, finding.Decision);
        Assert.Equal(10, fixture.Calls.Count(c => c.Action == "torrentgroup"));
    }

    [Fact]
    public async Task MalformedInventoryAndModeOrGroupChangesRemainUnknown()
    {
        using var fixture = new Fixture(_ => _ switch
        {
            "browse" => BrowseGroup(27),
            "torrentgroup" => GroupDetail(["""{"id":1,"media":"CD"}"""]),
            _ => throw new InvalidOperationException(),
        });
        var malformed = await fixture.Catalog.SearchAsync("red", [new ReleaseName("Artist", "Album")],
            "WEB", 1, "album", CancellationToken.None);

        Assert.NotNull(malformed);
        Assert.Equal(TrackerDecision.Unknown, malformed.Decision);
        Assert.False(malformed.SearchComplete);

        var complete = TrackerFindingForExisting("CD", "FLAC");
        Assert.Equal(TrackerDecision.Unknown,
            TrackerFindingClassifier.Classify(complete, "WEB", "album", "new-group").Decision);
        Assert.Equal(TrackerDecision.Unknown,
            TrackerFindingClassifier.Classify(complete, "WEB", "album", "existing-group", 99).Decision);
        Assert.Equal(TrackerDecision.IgnoreSameMediumFlac,
            TrackerFindingClassifier.Classify(complete, "CD", "album", "existing-group", 99).Decision);
    }

    [Fact]
    public async Task VerifiedSameMediumFlacStillExcludesWhenAnotherGroupIsMalformed()
    {
        using var fixture = new Fixture((action, _, value) => action switch
        {
            "browse" => BrowseGroups([27, 28]),
            "torrentgroup" => int.Parse(value) == 27
                ? GroupDetail([Torrent(1, "CD", "FLAC", "Lossless", 0)], 27)
                : GroupDetail(["""{"id":2,"media":"CD"}"""], 28),
            _ => throw new InvalidOperationException(),
        });
        var finding = await fixture.Catalog.SearchAsync("ops", [new ReleaseName("Artist", "Album")],
            "CD", 1, "album", CancellationToken.None);

        Assert.NotNull(finding);
        Assert.False(finding.SearchComplete);
        Assert.Equal(TrackerDecision.IgnoreSameMediumFlac, finding.Decision);
        Assert.Equal("ignored", finding.Status);
    }


    [Fact]
    public async Task EditionSubtitleIsNotDroppedToEstablishPresence()
    {
        using var fixture = new Fixture(_ => BrowseGroup(27, "Artist", "Album (Deluxe Edition)"));
        var finding = await fixture.Catalog.SearchAsync("red", [new ReleaseName("Artist", "Album")],
            "WEB", 1, "album", CancellationToken.None);

        Assert.NotNull(finding);
        Assert.Equal(TrackerDecision.Unknown, finding.Decision);
        Assert.False(finding.SearchComplete);
        Assert.DoesNotContain(fixture.Calls, c => c.Action == "torrentgroup");
    }

    [Fact]
    public async Task ExactTitleWithWrongArtistOrUnknownTypeStaysUnknown()
    {
        using var wrongArtist = new Fixture(_ => BrowseGroup(27, "Another Artist"));
        var artistFinding = await wrongArtist.Catalog.SearchAsync("red", [new ReleaseName("Artist", "Album")],
            "WEB", 1, "album", CancellationToken.None);
        Assert.NotNull(artistFinding);
        Assert.Equal(TrackerDecision.Unknown, artistFinding.Decision);

        using var unknownType = new Fixture(_ => BrowseGroup(27, "Artist", "Album", "Unrecognized"));
        var typeFinding = await unknownType.Catalog.SearchAsync("ops", [new ReleaseName("Artist", "Album")],
            "CD", 1, "ep", CancellationToken.None);
        Assert.NotNull(typeFinding);
        Assert.Equal(TrackerDecision.Unknown, typeFinding.Decision);
    }

    [Fact]
    public async Task NumericReleaseTypesMapEpFiveAndRejectSoundtrackThree()
    {
        using var ep = new Fixture((action, _, _) => action switch
        {
            "browse" => BrowseGroup(27, "Artist", "Album", "EP"),
            "torrentgroup" => GroupDetail([Torrent(1, "CD", "FLAC", "Lossless", 0)], 27, 5),
            _ => throw new InvalidOperationException(),
        });
        var epFinding = await ep.Catalog.SearchAsync("ops", [new ReleaseName("Artist", "Album")],
            "WEB", 1, "ep", CancellationToken.None);
        Assert.NotNull(epFinding);
        Assert.Equal(TrackerDecision.CandidateExistingGroup, epFinding.Decision);

        using var soundtrack = new Fixture((action, _, _) => action switch
        {
            "browse" => BrowseGroup(27, "Artist", "Album", "EP"),
            "torrentgroup" => GroupDetail([Torrent(1, "CD", "FLAC", "Lossless", 0)], 27, 3),
            _ => throw new InvalidOperationException(),
        });
        var soundtrackFinding = await soundtrack.Catalog.SearchAsync("ops", [new ReleaseName("Artist", "Album")],
            "WEB", 1, "ep", CancellationToken.None);
        Assert.NotNull(soundtrackFinding);
        Assert.Equal(TrackerDecision.Unknown, soundtrackFinding.Decision);
    }

    [Fact]
    public async Task PositivelyNonMusicBrowseRowsDoNotBlockNewGroup()
    {
        using var fixture = new Fixture(_ => BrowseGroup(27, "Artist", "Album", "Album", "Applications", 2));
        var finding = await fixture.Catalog.SearchAsync("red", [new ReleaseName("Artist", "Album")],
            "WEB", 1, "album", CancellationToken.None);

        Assert.NotNull(finding);
        Assert.Equal(TrackerDecision.CandidateNewGroup, finding.Decision);
    }

    [Fact]
    public async Task UnrecognizedCategoryCannotProveAbsenceOrPresence()
    {
        using var fixture = new Fixture(_ => _ switch
        {
            "browse" => BrowseGroup(27, "Artist", "Album", "Album", "Mystery", 77),
            "torrentgroup" => GroupDetail([Torrent(1, "CD", "FLAC", "Lossless", 0)], 27,
                categoryName: "Mystery", categoryId: 77),
            _ => throw new InvalidOperationException(),
        });
        var finding = await fixture.Catalog.SearchAsync("red", [new ReleaseName("Artist", "Album")],
            "WEB", 1, "album", CancellationToken.None);

        Assert.NotNull(finding);
        Assert.Equal(TrackerDecision.Unknown, finding.Decision);
        Assert.False(finding.SearchComplete);
    }

    [Fact]
    public async Task HashLookupUsesOnlyNamedTrackerAndRequiresReturnedHashAndIdentity()
    {
        using var fixture = new Fixture(_ => _ == "torrent"
            ? SourceTorrent("ab".PadRight(40, 'c').ToUpperInvariant(), "WEB", "FLAC")
            : throw new InvalidOperationException());
        var hash = "ab".PadRight(40, 'c');
        var result = await fixture.Catalog.LookupSourceByHashAsync("ops", hash,
            new ReleaseName("Artist", "Album"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Verified);
        Assert.Equal(27, result.GroupId);
        Assert.Single(fixture.Calls);
        Assert.Equal("ops", fixture.Calls[0].Tracker);
        Assert.Equal("torrent", fixture.Calls[0].Action);
        Assert.Equal(hash.ToUpperInvariant(), fixture.Calls[0].Query);
    }

    [Fact]
    public async Task MalformedBrowseCategoryCannotBeRescuedByValidGroupDetail()
    {
        using var fixture = new Fixture(action => action == "browse"
            ? BrowseGroup(27, categoryName: "Mystery", categoryId: 77)
            : GroupDetail([Torrent(1, "CD", "FLAC", "Lossless", 0)]));
        var finding = await fixture.Catalog.SearchAsync("red", [new ReleaseName("Artist", "Album")],
            "WEB", 1, "album", CancellationToken.None);
        Assert.NotNull(finding); Assert.Equal(TrackerDecision.Unknown, finding.Decision);
        Assert.DoesNotContain(fixture.Calls, call => call.Action == "torrentgroup");
    }

    [Fact]
    public async Task HashLookupWithoutExactReturnedHashIsUnknown()
    {
        using var fixture = new Fixture(_ => _ == "torrent"
            ? SourceTorrent(null, "WEB", "FLAC")
            : throw new InvalidOperationException());
        var result = await fixture.Catalog.LookupSourceByHashAsync("red", "ab".PadRight(40, 'c'),
            new ReleaseName("Artist", "Album"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Verified);
        Assert.Equal("unknown", result.Status);
    }

    [Theory]
    [InlineData("MQA Lossless")]
    [InlineData("Hybrid Lossless")]
    public async Task HashLookupRejectsMqaAndHybridSourceExceptions(string encoding)
    {
        var hash = "ab".PadRight(40, 'c');
        using var fixture = new Fixture(_ => SourceTorrent(hash.ToUpperInvariant(), "WEB", "FLAC", encoding));
        var result = await fixture.Catalog.LookupSourceByHashAsync("ops", hash,
            new ReleaseName("Artist", "Album"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Verified);
        Assert.Equal("unknown", result.Status);
    }

    private static TrackerFinding TrackerFindingForExisting(string medium, string format) => new()
    {
        SearchComplete = true,
        Matches = [new TrackerMatch(27, "Artist", "Album", 0, "", "album",
            [new TrackerTorrent(1, medium, format, "Lossless", 0, false)], true)],
    };

    private static string BrowseGroup(int id, string artist = "Artist", string album = "Album",
        string releaseType = "Album", string? categoryName = null, int? categoryId = null) =>
        BrowseGroups([id], artist, album, releaseType, categoryName, categoryId);

    private static string BrowseGroups(IEnumerable<int> ids, string artist = "Artist", string album = "Album",
        string releaseType = "Album", string? categoryName = null, int? categoryId = null) => JsonSerializer.Serialize(new
    {
        currentPage = 1, pages = 1,
        results = ids.Select(groupId => new { groupId, groupName = album, artist, releaseType, categoryName, categoryId }),
    });

    private static string BrowsePage(int page, int pages) => JsonSerializer.Serialize(new
    {
        currentPage = page, pages,
        results = new[] { new { groupId = 27, groupName = "Album", artist = "Artist", releaseType = "Album" } },
    });

    private static string EmptyBrowse() => """{"results":[]}""";

    private static string GroupDetail(IEnumerable<string> torrents, int groupId = 27, int releaseType = 1,
        string categoryName = "Music", int categoryId = 1) =>
        "{\"group\":{\"id\":" + groupId + ",\"name\":\"Album\",\"categoryId\":" + categoryId + ",\"categoryName\":" + JsonSerializer.Serialize(categoryName) + ",\"releaseType\":" + releaseType + "," +
        "\"musicInfo\":{\"artists\":[{\"name\":\"Artist\"}]}},\"torrents\":[" + string.Join(",", torrents) + "]}";

    private static string Torrent(int id, string medium, string format, string encoding, int seeders) =>
        JsonSerializer.Serialize(new { id, media = medium, format, encoding, seeders, scene = false });

    private static string SourceTorrent(string? hash, string medium, string format, string? encoding = null)
    {
        var torrent = new Dictionary<string, object?> { ["id"] = 101, ["media"] = medium, ["format"] = format };
        if (hash is not null) torrent["infoHash"] = hash;
        if (encoding is not null) torrent["encoding"] = encoding;
        return JsonSerializer.Serialize(new
        {
            group = new
            {
                id = 27, name = "Album", categoryId = 1, categoryName = "Music", releaseType = 1,
                musicInfo = new { artists = new[] { new { name = "Artist" } } },
            },
            torrent,
        });
    }

    private sealed class Fixture : IDisposable, IHttpClientFactory
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-catalog-" + Guid.NewGuid().ToString("N"));
        private readonly Handler _handler;
        private readonly IConfiguration _config;
        private readonly TrackerDiscoveryTests.AdvancingClock _clock = new();
        public List<(string Tracker, string Action, string Query)> Calls => _handler.Calls;
        public TrackerCatalogClient Catalog { get; }

        public Fixture(Func<string, string> response) : this((action, _, _) => response(action)) { }

        public Fixture(Func<string, string, string, string> response)
        {
            Directory.CreateDirectory(_root);
            _handler = new Handler(response);
            _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trackers:red:ApiKey"] = "red-key", ["Trackers:ops:ApiKey"] = "ops-key",
            }).Build();
            var queue = new TrackerDirectQueue(Path.Combine(_root, "cooldowns.json"), _config, this, _clock);
            Catalog = new TrackerCatalogClient(queue, _clock);
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        public void Dispose() { _handler.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }

        private sealed class Handler(Func<string, string, string, string> response) : HttpMessageHandler
        {
            public List<(string Tracker, string Action, string Query)> Calls { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
                var tracker = request.RequestUri.Host == "orpheus.network" ? "ops" : "red";
                var action = query["action"] ?? "";
                var search = query["searchstr"] ?? query["id"] ?? query["hash"] ?? "";
                Calls.Add((tracker, action, search));
                var body = "{\"status\":\"success\",\"response\":" + response(action, tracker, search) + "}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
        }
    }
}
