using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Admin;
using Octo.Services.Common;
using Octo.Services.Lidarr;
using Octo.Services.Soulseek;

namespace Octo.Tests;

public sealed class SoulseekDisabledTests
{
    [Fact]
    public async Task DisabledStartupAndClientOperationsNeverContactSlskdOrWarn()
    {
        var settings = new SoulseekSettings
        {
            Enabled = false,
            BaseUrl = "http://slskd.invalid",
            SearchWaitSeconds = 2,
        };
        var handler = new CountingHandler();
        var logger = new Mock<ILogger<SoulseekClient>>();
        var client = new SoulseekClient(new SingleClientFactory(handler), Options.Create(settings), logger.Object);
        var validator = new SoulseekStartupValidator(
            Options.Create(settings), client, new HttpClient(), new ConfigurationBuilder().Build());

        var validation = await validator.ValidateAsync(CancellationToken.None);

        Assert.True(validation.IsValid);
        Assert.Equal("Soulseek disabled", validation.Details);
        Assert.False(await client.IsReachableAsync());
        Assert.Null(await client.GetDownloadsDirectoryAsync());
        Assert.Empty(await client.SearchAsync("Artist - Title", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EnqueueDownloadAsync("peer", "track.flac", 1));
        Assert.Equal(SoulseekTransferState.Errored,
            await client.WaitForCompletionAsync("peer", "track.flac", perAttemptTimeoutSeconds: 30));

        Assert.Equal(0, handler.RequestCount);
        logger.Verify(log => log.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [Fact]
    public async Task DisabledSoulseekHeartStepFallsThroughToConfiguredLidarr()
    {
        var direct = new Mock<IDownloadService>();
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        lidarr.Setup(service => service.TryAcquireAlbumAsync("soulseek", "album-id", true, null))
            .ReturnsAsync(true);
        var coordinator = new HeartAcquisitionCoordinator(
            TestOptions.Monitor(new SubsonicSettings
            {
                HeartDownloadSources =
                [
                    new() { Source = HeartDownloadSource.Soulseek, SongEnabled = true, AlbumEnabled = true },
                    new() { Source = HeartDownloadSource.Lidarr, SongEnabled = true, AlbumEnabled = true },
                ],
            }),
            new TrackAcquisitionQueue(NullLogger<TrackAcquisitionQueue>.Instance), direct.Object,
            lidarr.Object, NullLogger<HeartAcquisitionCoordinator>.Instance,
            soulseekSettings: Options.Create(new SoulseekSettings { Enabled = false }));

        await coordinator.AcquireAlbumAsync("soulseek", "album-id");

        lidarr.Verify(service => service.TryAcquireAlbumAsync("soulseek", "album-id", true, null), Times.Once);
        direct.Verify(service => service.DownloadAlbumWithSourceAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DownloadSource>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()), Times.Never);
    }

    [Fact]
    public void SoulseekRemainsEnabledWhenOptionIsOmitted()
    {
        Assert.True(new SoulseekSettings().Enabled);
    }

    [Fact]
    public void DisabledSettingSurvivesSettingsWriteAndConfigurationReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "octo-soulseek-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var writer = new SettingsFileWriter(path);
            writer.Merge(JsonNode.Parse("""{"Soulseek":{"BaseUrl":"http://slskd","Username":"user","Password":"secret"}}""")!.AsObject());
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Soulseek:Enabled"] = "true" })
                .AddJsonFile(path, optional: false, reloadOnChange: false)
                .Build();
            var restarts = new RestartTracker(configuration);

            writer.Merge(JsonNode.Parse("""{"Soulseek":{"Enabled":false}}""")!.AsObject());
            configuration.Reload();
            var reloaded = new SoulseekSettings();
            configuration.GetSection("Soulseek").Bind(reloaded);

            Assert.False(reloaded.Enabled);
            Assert.Equal("http://slskd", reloaded.BaseUrl);
            Assert.Equal("user", reloaded.Username);
            Assert.Equal("secret", reloaded.Password);
            Assert.Contains("Soulseek:Enabled", restarts.Pending(configuration));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
