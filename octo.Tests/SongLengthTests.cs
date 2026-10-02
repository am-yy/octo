using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Domain;
using Octo.Models.Radio;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.CoverArt;
using Octo.Services.LastFm;
using Octo.Services.Metadata;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// The length a client is shown for a song found outside the library. Half of these went
/// out with the 180s placeholder, which the Octo app shows as no length at all: search rows
/// past the first page, and station rows Last.fm had no length for.
/// </summary>
public class SongLengthTests
{
    // ---- The rules ----------------------------------------------------------------

    [Theory]
    [InlineData(29, null)]
    [InlineData(30, 30)]
    [InlineData(417, 417)]
    [InlineData(1200, 1200)]
    [InlineData(1201, null)]
    [InlineData(3600, null)]
    public void SaneVideoLength_KeepsOnlyWhatCouldBeOneSong(int seconds, int? expected)
    {
        Assert.Equal(expected, SongLength.SaneVideoLength(seconds));
    }

    [Fact]
    public void Remember_StrongerSourceReplacesWeaker_NeverTheOtherWay()
    {
        var routing = new SoulseekRouting { Artist = "Daft Punk", Title = "Emotion" };

        Assert.True(SongLength.Remember(routing, 430, LengthSource.Video));
        Assert.True(SongLength.Remember(routing, 418, LengthSource.LastFm));
        Assert.True(SongLength.Remember(routing, 417, LengthSource.Deezer));
        Assert.Equal((417, LengthSource.Deezer), SongLength.Shown(routing));

        Assert.False(SongLength.Remember(routing, 418, LengthSource.LastFm));
        Assert.False(SongLength.Remember(routing, 430, LengthSource.Video));
        Assert.Equal((417, LengthSource.Deezer), SongLength.Shown(routing));
    }

    [Fact]
    public void Remember_NeverStoresAMissingZeroOrImplausibleLength()
    {
        var routing = new SoulseekRouting { Artist = "A", Title = "T" };

        Assert.False(SongLength.Remember(routing, null, LengthSource.Deezer));
        Assert.False(SongLength.Remember(routing, 0, LengthSource.LastFm));
        Assert.False(SongLength.Remember(routing, 3600, LengthSource.Video));
        Assert.False(SongLength.Remember(routing, 12, LengthSource.Video));

        Assert.Equal((null, LengthSource.None), SongLength.Shown(routing));
    }

    [Fact]
    public void Remember_ALongMetadataLengthIsNotBoundLikeAVideo()
    {
        // The range guards against a video carrying more than the song. A catalog length
        // for a 25-minute track is simply the track.
        var routing = new SoulseekRouting { Artist = "A", Title = "T" };
        Assert.True(SongLength.Remember(routing, 1500, LengthSource.Deezer));
    }

    // ---- The registry keeps it ----------------------------------------------------

    [Fact]
    public void Registry_ReMintingASong_KeepsTheLengthALookupFound()
    {
        var registry = new ExternalIdRegistry();
        var id = registry.Register(new SoulseekRouting { Artist = "Justice", Title = "Genesis" });
        Assert.True(registry.RememberLength(id, 234, LengthSource.Deezer));

        // The next search mints a fresh routing for the same song.
        var again = registry.Register(new SoulseekRouting { Artist = "Justice", Title = "Genesis" });

        Assert.Equal(id, again);
        Assert.Equal((234, LengthSource.Deezer), SongLength.Shown(registry.Lookup(id)!));
    }

    [Fact]
    public void Registry_LearnedAlbumAndLengthSurviveRemintAndRestartWithoutChangingDownloadExpectation()
    {
        var path = Path.Combine(Path.GetTempPath(), "octo-display-" + Guid.NewGuid() + ".json");
        try
        {
            string id;
            using (var first = new ExternalIdRegistry(path))
            {
                id = first.Register(new SoulseekRouting { Artist = "Lime Garden", Title = "Love Song" });
                first.RememberDeezerTrack(id, "42", "One More Thing", 191);
                Assert.Equal(id, first.Register(new SoulseekRouting { Artist = "Lime Garden", Title = "Love Song" }));
                Assert.Null(first.Lookup(id)!.Duration);
            }

            using var restarted = new ExternalIdRegistry(path);
            var frozen = new Song { Id = id, Artist = "Lime Garden", Title = "Love Song", Album = "", Duration = 180 };
            Assert.Equal(("One More Thing", (int?)191), restarted.GetDisplayMetadata(frozen));
            Assert.Equal("", frozen.Album);
            Assert.Equal(180, frozen.Duration);
            frozen.Album = "Explicit Release";
            Assert.Equal("Explicit Release", restarted.GetDisplayMetadata(frozen).Album);
            frozen.IsLocal = true;
            Assert.Equal(("Explicit Release", (int?)180), restarted.GetDisplayMetadata(frozen));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Registry_RememberLength_LeavesTheDownloadExpectationAlone()
    {
        // Duration is what a download ranks and checks peer files against. A length found
        // only so a row can show one must not start rejecting files.
        var registry = new ExternalIdRegistry();
        var id = registry.Register(new SoulseekRouting { Artist = "Kavinsky", Title = "Prelude" });

        registry.RememberLength(id, 95, LengthSource.LastFm);

        var routing = registry.Lookup(id)!;
        Assert.Null(routing.Duration);
        Assert.Null(routing.YouTubeId);
        Assert.Equal(95, routing.ShownDuration);
    }

    [Fact]
    public void Registry_RememberLength_IgnoresUnknownIdsAndNonSongs()
    {
        var registry = new ExternalIdRegistry();
        var album = registry.Register(new SoulseekRouting { Kind = RoutingKind.Album, Artist = "A", Album = "B" });

        Assert.False(registry.RememberLength("nope", 200, LengthSource.Deezer));
        Assert.False(registry.RememberLength(album, 200, LengthSource.Deezer));
    }

    [Fact]
    public void Registry_LengthOutlivesARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "octo-lengths-" + Guid.NewGuid() + ".json");
        try
        {
            string id;
            using (var first = new ExternalIdRegistry(path))
            {
                id = first.Register(new SoulseekRouting { Artist = "Daft Punk", Title = "Emotion" });
                first.RememberLength(id, 417, LengthSource.Video);
            }

            using var second = new ExternalIdRegistry(path);
            Assert.Equal((417, LengthSource.Video), SongLength.Shown(second.Lookup(id)!));
        }
        finally { File.Delete(path); }
    }

    // ---- Filled from each source, in order ----------------------------------------

    [Fact]
    public async Task Placeholder_CarriesTheRememberedLength_InsteadOfThePlaceholder()
    {
        var fixture = new LengthFixture();
        var svc = fixture.Service();
        var first = (await svc.SearchSongsByArtistTitleAsync("Justice", "Genesis")).Single();
        Assert.Equal(180, first.Duration);

        fixture.Registry.RememberLength(first.Id, 234, LengthSource.Deezer);

        var next = (await svc.SearchSongsByArtistTitleAsync("Justice", "Genesis")).Single();
        Assert.Equal(first.Id, next.Id);
        Assert.Equal(234, next.Duration);
    }

    [Fact]
    public async Task Placeholder_HandedInLength_LosesToARememberedDeezerLength()
    {
        var fixture = new LengthFixture();
        var svc = fixture.Service();
        var first = (await svc.SearchSongsByArtistTitleAsync("Mr. Oizo", "Positif", 1, 200)).Single();
        Assert.Equal(200, first.Duration);

        fixture.Registry.RememberLength(first.Id, 207, LengthSource.Deezer);

        var next = (await svc.SearchSongsByArtistTitleAsync("Mr. Oizo", "Positif", 1, 200)).Single();
        Assert.Equal(207, next.Duration);
    }

    [Fact]
    public async Task CompleteSongLengths_Deezer_IsTriedFirst()
    {
        var fixture = new LengthFixture { Deezer = { ["Justice Genesis"] = 234 }, LastFm = { ["Justice|Genesis"] = 240 } };
        var song = await fixture.StationRowAsync("Justice", "Genesis");

        Assert.Equal((234, LengthSource.Deezer), fixture.Shown(song));
        Assert.DoesNotContain(fixture.Requests, url => url.Contains("track.getInfo"));
    }

    [Fact]
    public async Task CompleteSongLengths_LastFmFollowsDeezerMiss_WithoutVideoFallback()
    {
        var fixture = new LengthFixture { LastFm = { ["Kavinsky|Prelude"] = 95 } };
        var song = await fixture.StationRowAsync("Kavinsky", "Prelude");

        Assert.Equal((95, LengthSource.LastFm), fixture.Shown(song));
        var requests = fixture.Requests.ToArray();
        var deezer = Array.FindIndex(requests, url => url.Contains("api.deezer.com/search", StringComparison.Ordinal));
        var lastFm = Array.FindIndex(requests, url => url.Contains("method=track.getInfo", StringComparison.Ordinal));
        Assert.True(deezer >= 0 && lastFm > deezer);
        Assert.DoesNotContain(requests, url => url.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompleteSongLengths_NoMetadataMatch_LeavesPlaceholderWithoutVideoLookup()
    {
        var fixture = new LengthFixture();
        var song = await fixture.StationRowAsync("Nobody", "Nothing");

        Assert.Equal((null, LengthSource.None), fixture.Shown(song));
        Assert.DoesNotContain(fixture.Requests, url => url.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase));
        var next = (await fixture.Service().SearchSongsByArtistTitleAsync("Nobody", "Nothing")).Single();
        Assert.Equal(180, next.Duration);
    }

    [Fact]
    public async Task CompleteSongLengths_AnswersFromDeezersCacheInline()
    {
        // A search for the same song already asked Deezer, so this response needs no lookup.
        var fixture = new LengthFixture { Deezer = { ["Justice Genesis"] = 234 } };
        var svc = fixture.Service();
        await fixture.DeezerService.EnrichTrackAsync("Justice", "Genesis");
        var song = (await svc.SearchSongsByArtistTitleAsync("Justice", "Genesis")).Single();

        svc.CompleteSongLengths([song]);

        Assert.Equal(234, song.Duration);
    }

    [Fact]
    public async Task CompleteSongLengths_LooksUpAtMostTwentyRowsPerResponse()
    {
        var fixture = new LengthFixture();
        var svc = fixture.Service();
        var songs = new List<Song>();
        for (var i = 0; i < 30; i++)
            songs.Add((await svc.SearchSongsByArtistTitleAsync("Artist", $"Song {i}")).Single());

        svc.CompleteSongLengths(songs);
        await svc.LastLengthWarm;

        // One row, one lookup: a miss may try the title alone too, but twenty rows are asked about.
        Assert.Equal(20, fixture.Requests.Count(url => url.Contains("api.deezer.com/search?q=Artist")));
    }

    [Fact]
    public async Task ResolveTopDurations_UsesDeezerIdAndFullCatalogDuration()
    {
        // Catalog duration remains valid even when longer than the legacy video ceiling.
        var fixture = new LengthFixture
        {
            Deezer = { ["Someone Live Set"] = 1500 },
            DeezerTitles = { ["Someone Live Set"] = "Live Set" },
            DeezerIds = { ["Someone Live Set"] = "987654321" }
        };
        var svc = fixture.Service();
        var song = (await svc.SearchSongsByArtistTitleAsync("Someone", "Live Set")).Single();

        await svc.ResolveTopDurationsAsync([song]);

        var routing = fixture.Registry.Lookup(song.Id)!;
        Assert.Equal(1500, song.Duration);
        Assert.Equal("987654321", routing.DeezerId);
        Assert.Equal((1500, LengthSource.Deezer), fixture.Shown(song));
        Assert.Null(routing.YouTubeId);
    }

    [Fact]
    public async Task CompleteSongLengths_KnownLengthAndIdStillFillMissingAlbum()
    {
        var fixture = new LengthFixture
        {
            Deezer = { ["Yard Act Land Of The Blind"] = 180 },
            DeezerIds = { ["Yard Act Land Of The Blind"] = "1621264612" },
            DeezerAlbums = { ["Yard Act Land Of The Blind"] = "The Overload" },
        };
        var svc = fixture.Service();
        var song = (await svc.SearchSongsByArtistTitleAsync("Yard Act", "Land Of The Blind")).Single();
        fixture.Registry.RememberDeezerTrack(song.Id, "1621264612", duration: 180);

        svc.CompleteSongLengths([song]);
        await svc.LastLengthWarm;

        Assert.Equal("The Overload", fixture.Registry.Lookup(song.Id)!.Album);
        Assert.Equal("The Overload", (await svc.GetSongAsync("soulseek", song.Id))!.Album);
        Assert.Equal(180, (await svc.GetSongAsync("soulseek", song.Id))!.Duration);
        Assert.Contains(fixture.Requests, url => url.Contains("/track/1621264612"));
        Assert.DoesNotContain(fixture.Requests, url => url.Contains("/search"));
        Assert.Equal("", song.Album); // Background work must not mutate a response being serialized.
    }
}

/// <summary>
/// Deezer and Last.fm as far as a length lookup needs them. Anything not listed is a miss,
/// answered the way each service answers one.
/// </summary>
internal sealed class LengthFixture
{
    public Dictionary<string, int> Deezer { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DeezerIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DeezerTitles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DeezerAlbums { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> LastFm { get; } = new(StringComparer.OrdinalIgnoreCase);
    public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();
    public ExternalIdRegistry Registry { get; } = new();
    public DeezerMetadataService DeezerService { get; }
    private readonly HttpMessageHandler _handler;
    private SoulseekMetadataService? _service;

    public LengthFixture()
    {
        _handler = new Handler(this);
        DeezerService = new DeezerMetadataService(new Factory(_handler),
            TestOptions.Monitor(new MetadataSettings()), new Mock<ILogger<DeezerMetadataService>>().Object);
    }

    public SoulseekMetadataService Service()
    {
        if (_service is not null) return _service;
        var lastFm = new LastFmService(new HttpClient(_handler),
            TestOptions.Monitor(new LastFmSettings { ApiKey = "key" }),
            Options.Create(new MetadataSettings()), new Mock<ILogger<LastFmService>>().Object);
        return _service = new SoulseekMetadataService(
            Registry, DeezerService,
            new CoverArtAggregator(Array.Empty<ICoverArtSource>(), new Mock<ILogger<CoverArtAggregator>>().Object),
            new Mock<ILogger<SoulseekMetadataService>>().Object, lastFm);
    }

    /// <summary>A station row with no length of its own, completed and looked up.</summary>
    public async Task<Song> StationRowAsync(string artist, string title)
    {
        var svc = Service();
        var song = (await svc.SearchSongsByArtistTitleAsync(artist, title)).Single();
        svc.CompleteSongLengths([song]);
        await svc.LastLengthWarm;
        return song;
    }

    public (int? Seconds, LengthSource Source) Shown(Song song) => SongLength.Shown(Registry.Lookup(song.Id)!);

    internal static string Answer(LengthFixture fixture, Uri uri, out HttpStatusCode status)
    {
        status = HttpStatusCode.OK;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (uri.Host == "api.deezer.com")
        {
            if (uri.AbsolutePath.StartsWith("/track/", StringComparison.Ordinal))
            {
                var trackId = uri.AbsolutePath["/track/".Length..];
                var key = fixture.DeezerIds.FirstOrDefault(pair => pair.Value == trackId).Key;
                if (key is null) return "{\"error\":{\"code\":800}}";
                return JsonSerializer.Serialize(new { id = trackId, duration = fixture.Deezer[key],
                    album = new { title = fixture.DeezerAlbums.GetValueOrDefault(key) } });
            }
            var q = query["q"] ?? "";
            var hit = fixture.Deezer.FirstOrDefault(pair => pair.Key.Equals(q, StringComparison.OrdinalIgnoreCase));
            if (hit.Key is null || uri.AbsolutePath != "/search") return "{\"data\":[]}";
            // Multiword titles must not masquerade as another artist's last-word title.
            var title = fixture.DeezerTitles.GetValueOrDefault(hit.Key) ?? hit.Key[(hit.Key.LastIndexOf(' ') + 1)..];
            var split = hit.Key.Length - title.Length - 1;
            var id = fixture.DeezerIds.TryGetValue(hit.Key, out var configuredId) ? configuredId
                : (700001 + fixture.Deezer.Keys.ToList().IndexOf(hit.Key)).ToString();
            return JsonSerializer.Serialize(new
            {
                data = new[] { new { id, title, duration = hit.Value,
                    artist = new { name = hit.Key[..split] },
                    album = new { title = fixture.DeezerAlbums.GetValueOrDefault(hit.Key) } } }
            });
        }
        if (uri.Host == "ws.audioscrobbler.com")
        {
            var key = $"{query["artist"]}|{query["track"]}";
            if (query["method"] == "track.getInfo" && fixture.LastFm.TryGetValue(key, out var seconds))
                return $"{{\"track\":{{\"name\":\"{query["track"]}\",\"duration\":\"{seconds * 1000}\",\"artist\":{{\"name\":\"{query["artist"]}\"}}}}}}";
            return "{\"error\":6,\"message\":\"Track not found\"}";
        }
        status = HttpStatusCode.NotFound;
        return "";
    }

    private sealed class Handler(LengthFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            fixture.Requests.Enqueue(request.RequestUri!.ToString());
            var body = Answer(fixture, request.RequestUri!, out var status);
            return Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>
/// What a client actually receives: search3 and a station's getPlaylist, through the real
/// metadata service, with Navidrome owning none of the songs.
/// </summary>
public sealed class SongLengthEndpointTests
{
    [Fact]
    public async Task ExternalDetail_BothApiFamiliesUseLearnedDisplayMetadata()
    {
        await using var web = new LengthWebFactory();
        using var client = web.CreateClient();
        var song = (await web.Metadata.SearchSongsByArtistTitleAsync("Lime Garden", "Love Song")).Single();
        web.Fixture.Registry.RememberDeezerTrack(song.Id, "42", "One More Thing", 191);

        using var rest = JsonDocument.Parse(await client.GetStringAsync(
            $"/rest/getSong?id={song.Id}&u=alice&t=token&s=salt&f=json"));
        var restSong = rest.RootElement.GetProperty("subsonic-response").GetProperty("song");
        Assert.Equal("One More Thing", restSong.GetProperty("album").GetString());
        Assert.Equal(191, restSong.GetProperty("duration").GetInt32());
        using var native = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/song/{song.Id}?u=alice&t=token&s=salt"));
        Assert.Equal("One More Thing", native.RootElement.GetProperty("album").GetString());
        Assert.Equal(191, native.RootElement.GetProperty("duration").GetInt32());
        Assert.Null(web.Fixture.Registry.Lookup(song.Id)!.Duration);
    }

    [Fact]
    public async Task DiscoveryPlaylist_BothApiFamiliesExposeLearnedAlbumAndDuration()
    {
        await using var web = new LengthWebFactory();
        web.InstallStation(new LastFmRadioTrack { Artist = "Yard Act", Title = "Land Of The Blind" });
        using var client = web.CreateClient();
        // First response mints a cold discovery row; registry learns metadata afterward.
        await web.PlaylistLengthsAsync(client);
        await web.Metadata.LastLengthWarm;
        var song = (await web.Metadata.SearchSongsByArtistTitleAsync("Yard Act", "Land Of The Blind")).Single();
        web.Fixture.Registry.RememberDeezerTrack(song.Id, "1621264612", "The Overload", 180);

        using var rest = JsonDocument.Parse(await client.GetStringAsync(
            $"/rest/getPlaylist?id={web.StationId}&u=alice&t=token&s=salt&f=json"));
        var playlist = rest.RootElement.GetProperty("subsonic-response").GetProperty("playlist");
        var entry = Assert.Single(playlist.GetProperty("entry").EnumerateArray());
        Assert.Equal("The Overload", entry.GetProperty("album").GetString());
        Assert.Equal(180, entry.GetProperty("duration").GetInt32());
        Assert.Equal(180, playlist.GetProperty("duration").GetInt32());
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"username\":\"alice\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        client.DefaultRequestHeaders.Add("X-Nd-Authorization", "Bearer header." + payload + ".signature");
        using var native = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/playlist/{web.StationId}/tracks?u=alice&t=token&s=salt&_end=0"));
        var track = Assert.Single(native.RootElement.EnumerateArray());
        Assert.Equal("The Overload", track.GetProperty("album").GetString());
        Assert.Equal(180, track.GetProperty("duration").GetInt32());
    }

    [Fact]
    public async Task Search3_RowsWithoutAMetadataLength_CarryOneInTheNextResponse()
    {
        await using var web = new LengthWebFactory();
        // Keep target rows outside the twelve-row inline enrichment window; their
        // Deezer/Last.fm lengths should arrive on the next response from background lookup.
        for (var i = 1; i <= 12; i++) web.Fixture.Deezer[$"Filler Song{i}"] = 200 + i;
        web.SearchTracks.AddRange(Enumerable.Range(1, 12).Select(i => ("Filler", $"Song{i}")));
        web.SearchTracks.AddRange([("Daft Punk", "Emotion"), ("Kavinsky", "Prelude"),
            ("Nobody", "Nothing"), ("Justice", "Genesis")]);
        web.Fixture.Deezer["Daft Punk Emotion"] = 417;
        web.Fixture.LastFm["Kavinsky|Prelude"] = 95;
        web.Fixture.Deezer["Justice Genesis"] = 234;
        using var client = web.CreateClient();

        var first = await web.Search3LengthsAsync(client);
        Assert.Equal(201, first["Filler|Song1"]);
        Assert.Contains(first["Daft Punk|Emotion"], new[] { 180, 417 });
        await web.Metadata.LastLengthWarm;

        var next = await web.Search3LengthsAsync(client);
        Assert.Equal(201, next["Filler|Song1"]);
        Assert.Equal(417, next["Daft Punk|Emotion"]);
        Assert.Equal(95, next["Kavinsky|Prelude"]);
        Assert.Equal(234, next["Justice|Genesis"]);
        Assert.Equal(180, next["Nobody|Nothing"]); // no catalog or Last.fm answer; no video fallback
        Assert.DoesNotContain(web.Fixture.Requests, url => url.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetPlaylist_StationRowsWithoutALength_CarryOneInTheNextResponse()
    {
        await using var web = new LengthWebFactory();
        web.Fixture.Deezer["Justice Genesis"] = 234;
        web.Fixture.LastFm["Kavinsky|Prelude"] = 95;
        web.Fixture.Deezer["Daft Punk Emotion"] = 417;
        web.InstallStation(
            new() { Artist = "Justice", Title = "Genesis" },
            new() { Artist = "Kavinsky", Title = "Prelude" },
            new() { Artist = "Daft Punk", Title = "Emotion" },
            new() { Artist = "Mr. Oizo", Title = "Positif", Duration = 207 },
            new() { Artist = "Nobody", Title = "Nothing" });
        using var client = web.CreateClient();

        var first = await web.PlaylistLengthsAsync(client);
        Assert.Equal(207, first["Mr. Oizo|Positif"]);
        Assert.Contains(first["Justice|Genesis"], new[] { 180, 234 });
        await web.Metadata.LastLengthWarm;

        var next = await web.PlaylistLengthsAsync(client);
        Assert.Equal(234, next["Justice|Genesis"]);
        Assert.Equal(95, next["Kavinsky|Prelude"]);
        Assert.Equal(417, next["Daft Punk|Emotion"]);
        Assert.Equal(207, next["Mr. Oizo|Positif"]);
        Assert.Equal(180, next["Nobody|Nothing"]);
    }

    [Fact]
    public async Task NativeSearch_AdvertisesDefaultFlacSourceForExternalSongs()
    {
        await using var web = new LengthWebFactory();
        web.SearchTracks.Add(("Daft Punk", "Emotion"));
        web.Fixture.Deezer["Daft Punk Emotion"] = 417;
        using var client = web.CreateClient();

        var body = await client.GetStringAsync("/api/song?title=emotion&_start=0&_end=1&u=alice&t=token&s=salt");
        using var document = JsonDocument.Parse(body);
        var song = Assert.Single(document.RootElement.EnumerateArray());

        Assert.EndsWith(".flac", song.GetProperty("path").GetString());
        Assert.Equal("flac", song.GetProperty("suffix").GetString());
    }
}

internal sealed class LengthWebFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "octo-length-web-" + Guid.NewGuid());
    public LengthFixture Fixture { get; } = new();
    public List<(string Artist, string Title)> SearchTracks { get; } = [];
    public SoulseekMetadataService Metadata => (SoulseekMetadataService)Services.GetRequiredService<IMusicMetadataService>();
    public string StationId => LastFmRadioStateStore.StationId("alice", "your-mix");

    public LengthWebFactory() => Directory.CreateDirectory(_directory);

    public void InstallStation(params LastFmRadioTrack[] tracks)
    {
        Services.GetRequiredService<LastFmRadioStateStore>().ReplaceStations("alice", [new LastFmRadioStation
        {
            Id = StationId, Key = "your-mix", Name = "Your Mix", Owner = "alice",
            Kind = LastFmRadioStationKind.YourMix, Personalized = true,
            CreatedUtc = DateTime.UtcNow.AddHours(-1), ChangedUtc = DateTime.UtcNow.AddHours(-1),
            ValidUntilUtc = DateTime.UtcNow.AddDays(1), Tracks = [.. tracks]
        }]);
    }

    public async Task<Dictionary<string, int>> Search3LengthsAsync(HttpClient client)
    {
        var body = await client.GetStringAsync(
            "/rest/search3?query=genesis&songCount=50&albumCount=0&artistCount=0&u=alice&t=token&s=salt&f=json");
        using var doc = JsonDocument.Parse(body);
        return Lengths(doc.RootElement.GetProperty("subsonic-response").GetProperty("searchResult3").GetProperty("song"));
    }

    public async Task<Dictionary<string, int>> PlaylistLengthsAsync(HttpClient client)
    {
        var body = await client.GetStringAsync($"/rest/getPlaylist?id={StationId}&u=alice&t=token&s=salt&f=json");
        using var doc = JsonDocument.Parse(body);
        return Lengths(doc.RootElement.GetProperty("subsonic-response").GetProperty("playlist").GetProperty("entry"));
    }

    private static Dictionary<string, int> Lengths(JsonElement songs) =>
        songs.EnumerateArray().ToDictionary(
            song => $"{song.GetProperty("artist").GetString()}|{song.GetProperty("title").GetString()}",
            song => song.GetProperty("duration").GetInt32());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Subsonic:Url"] = "http://navidrome.test",
                ["Subsonic:AutoDetectDownloadPath"] = "false",
                ["Library:DownloadPath"] = _directory,
                ["LastFm:ApiKey"] = "key",
                ["LastFm:EnableRadio"] = "true",
                ["LastFm:EnablePersonalizedStations"] = "true",
                ["LastFm:ExposeRadioAsPlaylists"] = "true",
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new Factory(new Upstream(this)));
            services.RemoveAll<ExternalIdRegistry>();
            services.AddSingleton(Fixture.Registry);
            services.RemoveAll<LastFmRadioStateStore>();
            services.AddSingleton(provider => new LastFmRadioStateStore(
                Path.Combine(_directory, "radio-state.json"),
                provider.GetRequiredService<IOptionsMonitor<LastFmSettings>>(),
                provider.GetRequiredService<ExternalIdRegistry>(),
                provider.GetRequiredService<ILogger<LastFmRadioStateStore>>()));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_directory, true); } catch { }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Navidrome with an empty library, Last.fm's track.search, and the length
    /// sources from <see cref="LengthFixture"/>.</summary>
    private sealed class Upstream(LengthWebFactory web) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            if (uri.Host == "navidrome.test")
            {
                if (uri.AbsolutePath.Equals("/api/song", StringComparison.OrdinalIgnoreCase))
                    return Ok("[]");
                var fields = uri.AbsolutePath.Contains("search3")
                    ? ",\"searchResult3\":{\"song\":[],\"album\":[],\"artist\":[]}" : "";
                return Ok("{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\"" + fields + "}}");
            }
            if (uri.Host == "ws.audioscrobbler.com" && query["method"] == "track.search")
                return Ok(JsonSerializer.Serialize(new { results = new { trackmatches = new {
                    track = web.SearchTracks.Select(t => new { name = t.Title, artist = t.Artist }) } } }));
            if (uri.Host == "ws.audioscrobbler.com" && query["method"] == "artist.gettoptracks")
                return Ok("{\"toptracks\":{\"track\":[]}}");

            web.Fixture.Requests.Enqueue(uri.ToString());
            var body = LengthFixture.Answer(web.Fixture, uri, out var status);
            return Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private static Task<HttpResponseMessage> Ok(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
