using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Octo.Models.Settings;
using Octo.Services.Deezer;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Local;

namespace Octo.Tests;

public sealed class ExternalPlaybackTests
{
    [Fact]
    public async Task ExternalPlaybackUsesDeezerRegardlessOfLegacyStorageMode()
    {
        var downloads = new Mock<IDownloadService>();
        downloads.Setup(service => service.GetDirectStreamAsync(
                "soulseek", "track-id", null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectStreamInfo
            {
                AudioStream = new MemoryStream([1, 2, 3]),
                ContentType = "audio/mpeg",
                ContentLength = 3,
                StatusCode = 200,
            });
        var library = new Mock<ILocalLibraryService>();
        library.Setup(service => service.ParseSongId("external-track"))
            .Returns((true, "soulseek", "track-id"));

        await using var factory = CreateFactory(downloads, library, waitForLossless: false);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/rest/stream?u=alice&t=tok&s=salt&id=external-track&f=json");

        response.EnsureSuccessStatusCode();
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync());
        downloads.Verify(service => service.GetDirectStreamAsync(
            "soulseek", "track-id", null,
            It.IsAny<CancellationToken>()), Times.Once);
        downloads.Verify(service => service.DownloadAndStreamAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RejectedCallerCannotStartExternalPlayback()
    {
        var downloads = new Mock<IDownloadService>();
        var library = new Mock<ILocalLibraryService>();
        library.Setup(service => service.ParseSongId("external-track")).Returns((true, "soulseek", "track-id"));
        await using var factory = CreateFactory(downloads, library, waitForLossless: false);
        using var client = factory.CreateClient();

        var response = await client.GetStringAsync("/rest/stream?u=bad&t=tok&s=salt&id=external-track&f=json");

        Assert.Contains("failed", response);
        Assert.Contains("40", response);
        downloads.Verify(service => service.GetDirectStreamAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WaitForLosslessStillAcquiresBeforePlaybackWhenEnabled()
    {
        var downloads = new Mock<IDownloadService>();
        var library = new Mock<ILocalLibraryService>();
        library.Setup(service => service.ParseSongId("external-track"))
            .Returns((true, "soulseek", "track-id"));

        await using var factory = CreateFactory(downloads, library, waitForLossless: true);
        using var client = factory.CreateClient();
        var responseTask = client.GetAsync("/rest/stream?u=alice&t=tok&s=salt&id=external-track&f=json");

        var queue = factory.Services.GetRequiredService<TrackAcquisitionQueue>();
        // Only a guard against hanging: under a full-suite load the first request through a
        // fresh host, sign-in check included, has taken over 5 seconds to reach the queue.
        using var dequeueTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = await queue.DequeueAsync(dequeueTimeout.Token);
        Assert.NotNull(request);
        Assert.False(request.IsStar);
        Assert.False(request.TriggerAlbumDownload);
        Assert.True(request.ForcePermanent);

        var path = Path.Combine(Path.GetTempPath(), $"octo-playback-{Guid.NewGuid():N}.flac");
        try
        {
            await File.WriteAllBytesAsync(path, [4, 5, 6]);
            request.Completion.TrySetResult(path);
            queue.Release(request);

            using var response = await responseTask;
            response.EnsureSuccessStatusCode();
            Assert.Equal([4, 5, 6], await response.Content.ReadAsByteArrayAsync());
            downloads.Verify(service => service.GetDirectStreamAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WaitForLosslessHeadDoesNotEnqueueAcquisition()
    {
        var downloads = new Mock<IDownloadService>();
        downloads.Setup(service => service.GetDirectStreamAsync(
                "soulseek", "track-id", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectStreamInfo
            {
                AudioStream = new MemoryStream([4, 5, 6]), ContentType = "audio/mpeg",
                ContentLength = 3, StatusCode = 200,
            });
        var library = new Mock<ILocalLibraryService>();
        library.Setup(service => service.ParseSongId("external-track"))
            .Returns((true, "soulseek", "track-id"));
        await using var factory = CreateFactory(downloads, library, waitForLossless: true);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Head,
            "/rest/stream?u=alice&t=tok&s=salt&id=external-track&f=json");
        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        var queue = factory.Services.GetRequiredService<TrackAcquisitionQueue>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.DequeueAsync(timeout.Token));
        downloads.Verify(service => service.GetDirectStreamAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Mock<IDownloadService> downloads,
        Mock<ILocalLibraryService> library,
        bool waitForLossless)
    {
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(service => service.GetSongAsync("soulseek", "track-id"))
            .ReturnsAsync(new Octo.Models.Domain.Song
            {
                Id = "external-track", Title = "Title", Artist = "Artist", ExternalProvider = "soulseek",
                ExternalId = "track-id", IsLocal = false,
            });
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Subsonic:Url"] = "http://navidrome.invalid",
                        ["Subsonic:StorageMode"] = "Cache",
                        ["Subsonic:DownloadSource"] = "Soulseek",
                        ["Subsonic:WaitForLosslessOnPlay"] = waitForLossless.ToString(),
                        ["Deezer:CacheEnabled"] = "false",
                        ["Deezer:CachePath"] = Path.GetTempPath(),
                        ["Library:DownloadPath"] = Path.GetTempPath(),
                    }));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    // These tests exercise controller fallback behavior with a fake direct source.
                    services.RemoveAll<DeezerDeliveryService>();
                    services.RemoveAll<IHttpClientFactory>();
                    services.RemoveAll<IMusicMetadataService>();
                    var http = new Mock<IHttpClientFactory>();
                    http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new AuthHandler()));
                    services.AddSingleton(http.Object);
                    services.AddSingleton(metadata.Object);
                    services.RemoveAll<IDownloadService>();
                    services.RemoveAll<ILocalLibraryService>();
                    services.AddSingleton(downloads.Object);
                    services.AddSingleton(library.Object);
                });
            });
    }
    private sealed class AuthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/rest/ping", request.RequestUri!.AbsolutePath);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.False(query.ContainsKey("id"));
            var status = query.GetValueOrDefault("u") == "alice" ? "ok" : "failed";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"subsonic-response\":{\"status\":\"" + status + "\"}}", Encoding.UTF8, "application/json") });
        }
    }
}
