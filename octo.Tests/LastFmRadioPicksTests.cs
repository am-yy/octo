using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Domain;
using Octo.Models.Radio;
using Octo.Models.Settings;
using Octo.Services.LastFm;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// What the listener chose (hearts and songs put in their playlists) feeds radio, and what
/// Discovery Mix keeps offering without being played steps aside.
/// </summary>
public sealed class LastFmRadioPicksTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "octo-picks-" + Guid.NewGuid());
    private static readonly DateTime Now = DateTime.UtcNow;

    public LastFmRadioPicksTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    [Fact]
    public void MergePlaylist_AWholeDiscographyCountsAsACoupleOfPicksOfItsArtist()
    {
        var songs = Enumerable.Range(0, 12).Select(index => Pick("Prolific", $"Track {index}"))
            .Append(Pick("Pair", "A")).Append(Pick("Pair", "B")).Append(Pick("Single", "The One")).ToArray();

        var merged = LastFmRadioStateStore.MergePlaylist(null, Read("p", "Mix", Now, songs), out var added);

        Assert.Equal(15, added);
        Assert.Equal(LastFmRadioStateStore.BatchArtistShare,
            merged.Songs.Where(song => song.Artist == "Prolific").Sum(song => song.Weight), 6);
        Assert.All(merged.Songs.Where(song => song.Artist != "Prolific"), song => Assert.Equal(1, song.Weight));
        Assert.All(merged.Songs, song => Assert.Equal(Now, song.PickedAtUtc));
        Assert.Equal(Now, merged.ChangedUtc);
    }

    [Fact]
    public void MergePlaylist_ASongStillThereKeepsWhenItWasAdded_OnlyNewSongsTakeTheNewDate()
    {
        var first = Now.AddDays(-20);
        var known = LastFmRadioStateStore.MergePlaylist(null,
            Read("p", "Mix", first, Pick("Kept", "Old Song"), Pick("Gone", "Removed Song")), out _);

        var merged = LastFmRadioStateStore.MergePlaylist(known, Read("p", "Mix renamed", Now,
            Pick("Kept", "Old Song"), Pick("Kept", "Old Song"), Pick("Fresh", "New Song"), Pick("", "Blank")), out var added);

        Assert.Equal(1, added);
        Assert.Equal("Mix renamed", merged.Name);
        Assert.Equal(["Old Song", "New Song"], merged.Songs.Select(song => song.Title));
        Assert.Equal(first, merged.Songs[0].PickedAtUtc);
        Assert.Equal(Now, merged.Songs[1].PickedAtUtc);
    }

    [Fact]
    public void MergePlaylist_AnUnreadPlaylistKeepsItsLastReading_AndOneNeverReadIsReadNextTime()
    {
        var known = LastFmRadioStateStore.MergePlaylist(null, Read("p", "Mix", Now.AddDays(-3), Pick("Kept", "Song")), out _);

        var kept = LastFmRadioStateStore.MergePlaylist(known, new("p", "New name", Now, null), out var added);
        Assert.Equal(0, added);
        Assert.Equal("New name", kept.Name);
        Assert.Equal(Now.AddDays(-3), kept.ChangedUtc);
        Assert.Single(kept.Songs);

        var never = LastFmRadioStateStore.MergePlaylist(null, new("q", "Never", Now, null), out _);
        Assert.Empty(never.Songs);
        Assert.Equal(default, never.ChangedUtc);
    }

    [Fact]
    public void ApplyPicks_ADeletedPlaylistTakesItsSongs_AndANullListingKeepsTheLastReading()
    {
        var state = Store();
        Assert.Equal(3, state.ApplyPicks("alice",
            [Read("a", "A", Now, Pick("One", "1"), Pick("Two", "2")), Read("b", "B", Now, Pick("Three", "3"))], null));
        Assert.Equal(0, state.ApplyPicks("alice", [Read("a", "A", Now, Pick("One", "1"), Pick("Two", "2"))], null));
        Assert.Equal(["a"], state.GetUser("alice").Playlists.Select(playlist => playlist.Id));

        Assert.Equal(0, state.ApplyPicks("alice", null, null));
        Assert.Equal(2, Assert.Single(state.GetUser("alice").Playlists).Songs.Count);
        Assert.Equal(Now, state.PlaylistMarks("alice")["a"]);
    }

    [Fact]
    public void Hearts_SavedHeartsAreReplacedOnEveryRead_StarsStayUntilUnstarred()
    {
        var state = Store();
        Assert.True(state.RecordHeart("alice", Pick("Starred", "Song", "local-1", "star", Now.AddDays(-3))));
        // Starring it again moves it to now; it is still one heart.
        Assert.True(state.RecordHeart("alice", Pick("Starred", "Song", "local-1", "star", Now.AddDays(-1))));
        Assert.Equal(1, state.ApplyPicks("alice", null, [Pick("Outside", "Old Heart", "ext-1", null, Now.AddDays(-2))]));
        Assert.Equal(1, state.ApplyPicks("alice", null, [Pick("Outside", "New Heart", "ext-2", null, Now)]));

        var hearts = state.GetUser("alice").Hearts;
        Assert.Equal(["New Heart", "Song"], hearts.Select(heart => heart.Title));
        Assert.Equal([LastFmRadioStateStore.SavedHeart, "star"], hearts.Select(heart => heart.Origin));
        Assert.Equal(Now.AddDays(-1), hearts[1].PickedAtUtc);

        Assert.True(state.RemoveHearts("alice", ["local-1"]));
        Assert.False(state.RemoveHearts("alice", ["local-1"]));
        Assert.Equal(["New Heart"], state.GetUser("alice").Hearts.Select(heart => heart.Title));
    }

    [Fact]
    public void Picks_ASongHeartedAndInAPlaylistCountsOnce_AtItsStrongestAndNewest()
    {
        var user = new LastFmRadioUserState
        {
            Hearts = [Pick("Both", "Song", "local-1", "star", Now.AddDays(-5))],
            Playlists =
            [
                new() { Id = "p", Songs = [Pick("Both", "Song", "local-1", null, Now.AddDays(-1), weight: 0.2), Pick("Only", "Listed")] },
            ],
        };

        var picks = LastFmRadioRecommendationService.Picks(user);

        Assert.Equal(2, picks.Count);
        var both = picks.Single(pick => pick.Artist == "Both");
        Assert.Equal(1, both.Weight);
        Assert.Equal(Now.AddDays(-1), both.PickedAtUtc);
    }

    [Fact]
    public void CountOffers_CountsOnlyArtistsTheListenerGotPast_AndAMixNeverPlayedOffersNobody()
    {
        var station = Discovery("Discovery Mix", Now.AddHours(-2),
            ("Passed", "One"), ("Passed", "Two"), ("Heard", "Hit"), ("Not Reached", "Later"));
        var user = new LastFmRadioUserState { Username = "alice" };

        LastFmRadioStateStore.CountOffers(user, station, Now);
        Assert.Empty(user.Offers);

        // A play from before the mix was built is not a play from it.
        user.Plays.Add(new LastFmRadioPlay { Artist = "Heard", Title = "Hit", PlayedAtUtc = Now.AddHours(-3) });
        LastFmRadioStateStore.CountOffers(user, station, Now);
        Assert.Empty(user.Offers);

        user.Plays.Add(new LastFmRadioPlay { Artist = "Heard", Title = "Hit", PlayedAtUtc = Now.AddHours(-1) });
        LastFmRadioStateStore.CountOffers(user, station, Now);
        var offer = Assert.Single(user.Offers);
        Assert.Equal("Passed", offer.Artist);
        Assert.Equal(1, offer.Times);
        Assert.Null(offer.RestUntilUtc);
    }

    [Fact]
    public void ReplaceStations_AnArtistPassedOverInThreeMixesRests_AndAnUnchangedRebuildCountsNothing()
    {
        var state = Store();
        state.RecordPlay("alice", new LastFmRadioPlay { Artist = "Heard", Title = "Hit", PlayedAtUtc = Now.AddHours(-1) });
        LastFmRadioStation Mix(int round) => Discovery($"Discovery Mix {round}", Now.AddHours(-2), ("Passed", "One"), ("Heard", "Hit"));
        LastFmRadioOffer? Offer() => state.GetUser("alice").Offers.SingleOrDefault();

        state.ReplaceStations("alice", [Mix(1)]);
        state.ReplaceStations("alice", [Mix(1)]);
        Assert.Null(Offer());

        state.ReplaceStations("alice", [Mix(2)]);
        state.ReplaceStations("alice", [Mix(3)]);
        Assert.Equal(2, Offer()!.Times);
        Assert.Null(Offer()!.RestUntilUtc);

        state.ReplaceStations("alice", [Mix(4)]);
        Assert.Equal(0, Offer()!.Times);
        Assert.True(Offer()!.RestUntilUtc > Now + LastFmRadioStateStore.OfferRest - TimeSpan.FromDays(1));

        // While it rests it is not counted again.
        state.ReplaceStations("alice", [Mix(5)]);
        Assert.Equal(0, Offer()!.Times);
    }

    [Fact]
    public void ChooseFrontierSeeds_RecentPicksLead_OldPicksDoNot_AndTheRestIsADrawFromTheStrongest()
    {
        var scores = Enumerable.Range(0, 40).ToDictionary(index => $"Artist {index}", index => 40.0 - index);
        var picks = new[]
        {
            Pick("Picked New", "a", at: Now.AddDays(-1)),
            Pick("Picked New", "b", at: Now.AddDays(-2)),
            Pick("Picked Old", "c", at: Now.AddDays(-90)),
            Pick("Artist 3", "d", at: Now.AddDays(-3)),
        };

        var seeds = LastFmRadioRecommendationService.ChooseFrontierSeeds(scores, picks, new Random(1), Now);

        Assert.Equal(("Picked New", 1d), seeds[0]);
        Assert.Equal(("Artist 3", 1d), seeds[1]);
        Assert.DoesNotContain(seeds, seed => seed.Artist == "Picked Old");
        Assert.Equal(LastFmRadioRecommendationService.FrontierSeeds, seeds.Count);
        Assert.Equal(seeds.Count, seeds.Select(seed => seed.Artist).Distinct().Count());
        Assert.All(seeds.Skip(2), seed =>
            Assert.True(int.Parse(seed.Artist["Artist ".Length..]) < LastFmRadioRecommendationService.FrontierSeedPool));

        // Different draws start from different artists, not the same top ten every time.
        var reached = Enumerable.Range(0, 20)
            .SelectMany(draw => LastFmRadioRecommendationService.ChooseFrontierSeeds(scores, [], new Random(draw), Now))
            .Select(seed => seed.Artist).ToHashSet();
        Assert.True(reached.Count > LastFmRadioRecommendationService.FrontierSeeds + 5, string.Join(", ", reached));
    }

    [Fact]
    public void RecentPlaylists_AreThoseThatGainedASongLately_NewestFirst()
    {
        LastFmRadioPlaylist Playlist(string name, int songs, int daysAgo) => new()
        {
            Id = name, Name = name,
            Songs = Enumerable.Range(0, songs).Select(index => Pick("Artist", $"{name} {index}", at: Now.AddDays(-daysAgo - index))).ToList(),
        };

        var chosen = LastFmRadioRecommendationService.RecentPlaylists(
            [Playlist("older", 5, 10), Playlist("newer", 5, 1), Playlist("stale", 5, 45), Playlist("tiny", 2, 0), Playlist("third", 3, 20)],
            2, Now);

        Assert.Equal(["newer", "older"], chosen.Select(playlist => playlist.Name));
    }

    /// <summary>Seed A and Seed B are played. Shared Y is a neighbour of both, Fresh X of one,
    /// and Hop Z only of Fresh X.</summary>
    private static readonly Dictionary<string, (string Name, double Match)[]> Graph = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Seed A"] = [("Seed B", .8), ("Fresh X", .7), ("Shared Y", .5)],
        ["Seed B"] = [("Shared Y", .6), ("Seed A", .9)],
        ["Fresh X"] = [("Hop Z", .9), ("Seed A", .5)],
        ["Shared Y"] = [("Fresh X", .4)],
    };

    [Fact]
    public async Task DiscoveryMix_LeavesOutARestingArtist()
    {
        static LastFmRadioStation Mix(IEnumerable<LastFmRadioStation> stations) =>
            Assert.Single(stations, station => station.Kind == LastFmRadioStationKind.Discovery);

        var usual = Mix(await Build(new PicksHandler(Graph)));
        var rested = Mix(await Build(new PicksHandler(Graph), state => Rest(state, "Shared Y")));

        Assert.Contains(usual.Tracks, track => track.Artist == "Shared Y");
        Assert.DoesNotContain(rested.Tracks, track => track.Artist == "Shared Y");
        Assert.Contains(rested.Tracks, track => track.Artist == "Fresh X");
    }

    [Fact]
    public async Task DiscoveryMix_ReachesPastEachNewArtistsTopThree()
    {
        var ranks = new List<int>();
        for (var seed = 0; seed < 10; seed++)
        {
            var mix = Assert.Single(await Build(new PicksHandler(Graph), seed: seed),
                station => station.Kind == LastFmRadioStationKind.Discovery);
            ranks.AddRange(mix.Tracks.Where(track => track.Title.Contains("-top-"))
                .Select(track => int.Parse(track.Title[(track.Title.LastIndexOf('-') + 1)..])));
        }

        Assert.NotEmpty(ranks);
        Assert.Contains(ranks, rank => rank >= 3);
    }

    [Fact]
    public async Task DiscoveryMix_StartsFromWhatTheListenerPickedLately()
    {
        var graph = new Dictionary<string, (string Name, double Match)[]>(Graph, StringComparer.OrdinalIgnoreCase)
        {
            ["Picked P"] = [("Fresh P", .9)],
        };

        var discovery = Assert.Single(await Build(new PicksHandler(graph),
                state => state.RecordHeart("alice", Pick("Picked P", "Loved", "lib-p", "star", DateTime.UtcNow))),
            station => station.Kind == LastFmRadioStationKind.Discovery);

        Assert.Equal("Picked P", discovery.Seeds[0]);
        Assert.Contains(discovery.Tracks, track => track.Artist == "Fresh P");
        // A picked artist is known, not a discovery.
        Assert.DoesNotContain(discovery.Tracks, track => track.Artist == "Picked P");
    }

    [Fact]
    public async Task PlaylistStation_IsMoreLikeARecentPlaylist_WithoutItsOwnSongs()
    {
        var similar = new Dictionary<string, (string Artist, string Title)[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["List One|Song 1"] = [("List Two", "Song 2"), ("Near 1", "N1"), ("Near 2", "N2")],
            ["List Two|Song 2"] = [("List One", "Song 1"), ("Near 3", "N3"), ("Near 4", "N4")],
            ["List Three|Song 3"] = [("Near 5", "N5"), ("Near 1", "N1b")],
        };
        LastFmRadioPick[] Songs() => [Pick("List One", "Song 1"), Pick("List Two", "Song 2"), Pick("List Three", "Song 3")];

        var stations = await Build(new PicksHandler(Graph, similar), state => state.ApplyPicks("alice",
            [Read("rock-id", "rock", DateTime.UtcNow.AddDays(-2), Songs()), Read("old-id", "old", DateTime.UtcNow.AddDays(-90), Songs())],
            null));

        var station = Assert.Single(stations, station => station.Kind == LastFmRadioStationKind.Playlist);
        Assert.Equal("More like rock", station.Name);
        Assert.Contains("List One", station.Seeds);
        Assert.NotEmpty(station.Tracks);
        Assert.All(station.Tracks, track => Assert.StartsWith("Near ", track.Artist));
    }

    [Fact]
    public async Task PickReader_ReadsOnlyTheListenersOwnHandMadePlaylists_AndOnlyWhenTheyChange()
    {
        var actions = new LibraryActionSettings
        {
            Enabled = true,
            Actions = [new LibraryActionDefinition { Action = LibraryAction.Delete, Name = "Delete", Enabled = true }],
        };
        var actionTitle = Assert.Single(LastFmRadioPickReader.ManagedNames(actions));
        var changed = DateTime.UtcNow.AddHours(-1);
        var navidrome = new FakeNavidrome
        {
            Playlists =
            [
                new("mine", "Road Trip", "Alice", changed, false),
                new("theirs", "Bob's", "bob", changed, false),
                new("smart", "Recently Added", "alice", changed, true),
                new("action", actionTitle, "alice", changed, false),
            ],
        };
        foreach (var id in new[] { "mine", "theirs", "smart", "action" })
            navidrome.Songs[id] = [new($"{id}-1", "Song", $"Artist {id}", "Album", "Rock", 200)];
        var state = Store();
        var reader = new LastFmRadioPickReader(navidrome, state, TestOptions.Monitor(actions),
            NullLogger<LastFmRadioPickReader>.Instance);

        Assert.Equal(1, await reader.SyncAsync("alice", CancellationToken.None));
        Assert.Equal(["mine"], navidrome.SongReads);
        var playlist = Assert.Single(state.GetUser("alice").Playlists);
        Assert.Equal("Road Trip", playlist.Name);
        Assert.Equal("Artist mine", Assert.Single(playlist.Songs).Artist);
        Assert.Equal(changed, playlist.Songs[0].PickedAtUtc);

        // Unchanged since the last reading: not read again.
        Assert.Equal(0, await reader.SyncAsync("alice", CancellationToken.None));
        Assert.Single(navidrome.SongReads);

        // Changed but unreadable: the last reading and its date stand, so it is read next time.
        navidrome.Playlists = [new("mine", "Road Trip", "alice", changed.AddMinutes(5), false)];
        navidrome.Songs.Remove("mine");
        await reader.SyncAsync("alice", CancellationToken.None);
        Assert.Equal(2, navidrome.SongReads.Count);
        Assert.Single(Assert.Single(state.GetUser("alice").Playlists).Songs);
        Assert.Equal(changed, state.PlaylistMarks("alice")["mine"]);

        // Navidrome not answering: the last reading stands.
        navidrome.Playlists = null;
        await reader.SyncAsync("alice", CancellationToken.None);
        Assert.Single(state.GetUser("alice").Playlists);

        // Gone from Navidrome: gone from radio.
        navidrome.Playlists = [];
        await reader.SyncAsync("alice", CancellationToken.None);
        Assert.Empty(state.GetUser("alice").Playlists);
    }

    [Fact]
    public async Task PickReader_PrefersOctosOwnCopy_WithItsOutsideSongs_AndReadsOctosHearts()
    {
        var saves = new ExternalSaveStore(Path.Combine(_directory, "external-saves.json"), NullLogger<ExternalSaveStore>.Instance);
        await saves.UpsertPlaylistAsync(new ExternalSavedPlaylist
        {
            UserId = "alice", Owner = "alice", Id = "kept", Name = "Kept",
            Tracks =
            [
                new() { SongId = "local-1", Song = new Song { Id = "local-1", Artist = "Local Artist", Title = "Here" } },
                new() { SongId = "outside-1", Song = new Song { Id = "outside-1", Artist = "Outside Artist", Title = "Not Yet" } },
            ],
        });
        await saves.SetHeartAsync("alice", new Song { Id = "outside-2", Artist = "Hearted Artist", Title = "Loved" }, true);
        await saves.SetHeartAsync("bob", new Song { Id = "outside-3", Artist = "Bob's Artist", Title = "Theirs" }, true);
        var navidrome = new FakeNavidrome { Playlists = [new("kept", "Kept", "alice", DateTime.UtcNow.AddDays(-1), false)] };
        navidrome.Songs["kept"] = [new("local-1", "Here", "Local Artist", null, null, null)];
        var state = Store();
        var reader = new LastFmRadioPickReader(navidrome, state, TestOptions.Monitor(new LibraryActionSettings()),
            NullLogger<LastFmRadioPickReader>.Instance, saves);

        Assert.Equal(3, await reader.SyncAsync("alice", CancellationToken.None));

        Assert.Empty(navidrome.SongReads);
        var user = state.GetUser("alice");
        Assert.Equal(["Here", "Not Yet"], Assert.Single(user.Playlists).Songs.Select(song => song.Title));
        var heart = Assert.Single(user.Hearts);
        Assert.Equal(("Hearted Artist", LastFmRadioStateStore.SavedHeart), (heart.Artist, heart.Origin));
    }

    [Fact]
    public void RadioPlaylistRows_ReadNavidromesOwnerUpdateTimeAndSmartPlaylists()
    {
        using var playlists = JsonDocument.Parse("""
            [{"id":"p1","name":"Mine","ownerName":"alice","updatedAt":"2026-11-01T10:00:00.123Z"},
             {"id":"p2","name":"Smart","ownerName":"alice","updatedAt":"2026-11-01T10:00:00Z","rules":{"all":[]}},
             {"id":"","name":"No id"}]
            """);
        using var songs = JsonDocument.Parse("""
            [{"mediaFileId":"m1","title":"T","artist":"A","album":"B","genre":"G","duration":201.6},{"title":"no id"}]
            """);

        var rows = RadioPlaylistRows.ParsePlaylists(playlists.RootElement);
        var tracks = RadioPlaylistRows.ParseSongs(songs.RootElement);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new RadioPlaylist("p1", "Mine", "alice", new DateTime(2026, 11, 1, 10, 0, 0, 123, DateTimeKind.Utc), false), rows[0]);
        Assert.True(rows[1].Smart);
        Assert.Equal(new RadioPlaylistSong("m1", "T", "A", "B", "G", 202), Assert.Single(tracks));
    }

    private LastFmRadioStateStore Store() => new(Path.Combine(_directory, Guid.NewGuid() + ".json"),
        TestOptions.Monitor(new LastFmSettings()), new ExternalIdRegistry(), new Mock<ILogger<LastFmRadioStateStore>>().Object);

    private static LastFmRadioPick Pick(string artist, string title, string? id = null, string? origin = null,
        DateTime? at = null, double weight = 1) => new()
    {
        SongId = id ?? $"{artist}-{title}", Artist = artist, Title = title, Origin = origin ?? "",
        PickedAtUtc = at ?? Now, Weight = weight,
    };

    private static LastFmRadioStateStore.PlaylistRead Read(string id, string name, DateTime changed,
        params LastFmRadioPick[] songs) => new(id, name, changed, songs);

    private static LastFmRadioStation Discovery(string name, DateTime built, params (string Artist, string Title)[] tracks) => new()
    {
        Id = "test-discovery", Key = "discovery", Name = name, Owner = "alice", Kind = LastFmRadioStationKind.Discovery,
        Personalized = true, CreatedUtc = built, ChangedUtc = built, ValidUntilUtc = built.AddHours(12),
        Tracks = tracks.Select(track => new LastFmRadioTrack { Artist = track.Artist, Title = track.Title }).ToList(),
    };

    /// <summary>Discovery Mix passes the artist over in enough played mixes that they rest.</summary>
    private static void Rest(LastFmRadioStateStore state, string artist)
    {
        for (var round = 0; round <= LastFmRadioStateStore.OfferLimit; round++)
            state.ReplaceStations("alice", [Discovery($"Discovery Mix {round}", DateTime.UtcNow.AddDays(-1),
                (artist, "Passed Over"), ("Seed A", "Seed A song 0"))]);
        Assert.Contains(state.GetUser("alice").Offers, offer => offer.Artist == artist && offer.RestUntilUtc > DateTime.UtcNow);
    }

    private async Task<IReadOnlyList<LastFmRadioStation>> Build(PicksHandler handler,
        Action<LastFmRadioStateStore>? prepare = null, int seed = 7)
    {
        var settings = TestOptions.Monitor(new LastFmSettings
        {
            ApiKey = "test-key", EnablePersonalizedStations = true, EnableDiscoveryStations = false, MinimumPlays = 3,
            RadioTrackCount = 50, ArtistStationCount = 0, GenreStationCount = 0,
        });
        var state = new LastFmRadioStateStore(Path.Combine(_directory, Guid.NewGuid() + ".json"), settings,
            new ExternalIdRegistry(), new Mock<ILogger<LastFmRadioStateStore>>().Object);
        for (var index = 0; index < 4; index++)
            foreach (var artist in new[] { "Seed A", "Seed B" })
                state.RecordPlay("alice", new LastFmRadioPlay
                {
                    Artist = artist, Title = $"{artist} song {index}", Genre = "Indie",
                    PlayedAtUtc = DateTime.UtcNow.AddHours(-index),
                });
        prepare?.Invoke(state);
        var lastFm = new LastFmService(new HttpClient(handler), settings,
            Options.Create(new MetadataSettings { Language = "en" }), new Mock<ILogger<LastFmService>>().Object);
        var service = new LastFmRadioRecommendationService(lastFm, state, settings,
            new Mock<ILogger<LastFmRadioRecommendationService>>().Object)
        {
            Randomizer = () => new Random(seed),
        };
        return await service.BuildAsync("alice");
    }

    /// <summary>Last.fm with a similar-artist graph, ten top tracks an artist (as many as asked
    /// for), and similar tracks for the songs given.</summary>
    private sealed class PicksHandler(Dictionary<string, (string Name, double Match)[]> artists,
        Dictionary<string, (string Artist, string Title)[]>? tracks = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var artist = query["artist"] ?? "";
            var limit = int.TryParse(query["limit"], out var asked) ? asked : 3;
            object body = query["method"] switch
            {
                "artist.getsimilar" => new { similarartists = new { artist = artists.GetValueOrDefault(artist, [])
                    .Select(item => new { name = item.Name, match = item.Match }).ToArray() } },
                "artist.gettoptracks" => new { toptracks = new { track = Enumerable.Range(0, Math.Min(limit, 10))
                    .Select(index => new { name = $"{artist}-top-{index}", artist = new { name = artist } }).ToArray() } },
                "track.getsimilar" => new { similartracks = new { track =
                    (tracks?.GetValueOrDefault($"{artist}|{query["track"]}") ?? [])
                    .Select(item => new { name = item.Title, match = 0.8, duration = 180000, artist = new { name = item.Artist } })
                    .ToArray() } },
                _ => new { },
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }
    }

    private sealed class FakeNavidrome : IRadioPlaylistSource
    {
        public List<RadioPlaylist>? Playlists { get; set; } = [];
        public Dictionary<string, List<RadioPlaylistSong>> Songs { get; } = new();
        public List<string> SongReads { get; } = [];

        public Task<IReadOnlyList<RadioPlaylist>?> ListRadioPlaylistsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RadioPlaylist>?>(Playlists);

        public Task<IReadOnlyList<RadioPlaylistSong>?> ListRadioPlaylistSongsAsync(string playlistId, CancellationToken ct)
        {
            SongReads.Add(playlistId);
            return Task.FromResult<IReadOnlyList<RadioPlaylistSong>?>(Songs.GetValueOrDefault(playlistId));
        }
    }
}
