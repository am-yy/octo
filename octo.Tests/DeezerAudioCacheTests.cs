using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
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
        // Failure wakes callers before the producer's finally block removes staging.
        var staging = Path.Combine(fixture.Root, ".staging");
        var cleanup = Stopwatch.StartNew();
        while (Directory.GetFiles(staging, "*.tmp").Length != 0 && cleanup.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Assert.Empty(Directory.GetFiles(staging, "*.tmp"));
    }

    [Fact]
    public async Task ProbeReportsSelectedSourceWithoutStartingCacheFill()
    {
        using var fixture = new Fixture();

        var source = await fixture.Cache.ProbeAsync(Song());

        Assert.NotNull(source);
        Assert.Equal("audio/flac", source.ContentType);
        Assert.Equal(fixture.Payload.Length, source.ExpectedLength);
        Assert.Equal(0, fixture.CdnRequests);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "42.flac")));
    }

    [FfmpegFact]
    public async Task Mp3CacheRequestsOnlyMp3_320AndPublishesMp3Path()
    {
        var mp3 = await CreateAudioAsync("mp3");
        using var fixture = new Fixture("MP3_320", DeezerDecryptedStreamTests.EncryptStripes(mp3, "1234567890"));

        var path = await fixture.Cache.EnsureAsync(Song("1234567890"));

        Assert.Equal(Path.Combine(fixture.Root, "1234567890.mp3"), path);
        Assert.Equal(mp3, await File.ReadAllBytesAsync(path!));
        Assert.Equal(["MP3_320"], fixture.RequestedFormats);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "1234567890.flac")));
    }

    [FfmpegFact]
    public async Task SwitchingCacheQualityRetainsBothTrackLevelPinnedSources()
    {
        using var fixture = new Fixture();
        var song = Song("1234567890");
        var flacPath = await fixture.Cache.EnsureAsync(song);
        var flac = await File.ReadAllBytesAsync(flacPath!);

        var mp3 = await CreateAudioAsync("mp3");
        fixture.Payload = DeezerDecryptedStreamTests.EncryptStripes(mp3, "1234567890");
        fixture.Settings.Set(SettingsWithCache(fixture.Settings.CurrentValue, cacheQuality: "MP3_320"));
        using var switched = new DeezerAudioCache(fixture.Resolver, fixture.Catalog, fixture.Ids,
            fixture.Settings, NullLogger<DeezerAudioCache>.Instance);
        var mp3Path = await switched.EnsureAsync(song);

        Assert.Equal("FLAC", fixture.Cache.SourceQuality);
        Assert.Equal("MP3_320", switched.SourceQuality);
        Assert.Equal(flac, await File.ReadAllBytesAsync(flacPath!));
        Assert.Equal(mp3, await File.ReadAllBytesAsync(mp3Path!));
        await switched.PinAsync(song, "heart:user:quality");
        fixture.Settings.Set(SettingsWithCache(fixture.Settings.CurrentValue, maxGiB: 0));
        await switched.CleanupAsync(CancellationToken.None);
        Assert.True(File.Exists(flacPath));
        Assert.True(File.Exists(mp3Path));
        await switched.UnpinAsync("1234567890", "heart:user:quality");
        await switched.CleanupAsync(CancellationToken.None);
        Assert.False(File.Exists(flacPath));
        Assert.False(File.Exists(mp3Path));
    }

    [FfmpegFact]
    public async Task ProgressiveLeaseStreamsFlacBeforeValidatedCachePublication()
    {
        var flac = await CreateAudioAsync("flac");
        using var fixture = new Fixture(payload: DeezerDecryptedStreamTests.EncryptStripes(flac, "1234567890"))
            { HoldAfterBytes = 2048 };
        await using var lease = (await fixture.Cache.OpenProgressiveAsync(Song("1234567890")))!;
        var received = new MemoryStream();
        var first = new byte[4096];

        var count = await lease.Stream.ReadAsync(first).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        received.Write(first, 0, count);
        await fixture.CdnHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(count > 0);
        Assert.False(lease.Completion.IsCompleted);
        Assert.Null(lease.ReadyPath);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "1234567890.flac")));

        fixture.ReleaseCdn.Release();
        await lease.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await lease.Stream.CopyToAsync(received);
        Assert.Equal(flac, received.ToArray());
        Assert.True(File.Exists(Path.Combine(fixture.Root, "1234567890.flac")));
    }

    private static async Task<byte[]> CreateAudioAsync(string format)
    {
        var path = Path.Combine(Path.GetTempPath(), $"octo-source-{Guid.NewGuid():N}.{format}");
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg", UseShellExecute = false, RedirectStandardError = true,
            CreateNoWindow = true,
        } };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "sine=frequency=440:duration=2", "-map", "0:a:0", "-c:a",
            format == "mp3" ? "libmp3lame" : "flac", "-b:a", "320k", "-y", path })
            process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        try { return await File.ReadAllBytesAsync(path); }
        finally { File.Delete(path); }
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
        Assert.InRange(fixture.MaximumConcurrentCdnRequests, 1, 3);
    }

    [Fact]
    public async Task QueuedSpeculativeFillPromotesToForegroundCapacity()
    {
        using var fixture = new Fixture { HoldCdn = true };
        var prewarm = fixture.Cache.PrewarmAsync(
            [Song("42"), Song("43"), Song("44")], topN: 3);
        await fixture.TwoCdnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Started("44").Task.IsCompleted);

        var playback = fixture.Cache.EnsureAsync(Song("44"), DeezerCachePriority.Playback);
        await fixture.Started("44").Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.ReleaseCdn.Release(12);
        await Task.WhenAll(prewarm, playback).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("44", fixture.CdnRequestIds.Take(3));
    }

    [Fact]
    public async Task PlaybackPreemptsUnusedSpeculativeAttemptWhenAllDownloadSlotsAreBusy()
    {
        using var fixture = new Fixture { HoldCdn = true };
        var prewarm = fixture.Cache.PrewarmAsync([Song("42"), Song("43")], topN: 2);
        await fixture.TwoCdnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var foreground1 = fixture.Cache.EnsureAsync(Song("45"), DeezerCachePriority.Playback);
        var foreground2 = fixture.Cache.EnsureAsync(Song("46"), DeezerCachePriority.Playback);
        await Task.WhenAll(fixture.Started("45").Task, fixture.Started("46").Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, fixture.ActiveCdnRequests);

        var urgent = fixture.Cache.EnsureAsync(Song("44"), DeezerCachePriority.Playback);
        await fixture.Started("44").Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("44", fixture.CdnRequestIds);

        fixture.ReleaseCdn.Release(20);
        await Task.WhenAll(prewarm, foreground1, foreground2, urgent).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.InRange(fixture.MaximumConcurrentCdnRequests, 1, 4);
    }

    [Fact]
    public async Task EncodedLibraryVariantUsesContentFingerprintWithoutDeezerTrackId()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, ".staging", "encoded.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = "encoded library source"u8.ToArray();
        await File.WriteAllBytesAsync(path, content);

        await fixture.Cache.RegisterEncodedVariantAsync("library-profile", "", path, DateTime.UtcNow);

        using var lease = fixture.Cache.OpenEncodedRead(path)!;
        Assert.Equal("sha256-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))
            .ToLowerInvariant(), lease.Fingerprint);
        Assert.Equal(content, await ReadAllAsync(lease.Stream));
    }

    [Fact]
    public async Task ServiceStartupRemovesOrphanedStagingFiles()
    {
        using var fixture = new Fixture();
        var orphan = Path.Combine(fixture.Root, ".staging", "encode-abandoned.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        await File.WriteAllTextAsync(orphan, "partial");

        await fixture.Cache.StartAsync(CancellationToken.None);
        try { Assert.False(File.Exists(orphan)); }
        finally { await fixture.Cache.StopAsync(CancellationToken.None); }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
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

    private static DeezerSettings SettingsWithCache(DeezerSettings settings, double maxGiB = 20,
        string? cacheQuality = null) => new()
    {
        Arl = settings.Arl, ArlFallback = settings.ArlFallback, Quality = settings.Quality,
        CacheQuality = cacheQuality ?? settings.CacheQuality,
        MaxConcurrentDownloads = settings.MaxConcurrentDownloads,
        MaxConcurrentBackgroundDownloads = settings.MaxConcurrentBackgroundDownloads,
        MaxConcurrentTranscodes = settings.MaxConcurrentTranscodes,
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
        public byte[] Payload { get; set; } = Audio;
        public TestOptionsMonitor<DeezerSettings> Settings { get; }
        public DeezerAudioCache Cache { get; }
        public DeezerResolver Resolver { get; }
        public ExternalIdRegistry Ids => _ids;
        public DeezerMetadataService Catalog => _catalog;
        public TaskCompletionSource CdnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoCdnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CdnHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim ReleaseCdn { get; } = new(0);
        public bool HoldCdn { get; init; }
        public int HoldAfterBytes { get; init; }
        public bool CorruptCdn { get; init; }
        public int AdvertisedExtraBytes { get; init; }
        public int CdnRequests;
        public int ActiveCdnRequests;
        public int MaximumConcurrentCdnRequests;
        public System.Collections.Concurrent.ConcurrentQueue<string> CdnRequestIds { get; } = new();
        public string[] RequestedFormats = [];
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _startedById = new();

        public Fixture(string cacheQuality = "FLAC", byte[]? payload = null)
        {
            Payload = payload ?? Audio;
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
                CacheQuality = cacheQuality, CacheMaxGiB = 20, CacheRetentionDays = 7,
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
                var format = RequestedFormats.Single();
                var extension = format == "FLAC" ? "flac" : "mp3";
                return Json(new { data = new[] { new { media = new[] { new { format,
                    sources = new[] { new { url = $"https://cdn.deezer.test/{id}.{extension}" } } } } } } });
            }
            Assert.Equal("cdn.deezer.test", request.RequestUri.Host);
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent([]) };
                head.Content.Headers.ContentLength = Payload.Length + AdvertisedExtraBytes;
                head.Headers.ETag = new EntityTagHeaderValue("\"source-v1\"");
                head.Content.Headers.LastModified = DateTimeOffset.UnixEpoch;
                return head;
            }
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
                var audio = Payload.ToArray();
                if (CorruptCdn) audio[^1] ^= 0xff;
                HttpContent content = HoldAfterBytes > 0
                    ? new StreamContent(new ProgressiveCdnStream(audio, HoldAfterBytes, ReleaseCdn, CdnHeld))
                    : new ByteArrayContent(audio);
                content.Headers.ContentLength = audio.Length + AdvertisedExtraBytes;
                var media = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                media.Headers.ETag = new EntityTagHeaderValue("\"source-v1\"");
                return media;
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

        private sealed class ProgressiveCdnStream(byte[] bytes, int holdAfterBytes,
            SemaphoreSlim release, TaskCompletionSource held) : Stream
        {
            private int _position;
            private bool _released;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => bytes.Length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> destination,
                CancellationToken cancellationToken = default)
            {
                if (_position >= bytes.Length || destination.IsEmpty) return 0;
                if (!_released && _position >= holdAfterBytes)
                {
                    held.TrySetResult();
                    await release.WaitAsync(cancellationToken);
                    _released = true;
                }
                var boundary = !_released ? Math.Min(holdAfterBytes, bytes.Length) : bytes.Length;
                var count = Math.Min(destination.Length, boundary - _position);
                bytes.AsMemory(_position, count).CopyTo(destination);
                _position += count;
                return count;
            }
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

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
