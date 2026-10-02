using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Octo.Models.Domain;
using Octo.Services;
using Octo.Services.Local;
using Octo.Services.Subsonic;

namespace Octo.Tests;

public sealed class ExternalSaveEndpointTests
{
    [Fact]
    public async Task SubsonicSaveIsImmediateDurableOrderedAndHeartedWithoutLidarr()
    {
        using var fixture = new SavesFixture();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        var query = "u=alice&t=token&s=salt&f=json";
        var created = JsonNode.Parse(await client.GetStringAsync("/rest/createPlaylist?" + query
            + "&name=Saved&songId=real&songId=ext-deezer-123&songId=ext-deezer-123"))!;
        Assert.Equal("ok", created["subsonic-response"]!["status"]!.ToString());
        var saved = fixture.Store.GetPlaylist("alice", "p1")!;
        Assert.Equal(new[] { "real", "ext-deezer-123", "ext-deezer-123" }, saved.Tracks.Select(t => t.SongId));
        Assert.Equal(3, saved.Tracks.Select(t => t.OccurrenceId).Distinct().Count());
        Assert.All(fixture.Upstream.MirroredIds, id => Assert.Equal("real", id));
        var heart = await client.GetStringAsync("/rest/star?" + query + "&id=ext-deezer-123");
        Assert.Contains("\"ok\"", heart);
        var favorites = await client.GetStringAsync("/rest/getStarred2?" + query);
        Assert.Contains("Outside", favorites);
        var detail = await client.GetStringAsync("/rest/getSong?" + query + "&id=ext-deezer-123");
        Assert.Contains("starred", detail);
        var removed = await client.GetStringAsync("/rest/updatePlaylist?" + query + "&playlistId=p1&songIndexToRemove=1");
        Assert.Contains("\"ok\"", removed);
        saved = fixture.Store.GetPlaylist("alice", "p1")!;
        Assert.Equal(2, saved.Tracks.Count);
        var restarted = new ExternalSaveStore(fixture.StatePath, NullLogger<ExternalSaveStore>.Instance);
        Assert.Equal(saved.Tracks.Select(t => t.OccurrenceId), restarted.GetPlaylist("alice", "p1")!.Tracks.Select(t => t.OccurrenceId));
        Assert.Single(restarted.GetHearts("alice"));
        Assert.Single(restarted.GetPendingAcquisitions());
    }

    [Fact]
    public async Task NativePositionsTranslateDuplicatesRemovalReorderAndUnlimitedPagination()
    {
        using var fixture = new SavesFixture();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Nd-Authorization", "Bearer " + Token("alice"));
        using var added = await client.PostAsync("/api/playlist/p1/tracks", Json("""{"ids":["ext-deezer-123","ext-deezer-123"]}"""));
        added.EnsureSuccessStatusCode();
        using var all = await client.GetAsync("/api/playlist/p1/tracks?_end=0");
        Assert.Equal("3", all.Headers.GetValues("X-Total-Count").Single());
        var rows = JsonNode.Parse(await all.Content.ReadAsStringAsync())!.AsArray();
        Assert.Equal(new[] { "1", "2", "3" }, rows.Select(r => r!["id"]!.ToString()));
        var before = fixture.Store.GetPlaylist("alice", "p1")!.Tracks.Select(t => t.OccurrenceId).ToArray();
        using var move = await client.PutAsync("/api/playlist/p1/tracks/3", Json("""{"insert_before":"1"}"""));
        move.EnsureSuccessStatusCode();
        Assert.Equal(before[2], fixture.Store.GetPlaylist("alice", "p1")!.Tracks[0].OccurrenceId);
        using var remove = await client.DeleteAsync("/api/playlist/p1/tracks?id=3");
        remove.EnsureSuccessStatusCode();
        var after = fixture.Store.GetPlaylist("alice", "p1")!;
        Assert.Equal(new[] { "ext-deezer-123", "real" }, after.Tracks.Select(t => t.SongId));
        using var list = await client.GetAsync("/api/playlist?_end=-1");
        list.EnsureSuccessStatusCode();
        Assert.Contains("\"songCount\":2", await list.Content.ReadAsStringAsync());
        Assert.All(fixture.Upstream.MirroredIds, id => Assert.Equal("real", id));
    }

    [Fact]
    public async Task NativeCreateFavoritesAndDelayedImportKeepPositionsAndExternalAliases()
    {
        using var fixture = new SavesFixture();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Nd-Authorization", "Bearer " + Token("alice"));
        using var created = await client.PostAsync("/api/playlist", Json("""{"name":"Saved","tracks":[{"mediaFileId":"real"},{"mediaFileId":"ext-deezer-123"}]}"""));
        created.EnsureSuccessStatusCode();
        Assert.Contains("\"songCount\":2", await created.Content.ReadAsStringAsync());
        using var heart = await client.PutAsync("/api/song/ext-deezer-123", Json("""{"starred":true}"""));
        heart.EnsureSuccessStatusCode();
        using var favorites = await client.GetAsync("/api/song?starred=true&_start=0&_end=1");
        Assert.Equal("1", favorites.Headers.GetValues("X-Total-Count").Single());
        Assert.Contains("Outside", await favorites.Content.ReadAsStringAsync());
        var before = fixture.Store.GetPlaylist("alice", "p1")!.Tracks.Select(t => t.OccurrenceId).ToArray();
        var imported = Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "imported.flac");
        await File.WriteAllBytesAsync(imported, DeezerAudioCacheTests.MinimalFlacSample());
        await fixture.Store.MarkImportedAsync("deezer", "123", "library-123", imported);
        using var tracks = await client.GetAsync("/api/playlist/p1/tracks?_end=0");
        var rows = JsonNode.Parse(await tracks.Content.ReadAsStringAsync())!.AsArray();
        Assert.Equal("library-123", rows[1]!["mediaFileId"]!.ToString());
        Assert.True(rows[1]!["starred"]!.GetValue<bool>());
        Assert.Equal(before, fixture.Store.GetPlaylist("alice", "p1")!.Tracks.Select(t => t.OccurrenceId));
        var detail = await client.GetStringAsync("/rest/getSong?u=alice&f=json&id=ext-deezer-123");
        Assert.Contains("library-123", detail);
        Assert.Contains("library-123", fixture.Upstream.MirroredHeartIds);
        using var aliasPlay = new HttpRequestMessage(HttpMethod.Get, "/rest/stream?u=alice&f=json&id=ext-deezer-123");
        aliasPlay.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
        using var audio = await client.SendAsync(aliasPlay);
        Assert.Equal(HttpStatusCode.PartialContent, audio.StatusCode);
        Assert.Equal("fLaC", Encoding.ASCII.GetString(await audio.Content.ReadAsByteArrayAsync()));
        var restarted = new ExternalSaveStore(fixture.StatePath, NullLogger<ExternalSaveStore>.Instance);
        Assert.Equal("library-123", restarted.CanonicalSongId("ext-deezer-123"));
        Assert.True(restarted.IsHearted("alice", "ext-deezer-123"));
        var unheart = await client.GetStringAsync("/rest/unstar?u=alice&f=json&id=ext-deezer-123");
        Assert.Contains("\"ok\"", unheart);
        Assert.Empty(fixture.Store.GetHearts("alice"));
    }

    [Fact]
    public async Task MalformedMixedMutationCannotChangeDurablePlaylist()
    {
        using var fixture = new SavesFixture();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Nd-Authorization", "Bearer " + Token("alice"));
        using var bad = await client.PostAsync("/api/playlist/p1/tracks", Json("{"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Empty(fixture.Store.Snapshot().Playlists);
    }

    [Fact]
    public async Task RejectedCallerAndPlaylistOwnerCannotSaveOrSeeFavorites()
    {
        using var fixture = new SavesFixture();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        var denied = await client.GetStringAsync("/rest/star?u=bad&f=json&id=ext-deezer-123");
        Assert.Contains("failed", denied);
        Assert.Empty(fixture.Store.Snapshot().Hearts);
        var owner = await client.GetStringAsync("/rest/updatePlaylist?u=bob&f=json&playlistId=p1&songIdToAdd=ext-deezer-123");
        Assert.Contains("failed", owner);
        Assert.Empty(fixture.Store.Snapshot().Playlists);
        client.DefaultRequestHeaders.Add("X-Nd-Authorization", "Bearer denied");
        using var native = await client.PostAsync("/api/playlist/p1/tracks", Json("""{"ids":["ext-deezer-123"]}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, native.StatusCode);
        Assert.Empty(fixture.Store.Snapshot().Playlists);
    }

    [Fact]
    public async Task DiskWriteFailureDoesNotClaimSaveSucceeded()
    {
        using var fixture = new SavesFixture();
        Directory.CreateDirectory(fixture.StatePath);
        await using var app = fixture.App();
        using var client = app.CreateClient();
        var result = await client.GetStringAsync("/rest/star?u=alice&f=json&id=ext-deezer-123");
        Assert.Contains("failed", result);
        Assert.Empty(fixture.Store.Snapshot().Hearts);
    }

    [Fact]
    public async Task CompletedFlacPlaybackUsesAspNetRangesAndNeverAcquiresFromLidarr()
    {
        using var audio = new DeezerAudioCacheTests.Fixture();
        using var fixture = new SavesFixture();
        await using var app = fixture.App(audio.Cache);
        using var client = app.CreateClient();
        var detail = JsonNode.Parse(await client.GetStringAsync("/rest/getSong?id=ext-deezer-123&f=json"));
        Assert.Equal("flac", detail!["subsonic-response"]!["song"]!["suffix"]!.ToString());
        using var seek = new HttpRequestMessage(HttpMethod.Get, "/rest/stream?u=alice&id=ext-deezer-123&f=json");
        seek.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        using var range = await client.SendAsync(seek);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("audio/flac", range.Content.Headers.ContentType!.MediaType);
        Assert.Equal(audio.Payload[10..20], await range.Content.ReadAsByteArrayAsync());
        using var invalid = new HttpRequestMessage(HttpMethod.Get, "/rest/stream?u=alice&id=ext-deezer-123&f=json");
        invalid.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(audio.Payload.Length + 1, null);
        using var unsatisfied = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfied.StatusCode);
        Assert.Empty(fixture.Store.Snapshot().Acquisitions);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(audio.Root, "cache-index.json")));
        Assert.Null(state!["tracks"]!["123"]!["lastPlayedUtc"]);
        await client.GetStringAsync("/rest/scrobble?u=bad&id=ext-deezer-123&f=json&submission=false");
        state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(audio.Root, "cache-index.json")));
        Assert.Null(state!["tracks"]!["123"]!["lastPlayedUtc"]);
        await client.GetStringAsync("/rest/scrobble?u=alice&id=ext-deezer-123&f=json&submission=false");
        state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(audio.Root, "cache-index.json")));
        Assert.NotNull(state!["tracks"]!["123"]!["lastPlayedUtc"]);
    }

    [Fact]
    public async Task RejectedCallerCannotFillOrReadFlacCache()
    {
        using var audio = new DeezerAudioCacheTests.Fixture();
        using var fixture = new SavesFixture();
        await using var app = fixture.App(audio.Cache);
        using var client = app.CreateClient();
        Assert.Contains("failed", await client.GetStringAsync("/rest/stream?u=bad&id=ext-deezer-123&f=json"));
        Assert.Equal(0, audio.CdnRequests);
        Assert.Null(audio.Cache.GetReadyPath(new Song { DeezerId = "123" }));
        await client.GetByteArrayAsync("/rest/stream?u=alice&id=ext-deezer-123&f=json");
        Assert.Contains("failed", await client.GetStringAsync("/rest/stream?u=bad&id=ext-deezer-123&f=json"));
        Assert.Equal(1, audio.CdnRequests);
    }

    [Theory]
    [InlineData("download", false, false)]
    [InlineData("download.view", true, false)]
    [InlineData("download.view", true, true)]
    public async Task CachedDownloadServesFlacAttachmentAndRangesWithoutRecordingPlayback(string endpoint, bool warm, bool apiKey)
    {
        using var audio = new DeezerAudioCacheTests.Fixture();
        using var fixture = new SavesFixture();
        if (warm) await audio.Cache.EnsureAsync(new Song { DeezerId = "123" });
        await using var app = fixture.App(audio.Cache);
        using var client = app.CreateClient();
        var auth = apiKey ? "apiKey=key&v=1.16.1&c=test" : "u=alice";
        var url = $"/rest/{endpoint}?{auth}&id=ext-deezer-123&f=json";

        using var download = await client.GetAsync(url);
        download.EnsureSuccessStatusCode();
        Assert.Equal("audio/flac", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("Artist - Outside.flac", download.Content.Headers.ContentDisposition.FileNameStar);
        Assert.Equal(audio.Payload, await download.Content.ReadAsByteArrayAsync());
        using var seek = new HttpRequestMessage(HttpMethod.Get, url);
        seek.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        using var range = await client.SendAsync(seek);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(audio.Payload[10..20], await range.Content.ReadAsByteArrayAsync());
        using var invalid = new HttpRequestMessage(HttpMethod.Get, url);
        invalid.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(audio.Payload.Length + 1, null);
        using var unsatisfied = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfied.StatusCode);
        Assert.Equal(1, audio.CdnRequests);
        Assert.Empty(fixture.Store.Snapshot().Acquisitions);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(audio.Root, "cache-index.json")));
        Assert.Null(state!["tracks"]!["123"]!["lastPlayedUtc"]);
    }

    [Theory]
    [InlineData("bad", false, 40)]
    [InlineData("alice", true, 50)]
    public async Task CachedDownloadHonorsNavidromeAuthenticationAndDownloadPermission(string user, bool disabled, int code)
    {
        using var audio = new DeezerAudioCacheTests.Fixture();
        using var fixture = new SavesFixture();
        fixture.Upstream.DownloadsDisabled = disabled;
        await using var app = fixture.App(audio.Cache);
        using var client = app.CreateClient();
        var url = $"/rest/download.view?u={user}&id=ext-deezer-123&f=json";

        var rejected = JsonNode.Parse(await client.GetStringAsync(url));
        Assert.Equal(code, rejected!["subsonic-response"]!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(0, audio.CdnRequests);
        await audio.Cache.EnsureAsync(new Song { DeezerId = "123" });
        rejected = JsonNode.Parse(await client.GetStringAsync(url));
        Assert.Equal(code, rejected!["subsonic-response"]!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(1, audio.CdnRequests);
    }

    [Fact]
    public async Task LibraryAndImportedAliasDownloadsKeepUpstreamIdAndAttachment()
    {
        using var fixture = new SavesFixture();
        await fixture.Store.SetHeartAsync("alice", new Song { Id = "ext-deezer-123", Title = "Outside",
            Artist = "Artist", ExternalProvider = "deezer", ExternalId = "123" }, true);
        var imported = Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "imported.flac");
        await File.WriteAllBytesAsync(imported, DeezerAudioCacheTests.MinimalFlacSample());
        await fixture.Store.MarkImportedAsync("deezer", "123", "library-123", imported);
        await using var app = fixture.App();
        using var client = app.CreateClient();

        foreach (var id in new[] { "real", "ext-deezer-123" })
        {
            using var response = await client.GetAsync("/rest/download.view?u=alice&f=json&id=" + id);
            response.EnsureSuccessStatusCode();
            Assert.Equal("audio/flac", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("Owned.flac", response.Content.Headers.ContentDisposition!.FileNameStar);
            Assert.Equal(new byte[] { 4, 5, 6 }, await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(new[] { "real", "library-123" }, fixture.Upstream.DownloadedIds);
    }

    [Fact]
    public async Task DurableMetadataRecognizesImportBeforeLidarrSubmission()
    {
        using var fixture = new SavesFixture();
        var song = new Song { Id = "ext-deezer-123", Artist = "Artist", Title = "Outside",
            ExternalProvider = "deezer", ExternalId = "123", DeezerId = "123" };
        await fixture.Store.SetHeartAsync("alice", song, true);
        var path = Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "imported.flac");
        await File.WriteAllBytesAsync(path, DeezerAudioCacheTests.MinimalFlacSample());
        await using var app = fixture.App(imported: new Song { Id = "library-123", IsLocal = true, Suffix = "flac", LocalPath = path });
        using var client = app.CreateClient();
        var service = app.Services.GetRequiredService<Octo.Services.Lidarr.ILidarrHeartAcquisitionService>();

        Assert.True(await service.TryAcquireTrackAsync("deezer", "123"));
        Assert.Equal("imported", fixture.Store.Snapshot().Acquisitions.Single().Status);
        Assert.Equal("library-123", fixture.Store.CanonicalSongId(song.Id));
        Assert.Empty(fixture.Upstream.Requests);
    }

    [Fact]
    public async Task NativeSessionMirrorsImportedHeartAfterRestartWithoutLoginCapture()
    {
        using var fixture = new SavesFixture();
        await fixture.Store.SetHeartAsync("alice", new Song { Id = "ext-deezer-123", Title = "Outside",
            ExternalProvider = "deezer", ExternalId = "123" }, true);
        var path = Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "imported.flac");
        await File.WriteAllBytesAsync(path, DeezerAudioCacheTests.MinimalFlacSample());
        await fixture.Store.MarkImportedAsync("deezer", "123", "library-123", path);
        await using var app = fixture.App();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Nd-Authorization", "Bearer " + Token("alice"));

        await client.GetStringAsync("/api/song?starred=true");

        Assert.Contains("library-123", fixture.Upstream.MirroredHeartIds);
        Assert.DoesNotContain(fixture.Store.GetHeartMutations("alice"), h => h.PendingMirror);
    }

    [Fact]
    public async Task RejectedScrobbleDoesNotPrefetchUpcomingAudio()
    {
        using var audio = new DeezerAudioCacheTests.Fixture();
        using var fixture = new SavesFixture();
        await using var app = fixture.App(audio.Cache);
        using var client = app.CreateClient();
        app.Services.GetRequiredService<Octo.Services.Soulseek.RadioQueueStore>()
            .Register(["ext-deezer-current", "ext-deezer-123"]);

        Assert.Contains("failed", await client.GetStringAsync("/rest/scrobble?u=bad&id=ext-deezer-current&f=json"));

        fixture.Metadata.Verify(m => m.PrewarmDeezerIdsForSongIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, audio.CdnRequests);
    }

    [Fact]
    public async Task RejectedCatalogCallerCannotPrefetchAudio()
    {
        using var audio = new DeezerAudioCacheTests.Fixture();
        using var fixture = new SavesFixture();
        fixture.Metadata.Setup(m => m.GetPlaylistAsync("deezer", "1"))
            .ReturnsAsync(new Octo.Models.Subsonic.ExternalPlaylist { Id = "pl-deezer-1", Name = "Discovery" });
        fixture.Metadata.Setup(m => m.GetPlaylistTracksAsync("deezer", "1"))
            .ReturnsAsync([new Song { Id = "ext-deezer-123", DeezerId = "123", Artist = "Artist", Title = "Outside" }]);
        await using var app = fixture.App(audio.Cache);
        using var client = app.CreateClient();

        await client.GetStringAsync("/rest/getAlbum?u=bad&id=pl-deezer-1&f=json");

        fixture.Metadata.Verify(m => m.PrewarmDeezerIdsAsync(It.IsAny<IEnumerable<Song>>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, audio.CdnRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerCanRetryDurableRemovalWhenUpstreamPlaylistAlreadyDeleted(bool native)
    {
        using var fixture = new SavesFixture();
        await fixture.Store.UpsertPlaylistAsync(new ExternalSavedPlaylist { Id = "p1", UserId = "alice", Owner = "alice",
            Name = "Saved", Tracks = [new ExternalPlaylistTrack { SongId = "ext-deezer-123",
                Song = new Song { Id = "ext-deezer-123", ExternalProvider = "deezer", ExternalId = "123" } }] });
        fixture.Upstream.PlaylistMissing = true;
        var backup = fixture.StatePath + ".backup";
        File.Move(fixture.StatePath, backup);
        Directory.CreateDirectory(fixture.StatePath);
        await using var app = fixture.App();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Nd-Authorization", "Bearer " + Token("alice"));
        using var failed = native ? await client.DeleteAsync("/api/playlist/p1")
            : await client.GetAsync("/rest/deletePlaylist?u=alice&f=json&id=p1");
        Assert.True(native ? failed.StatusCode == HttpStatusCode.InternalServerError
            : (await failed.Content.ReadAsStringAsync()).Contains("failed"));
        Assert.NotNull(fixture.Store.GetPlaylist("alice", "p1"));
        Directory.Delete(fixture.StatePath);
        File.Move(backup, fixture.StatePath);
        using var removed = native ? await client.DeleteAsync("/api/playlist/p1")
            : await client.GetAsync("/rest/deletePlaylist?u=alice&f=json&id=p1");
        removed.EnsureSuccessStatusCode();
        if (!native) Assert.Contains("ok", await removed.Content.ReadAsStringAsync());
        Assert.Null(fixture.Store.GetPlaylist("alice", "p1"));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static string Token(string user) => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { username = user })))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    private sealed class SavesFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-save-api-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(_root, "state.json");
        public ExternalSaveStore Store { get; }
        public SaveHandler Upstream { get; } = new();
        public Mock<IMusicMetadataService> Metadata { get; } = new();
        public SavesFixture()
        {
            Directory.CreateDirectory(_root);
            Store = new ExternalSaveStore(StatePath, NullLogger<ExternalSaveStore>.Instance);
        }
        public WebApplicationFactory<Program> App(Octo.Services.Deezer.DeezerAudioCache? cache = null, Song? imported = null)
        {
            var library = new Mock<ILocalLibraryService>();
            library.Setup(l => l.ParseSongId(It.IsAny<string>())).Returns((string id) =>
                id.StartsWith("ext-deezer-") ? (true, "deezer", id[11..]) : (false, null, null));
            library.Setup(l => l.FindImportedSongAsync(It.IsAny<Song>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Song song, CancellationToken _) => song.Artist == "Artist" && song.Title == "Outside" ? imported : null);
            var metadata = Metadata;
            metadata.Setup(m => m.GetSongAsync("deezer", "123")).ReturnsAsync(new Song
            { Id = "ext-deezer-123", Title = "Outside", Artist = "Artist", Duration = 180,
                ExternalProvider = "deezer", ExternalId = "123", DeezerId = "123" });
            var http = new Mock<IHttpClientFactory>();
            http.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(Upstream, false));
            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Subsonic:Url"] = "http://navidrome.invalid", ["Library:DownloadPath"] = _root,
                    ["Deezer:CacheEnabled"] = (cache is not null).ToString(), ["LastFm:ExposeRadioAsPlaylists"] = "false" }));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>(); services.RemoveAll<IHttpClientFactory>();
                    services.RemoveAll<ILocalLibraryService>(); services.RemoveAll<IMusicMetadataService>();
                    services.RemoveAll<ExternalSaveStore>();
                    services.AddSingleton(http.Object); services.AddSingleton(library.Object);
                    services.AddSingleton(metadata.Object); services.AddSingleton(Store);
                    if (cache is not null)
                    { services.RemoveAll<Octo.Services.Deezer.DeezerAudioCache>(); services.AddSingleton(cache); }
                });
            });
        }
        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class SaveHandler : HttpMessageHandler
    {
        public bool PlaylistMissing { get; set; }
        public bool DownloadsDisabled { get; set; }
        public List<string> DownloadedIds { get; } = [];
        public List<string> Requests { get; } = [];
        public List<string> MirroredIds { get; } = [];
        public List<string> MirroredHeartIds { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (request.Headers.TryGetValues("X-Nd-Authorization", out var auth) && auth.Any(a => a.Contains("denied")))
                return Reply("{}", HttpStatusCode.Unauthorized);
            if (query.GetValueOrDefault("u") == "bad") return Reply("""{"subsonic-response":{"status":"failed","error":{"code":40}}}""");
            if (path == "/rest/tokenInfo") return Envelope("tokenInfo", """{"username":"alice"}""");
            if (path == "/rest/getUser")
            {
                Assert.False(query.ContainsKey("id"));
                Assert.Equal("alice", query.GetValueOrDefault("username").ToString());
                return Envelope("user", "{\"username\":\"alice\",\"downloadRole\":" + (!DownloadsDisabled).ToString().ToLowerInvariant() + "}");
            }
            if (path == "/rest/download")
            {
                DownloadedIds.Add(query.GetValueOrDefault("id").ToString());
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5, 6]) };
                response.Content.Headers.ContentType = new("audio/flac");
                response.Content.Headers.ContentDisposition = new("attachment") { FileNameStar = "Owned.flac" };
                return response;
            }
            if (path == "/rest/stream")
            {
                var audio = Encoding.ASCII.GetBytes("fLaC");
                var response = new HttpResponseMessage(request.Headers.Range is null
                    ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(audio) };
                response.Content.Headers.ContentType = new("audio/flac");
                response.Content.Headers.ContentLength = audio.Length;
                if (request.Headers.Range is { Ranges.Count: 1 } range && range.Ranges.Single().From is long start)
                    response.Content.Headers.ContentRange = new(start, range.Ranges.Single().To ?? audio.Length - 1, audio.Length);
                return response;
            }
            if (PlaylistMissing && path is "/rest/getPlaylist" or "/rest/deletePlaylist")
                return Reply("""{"subsonic-response":{"status":"failed","error":{"code":70}}}""");
            if (PlaylistMissing && path == "/api/playlist/p1") return Reply("{}", HttpStatusCode.NotFound);
            const string song = """{"id":"real","title":"Owned","artist":"Artist","duration":180,"suffix":"flac"}""";
            const string playlist = """{"id":"p1","name":"Saved","owner":"alice","ownerName":"alice","songCount":1,"duration":180,"entry":[{"id":"real","title":"Owned","artist":"Artist","duration":180,"suffix":"flac"}]}""";
            if (path == "/api/playlist" && request.Method == HttpMethod.Get) return Reply("[" + playlist + "]");
            if (path == "/api/playlist" && request.Method == HttpMethod.Post)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                MirroredIds.Clear(); MirroredIds.AddRange(body["tracks"]!.AsArray().Select(t => t!["mediaFileId"]!.ToString()));
                return Reply(playlist);
            }
            if (path == "/api/song") return Reply("[]");
            if (path == "/api/playlist/p1" && request.Method == HttpMethod.Get) return Reply(playlist);
            if (path == "/api/playlist/p1/tracks") return Reply("""[{"id":"1","mediaFileId":"real","title":"Owned","artist":"Artist","duration":180,"suffix":"flac"}]""");
            if (path == "/api/playlist/p1" && request.Method == HttpMethod.Put)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                MirroredIds.Clear(); MirroredIds.AddRange(body["tracks"]!.AsArray().Select(t => t!["mediaFileId"]!.ToString()));
                return Reply(playlist);
            }
            if (path.StartsWith("/api/song/") && request.Method == HttpMethod.Put)
            {
                Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
                Assert.True(request.Headers.Contains("X-Nd-Authorization"));
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                if (body["starred"]?.GetValue<bool>() == true) MirroredHeartIds.Add(path[10..]);
            }
            if (path.StartsWith("/api/song/"))
            { var row = JsonNode.Parse(song)!; row["id"] = path[10..]; return Reply(row.ToJsonString()); }
            if (path == "/rest/getSong")
            { var row = JsonNode.Parse(song)!; row["id"] = query.GetValueOrDefault("id").ToString(); return Envelope("song", row.ToJsonString()); }
            if (path is "/rest/getPlaylist" or "/rest/createPlaylist")
            {
                if (path.EndsWith("createPlaylist"))
                { MirroredIds.Clear(); MirroredIds.AddRange(query.GetValueOrDefault("songId").Select(id => id!)); }
                return Envelope("playlist", playlist);
            }
            if (path == "/rest/getPlaylists") return Envelope("playlists", "{\"playlist\":[" + playlist + "]}");
            if (path == "/rest/getStarred2") return Envelope("starred2", "{}");
            if (path == "/rest/star") MirroredHeartIds.Add(query.GetValueOrDefault("id").ToString());
            return Reply("""{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
        }
        private static HttpResponseMessage Envelope(string key, string value) => Reply("{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\",\"" + key + "\":" + value + "}}");
        private static HttpResponseMessage Reply(string value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
            { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
