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
