using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Octo.Models.Domain;
using Octo.Models.Radio;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.LastFm;
using Octo.Services.Library;

namespace Octo.Tests;

/// <summary>"Keep these?": outside songs played often, on more than one day, and never added.</summary>
public sealed class KeepThesePlaylistTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static LastFmRadioPlay Play(string title, double daysAgo, bool local = false, string source = "scrobble",
        bool hearted = false) => new()
    {
        Artist = "Band", Title = title, IsLocal = local, Source = source, Hearted = hearted,
        PlayedAtUtc = Now.AddDays(-daysAgo),
    };

    [Fact]
    public void ThreePlaysOnTwoDays_IsAskedAbout_ThreeOnOneDayIsNot()
    {
        var picks = KeepThesePlaylistService.Pick(
        [
            Play("Twice a week", 1), Play("Twice a week", 1.1), Play("Twice a week", 3),
            Play("One evening", 1), Play("One evening", 1.01), Play("One evening", 1.02),
        ], None, Now);
        var (play, plays) = Assert.Single(picks);
        Assert.Equal("Twice a week", play.Title);
        Assert.Equal(3, plays);
    }

    [Fact]
    public void OwnedHeartedDismissedStaleAndBootstrapSongs_AreLeftOut()
    {
        var dismissed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { LastFmRadioSeedNormalizer.TrackKey("Band", "One star") };
        IEnumerable<LastFmRadioPlay> Thrice(string title, Func<int, LastFmRadioPlay>? make = null) =>
            Enumerable.Range(0, 3).Select(day => make?.Invoke(day) ?? Play(title, day + 1));
        var picks = KeepThesePlaylistService.Pick(
            Thrice("Library", day => Play("Library", day + 1, local: true))
                .Concat(Thrice("Hearted", day => Play("Hearted", day + 1, hearted: day == 2)))
                .Concat(Thrice("One star"))
                .Concat(Thrice("Long ago", day => Play("Long ago", 100 + day)))
                .Concat(Thrice("Random pick", day => Play("Random pick", day + 1, source: "bootstrap-random")))
                .Concat(Thrice("Wanted")),
            dismissed, Now);
        Assert.Equal(["Wanted"], picks.Select(pick => pick.Play.Title));
    }

    [Fact]
    public void MostPlayedComesFirst_AndTheListHoldsFifty()
    {
        var plays = Enumerable.Range(0, 60).SelectMany(song =>
            Enumerable.Range(0, 3 + (song == 59 ? 2 : 0)).Select(day => Play("Song " + song, day + 1))).ToList();
        var picks = KeepThesePlaylistService.Pick(plays, None, Now);
        Assert.Equal(KeepThesePlaylistService.MaxSongs, picks.Count);
        Assert.Equal("Song 59", picks[0].Play.Title);
    }

    [Fact]
    public void ASongTheLibraryHas_IsLeftOut_NotListedAsTheirCopy()
    {
        var rows = new[]
        {
            new TopSongsService.TopSong(1, new Song { Id = "out-1", Title = "Wanted", Artist = "Band" }, 3, null),
            new TopSongsService.TopSong(2, new Song { Id = "out-2", Title = "Owned", Artist = "Band" }, 3, null),
        };
        using var owned = JsonDocument.Parse("""{"id":"lib-2","title":"Owned","artist":"Band"}""");
        var (entries, outside) = PopularPlaylistService.Assemble(rows, [null, owned.RootElement.Clone()],
            outsideShown: true, ExplicitFilter.All, song => new JsonObject { ["id"] = song.Id }, ownedListed: false);
        Assert.Equal(["out-1"], entries.Select(entry => entry["id"]!.GetValue<string>()));
        Assert.Equal(["out-1"], outside.Select(song => song.Id));
    }

    [Fact]
    public async Task GetPlaylists_ListsItForTheListener_AndGetPlaylistServesItsSongs()
    {
        await using var factory = new ChartWebFactory();
        using var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<LastFmRadioStateStore>();
        for (var day = 0; day < 3; day++)
            state.RecordPlay("alice", new LastFmRadioPlay
            {
                Artist = "Nobody Owns", Title = "This Song", Album = "Out There", Duration = 200,
                PlayedAtUtc = DateTime.UtcNow.AddDays(-day - 1),
            });
        const string alice = "u=alice&t=good&s=salt&v=1.16.1&c=Feishin";

        using var listed = JsonDocument.Parse(await client.GetStringAsync($"/rest/getPlaylists.view?{alice}&f=json"));
        var row = listed.RootElement.GetProperty("subsonic-response").GetProperty("playlists").GetProperty("playlist")
            .EnumerateArray().Single(item => item.GetProperty("name").GetString() == KeepThesePlaylistService.Name);
        Assert.Equal(KeepThesePlaylistService.PlaylistId("alice"), row.GetProperty("id").GetString());
        Assert.True(row.GetProperty("readonly").GetBoolean());

        using var opened = JsonDocument.Parse(await client.GetStringAsync(
            $"/rest/getPlaylist.view?{alice}&f=json&id={KeepThesePlaylistService.PlaylistId("alice")}"));
        var entry = Assert.Single(opened.RootElement.GetProperty("subsonic-response").GetProperty("playlist")
            .GetProperty("entry").EnumerateArray());
        Assert.Equal("This Song", entry.GetProperty("title").GetString());
        Assert.Equal("Nobody Owns", entry.GetProperty("artist").GetString());
    }
}
