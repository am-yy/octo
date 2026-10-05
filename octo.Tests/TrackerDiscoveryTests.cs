using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Lidarr;
using Octo.Services.Subsonic;
using Octo.Services.Trackers;

namespace Octo.Tests;

public sealed class TrackerDiscoveryTests
{
    [Fact]
    public async Task AcceptedAlbumSearchWithoutGrabRemainsEligibleForDiscovery()
    {
        using var f = new Fixture();
        await f.SaveSongAsync();
        await f.Saves.AssociateAcquisitionWithAlbumAsync("deezer", "42", "release-group");
        await f.Saves.MarkAlbumSearchSubmittedAsync("release-group", 7);
        await f.Service.RefreshAsync();
        Assert.Equal("unresolved", Assert.Single(await f.Service.ListAsync()).Acquisition);
        Assert.Equal(4, f.TrackerRequests);
    }

    [Fact]
    public async Task GrabClosureSurvivesClockRestartMetadataAndNewSavedTrack()
    {
        using var f = new Fixture { Grabbed = true };
        await f.SaveSongAsync();
        await f.Service.RefreshAsync();
        Assert.Equal("grabbed", Assert.Single(await f.Service.ListAsync()).Acquisition);
        f.Clock.Advance(TimeSpan.FromDays(2));
        f.Restart();
        await f.Service.ListAsync(); // Admin page reads cannot reopen closure.
        await f.SaveSongAsync("43");
        await f.SaveSongAsync("42", "Renamed album");
        f.Grabbed = false; // Even pruned history and disappeared queue must not reopen it.
        await f.Service.RefreshAsync();
        Assert.Equal(0, f.TrackerRequests);
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("grabbed", row.Acquisition);
        await f.Saves.SetHeartAsync("listener", f.Song("42", "Renamed album"), false);
        await f.Saves.SetHeartAsync("listener", f.Song("43"), false);
        await f.Service.RefreshAsync();
        await f.SaveSongAsync("44");
        await f.Service.RefreshAsync();
        Assert.Equal(0, f.TrackerRequests);
        await f.Service.RecheckAsync(row.Key);
        await f.Service.RefreshAsync();
        Assert.True(f.TrackerRequests > 0);
        var after = f.TrackerRequests;
        f.Clock.Advance(TimeSpan.FromDays(2));
        await f.Service.RefreshAsync();
        Assert.Equal(after, f.TrackerRequests); // Recheck is one explicit pass, not unsuppression forever.
    }

    [Theory]
    [InlineData(2, "complete", 0)]
    [InlineData(1, "unresolved", 4)]
    public async Task InitialReconciliationRequiresWholeRelease(int files, string acquisition, int requests)
    {
        using var f = new Fixture { ImportedFiles = files };
        await f.SaveSongAsync(local: true);
        await f.Service.RefreshAsync();
        Assert.Equal(acquisition, Assert.Single(await f.Service.ListAsync()).Acquisition);
        Assert.Equal(requests, f.TrackerRequests);
    }

    [Fact]
    public async Task ImportedHeartAliasRemainsVisibleAndCompleteRecheckIsOnlyOneExplicitPass()
    {
        using var f = new Fixture { ImportedFiles = 1 };
        await f.SaveSongAsync();
        await f.Service.RefreshAsync();
        var key = Assert.Single(await f.Service.ListAsync()).Key;
        var path = Path.ChangeExtension(f.CooldownPath, ".flac");
        await File.WriteAllBytesAsync(path, "fLaC"u8.ToArray());
        await f.Saves.MarkImportedAsync("deezer", "42", "local-17", path);
        f.Clock.Advance(TimeSpan.FromDays(2));
        f.Restart();
        await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal(key, row.Key);
        Assert.True(row.Saved);
        Assert.Contains("local-17", row.ReferenceIds);
        Assert.Equal("unresolved", row.Acquisition);
        Assert.Equal(8, f.TrackerRequests); // One imported song still permits scheduled album discovery.

        f.ImportedFiles = 2;
        await f.Service.RefreshAsync();
        Assert.Equal("complete", Assert.Single(await f.Service.ListAsync()).Acquisition);
        Assert.Equal(8, f.TrackerRequests);
        await f.Service.RecheckAsync(key);
        await f.Service.RefreshAsync();
        Assert.Equal(12, f.TrackerRequests);
        f.Clock.Advance(TimeSpan.FromDays(2));
        f.Restart();
        await f.SaveSongAsync("43");
        await f.Service.RefreshAsync();
        Assert.Equal(12, f.TrackerRequests);
    }

    [Fact]
    public async Task FailureAfterGrabStaysSuspendedUntilExplicitRecheck()
    {
        using var f = new Fixture { Grabbed = true, Failed = true };
        await f.SaveSongAsync();
        await f.Service.RefreshAsync();
        Assert.Equal("failed", Assert.Single(await f.Service.ListAsync()).Acquisition);
        f.Clock.Advance(TimeSpan.FromDays(2));
        f.Restart();
        await f.Service.RefreshAsync();
        Assert.Equal(0, f.TrackerRequests);
    }

    [Fact]
    public async Task NewlyGrabbedReleaseDropsRemainingQueuedSearches()
    {
        using var f = new Fixture();
        await f.SaveSongAsync();
        f.OnTrackerRequest = () => f.Grabbed = true;
        await f.Service.RefreshAsync();
        Assert.Equal(1, f.TrackerRequests);
        Assert.Equal("grabbed", Assert.Single(await f.Service.ListAsync()).Acquisition);
    }

    [Fact]
    public async Task UnavailableInitialReconciliationCannotProduceDiscoveryRequests()
    {
        using var f = new Fixture { LidarrUnavailable = true };
        await f.SaveSongAsync();
        await f.Service.RefreshAsync();
        Assert.Equal(0, f.TrackerRequests);
        Assert.NotNull(Assert.Single(await f.Service.ListAsync()).ReconciliationError);
    }

    [Theory]
    [InlineData("[null]", 1, "unknown")]
    [InlineData("[]", 21, "unknown")]
    [InlineData("[{\"artist\":\"Other artist\",\"groupName\":\"Album\",\"groupId\":4,\"torrents\":[{}]}]", 1, "unknown")]
    [InlineData("[{\"artist\":\"Artist\",\"groupName\":\"Album\",\"groupId\":4,\"torrents\":[{\"format\":\"MP3\",\"seeders\":0}]}]", 1, "present")]
    [InlineData("[{\"artist\":{},\"groupName\":\"Other title\"}]", 1, "unknown")]
    [InlineData("[]", 2, "unknown")]
    [InlineData("[{\"artist\":\"Other\",\"groupName\":\"Other title\"}]", 2, "missing")]
    public async Task OnlyExhaustiveUnambiguousResponsesEstablishAbsence(string results, int pages, string expected)
    {
        using var f = new Fixture { Results = results, Pages = pages };
        await f.SaveSongAsync();
        await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal(expected, row.Red.Status);
        Assert.Equal(expected, row.Ops.Status);
        if (pages == 2 && expected == "missing") Assert.Equal(8, f.TrackerRequests);
    }

    [Theory]
    [InlineData("{\"results\":[],\"youMightLike\":[]}", "missing", 4)]
    [InlineData("{\"results\":[],\"pages\":1}", "unknown", 2)]
    [InlineData("{\"results\":[],\"currentPage\":1}", "unknown", 2)]
    [InlineData("{\"results\":[],\"pages\":null,\"currentPage\":null}", "unknown", 2)]
    [InlineData("{\"results\":[{\"artist\":\"Other\",\"groupName\":\"Other\"}]}", "unknown", 2)]
    public async Task GazelleEmptyFirstPageWithoutPaginationIsComplete(string body, string status, int requests)
    {
        using var f = new Fixture { BrowseBody = _ => body };
        await f.SaveSongAsync();
        await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal(status, row.Red.Status);
        Assert.Equal(status, row.Ops.Status);
        Assert.Equal(requests, f.TrackerRequests);
    }

    [Fact]
    public async Task EmptyLaterPageWithoutPaginationCannotEstablishAbsence()
    {
        using var f = new Fixture { BrowseBody = page => page == 1
            ? """{"results":[{"artist":"Other","groupName":"Other"}],"pages":2,"currentPage":1}"""
            : """{"results":[],"youMightLike":[]}""" };
        await f.SaveSongAsync();
        await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("unknown", row.Red.Status);
        Assert.Equal("unknown", row.Ops.Status);
    }

    [Theory]
    [InlineData(1, 11)]
    [InlineData(30, 31)]
    [InlineData(null, 60)]
    public async Task RetryAfterPreservesMinimumAndPersistsAcrossRestart(int? retry, int minimum)
    {
        using var f = new Fixture { RateLimited = true, RetryAfter = retry };
        var start = f.Clock.GetUtcNow();
        await Assert.ThrowsAsync<TrackerRequestRejectedException>(() => f.Queue.GetAsync("red", "browse", new Dictionary<string, string>(), CancellationToken.None));
        var cooldowns = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(await File.ReadAllTextAsync(f.CooldownPath))!;
        Assert.True(cooldowns["red"] >= start.AddSeconds(minimum));
        f.RateLimited = false;
        f.Restart();
        await f.Queue.GetAsync("red", "browse", new Dictionary<string, string>(), CancellationToken.None);
        Assert.True(f.RequestTimes.Last() >= start.AddSeconds(minimum));
    }

    [Fact]
    public async Task ConcurrentCallersShareOneLaneAndMinimumSpacing()
    {
        using var f = new Fixture();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Queue.GetAsync("red", "browse", new Dictionary<string, string>(), CancellationToken.None)));
        Assert.Equal(3, f.RequestTimes.Count);
        Assert.All(f.RequestTimes.Zip(f.RequestTimes.Skip(1)), p => Assert.True(p.Second - p.First >= TimeSpan.FromSeconds(11)));
    }

    internal sealed class AdvancingClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
        public Action? OnDelay { get; set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            OnDelay?.Invoke();
            Advance(dueTime);
            return new TimerHandle(callback, state);
        }
        private sealed class TimerHandle : ITimer
        {
            private readonly Timer _timer;
            public TimerHandle(TimerCallback callback, object? state) => _timer = new(callback, state, 1, Timeout.Infinite);
            public bool Change(TimeSpan dueTime, TimeSpan period) => _timer.Change(dueTime, period);
            public void Dispose() => _timer.Dispose();
            public ValueTask DisposeAsync() => _timer.DisposeAsync();
        }
    }

    private sealed class Fixture : HttpMessageHandler, IHttpClientFactory
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-tracker-" + Guid.NewGuid().ToString("N"));
        private readonly IConfiguration _config;
        private readonly LidarrClient _lidarr;
        public AdvancingClock Clock { get; } = new();
        public ExternalSaveStore Saves { get; }
        public TrackerDirectQueue Queue { get; private set; } = null!;
        public TrackerOpportunityService Service { get; private set; } = null!;
        public string CooldownPath => Path.Combine(_root, "cooldowns.json");
        public int TrackerRequests { get; private set; }
        public List<DateTimeOffset> RequestTimes { get; } = [];
        public bool RateLimited { get; set; }
        public int? RetryAfter { get; init; } = 1;
        public bool Grabbed { get; set; }
        public bool Failed { get; init; }
        public int ImportedFiles { get; set; }
        public bool LidarrUnavailable { get; init; }
        public Action? OnTrackerRequest { get; set; }
        public string Results { get; init; } = "[]";
        public int Pages { get; init; } = 1;
        public Func<int, string>? BrowseBody { get; init; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Saves = new ExternalSaveStore(Path.Combine(_root, "saves.json"), NullLogger<ExternalSaveStore>.Instance);
            _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trackers:red:ApiKey"] = "fake-red-key", ["Trackers:ops:ApiKey"] = "fake-ops-key",
            }).Build();
            _lidarr = new LidarrClient(this, TestOptions.Monitor(new LidarrSettings { BaseUrl = "http://lidarr.invalid", ApiKey = "fake-lidarr-key" }));
            Restart();
        }
        public void Restart()
        {
            Service?.Dispose();
            Queue = new TrackerDirectQueue(CooldownPath, _config, this, Clock);
            Service = new TrackerOpportunityService(Saves, _lidarr, Queue, Path.Combine(_root, "opportunities.json"), NullLogger<TrackerOpportunityService>.Instance, Clock);
        }
        public Song Song(string id = "42", string album = "Album", bool local = false) => new()
        {
            Id = "ext-deezer-" + id, ExternalProvider = "deezer", ExternalId = id,
            Artist = "Artist", Album = album, Title = "Track", IsLocal = local,
        };
        public Task SaveSongAsync(string id = "42", string album = "Album", bool local = false) => Saves.SetHeartAsync("listener", Song(id, album, local), true);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "lidarr.invalid")
            {
                var path = request.RequestUri.AbsolutePath;
                string content = path switch
                {
                    "/api/v1/album" => """[{"id":7,"foreignAlbumId":"release-group","title":"Album","artist":{"artistName":"Artist"}}]""",
                    "/api/v1/album/7" => JsonSerializer.Serialize(new { id = 7, statistics = new { trackCount = 2, trackFileCount = ImportedFiles } }),
                    "/api/v1/track" => JsonSerializer.Serialize(Enumerable.Range(1, 2).Select(i => new { id = i, title = "Track " + i, trackNumber = i.ToString(), trackFileId = i <= ImportedFiles ? i : 0, hasFile = i <= ImportedFiles })),
                    "/api/v1/trackFile" => JsonSerializer.Serialize(Enumerable.Range(1, ImportedFiles).Select(i => new { id = i, path = "/music/Artist/Album/" + i + ".flac", size = 100 })),
                    "/api/v1/history" => Grabbed
                        ? JsonSerializer.Serialize(new { totalRecords = 1, records = new[] { new { albumId = 7, eventType = Failed && request.RequestUri.Query.Contains("eventType=4") ? "downloadFailed" : "grabbed", downloadId = "hash" } } })
                        : """{"totalRecords":0,"records":[]}""",
                    _ => "[]",
                };
                return Task.FromResult(new HttpResponseMessage(LidarrUnavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(content) });
            }
            TrackerRequests++;
            RequestTimes.Add(Clock.GetUtcNow());
            OnTrackerRequest?.Invoke();
            var page = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["page"] ?? "1";
            var body = BrowseBody?.Invoke(int.Parse(page)) ?? "{\"currentPage\":" + page + ",\"pages\":" + Pages + ",\"results\":" + Results + "}";
            var response = new HttpResponseMessage(RateLimited ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            { Content = new StringContent("{\"status\":\"success\",\"response\":" + body + "}") };
            if (RateLimited && RetryAfter is int retry) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retry));
            return Task.FromResult(response);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Service.Dispose(); Directory.Delete(_root, true); }
            base.Dispose(disposing);
        }
    }
}
