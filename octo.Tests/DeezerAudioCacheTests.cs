using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Deezer;
using Octo.Services.Metadata;
using Octo.Services.Soulseek;

namespace Octo.Tests;

public sealed class DeezerAudioCacheTests
{
    internal static byte[] MinimalFlacSample()
        => Convert.FromBase64String("ZkxhQ4AAACIAoACgAAAMAAAMAfQA8AAAAKAE28ZtjkVZdpuzipLNb32d//hkCACfNwAAAEEt");

    [Fact]
    public async Task DownloadsStrictFlacAtomicallyAndPersistsLastPlayedTime()
    {
        using var fixture = new Fixture();
        var song = Song();

        var path = await fixture.Cache.EnsureAsync(song);

        Assert.Equal(Path.Combine(fixture.Root, "42.flac"), path);
        Assert.Equal(fixture.Payload, await File.ReadAllBytesAsync(path!));
        Assert.True(DeezerAudioCache.IsValidFlac(path!));
        Assert.Equal(["FLAC"], fixture.RequestedFormats);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, ".staging"), "*.tmp"));

        await fixture.Cache.MarkPlayedAsync(song);
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "cache-index.json")));
        Assert.NotEqual(JsonValueKind.Null, state.RootElement.GetProperty("tracks").GetProperty("42")
            .GetProperty("lastPlayedUtc").ValueKind);
    }

    [Fact]
    public async Task ResolvesAndStoresKnownRegistryDeezerId()
    {
        using var fixture = new Fixture();
        var id = fixture.Ids.Register(new SoulseekRouting
        {
            Artist = "Artist", Title = "Title", DeezerId = "42",
        });
        var song = new Song
        {
            Id = id, ExternalId = id, ExternalProvider = "soulseek",
            Artist = "Artist", Title = "Title", Album = "Album",
        };

        var path = await fixture.Cache.EnsureAsync(song);

        Assert.NotNull(path);
        Assert.Equal("42", song.DeezerId);
        Assert.Equal(["42"], fixture.CdnRequestIds.ToArray());
    }

    [Fact]
    public async Task InterruptedDownloadLeavesNoPublishedOrStagingFile()
    {
        using var fixture = new Fixture { AdvertisedExtraBytes = 5 };

        var path = await fixture.Cache.EnsureAsync(Song());

        Assert.Null(path);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "42.flac")));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, ".staging"), "*.tmp"));
    }

    [Fact]
    public async Task FullSizeFlacWithCorruptFrameIsRejectedBeforePublish()
    {
        using var fixture = new Fixture { CorruptCdn = true };

        var path = await fixture.Cache.EnsureAsync(Song());

        Assert.Null(path);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "42.flac")));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, ".staging"), "*.tmp"));
    }

    [Fact]
    public async Task RestartedCacheEvictsByPersistedLastPlayedTime()
    {
        using var fixture = new Fixture();
        var path = await fixture.Cache.EnsureAsync(Song());
        await fixture.Cache.MarkPlayedAsync(Song());
        var indexPath = Path.Combine(fixture.Root, "cache-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["tracks"]!["42"]!["lastPlayedUtc"] = DateTime.UtcNow.AddDays(-8);
        await File.WriteAllTextAsync(indexPath, index.ToJsonString());

        using var restarted = new DeezerAudioCache(fixture.Resolver, fixture.Catalog,
            fixture.Ids, fixture.Settings, NullLogger<DeezerAudioCache>.Instance);
        await restarted.CleanupAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CancellingOneWaiterLeavesSharedDownloadRunningForOtherWaiter()
    {
        using var fixture = new Fixture { HoldCdn = true };
        using var cancellation = new CancellationTokenSource();
        var song = Song();

        var first = fixture.Cache.EnsureAsync(song, cancellationToken: cancellation.Token);
        await fixture.CdnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Cache.EnsureAsync(song);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        fixture.ReleaseCdn.Release(2);

        var path = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(1, fixture.CdnRequests);
    }

    [Fact]
    public async Task PrewarmUsesKnownIdsAndRunsAtMostTwoTransfersWithPlaybackPriority()
    {
        using var fixture = new Fixture { HoldCdn = true };
        var speculativeSongs = new[] { Song("42"), Song("43"), Song("44"), Song("46") };

        var prewarm = fixture.Cache.PrewarmAsync(speculativeSongs, topN: 4);
        await fixture.TwoCdnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.ActiveCdnRequests);
        Assert.Equal(2, fixture.MaximumConcurrentCdnRequests);

        var playback = fixture.Cache.EnsureAsync(Song("45"));
        fixture.ReleaseCdn.Release();
        await fixture.Started("45").Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("45", fixture.CdnRequestIds.Take(3));

        fixture.ReleaseCdn.Release(8);
        await Task.WhenAll(prewarm, playback).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(fixture.MaximumConcurrentCdnRequests, 1, 2);
    }

    [Fact]
    public async Task ReaderLeaseAndPinsProtectFilesFromSizeEviction()
    {
        using var fixture = new Fixture();
        var song = Song();
        var path = await fixture.Cache.EnsureAsync(song);
        using var reader = fixture.Cache.OpenRead(song)!;
        fixture.Settings.Set(SettingsWithCache(fixture.Settings.CurrentValue, maxGiB: 0));

        await fixture.Cache.CleanupAsync(CancellationToken.None);
        Assert.True(File.Exists(path));
        reader.Dispose();
        await fixture.Cache.CleanupAsync(CancellationToken.None);
        Assert.False(File.Exists(path));

        fixture.Settings.Set(SettingsWithCache(fixture.Settings.CurrentValue, maxGiB: 20));
        await fixture.Cache.EnsureAsync(song);
        await fixture.Cache.PinAsync(song, "heart:user:one");
        fixture.Settings.Set(SettingsWithCache(fixture.Settings.CurrentValue, maxGiB: 0));
        await fixture.Cache.CleanupAsync(CancellationToken.None);
        Assert.True(File.Exists(path));
        await fixture.Cache.UnpinAsync("42", "heart:user:one");
        await fixture.Cache.CleanupAsync(CancellationToken.None);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ProtectedFillsUseBothTransferSlots()
    {
        using var fixture = new Fixture { HoldCdn = true };
        await fixture.Cache.ReplacePinsAsync(new[] { (Song("42"), "heart/alice"), (Song("43"), "heart/bob") });
        await fixture.Cache.StartAsync(CancellationToken.None);
        try
        {
            await fixture.TwoCdnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, fixture.MaximumConcurrentCdnRequests);
            fixture.ReleaseCdn.Release(2);
            await Task.WhenAll(fixture.Cache.EnsureAsync(Song("42")), fixture.Cache.EnsureAsync(Song("43")));
        }
        finally { await fixture.Cache.StopAsync(CancellationToken.None); }
    }

    private static Song Song() => new()
    {
        Id = "virtual-song", ExternalId = "virtual-song", ExternalProvider = "soulseek",
        DeezerId = "42", Artist = "Artist", Title = "Title", Album = "Album", Duration = 180,
    };

    private static Song Song(string deezerId) => new()
    {
        Id = "virtual-song-" + deezerId, ExternalId = "virtual-song-" + deezerId,
        ExternalProvider = "soulseek", DeezerId = deezerId, Artist = "Artist",
        Title = "Title " + deezerId, Album = "Album", Duration = 180,
    };

    private static DeezerSettings SettingsWithCache(DeezerSettings settings, double maxGiB) => new()
    {
        Arl = settings.Arl, ArlFallback = settings.ArlFallback, Quality = settings.Quality,
        CacheEnabled = settings.CacheEnabled, CachePath = settings.CachePath,
        CacheMaxGiB = maxGiB, CacheRetentionDays = settings.CacheRetentionDays,
    };

    internal sealed class Fixture : HttpMessageHandler, IHttpClientFactory, IDisposable
    {
        private static readonly byte[] Audio = MinimalFlacSample();
        private readonly DeezerMetadataService _catalog;
        private readonly ExternalIdRegistry _ids;
        private readonly string _registryPath;

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "octo-deezer-cache-test-" + Guid.NewGuid().ToString("N"));
        public byte[] Payload => Audio;
        public TestOptionsMonitor<DeezerSettings> Settings { get; }
        public DeezerAudioCache Cache { get; }
        public DeezerResolver Resolver { get; }
        public ExternalIdRegistry Ids => _ids;
        public DeezerMetadataService Catalog => _catalog;
        public TaskCompletionSource CdnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoCdnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim ReleaseCdn { get; } = new(0);
        public bool HoldCdn { get; init; }
        public bool CorruptCdn { get; init; }
        public int AdvertisedExtraBytes { get; init; }
        public int CdnRequests;
        public int ActiveCdnRequests;
        public int MaximumConcurrentCdnRequests;
        public System.Collections.Concurrent.ConcurrentQueue<string> CdnRequestIds { get; } = new();
        public string[] RequestedFormats = [];
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _startedById = new();

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Deezer:Arl"] = "primary" }).Build();
            _catalog = new DeezerMetadataService(this, TestOptions.Monitor(new MetadataSettings()),
                NullLogger<DeezerMetadataService>.Instance);
            Resolver = new DeezerResolver(this, config, NullLogger<DeezerResolver>.Instance, _catalog);
            _registryPath = Path.Combine(Root, "external-ids.json");
            _ids = new ExternalIdRegistry(_registryPath, NullLogger<ExternalIdRegistry>.Instance);
            Settings = TestOptions.Monitor(new DeezerSettings
            {
                Arl = "primary", CacheEnabled = true, CachePath = Root,
                CacheMaxGiB = 20, CacheRetentionDays = 7,
            });
            Cache = new DeezerAudioCache(Resolver, _catalog, _ids, Settings,
                NullLogger<DeezerAudioCache>.Instance);
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("deezer.getUserData"))
            {
                var response = Json(new { results = new { checkForm = "api", USER = new
                    { USER_ID = 7, OPTIONS = new { license_token = "license" } } } });
                response.Headers.Add("Set-Cookie", "sid=session; Path=/; HttpOnly");
                return response;
            }
            if (url.Contains("deezer.pageTrack"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var id = body.RootElement.GetProperty("SNG_ID").GetString()!;
                return Json(new { results = new { DATA = new { SNG_ID = id, TRACK_TOKEN = "track-token-" + id } } });
            }
            if (request.RequestUri.Host == "media.deezer.com")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                RequestedFormats = body.RootElement.GetProperty("media")[0].GetProperty("formats")
                    .EnumerateArray().Select(item => item.GetProperty("format").GetString()!).ToArray();
                var token = body.RootElement.GetProperty("track_tokens")[0].GetString()!;
                var id = token["track-token-".Length..];
                return Json(new { data = new[] { new { media = new[] { new { format = "FLAC",
                    sources = new[] { new { url = $"https://cdn.deezer.test/{id}.flac" } } } } } } });
            }
            Assert.Equal("cdn.deezer.test", request.RequestUri.Host);
            Interlocked.Increment(ref CdnRequests);
            var pathId = Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath);
            CdnRequestIds.Enqueue(pathId);
            _startedById.GetOrAdd(pathId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
            var active = Interlocked.Increment(ref ActiveCdnRequests);
            UpdateMaximum(active);
            CdnStarted.TrySetResult();
            if (active >= 2) TwoCdnStarted.TrySetResult();
            try
            {
                if (HoldCdn) await ReleaseCdn.WaitAsync(ct);
                var audio = Audio.ToArray();
                if (CorruptCdn) audio[^1] ^= 0xff;
                var content = new ByteArrayContent(audio);
                content.Headers.ContentLength = audio.Length + AdvertisedExtraBytes;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            finally { Interlocked.Decrement(ref ActiveCdnRequests); }
        }

        public TaskCompletionSource Started(string id) => _startedById.GetOrAdd(id,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var maximum = Volatile.Read(ref MaximumConcurrentCdnRequests);
                if (maximum >= value || Interlocked.CompareExchange(ref MaximumConcurrentCdnRequests, value, maximum) == maximum)
                    return;
            }
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value)) };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Cache.Dispose();
                Resolver.Dispose();
                _catalog.Dispose();
                _ids.Dispose();
                ReleaseCdn.Dispose();
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                if (File.Exists(_registryPath)) File.Delete(_registryPath);
            }
            base.Dispose(disposing);
        }
    }
}
