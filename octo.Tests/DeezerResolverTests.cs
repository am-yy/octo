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
    public async Task ExpiredPrimaryUsesFallbackAccountWithItsOwnTokens()
    {
        using var fixture = new Fixture { RejectPrimary = true };
        fixture.Config["Deezer:ArlFallback"] = "fallback";
        var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        opened.stream.Dispose(); opened.owner.Dispose();
        Assert.Equal(["arl=primary", "arl=fallback"], fixture.AuthCookies);
        Assert.All(fixture.PageCookies, cookie => Assert.Equal("arl=fallback; sid=fallback-session", cookie));
        Assert.Contains("fallback-license", fixture.MediaBodies.Single());
        Assert.All(fixture.MediaCookies, cookie => Assert.Empty(cookie));
    }

    [Fact]
    public async Task FallbackTrackUsesItsOwnSessionBoundToken()
    {
        using var fixture = new Fixture { FallbackTrack = true };
        var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        opened.stream.Dispose(); opened.owner.Dispose();
        Assert.Equal(["42", "43"], fixture.PageIds);
        Assert.Contains("token-43", fixture.MediaBodies.Last());
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
        Assert.Equal(["arl=primary", "arl=primary"], fixture.AuthCookies);
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

    [Fact]
    public async Task AccountSpecificCdnFailureUsesFallbackAccountAfterRefresh()
    {
        using var fixture = new Fixture { RejectPrimaryCdn = true };
        fixture.Config["Deezer:ArlFallback"] = "fallback";
        var opened = (await fixture.Resolver.OpenStreamAsync("42"))!.Value;
        opened.stream.Dispose(); opened.owner.Dispose();
        Assert.Equal(["arl=primary", "arl=primary", "arl=fallback"], fixture.AuthCookies);
        Assert.Contains("fallback-license", fixture.MediaBodies.Last());
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
        public bool FallbackTrack { get; init; }
        public bool NoFallbackId { get; init; }
        public bool RejectFirstUrl { get; init; }
        public bool RejectPrimaryCdn { get; init; }
        public int AdvertisedExtraBytes { get; init; }
        public string[] AvailableFormats { get; set; } = ["MP3_128", "MP3_320", "FLAC"];
        public List<string> AuthCookies { get; } = [];
        public List<string> PageCookies { get; } = [];
        public List<string> PageIds { get; } = [];
        public List<string> MediaBodies { get; } = [];
        public List<string> MediaCookies { get; } = [];
        public List<string?> CdnRanges { get; } = [];
        private readonly DeezerMetadataService _catalog;
        public Fixture()
        {
            _catalog = new DeezerMetadataService(this, TestOptions.Monitor(new MetadataSettings()),
                NullLogger<DeezerMetadataService>.Instance);
            Resolver = new DeezerResolver(this, Config, NullLogger<DeezerResolver>.Instance, _catalog);
        }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : "";
            if (url.Contains("deezer.getUserData"))
            {
                AuthCookies.Add(cookie);
                if (RejectPrimary && cookie == "arl=primary") return Json(new { results = new { USER = new { USER_ID = 0 } } });
                var account = cookie[4..];
                var response = Json(new { results = new { checkForm = account + "-api", USER = new { USER_ID = 1, OPTIONS = new { license_token = account + "-license" } } } });
                response.Headers.Add("Set-Cookie", $"sid={account}-session; Path=/; HttpOnly");
                return response;
            }
            if (url.Contains("deezer.pageTrack"))
            {
                PageCookies.Add(cookie);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var id = body.RootElement.GetProperty("SNG_ID").GetString()!;
                PageIds.Add(id);
                return Json(new { results = new { DATA = new { SNG_ID = id, TRACK_TOKEN = "token-" + id, SNG_TITLE = "Song", ART_NAME = "Artist", FALLBACK = new { SNG_ID = NoFallbackId ? "0" : "43" } } }, error = Array.Empty<object>() });
            }
            if (request.RequestUri.Host == "media.deezer.com")
            {
                MediaCookies.Add(cookie);
                var body = await request.Content!.ReadAsStringAsync(ct);
                MediaBodies.Add(body);
                if (FallbackTrack && body.Contains("token-42")) return Json(new { data = new[] { new { media = Array.Empty<object>() } } });
                using var doc = JsonDocument.Parse(body);
                var account = doc.RootElement.GetProperty("license_token").GetString()!.Split('-')[0];
                // Deliberately reversed preference order: resolver must choose requested quality.
                return Json(new { data = new[] { new { media = AvailableFormats
                    .Select(format => new { format, sources = new[] { new { url = $"https://cdn.deezer.test/{format}?account={account}&version={MediaBodies.Count}" } } }).ToArray() } } });
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
            CdnRanges.Add(request.Headers.Range?.ToString());
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (RejectFirstUrl && query["version"] == "1" || RejectPrimaryCdn && query["account"] == "primary")
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
