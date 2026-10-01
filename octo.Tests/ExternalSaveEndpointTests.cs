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
        using var seek = new HttpRequestMessage(HttpMethod.Get, "/rest/stream?id=ext-deezer-123&f=json");
        seek.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        using var range = await client.SendAsync(seek);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("audio/flac", range.Content.Headers.ContentType!.MediaType);
        Assert.Equal(audio.Payload[10..20], await range.Content.ReadAsByteArrayAsync());
        using var invalid = new HttpRequestMessage(HttpMethod.Get, "/rest/stream?id=ext-deezer-123&f=json");
        invalid.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(audio.Payload.Length + 1, null);
        using var unsatisfied = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfied.StatusCode);
        Assert.Empty(fixture.Store.Snapshot().Acquisitions);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(audio.Root, "cache-index.json")));
        Assert.Null(state!["tracks"]!["123"]!["lastPlayedUtc"]);
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
        public SavesFixture()
        {
            Directory.CreateDirectory(_root);
            Store = new ExternalSaveStore(StatePath, NullLogger<ExternalSaveStore>.Instance);
        }
        public WebApplicationFactory<Program> App(Octo.Services.Deezer.DeezerAudioCache? cache = null)
        {
            var library = new Mock<ILocalLibraryService>();
            library.Setup(l => l.ParseSongId(It.IsAny<string>())).Returns((string id) =>
                id.StartsWith("ext-deezer-") ? (true, "deezer", id[11..]) : (false, null, null));
            var metadata = new Mock<IMusicMetadataService>();
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
        public List<string> MirroredIds { get; } = [];
        public List<string> MirroredHeartIds { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (request.Headers.TryGetValues("X-Nd-Authorization", out var auth) && auth.Any(a => a.Contains("denied")))
                return Reply("{}", HttpStatusCode.Unauthorized);
            if (query.GetValueOrDefault("u") == "bad") return Reply("""{"subsonic-response":{"status":"failed","error":{"code":40}}}""");
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
