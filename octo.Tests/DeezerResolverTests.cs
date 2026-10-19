using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Deezer;
using Octo.Services.Soulseek;
using Octo.Services.Metadata;

namespace Octo.Tests;

public sealed class DeezerResolverTests
{
    [Theory]
    [InlineData("bytes=2-5", 206, "bytes 2-5/12", "2345")]
    [InlineData("bytes=9-", 206, "bytes 9-11/12", "9ab")]
    [InlineData("bytes=-3", 206, "bytes 9-11/12", "9ab")]
    [InlineData("bytes=12-", 416, "bytes */12", "")]
    [InlineData("bytes=-0", 416, "bytes */12", "")]
    [InlineData(null, 200, null, "0123456789ab")]
    public async Task PlaybackReturnsRequestedPlaintextRange(string? range, int status, string? contentRange, string expected)
    {
        using var fixture = new Fixture();
        var opened = await fixture.Resolver.OpenStreamAsync("42", range);
        Assert.NotNull(opened);
        var result = opened.Value;
        using (result.owner)
        await using (result.stream)
        {
            Assert.Equal(status, result.statusCode);
            Assert.Equal(contentRange, result.contentRange);
            Assert.Equal("audio/mpeg", result.contentType);
            using var reader = new StreamReader(result.stream);
            Assert.Equal(expected, await reader.ReadToEndAsync());
            Assert.Equal(expected.Length, result.contentLength);
        }
        Assert.Contains("\"type\":\"FULL\"", fixture.MediaBodies.Single());
        Assert.DoesNotContain("FLAC", fixture.MediaBodies.Single());
        Assert.Contains("MP3_320", fixture.MediaBodies.Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeekingAlignsCdnRangeAndKeepsAbsoluteStripeIndex(bool ignoreCdnRange)
    {
        using var fixture = new Fixture { Payload = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray(), IgnoreRange = ignoreCdnRange };
        var opened = (await fixture.Resolver.OpenStreamAsync("42", "bytes=2301-4198"))!.Value;
        using (opened.owner)
        await using (opened.stream)
        {
            using var output = new MemoryStream();
            await opened.stream.CopyToAsync(output);
            Assert.Equal(fixture.Payload[2301..4199], output.ToArray());
            Assert.Equal(1898, opened.contentLength);
            Assert.Equal("bytes 2301-4198/5000", opened.contentRange);
        }
        Assert.Equal("bytes=2048-", fixture.CdnRanges.Last());
    }

    [Fact]
    public async Task ResolvesShareOneLoginAndSessionCookie()
    {
        using var fixture = new Fixture();
        foreach (var id in new[] { "42", "43", "44", "46", "47" })
        {
            var opened = (await fixture.Resolver.OpenStreamAsync(id))!.Value;
            opened.stream.Dispose(); opened.owner.Dispose();
        }
        Assert.True(await fixture.Resolver.IsAvailableAsync());
        Assert.Equal(["arl=primary"], fixture.AuthCookies);
        Assert.All(fixture.PageCookies, cookie => Assert.Equal("arl=primary; sid=primary-session", cookie));
        Assert.All(fixture.MediaCookies, cookie => Assert.Empty(cookie));
    }

    [Fact]
    public async Task RejectedArlLatchesOffWithoutNetworkUntilArlChanges()
    {
        using var fixture = new Fixture { RejectPrimary = true };
        Assert.Null(await fixture.Resolver.OpenStreamAsync("42"));
        var requests = fixture.Requests.Count;

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await fixture.Resolver.OpenStreamAsync("43"));
            Assert.Null(await fixture.Resolver.EstablishSourceAsync("44", "FLAC"));
            Assert.False(await fixture.Resolver.IsAvailableAsync());
        }

        Assert.Equal(requests, fixture.Requests.Count);
        Assert.True(fixture.Resolver.ArlRejected);
        fixture.Config["Deezer:Arl"] = "changed";
        Assert.False(fixture.Resolver.ArlRejected);
        var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        opened.stream.Dispose(); opened.owner.Dispose();
        Assert.Equal(["arl=primary", "arl=changed"], fixture.AuthCookies);
    }

    private sealed class AlertSink : Octo.Services.Notifications.INotificationSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<Octo.Services.Notifications.NotificationMessage> Sent { get; } = new();
        public string Name => "alerts";
        public bool IsConfigured => true;
        public Task SendAsync(Octo.Services.Notifications.NotificationMessage message, CancellationToken ct)
        {
            Sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ARejectedArl_IsAnnouncedOnce_AndAnotherRejectedArlAgain()
    {
        var sink = new AlertSink();
        var notifications = new Octo.Services.Notifications.NotificationService([sink],
            TestOptions.Monitor(new NotificationSettings()), NullLogger<Octo.Services.Notifications.NotificationService>.Instance);
        using var fixture = new Fixture(notifications) { RejectPrimary = true };
        for (var i = 0; i < 3; i++) Assert.Null(await fixture.Resolver.OpenStreamAsync("42"));
        fixture.Config["Deezer:Arl"] = "bad!arl";
        Assert.False(await fixture.Resolver.IsAvailableAsync());
        Assert.False(await fixture.Resolver.IsAvailableAsync());

        // Notify is fire and forget.
        for (var wait = 0; wait < 50 && sink.Sent.Count < 2; wait++) await Task.Delay(20);
        await Task.Delay(100);
        Assert.Equal(2, sink.Sent.Count);
        Assert.All(sink.Sent, message => Assert.Equal("Deezer ARL rejected", message.Title));
    }

    [Fact]
    public async Task FailedLoginCoolsDownWithoutRetrying()
    {
        using var fixture = new Fixture { FailLogin = true };
        Assert.Null(await fixture.Resolver.OpenStreamAsync("42"));
        Assert.Null(await fixture.Resolver.EstablishSourceAsync("43", "FLAC"));
        Assert.False(await fixture.Resolver.IsAvailableAsync());
        Assert.Single(fixture.AuthCookies);
        Assert.False(fixture.Resolver.ArlRejected);
    }

    [Fact]
    public async Task UnavailableTrackIsRememberedAndNotAskedAgain()
    {
        using var fixture = new Fixture { AvailableFormats = ["MP3_320"] };
        Assert.Null(await fixture.Resolver.EstablishSourceAsync("42", "FLAC"));
        var requests = fixture.Requests.Count;

        Assert.Null(await fixture.Resolver.EstablishSourceAsync("42", "FLAC"));
        Assert.Null(await fixture.Resolver.OpenFlacStreamAsync("42"));

        Assert.Equal(requests, fixture.Requests.Count);
        // A different quality is a different question.
        var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        opened.stream.Dispose(); opened.owner.Dispose();
    }

    [Fact]
    public async Task StaleSessionLogsInOnceMoreThenSucceeds()
    {
        using var fixture = new Fixture();
        var first = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        first.stream.Dispose(); first.owner.Dispose();

        fixture.SessionErrorOnce = true;
        var second = (await fixture.Resolver.OpenStreamAsync("43"))!.Value;
        second.stream.Dispose(); second.owner.Dispose();

        Assert.Equal(["arl=primary", "arl=primary"], fixture.AuthCookies);
        Assert.Equal(["42", "43", "43"], fixture.PageIds);
    }

    [Fact]
    public async Task ExpiredLicenseLogsInOnceMoreThenSucceeds()
    {
        using var fixture = new Fixture();
        var first = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        first.stream.Dispose(); first.owner.Dispose();

        fixture.LicenseErrorOnce = true;
        var second = (await fixture.Resolver.OpenStreamAsync("43"))!.Value;
        second.stream.Dispose(); second.owner.Dispose();

        Assert.Equal(["arl=primary", "arl=primary"], fixture.AuthCookies);
        Assert.Equal(3, fixture.MediaBodies.Count);
    }

    [Theory]
    [InlineData("https://media-eu.deezer.com/", "media-eu.deezer.com")]
    [InlineData("https://media.evil.test", "media.deezer.com")]
    [InlineData("http://media.deezer.com", "media.deezer.com")]
    [InlineData(null, "media.deezer.com")]
    public async Task LicenseTokenGoesOnlyToAdvertisedDeezerMediaService(string? advertised, string expected)
    {
        using var fixture = new Fixture { MediaService = advertised };
        var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        opened.stream.Dispose(); opened.owner.Dispose();
        Assert.Equal(expected, fixture.MediaHosts.Single());
    }

    [Fact]
    public async Task PrefetchResolvesManyTracksInOneRoundTrip()
    {
        using var fixture = new Fixture();
        await fixture.Resolver.PrefetchAsync(["42", "43", "44"], "FLAC");
        foreach (var id in new[] { "42", "43", "44" })
        {
            var established = (await fixture.Resolver.EstablishSourceAsync(id, "FLAC"))!.Value;
            established.opened.stream.Dispose(); established.opened.owner.Dispose();
            Assert.Contains($"track=token-{id}", established.info.Media!.Url);
        }
        Assert.Equal(["42,43,44"], fixture.PageBatches);
        Assert.Contains("token-44", Assert.Single(fixture.MediaBodies));
        Assert.All(fixture.Requests.Where(url => url.Contains("gw-light")), url => Assert.Contains("&cid=", url));
    }

    [Fact]
    public async Task FailedSourceFallsThroughToNextWithoutResolvingAgain()
    {
        using var fixture = new Fixture { TwoSources = true, RejectSourceA = true };
        var established = (await fixture.Resolver.EstablishSourceAsync("42", "FLAC"))!.Value;
        established.opened.stream.Dispose(); established.opened.owner.Dispose();
        Assert.Contains("source=b", established.info.Media!.Url);
        Assert.Single(fixture.MediaBodies);
        Assert.Equal(["GET", "GET"], fixture.CdnMethods);
    }

    [Fact]
    public async Task MediaIsReusedUntilDeezerExpiry()
    {
        using var fixture = new Fixture { MediaExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() };
        for (var i = 0; i < 2; i++)
        {
            var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
            opened.stream.Dispose(); opened.owner.Dispose();
        }
        Assert.Single(fixture.MediaBodies);

        // Inside the renewal margin: never cached, so every open asks again.
        fixture.MediaExpiry = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();
        for (var i = 0; i < 2; i++)
        {
            var opened = (await fixture.Resolver.OpenStreamAsync("43"))!.Value;
            opened.stream.Dispose(); opened.owner.Dispose();
        }
        Assert.Equal(3, fixture.MediaBodies.Count);
    }

    [Fact]
    public async Task RangeOpenRenewsAnExpiredDescriptorUrl()
    {
        using var fixture = new Fixture { Payload = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray() };
        var info = (await fixture.Resolver.ProbeSourceAsync("42", "FLAC"))!;
        fixture.RejectFirstUrl = true;

        var opened = (await fixture.Resolver.OpenSourceRangeAsync(info, 2301))!.Value;
        using (opened.owner)
        await using (opened.stream)
        {
            using var output = new MemoryStream();
            await opened.stream.CopyToAsync(output);
            Assert.Equal(fixture.Payload[2301..], output.ToArray());
        }
        Assert.Equal(2, fixture.MediaBodies.Count);
    }

    [Fact]
    public async Task RegionalFallbackTrackUsesItsOwnSessionBoundToken()
    {
        using var fixture = new Fixture { FallbackTrack = true, RegionalFallback = true };
        var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        opened.stream.Dispose(); opened.owner.Dispose();
        Assert.Equal(["42", "43"], fixture.PageIds);
        Assert.Contains("token-43", fixture.MediaBodies.Last());
    }

    [Fact]
    public async Task FallbackWithDifferentRecordingVersionIsRejected()
    {
        using var fixture = new Fixture { FallbackTrack = true, WrongFallbackVersion = true };

        var opened = await fixture.Resolver.OpenStreamAsync("42");

        Assert.Null(opened);
        Assert.Equal(["42", "43"], fixture.PageIds);
        Assert.Single(fixture.MediaBodies);
        Assert.Contains("token-42", fixture.MediaBodies[0]);
    }

    [Fact]
    public async Task SngIdSubstitutionRequiresMatchingOriginalIdentity()
    {
        using var fixture = new Fixture { InitialSngIdSubstitution = true, WrongSngIdSubstitutionVersion = true };

        var opened = await fixture.Resolver.OpenStreamAsync("42");

        Assert.Null(opened);
        Assert.Equal(["42"], fixture.PageIds);
        Assert.Equal(["42"], fixture.OriginalIdentityIds);
        Assert.Empty(fixture.MediaBodies);
    }

    [Fact]
    public async Task SngIdSubstitutionIsAllowedWhenOriginalIdentityMatches()
    {
        using var fixture = new Fixture { InitialSngIdSubstitution = true };

        var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;

        opened.stream.Dispose(); opened.owner.Dispose();
        Assert.Equal(["42"], fixture.PageIds);
        Assert.Equal(["42"], fixture.OriginalIdentityIds);
        Assert.Contains("token-43", fixture.MediaBodies.Single());
    }

    [Fact]
    public async Task SngIdSubstitutionFailsClosedWhenOriginalIdentityIsUnavailable()
    {
        using var fixture = new Fixture { InitialSngIdSubstitution = true, MissingOriginalTrackDetail = true };

        var opened = await fixture.Resolver.OpenStreamAsync("42");

        Assert.Null(opened);
        Assert.Equal(["42"], fixture.OriginalIdentityIds);
        Assert.Empty(fixture.MediaBodies);
    }

    [Fact]
    public async Task MissingFallbackIdAndIsrcUseReadableCatalogMatchWithoutChangingVersion()
    {
        using var fixture = new Fixture { FallbackTrack = true, NoFallbackId = true };
        var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        opened.stream.Dispose(); opened.owner.Dispose();
        Assert.Equal(["42", "44"], fixture.PageIds);
        Assert.Contains("token-44", fixture.MediaBodies.Last());
    }

    [Fact]
    public async Task ExpiredCdnUrlIsEvictedAndResolvedAgain()
    {
        using var fixture = new Fixture { RejectFirstUrl = true };
        var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        opened.stream.Dispose(); opened.owner.Dispose();
        Assert.Equal(["arl=primary"], fixture.AuthCookies);
        Assert.Equal(2, fixture.MediaBodies.Count);
    }

    [Fact]
    public async Task StrictFlacRequestDoesNotAskForOrFallBackToMp3()
    {
        using var fixture = new Fixture { AvailableFormats = ["MP3_320", "MP3_128"] };

        var opened = await fixture.Resolver.OpenFlacStreamAsync("42");

        Assert.Null(opened);
        Assert.NotEmpty(fixture.MediaBodies);
        foreach (var body in fixture.MediaBodies)
        {
            using var document = JsonDocument.Parse(body);
            var formats = document.RootElement.GetProperty("media")[0].GetProperty("formats")
                .EnumerateArray().Select(item => item.GetProperty("format").GetString()).ToArray();
            Assert.Equal("FLAC", Assert.Single(formats));
        }
        Assert.All(fixture.MediaCookies, cookie => Assert.Empty(cookie));
    }

    [Fact]
    public async Task StrictFlacRequestRefreshesExpiredCdnUrlWithoutLossyFallback()
    {
        using var fixture = new Fixture { RejectFirstUrl = true };

        var opened = await fixture.Resolver.OpenFlacStreamAsync("42");

        Assert.NotNull(opened);
        Assert.Equal("audio/flac", opened.Value.contentType);
        opened.Value.stream.Dispose();
        opened.Value.owner.Dispose();
        Assert.Equal(2, fixture.MediaBodies.Count);
        Assert.All(fixture.MediaBodies, body => Assert.Contains("FLAC", body));
        Assert.All(fixture.MediaBodies, body => Assert.DoesNotContain("MP3", body));
        Assert.All(fixture.MediaCookies, cookie => Assert.Empty(cookie));
    }

    [Fact]
    public async Task LossyFallbackMediaCacheCannotSatisfyStrictFlacRequest()
    {
        using var fixture = new Fixture { AvailableFormats = ["MP3_320"] };
        var fallback = (await fixture.Resolver.OpenStreamAsync("42", quality: "FLAC"))!.Value;
        Assert.Equal("audio/mpeg", fallback.contentType);
        fallback.stream.Dispose();
        fallback.owner.Dispose();

        fixture.AvailableFormats = ["FLAC"];
        var strict = await fixture.Resolver.OpenFlacStreamAsync("42");

        Assert.NotNull(strict);
        Assert.Equal("audio/flac", strict.Value.contentType);
        strict.Value.stream.Dispose();
        strict.Value.owner.Dispose();
        Assert.Equal(2, fixture.MediaBodies.Count);
        Assert.DoesNotContain("MP3", fixture.MediaBodies.Last());
        Assert.All(fixture.MediaCookies, cookie => Assert.Empty(cookie));
    }

    [Theory]
    [InlineData("FLAC", "audio/flac")]
    [InlineData("MP3_320", "audio/mpeg")]
    public async Task EstablishmentRefreshesRejectedInitialGetWithinSharedBudget(string quality, string contentType)
    {
        using var fixture = new Fixture { RejectFirstGet = true };

        var established = await fixture.Resolver.EstablishSourceAsync("42", quality);

        Assert.NotNull(established);
        using (established.Value.opened.owner)
        await using (established.Value.opened.stream)
            Assert.Equal(contentType, established.Value.opened.contentType);
        Assert.Equal(["GET", "GET"], fixture.CdnMethods);
        Assert.Equal(2, fixture.MediaBodies.Count);
    }

    [Fact]
    public async Task EstablishmentStopsAfterOneRefresh()
    {
        using var fixture = new Fixture { RejectAllCdn = true };

        var established = await fixture.Resolver.EstablishSourceAsync("42", "MP3_320");

        Assert.Null(established);
        Assert.Equal(2, fixture.MediaBodies.Count);
        Assert.Equal(["GET", "GET"], fixture.CdnMethods);
        Assert.Single(fixture.AuthCookies);
    }

    [Fact]
    public async Task EstablishmentCancellationStopsRetries()
    {
        using var fixture = new Fixture { BlockCdn = true };
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Resolver.EstablishSourceAsync("42", "FLAC", cancellation.Token);

        await fixture.CdnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(["GET"], fixture.CdnMethods);
        Assert.Single(fixture.MediaBodies);
    }

    [Fact]
    public async Task SavedCredentialsApplyToNextRequestWithoutRestart()
    {
        using var fixture = new Fixture();
        var first = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        first.stream.Dispose(); first.owner.Dispose();
        fixture.Config["Deezer:Arl"] = "changed";
        var second = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        second.stream.Dispose(); second.owner.Dispose();
        Assert.Equal(["arl=primary", "arl=changed"], fixture.AuthCookies);
        Assert.Contains("changed-license", fixture.MediaBodies.Last());
    }

    [Fact]
    public async Task PermanentCopyUsesFlacAndNeverDeletesExistingDestination()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(Path.GetTempPath(), $"octo-deezer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "track");
        try
        {
            Assert.Equal(destination + ".flac", await fixture.Resolver.DownloadAsync("42", destination));
            Assert.Contains("FLAC", fixture.MediaBodies.Single());
            var original = await File.ReadAllBytesAsync(destination + ".flac");
            await Assert.ThrowsAsync<IOException>(() => fixture.Resolver.DownloadAsync("42", destination));
            Assert.Equal(original, await File.ReadAllBytesAsync(destination + ".flac"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task IncompleteDownloadRemovesOnlyItsPartialFile()
    {
        using var fixture = new Fixture { AdvertisedExtraBytes = 5 };
        var directory = Path.Combine(Path.GetTempPath(), $"octo-deezer-{Guid.NewGuid():N}");
        var destination = Path.Combine(directory, "track");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.Resolver.DownloadAsync("42", destination));
            Assert.False(File.Exists(destination + ".flac"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("YouTube", DownloadSource.Deezer)]
    [InlineData("SoulseekThenYouTube", DownloadSource.SoulseekThenDeezer)]
    [InlineData("Deezer", DownloadSource.Deezer)]
    public void LegacySourceNamesBindToReplacement(string name, DownloadSource expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Subsonic:DownloadSource"] = name,
            ["Subsonic:HeartDownloadSources:0:Source"] = "YouTube",
            ["Subsonic:HeartDownloadSources:0:SongEnabled"] = "true",
        }).Build();
        var settings = config.GetSection("Subsonic").Get<SubsonicSettings>()!;
        Assert.Equal(expected, settings.DownloadSource);
        Assert.Equal(HeartDownloadSource.Deezer, settings.HeartDownloadSources.Single().Source);
    }

    [Fact]
    public void CatalogResolutionKeepsLegacyIdentityAndNewIdSurvivesReregistration()
    {
        using var registry = new ExternalIdRegistry();
        var route = new SoulseekRouting { Artist = "Artist", Title = "Title", Duration = 180 };
        var id = registry.Register(route);
        registry.RememberDeezerTrack(id, "42");
        var repeated = registry.Register(new SoulseekRouting { Artist = "Artist", Title = "Title", Duration = 180 });
        Assert.Equal(id, repeated);
        Assert.Equal("42", registry.Lookup(id)!.DeezerId);
        var legacy = SoulseekMetadataService.TryDecodeExternalId("yt|legacy-video|QXJ0aXN0|VGl0bGU|180")!;
        Assert.Null(legacy.DeezerId);
        Assert.Equal("legacy-video", legacy.YouTubeId);
        Assert.Equal("Artist", legacy.Artist);
        var replacement = SoulseekMetadataService.TryDecodeExternalId(SoulseekMetadataService.EncodeExternalId(route))!;
        Assert.Equal("42", replacement.DeezerId);
        Assert.Null(replacement.YouTubeId);
    }

    private sealed class Fixture : HttpMessageHandler, IHttpClientFactory
    {
        public IConfigurationRoot Config { get; } = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Deezer:Arl"] = "primary" }).Build();
        public DeezerResolver Resolver { get; }
        public byte[] Payload { get; init; } = "0123456789ab"u8.ToArray();
        public bool IgnoreRange { get; init; }
        public bool RejectPrimary { get; init; }
        public bool FailLogin { get; init; }
        public bool SessionErrorOnce { get; set; }
        public List<string> Requests { get; } = [];
        public bool FallbackTrack { get; init; }
        public bool NoFallbackId { get; init; }
        public bool RegionalFallback { get; init; }
        public bool WrongFallbackVersion { get; init; }
        public bool InitialSngIdSubstitution { get; init; }
        public bool WrongSngIdSubstitutionVersion { get; init; }
        public bool MissingOriginalTrackDetail { get; init; }
        public bool RejectFirstUrl { get; set; }
        public bool RejectPrimaryCdn { get; init; }
        public bool RejectFirstGet { get; init; }
        public bool RejectAllCdn { get; init; }
        public bool BlockCdn { get; init; }
        public bool TwoSources { get; init; }
        public bool RejectSourceA { get; init; }
        public bool LicenseErrorOnce { get; set; }
        public string? MediaService { get; init; }
        public long? SessionExpiry { get; init; }
        public long? MediaExpiry { get; set; }
        public int AdvertisedExtraBytes { get; init; }
        public string[] AvailableFormats { get; set; } = ["MP3_128", "MP3_320", "FLAC"];
        public List<string> AuthCookies { get; } = [];
        public List<string> PageCookies { get; } = [];
        public List<string> PageIds { get; } = [];
        public List<string> OriginalIdentityIds { get; } = [];
        public List<string> MediaBodies { get; } = [];
        public List<string> MediaCookies { get; } = [];
        public List<string> MediaHosts { get; } = [];
        public List<string> PageBatches { get; } = [];
        public List<string?> CdnRanges { get; } = [];
        public List<string> CdnMethods { get; } = [];
        public TaskCompletionSource CdnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DeezerMetadataService _catalog;
        public Fixture(Octo.Services.Notifications.NotificationService? notifications = null)
        {
            _catalog = new DeezerMetadataService(this, TestOptions.Monitor(new MetadataSettings()),
                NullLogger<DeezerMetadataService>.Instance);
            Resolver = new DeezerResolver(this, Config, NullLogger<DeezerResolver>.Instance, _catalog, notifications);
        }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : "";
            if (url.Contains("deezer.getUserData"))
            {
                AuthCookies.Add(cookie);
                if (FailLogin) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                if (RejectPrimary && cookie == "arl=primary") return Json(new { results = new { USER = new { USER_ID = 0 } } });
                var account = cookie[4..];
                var response = Json(new { results = new { checkForm = account + "-api", URL_MEDIA = MediaService,
                    USER = new { USER_ID = 1, OPTIONS = new { license_token = account + "-license", expiration_timestamp = SessionExpiry } } } });
                response.Headers.Add("Set-Cookie", $"sid={account}-session; Path=/; HttpOnly");
                return response;
            }
            if (url.Contains("song.getListData"))
            {
                PageCookies.Add(cookie);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var ids = body.RootElement.GetProperty("sng_ids").EnumerateArray().Select(id => id.GetString()!).ToArray();
                PageIds.AddRange(ids);
                PageBatches.Add(string.Join(",", ids));
                if (SessionErrorOnce)
                {
                    SessionErrorOnce = false;
                    return Json(new { results = new { }, error = new { VALID_TOKEN_REQUIRED = "Invalid CSRF token" } });
                }
                return Json(new { results = new { data = ids.Select(id =>
                {
                    var wrongVersion = WrongFallbackVersion && id == "43"
                        || WrongSngIdSubstitutionVersion && id == "42";
                    var title = RegionalFallback && id == "43" ? "Sóng" : "Song";
                    var actualId = InitialSngIdSubstitution && id == "42" ? "43" : id;
                    return new { SNG_ID = actualId, TRACK_TOKEN = "token-" + actualId, SNG_TITLE = title, VERSION = wrongVersion ? "Live" : null, ART_NAME = "Artist", FALLBACK = new { SNG_ID = NoFallbackId ? "0" : "43" } };
                }).ToArray(), count = ids.Length }, error = Array.Empty<object>() });
            }
            if (request.RequestUri.AbsolutePath == "/v1/get_url")
            {
                MediaCookies.Add(cookie);
                MediaHosts.Add(request.RequestUri.Host);
                var body = await request.Content!.ReadAsStringAsync(ct);
                MediaBodies.Add(body);
                if (LicenseErrorOnce)
                {
                    LicenseErrorOnce = false;
                    return Json(new { errors = new[] { new { code = 1001, message = "License token has expired" } } });
                }
                using var doc = JsonDocument.Parse(body);
                var account = doc.RootElement.GetProperty("license_token").GetString()!.Split('-')[0];
                var tokens = doc.RootElement.GetProperty("track_tokens").EnumerateArray().Select(token => token.GetString()!);
                string[] sources = TwoSources ? ["a", "b"] : ["a"];
                // Deliberately reversed preference order: resolver must choose requested quality.
                return Json(new { data = tokens.Select(token => new { media = FallbackTrack && token == "token-42" ? [] : AvailableFormats
                    .Select(format => new { format, exp = MediaExpiry, sources = sources.Select(source => new { url = $"https://cdn.deezer.test/{format}?account={account}&version={MediaBodies.Count}&source={source}&track={token}" }).ToArray() }).ToArray() }).ToArray() });
            }
            if (request.RequestUri.Host == "api.deezer.com" && request.RequestUri.AbsolutePath == "/track/42")
            {
                OriginalIdentityIds.Add("42");
                if (MissingOriginalTrackDetail) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return Json(new { id = 42, title = "Song", artist = new { name = "Artist" } });
            }
            if (request.RequestUri.Host == "api.deezer.com")
                return Json(new { data = new[]
                {
                    new { id = 42, title = "Song", readable = true, artist = new { name = "Artist" } },
                    new { id = 43, title = "Song (Live)", readable = true, artist = new { name = "Artist" } },
                    new { id = 45, title = "Song", readable = false, artist = new { name = "Artist" } },
                    new { id = 44, title = "Song", readable = true, artist = new { name = "Artist" } },
                } });
            Assert.Equal("cdn.deezer.test", request.RequestUri.Host);
            Assert.Empty(cookie); // Account secrets never travel to media sources.
            CdnMethods.Add(request.Method.Method);
            CdnStarted.TrySetResult();
            if (BlockCdn) await Task.Delay(Timeout.Infinite, ct);
            CdnRanges.Add(request.Headers.Range?.ToString());
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (RejectAllCdn || RejectFirstUrl && query["version"] == "1"
                || RejectSourceA && query["source"] == "a"
                || RejectPrimaryCdn && query["account"] == "primary"
                || RejectFirstGet && request.Method == HttpMethod.Get && query["version"] == "1")
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            var start = IgnoreRange ? 0 : (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            var result = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                { Content = new ByteArrayContent(Payload[start..]) };
            result.Content.Headers.ContentLength = Payload.Length - start + AdvertisedExtraBytes;
            if (start > 0) result.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, Payload.Length - 1, Payload.Length);
            return result;
        }
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body)) };
        protected override void Dispose(bool disposing) { if (disposing) { Resolver.Dispose(); _catalog.Dispose(); } base.Dispose(disposing); }
    }
}
