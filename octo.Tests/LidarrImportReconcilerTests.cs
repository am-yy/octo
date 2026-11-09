using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Domain;
using Octo.Services.Lidarr;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>Placing a saved song in the album Lidarr imported for it, and searching again for one that never came.</summary>
public sealed class LidarrImportReconcilerTests
{
    private static LidarrImportedTrack Track(int number, string title, int seconds, bool hasFile = true) =>
        new(number, title, number, seconds, hasFile, hasFile ? $"/music/Band/Album/{number:00} - {title}.flac" : null, 1, "Band");

    [Fact]
    public void ACompilationCutThatRunsLonger_StillFindsTheAlbumTrack()
    {
        // Blur's "Girls & Boys" from a best-of runs 259 s; Parklife's runs 291 s.
        var picked = LidarrImportReconciler.PickTrack(
            [Track(1, "Girls & Boys", 291), Track(2, "Tracy Jacks", 260)],
            new Song { Artist = "Blur", Title = "Girls & Boys", Duration = 259 });
        Assert.Equal("Girls & Boys", picked?.Title);
    }

    [Fact]
    public void SeveralCutsOnADeluxeAlbum_TakeTheNearestLength_NotNone()
    {
        var picked = LidarrImportReconciler.PickTrack(
            [Track(3, "Heart-Shaped Box", 269), Track(15, "Heart-Shaped Box", 279), Track(27, "Heart-Shaped Box", 282)],
            new Song { Artist = "Nirvana", Title = "Heart‐Shaped Box", Duration = 281 });
        Assert.Equal(27, picked?.Id);
    }

    [Fact]
    public void ALiveCutOrAMissingFile_NeverStandsInForTheSong()
    {
        var wanted = new Song { Artist = "The Verve", Title = "Lucky Man", Duration = 289 };
        Assert.Null(LidarrImportReconciler.PickTrack([Track(1, "Lucky Man (Live)", 289)], wanted));
        Assert.Null(LidarrImportReconciler.PickTrack([Track(1, "Lucky Man", 289, hasFile: false)], wanted));
        Assert.Null(LidarrImportReconciler.PickTrack([Track(1, "Bitter Sweet Symphony", 289)], wanted));
    }

    [Fact]
    public void AnAlbumThatNeverCame_IsSearchedAgainLessOftenEachTime_ThenNoMore()
    {
        var now = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        ExternalAcquisitionIntent Waiting(int attempts, double daysSince) =>
            new() { Status = "submitted", LidarrAlbumId = 7, SearchAttempts = attempts, UpdatedUtc = now.AddDays(-daysSince) };

        Assert.False(LidarrImportReconciler.SearchDue([Waiting(0, 0.5)], now));
        Assert.True(LidarrImportReconciler.SearchDue([Waiting(0, 1)], now));
        Assert.False(LidarrImportReconciler.SearchDue([Waiting(2, 3)], now));
        Assert.True(LidarrImportReconciler.SearchDue([Waiting(2, 4)], now));
        Assert.False(LidarrImportReconciler.SearchDue([Waiting(LidarrImportReconciler.MaxSearches, 365)], now));
        // Songs waiting on one album share its searches: the most recent activity counts.
        Assert.False(LidarrImportReconciler.SearchDue([Waiting(0, 5), Waiting(1, 0.2)], now));
    }

    [Fact]
    public async Task SearchingAgain_CountsForEverySongWaitingOnThatAlbum_AndKeepsTheReason()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new ExternalSaveStore(Path.Combine(dir.FullName, "saves.json"), NullLogger<ExternalSaveStore>.Instance);
            foreach (var (id, title) in new[] { ("1", "One"), ("2", "Two") })
            {
                await store.QueueAcquisitionAsync("deezer", id, new Song { Artist = "Band", Title = title }, "admin");
                await store.TryClaimAcquisitionAsync("deezer", id);
                await store.AssociateAcquisitionWithAlbumAsync("deezer", id, "mbid-album");
            }
            await store.QueueAcquisitionAsync("deezer", "3", new Song { Artist = "Other", Title = "Elsewhere" }, "admin");
            Assert.True(await store.TryBeginAlbumSearchAsync("mbid-album"));
            await store.MarkAlbumSearchSubmittedAsync("mbid-album", 7);

            await store.NoteSearchedAgainAsync(7, null);
            await store.NoteSearchedAgainAsync(7, "Lidarr found no release after 2 more searches.");

            var intents = store.Snapshot().Acquisitions;
            Assert.All(intents.Where(i => i.LidarrAlbumId == 7), i =>
            {
                Assert.Equal("submitted", i.Status);
                Assert.Equal(2, i.SearchAttempts);
                Assert.Equal("Lidarr found no release after 2 more searches.", i.LastError);
            });
            Assert.Equal(2, intents.Count(i => i.LidarrAlbumId == 7));
            Assert.Equal(0, intents.Single(i => i.ExternalId == "3").SearchAttempts);

            // The count survives a restart.
            var reloaded = new ExternalSaveStore(Path.Combine(dir.FullName, "saves.json"), NullLogger<ExternalSaveStore>.Instance);
            Assert.Equal(2, reloaded.Snapshot().Acquisitions.First(i => i.LidarrAlbumId == 7).SearchAttempts);
        }
        finally { dir.Delete(recursive: true); }
    }
}
