using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Settings;
using Octo.Services.Lidarr;

namespace Octo.Tests;

public class LidarrClientTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Url, string? ApiKey, string? Body)> Requests { get; } = new();
        public required Func<HttpRequestMessage, string> Respond { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.RequestUri.ToString(),
                request.Headers.TryGetValues("X-Api-Key", out var values) ? values.Single() : null, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Respond(request)),
            };
        }
    }

    private static LidarrClient Build(Handler handler, LidarrSettings? settings = null)
    {
        settings ??= new LidarrSettings
        {
            BaseUrl = "http://lidarr:8686",
            ApiKey = "secret",
            RootFolderPath = "/data/music",
            QualityProfileId = 3,
            MetadataProfileId = 4,
        };
        var monitor = new Mock<IOptionsMonitor<LidarrSettings>>();
        monitor.SetupGet(x => x.CurrentValue).Returns(settings);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));
        return new LidarrClient(factory.Object, monitor.Object);
    }

    [Fact]
    public async Task OptionsUseApiKeyAndMapServerChoices()
    {
        var handler = new Handler
        {
            Respond = req => req.RequestUri!.AbsolutePath switch
            {
                "/api/v1/rootfolder" => "[{\"id\":1,\"path\":\"/data/music\"}]",
                "/api/v1/qualityprofile" => "[{\"id\":2,\"name\":\"Lossless\"}]",
                "/api/v1/metadataprofile" => "[{\"id\":3,\"name\":\"Standard\"}]",
                _ => "[]",
            },
        };

        var result = await Build(handler).GetOptionsAsync();

        Assert.Equal("/data/music", Assert.Single(result.RootFolders).Path);
        Assert.Equal("Lossless", Assert.Single(result.QualityProfiles).Name);
        Assert.Equal("Standard", Assert.Single(result.MetadataProfiles).Name);
        Assert.All(handler.Requests, r => Assert.Equal("secret", r.ApiKey));
    }

    [Fact]
    public async Task ConnectionTestUsesEnteredUrlAndApiKeyWithoutSaving()
    {
        var handler = new Handler
        {
            Respond = request => request.RequestUri!.AbsolutePath == "/api/v1/system/status" ? "{}" : "[]",
        };

        await Build(handler).TestConnectionAsync("http://new-lidarr:8686/", "entered-key");

        Assert.Contains(handler.Requests, request =>
            request.Path == "/api/v1/system/status"
            && request.Url == "http://new-lidarr:8686/api/v1/system/status");
        Assert.All(handler.Requests, request => Assert.Equal("entered-key", request.ApiKey));
        Assert.Contains(handler.Requests, request => request.Path == "/api/v1/rootfolder");
        Assert.Contains(handler.Requests, request => request.Path == "/api/v1/qualityprofile");
        Assert.Contains(handler.Requests, request => request.Path == "/api/v1/metadataprofile");
    }

    [Fact]
    public void AlbumSelectionRequiresExactIdentityAndUsesYearToDisambiguate()
    {
        var candidates = new[]
        {
            Candidate("a", "In Rainbows", "Radiohead", 2007),
            Candidate("b", "In Rainbows", "Radiohead", 2025),
        };

        Assert.Equal("a", LidarrClient.SelectBestAlbum(candidates, "RADIOHEAD", "In-Rainbows", 2007)!.ForeignAlbumId);
        Assert.Null(LidarrClient.SelectBestAlbum(candidates, "Radiohead", "In Rainbows", null));
        Assert.Null(LidarrClient.SelectBestAlbum(candidates, "Other", "In Rainbows", 2007));
    }

    [Fact]
    public void ReleaseTitleAliasMatchesOnlySameArtistAndPrefersCanonicalTitle()
    {
        const string edition = "Beverly Hells (Special Edition)";
        var alias = Candidate("a", "Beverly Hells", "SWIMM", 2015);
        alias.Resource["releases"] = new JsonArray(new JsonObject { ["title"] = edition });
        var duplicateReleaseGroup = Candidate("b", "Beverly Hells", "SWIMM", 2025);
        duplicateReleaseGroup.Resource["releases"] = alias.Resource["releases"]!.DeepClone();
        var canonical = Candidate("c", edition, "SWIMM", 2016);

        Assert.Equal("a", LidarrClient.SelectBestAlbum([alias, alias], "Swimm", edition, null)!.ForeignAlbumId);
        Assert.Null(LidarrClient.SelectBestAlbum([alias], "Other", edition, null));
        Assert.Null(LidarrClient.SelectBestAlbum([alias], "Swimm", "Beverly Hells (Deluxe)", null));
        Assert.Null(LidarrClient.SelectBestAlbum([alias, duplicateReleaseGroup], "Swimm", edition, null));
        Assert.Equal("a", LidarrClient.SelectBestAlbum([alias, duplicateReleaseGroup], "Swimm", edition, 2015)!.ForeignAlbumId);
        Assert.Equal("c", LidarrClient.SelectBestAlbum([alias, canonical], "Swimm", edition, null)!.ForeignAlbumId);
    }

    [Fact]
    public async Task AlbumLookupAcceptsEditionTitleUnderCanonicalGroupTitle()
    {
        var handler = new Handler
        {
            Respond = _ => """
                [{"foreignAlbumId":"b99f5cb1-0032-4548-a2b4-a52226d920bf","title":"Beverly Hells",
                  "releaseDate":"2015-08-28","artist":{"artistName":"SWIMM"},
                  "releases":[{"title":"Beverly Hells (Special Edition)"},{"title":"Beverly Hells"}]}]
                """,
        };

        var album = await Build(handler).ResolveAlbumAsync("Swimm", "Beverly Hells (Special Edition)", 2016);

        Assert.Equal("b99f5cb1-0032-4548-a2b4-a52226d920bf", album.ForeignAlbumId);
        Assert.Equal("Beverly Hells", album.Title);
    }

    [Fact]
    public async Task NewAlbumUsesChosenDefaultsAndMonitorsOnlyRequestedAlbum()
    {
        Assert.True(new LidarrSettings().RefreshArtistOnAdd);
        var handler = new Handler
        {
            Respond = req => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
            {
                ("GET", "/api/v1/album") => "[]",
                ("GET", "/api/v1/artist") => "[]",
                ("POST", "/api/v1/album") => "{\"id\":42,\"artistId\":7}",
                ("GET", "/api/v1/track") => "[{\"id\":1}]",
                ("POST", "/api/v1/command") => """{"id":9,"status":"completed","result":"successful"}""",
                _ => "[]",
            },
        };

        var id = await Build(handler).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020));

        Assert.Equal(42, id);
        var add = JsonNode.Parse(handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/api/v1/album").Body!)!;
        Assert.True(add["monitored"]!.GetValue<bool>());
        Assert.False(add["addOptions"]!["searchForNewAlbum"]!.GetValue<bool>());
        Assert.Equal("/data/music", add["artist"]!["rootFolderPath"]!.GetValue<string>());
        Assert.Equal(3, add["artist"]!["qualityProfileId"]!.GetValue<int>());
        Assert.Equal(4, add["artist"]!["metadataProfileId"]!.GetValue<int>());
        Assert.True(add["artist"]!["monitored"]!.GetValue<bool>());
        Assert.Equal("none", add["artist"]!["monitorNewItems"]!.GetValue<string>());
        Assert.Equal("unknown", add["artist"]!["addOptions"]!["monitor"]!.GetValue<string>());
        Assert.False(add["artist"]!["addOptions"]!["searchForMissingAlbums"]!.GetValue<bool>());
        Assert.Equal("mbid", Assert.Single((JsonArray)add["artist"]!["addOptions"]!["albumsToMonitor"]!)!.GetValue<string>());
        var commands = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/api/v1/command")
            .Select(r => JsonNode.Parse(r.Body!)!)
            .ToList();
        Assert.Equal(2, commands.Count);
        Assert.Equal("RefreshArtist", commands[0]["name"]!.GetValue<string>());
        Assert.Equal(7, commands[0]["artistIds"]![0]!.GetValue<int>());
        Assert.Null(commands[0]["isNewArtist"]); // Match Lidarr's native command defaults for deduplication.
        Assert.Equal("AlbumSearch", commands[1]["name"]!.GetValue<string>());
        Assert.Equal(42, Assert.Single((JsonArray)commands[1]["albumIds"]!)!.GetValue<int>());
    }

    [Fact]
    public async Task NewArtistCatalogRefreshCanBeDisabled()
    {
        var handler = new Handler
        {
            Respond = req => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
            {
                ("GET", "/api/v1/album") => "[]",
                ("GET", "/api/v1/artist") => "[]",
                ("POST", "/api/v1/album") => "{\"id\":42,\"artistId\":7}",
                ("GET", "/api/v1/track") => "[{\"id\":1}]",
                ("POST", "/api/v1/command") => """{"id":9,"status":"completed","result":"successful"}""",
                _ => "[]",
            },
        };
        var settings = new LidarrSettings
        {
            BaseUrl = "http://lidarr:8686",
            ApiKey = "secret",
            RootFolderPath = "/data/music",
            QualityProfileId = 3,
            MetadataProfileId = 4,
            RefreshArtistOnAdd = false,
        };

        await Build(handler, settings).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020));

        var album = JsonNode.Parse(handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/api/v1/album").Body!)!;
        Assert.True(album["monitored"]!.GetValue<bool>());

        var commands = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/api/v1/command")
            .Select(r => JsonNode.Parse(r.Body!)!["name"]!.GetValue<string>())
            .ToList();
        Assert.Equal(["AlbumSearch"], commands);
    }

    [Fact]
    public async Task RequestedAlbumMonitoringCanBeDisabledWithoutMonitoringArtistOrOtherAlbums()
    {
        var handler = new Handler
        {
            Respond = req => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
            {
                ("GET", "/api/v1/album") => "[]",
                ("GET", "/api/v1/artist") => "[]",
                ("POST", "/api/v1/album") => "{\"id\":42,\"artistId\":7}",
                ("GET", "/api/v1/track") => "[{\"id\":1}]",
                ("POST", "/api/v1/command") => """{"id":9,"status":"completed","result":"successful"}""",
                _ => "[]",
            },
        };
        var settings = new LidarrSettings
        {
            BaseUrl = "http://lidarr:8686",
            ApiKey = "secret",
            RootFolderPath = "/data/music",
            QualityProfileId = 3,
            MetadataProfileId = 4,
            MonitorRequestedAlbums = false,
        };

        await Build(handler, settings).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020));

        var add = JsonNode.Parse(handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/api/v1/album").Body!)!;
        Assert.False(add["monitored"]!.GetValue<bool>());
        Assert.False(add["artist"]!["monitored"]!.GetValue<bool>());
        Assert.Equal("none", add["artist"]!["addOptions"]!["monitor"]!.GetValue<string>());
        Assert.Empty((JsonArray)add["artist"]!["addOptions"]!["albumsToMonitor"]!);
        var commands = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/api/v1/command")
            .Select(r => JsonNode.Parse(r.Body!)!["name"]!.GetValue<string>())
            .ToList();
        Assert.Equal(["RefreshArtist", "AlbumSearch"], commands);
    }

    [Fact]
    public async Task FailedDurableSubmissionMarkerPreventsAlbumSearch()
    {
        var handler = new Handler
        {
            Respond = req => req.RequestUri!.AbsolutePath switch
            {
                "/api/v1/album" => "[{\"id\":17}]",
                "/api/v1/track" => "[{\"id\":1}]",
                _ => "{}",
            },
        };
        var settings = new LidarrSettings
        {
            BaseUrl = "http://lidarr:8686",
            ApiKey = "secret",
            RootFolderPath = "/data/music",
            QualityProfileId = 3,
            MetadataProfileId = 4,
            MonitorRequestedAlbums = false,
        };

        await Assert.ThrowsAsync<IOException>(() => Build(handler, settings).EnsureAlbumAndSearchAsync(
            Candidate("album", "Album", "Artist", 2020), beforeSearch: id =>
            {
                Assert.Equal(17, id);
                throw new IOException("Disk full");
            }));

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/v1/command");
    }

    [Fact]
    public async Task ExistingArtistSettingsArePreservedWhenAddingAlbum()
    {
        var handler = new Handler
        {
            Respond = req => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
            {
                ("GET", "/api/v1/album") => "[]",
                ("GET", "/api/v1/artist") =>
                    "[{\"id\":7,\"foreignArtistId\":\"artist-mbid\",\"path\":\"/existing/Artist\",\"qualityProfileId\":9,\"metadataProfileId\":10,\"monitored\":true,\"monitorNewItems\":\"all\"}]",
                ("POST", "/api/v1/album") => "{\"id\":42,\"artistId\":7}",
                ("GET", "/api/v1/track") => "[{\"id\":1}]",
                ("POST", "/api/v1/command") => """{"id":9,"status":"completed","result":"successful"}""",
                _ => "[]",
            },
        };

        await Build(handler).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020));

        var add = JsonNode.Parse(handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Path == "/api/v1/album").Body!)!;
        Assert.Equal(7, add["artistId"]!.GetValue<int>());
        Assert.Equal("/existing/Artist", add["artist"]!["path"]!.GetValue<string>());
        Assert.Equal(9, add["artist"]!["qualityProfileId"]!.GetValue<int>());
        Assert.Equal(10, add["artist"]!["metadataProfileId"]!.GetValue<int>());
        Assert.True(add["monitored"]!.GetValue<bool>());
        Assert.True(add["artist"]!["monitored"]!.GetValue<bool>());
        Assert.Equal("all", add["artist"]!["monitorNewItems"]!.GetValue<string>());
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
        var command = JsonNode.Parse(handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/api/v1/command").Body!)!;
        Assert.Equal("AlbumSearch", command["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task RequestedAlbumMonitoringEnablesExistingArtistWithoutChangingOtherAlbums()
    {
        var handler = new Handler
        {
            Respond = req => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
            {
                ("GET", "/api/v1/album") => "[]",
                ("GET", "/api/v1/artist") =>
                    "[{\"id\":7,\"foreignArtistId\":\"artist-mbid\",\"monitored\":false,\"monitorNewItems\":\"all\"}]",
                ("POST", "/api/v1/album") => "{\"id\":42,\"artistId\":7}",
                ("GET", "/api/v1/track") => "[{\"id\":1}]",
                ("POST", "/api/v1/command") => """{"id":9,"status":"completed","result":"successful"}""",
                ("PUT", "/api/v1/artist/editor") => "{}",
                _ => "[]",
            },
        };

        await Build(handler).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020));

        var add = JsonNode.Parse(handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Path == "/api/v1/album").Body!)!;
        Assert.True(add["artist"]!["monitored"]!.GetValue<bool>());
        Assert.Equal("none", add["artist"]!["monitorNewItems"]!.GetValue<string>());
        var updateRequest = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/v1/artist/editor", updateRequest.Path);
        var update = JsonNode.Parse(updateRequest.Body!)!;
        Assert.Equal(7, update["artistIds"]![0]!.GetValue<int>());
        Assert.True(update["monitored"]!.GetValue<bool>());
        Assert.Equal("none", update["monitorNewItems"]!.GetValue<string>());
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put && r.Path == "/api/v1/album/monitor");
    }

    [Fact]
    public async Task ExistingAlbumSearchMonitorsOnlyRequestedAlbum()
    {
        var handler = new Handler
        {
            Respond = req => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
            {
                ("GET", "/api/v1/album") =>
                    "[{\"id\":12,\"foreignAlbumId\":\"mbid\",\"monitored\":true,\"artist\":{\"id\":7,\"monitored\":false}}]",
                ("GET", "/api/v1/track") => "[{\"id\":1}]",
                ("POST", "/api/v1/command") => """{"id":9,"status":"completed","result":"successful"}""",
                ("PUT", "/api/v1/artist/editor") => "{}",
                _ => "[]",
            },
        };

        await Build(handler).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020));

        var artistUpdate = JsonNode.Parse(handler.Requests.Single(r =>
            r.Method == HttpMethod.Put && r.Path == "/api/v1/artist/editor").Body!)!;
        Assert.Equal(7, artistUpdate["artistIds"]![0]!.GetValue<int>());
        Assert.True(artistUpdate["monitored"]!.GetValue<bool>());
        Assert.Equal("none", artistUpdate["monitorNewItems"]!.GetValue<string>());
        var monitor = JsonNode.Parse(handler.Requests.Single(r =>
            r.Method == HttpMethod.Put && r.Path == "/api/v1/album/monitor").Body!)!;
        Assert.Equal(12, monitor["albumIds"]![0]!.GetValue<int>());
        Assert.True(monitor["monitored"]!.GetValue<bool>());
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/v1/artist");
    }

    [Fact]
    public async Task ExistingAlbumSearchDoesNotChangeMonitoring()
    {
        var handler = new Handler
        {
            Respond = req => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
            {
                ("GET", "/api/v1/album") =>
                    "[{\"id\":12,\"foreignAlbumId\":\"mbid\",\"monitored\":false,\"artist\":{\"id\":7,\"qualityProfileId\":9}}]",
                ("GET", "/api/v1/track") => "[{\"id\":1}]",
                ("POST", "/api/v1/command") => """{"id":9,"status":"completed","result":"successful"}""",
                _ => "[]",
            },
        };

        var settings = new LidarrSettings
        {
            BaseUrl = "http://lidarr:8686",
            ApiKey = "secret",
            RootFolderPath = "/data/music",
            QualityProfileId = 3,
            MetadataProfileId = 4,
            MonitorRequestedAlbums = false,
        };
        var id = await Build(handler, settings).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020));

        Assert.Equal(12, id);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/v1/artist");
        var command = JsonNode.Parse(handler.Requests.Single(r => r.Method == HttpMethod.Post).Body!)!;
        Assert.Equal("AlbumSearch", command["name"]!.GetValue<string>());
        Assert.Equal(12, command["albumIds"]![0]!.GetValue<int>());
    }

    [Fact]
    public async Task AlbumSearchWaitsForFallbackArtistRefreshAndTrackMetadata()
    {
        var ready = false;
        var commandPosts = 0;
        var handler = new Handler
        {
            Respond = req =>
            {
                if (req.RequestUri!.AbsolutePath == "/api/v1/track")
                    return ready ? """[{"id":1}]""" : "[]";
                if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/api/v1/command")
                {
                    if (++commandPosts == 1) return """{"id":9,"status":"started"}""";
                    Assert.True(ready, "Search must wait for album tracks to load.");
                    return """{"id":10}""";
                }
                if (req.RequestUri.AbsolutePath == "/api/v1/command/9")
                {
                    ready = true;
                    return """{"id":9,"status":"completed","result":"successful"}""";
                }
                return req.Method == HttpMethod.Post ? """{"id":42,"artistId":7}""" : "[]";
            },
        };

        Assert.Equal(42, await Build(handler).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020)));
        var commands = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/api/v1/command")
            .Select(r => JsonNode.Parse(r.Body!)!["name"]!.GetValue<string>())
            .ToList();
        Assert.Equal(["RefreshArtist", "AlbumSearch"], commands);
        Assert.All(handler.Requests.Where(r => r.Path.StartsWith("/api/v1/track")),
            r => Assert.Equal("/api/v1/track?albumId=42", r.Path));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("started")]
    [InlineData("completed")]
    public async Task NewArtistReusesLidarrRefreshInsteadOfStartingAnother(string status)
    {
        var ready = status == "completed";
        var handler = new Handler
        {
            Respond = req =>
            {
                if (req.RequestUri!.AbsolutePath == "/api/v1/command/9")
                {
                    ready = true;
                    return """{"id":9,"status":"completed","result":"successful"}""";
                }
                return (req.Method.Method, req.RequestUri.AbsolutePath) switch
                {
                    ("GET", "/api/v1/album") or ("GET", "/api/v1/artist") => "[]",
                    ("POST", "/api/v1/album") => """{"id":42,"artistId":7}""",
                    ("GET", "/api/v1/track") => ready ? """[{"id":1}]""" : "[]",
                    ("GET", "/api/v1/command") => $$"""[{"id":9,"name":"RefreshArtist","body":{"artistIds":[7]},"status":"{{status}}","result":"successful"}]""",
                    _ => """{"id":10,"status":"completed","result":"successful"}""",
                };
            },
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Build(handler).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020), deadline.Token);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/v1/command");
        Assert.Equal("AlbumSearch", JsonNode.Parse(post.Body!)!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("album")]
    [InlineData("artist")]
    [InlineData("all-artists")]
    [InlineData("bulk-artists")]
    [InlineData("other-artist")]
    public async Task EmptyExistingAlbumRefreshesOnceAfterMatchingActiveWork(string active)
    {
        var joined = false;
        var refreshed = false;
        var commandPosts = 0;
        var body = active switch
        {
            "album" => """ "name":"RefreshAlbum","body":{"albumId":42}""",
            "all-artists" => """ "name":"RefreshArtist","body":{"artistIds":[]}""",
            "bulk-artists" => """ "name":"BulkRefreshArtist","body":{"artistIds":[7,8]}""",
            "other-artist" => """ "name":"RefreshArtist","body":{"artistIds":[8]}""",
            _ => """ "name":"RefreshArtist","body":{"artistIds":[7]}""",
        };
        var handler = new Handler
        {
            Respond = req =>
            {
                if (req.RequestUri!.AbsolutePath == "/api/v1/command/9")
                {
                    joined = true;
                    return """{"id":9,"status":"completed","result":"successful"}""";
                }
                if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/api/v1/command")
                {
                    Assert.True(active is "none" or "other-artist" || joined);
                    commandPosts++;
                    refreshed = true;
                    return """{"id":10,"status":"completed","result":"successful"}""";
                }
                return (req.Method.Method, req.RequestUri.AbsolutePath) switch
                {
                    ("GET", "/api/v1/album") => """[{"id":42,"artistId":7,"artist":{"id":7,"monitored":true}}]""",
                    ("GET", "/api/v1/track") => refreshed ? """[{"id":1}]""" : "[]",
                    ("GET", "/api/v1/command") => active == "none" ? "[]" : "[{\"id\":9,\"status\":\"started\"," + body + "}]",
                    _ => "{}",
                };
            },
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var marker = false;

        await Build(handler).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020), deadline.Token,
            beforeSearch: id => { Assert.True(refreshed); marker = true; return Task.CompletedTask; });

        Assert.True(marker);
        Assert.Equal(2, commandPosts);
        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post && r.Path == "/api/v1/command")
            .Select(r => JsonNode.Parse(r.Body!)!).ToList();
        Assert.Equal("RefreshAlbum", posts[0]["name"]!.GetValue<string>());
        Assert.Equal(42, posts[0]["albumId"]!.GetValue<int>());
        Assert.Equal("AlbumSearch", posts[1]["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("completed", "successful")]
    [InlineData("completed", "unsuccessful")]
    [InlineData("failed", "unsuccessful")]
    [InlineData("aborted", "unsuccessful")]
    [InlineData("cancelled", "unsuccessful")]
    [InlineData("orphaned", "unsuccessful")]
    public async Task UnusableRefreshNeverSubmitsAlbumSearch(string status, string result)
    {
        var handler = new Handler
        {
            Respond = req => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
            {
                ("GET", "/api/v1/album") => """[{"id":42,"artistId":7,"artist":{"id":7,"monitored":true}}]""",
                ("GET", "/api/v1/track") or ("GET", "/api/v1/command") => "[]",
                ("POST", "/api/v1/command") => $$"""{"id":9,"status":"{{status}}","result":"{{result}}"}""",
                _ => "{}",
            },
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var marked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(handler).EnsureAlbumAndSearchAsync(
            Candidate("mbid", "Album", "Artist", 2020), deadline.Token,
            beforeSearch: _ => { marked = true; return Task.CompletedTask; }));

        Assert.False(marked);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/v1/command");
        Assert.Equal("RefreshAlbum", JsonNode.Parse(post.Body!)!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveRefreshHonorsTimeoutAndCallerCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler
        {
            Respond = req =>
            {
                if (req.RequestUri!.AbsolutePath == "/api/v1/command" && cancel) cancellation.Cancel();
                return req.RequestUri.AbsolutePath switch
                {
                    "/api/v1/album" => """[{"id":42,"artistId":7,"artist":{"id":7,"monitored":true}}]""",
                    "/api/v1/track" => "[]",
                    "/api/v1/command" => """[{"id":9,"name":"RefreshArtist","body":{"artistIds":[7]},"status":"started"}]""",
                    "/api/v1/command/9" => """{"id":9,"status":"started"}""",
                    _ => "{}",
                };
            },
        };
        var settings = new LidarrSettings { BaseUrl = "http://lidarr:8686", ApiKey = "secret",
            RootFolderPath = "/data/music", QualityProfileId = 3, MetadataProfileId = 4, ImportTimeoutSeconds = 1 };
        var work = Build(handler, settings).EnsureAlbumAndSearchAsync(Candidate("mbid", "Album", "Artist", 2020), cancellation.Token);

        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        else await Assert.ThrowsAsync<TimeoutException>(() => work);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AlbumTracksJoinTrackFilesReturnedBySeparateEndpoint()
    {
        var handler = new Handler
        {
            Respond = req => req.RequestUri!.AbsolutePath switch
            {
                "/api/v1/track" =>
                    "[{\"id\":1,\"title\":\"Song\",\"trackNumber\":\"1\",\"duration\":123000,\"hasFile\":true,\"trackFileId\":8}]",
                "/api/v1/trackFile" =>
                    "[{\"id\":8,\"path\":\"/data/music/Artist/Album/01.flac\",\"size\":456}]",
                _ => "[]",
            },
        };

        var track = Assert.Single(await Build(handler).GetAlbumTracksAsync(42));

        Assert.True(track.HasFile);
        Assert.Equal("/data/music/Artist/Album/01.flac", track.Path);
        Assert.Equal(456, track.SizeBytes);
        Assert.Contains(handler.Requests, r => r.Path == "/api/v1/track?albumId=42");
        Assert.Contains(handler.Requests, r => r.Path == "/api/v1/trackFile?albumId=42");
    }

    [Fact]
    public async Task ImportCompletionUsesAlbumStatisticsNotAlternateReleaseRows()
    {
        var handler = new Handler
        {
            Respond = req => req.RequestUri!.AbsolutePath switch
            {
                "/api/v1/album/42" =>
                    "{\"id\":42,\"statistics\":{\"trackCount\":1,\"trackFileCount\":1}}",
                "/api/v1/track" =>
                    "[{\"id\":1,\"title\":\"Song\",\"hasFile\":true,\"trackFileId\":8},{\"id\":2,\"title\":\"Alternate release bonus\",\"hasFile\":false,\"trackFileId\":0}]",
                "/api/v1/trackFile" =>
                    "[{\"id\":8,\"path\":\"/data/music/Artist/Album/01.flac\",\"size\":456}]",
                _ => "[]",
            },
        };

        var state = await Build(handler).GetAlbumImportStateAsync(42);

        Assert.True(state.IsComplete);
        Assert.Equal(1, state.TrackCount);
        Assert.Equal(2, state.Tracks.Count);
    }

    [Fact]
    public void ImportedPathsTranslateRelativeToSelectedRootAndRejectEscapes()
    {
        var translated = LidarrHeartAcquisitionService.TranslateImportedPath(
            "/data/music/Radiohead/In Rainbows/01.flac", "/data/music", "/music");
        Assert.Equal(Path.GetFullPath("/music/Radiohead/In Rainbows/01.flac"), translated);
        Assert.Throws<InvalidOperationException>(() =>
            LidarrHeartAcquisitionService.TranslateImportedPath("/downloads/other.flac", "/data/music", "/music"));
    }

    private static LidarrAlbumCandidate Candidate(string foreignId, string title, string artist, int? year)
    {
        var resource = new JsonObject
        {
            ["foreignAlbumId"] = foreignId,
            ["title"] = title,
            ["releaseDate"] = year is null ? null : $"{year}-01-01T00:00:00Z",
            ["artist"] = new JsonObject
            {
                ["artistName"] = artist,
                ["foreignArtistId"] = "artist-mbid",
            },
        };
        return new LidarrAlbumCandidate(0, foreignId, title, artist, year, resource);
    }
}
