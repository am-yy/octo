using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Deezer;
using Octo.Services.Metadata;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Tests;

public sealed class ExternalSaveStoreTests
{
    [Fact]
    public async Task OrderedOccurrencesHeartsAndMetadataSurviveRestartAndSnapshotsAreDetached()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp.Path);
        var song = DeezerSong();
        await store.UpsertPlaylistAsync(new ExternalSavedPlaylist
        {
            UserId = "alice", Owner = "alice", Id = "pl-1", Name = "Saved",
            Tracks =
            [
                new() { OccurrenceId = "first", SongId = song.Id, Song = song },
                new() { OccurrenceId = "duplicate", SongId = song.Id, Song = song },
            ],
        });
        await store.SetHeartAsync("alice", song, true);
        await store.SetHeartAsync("bob", song, true);

        var detached = store.GetPlaylist("alice", "pl-1")!;
        detached.Tracks[0].Song!.Title = "caller mutation";
        detached.Tracks.Clear();

        var restarted = NewStore(temp.Path);
        var playlist = restarted.GetPlaylist("alice", "pl-1")!;
        Assert.Equal(["first", "duplicate"], playlist.Tracks.Select(track => track.OccurrenceId));
        Assert.Equal([song.Id, song.Id], playlist.Tracks.Select(track => track.SongId));
        Assert.Equal("Track", restarted.GetSong(song.Id)!.Title);
        Assert.Single(restarted.GetHearts("alice"));
        Assert.Single(restarted.GetHearts("bob"));
        Assert.True(restarted.IsHearted("alice", song.Id));
        Assert.False(restarted.IsHearted("carol", song.Id));
    }

    [Fact]
    public async Task FailedAtomicWriteDoesNotPublishMutationInMemory()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "external-saves.json");
        var store = new ExternalSaveStore(path, NullLogger<ExternalSaveStore>.Instance);
        var song = DeezerSong();
        await store.SetHeartAsync("alice", song, true);
        File.Delete(path);
        Directory.CreateDirectory(path);

        await Assert.ThrowsAnyAsync<IOException>(() => store.SetHeartAsync("alice", song, false));

        Assert.True(store.IsHearted("alice", song.Id));
        Assert.Single(store.GetHeartMutations("alice"));
    }

    [Fact]
    public async Task AlbumSearchClaimPersistsAndSuppressesDuplicateSearchAfterRestart()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp.Path);
        var song = DeezerSong();
        await store.SetHeartAsync("alice", song, true);
        var intent = await store.TryClaimAcquisitionAsync("deezer", "42");
        Assert.NotNull(intent);
        await store.AssociateAcquisitionWithAlbumAsync("deezer", "42", "mb-release-group");
        Assert.True(await store.TryBeginAlbumSearchAsync("mb-release-group"));
        await store.MarkAlbumSearchSubmittedAsync("mb-release-group", 17);

        var restarted = NewStore(temp.Path);
        Assert.False(await restarted.TryBeginAlbumSearchAsync("mb-release-group"));
        Assert.Equal("submitted", restarted.GetAlbumSearch("mb-release-group")!.Status);
        Assert.Equal(17, Assert.Single(restarted.GetSubmittedAcquisitions()).LidarrAlbumId);
        Assert.Empty(restarted.GetPendingAcquisitions());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRetriesPreparationButNeverReplaysAmbiguousSubmission(bool submitting)
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp.Path);
        await store.SetHeartAsync("alice", DeezerSong(), true);
        await store.TryClaimAcquisitionAsync("deezer", "42");
        await store.AssociateAcquisitionWithAlbumAsync("deezer", "42", "album");
        Assert.True(await store.TryBeginAlbumSearchAsync("album"));
        if (submitting)
        {
            await store.MarkAlbumSearchSubmittingAsync("album", 17);
            await store.MarkAcquisitionFailedAsync("deezer", "42", "Connection lost");
        }

        var restarted = NewStore(temp.Path);
        Assert.Equal(submitting ? 0 : 1, await restarted.RecoverInterruptedAcquisitionsAsync(afterRestart: true));
        Assert.Equal(!submitting, await restarted.TryBeginAlbumSearchAsync("album"));
        if (submitting)
        {
            Assert.Empty(restarted.GetPendingAcquisitions());
            Assert.Equal(17, restarted.GetAlbumSearch("album")!.LidarrAlbumId);
        }
        else Assert.Single(restarted.GetPendingAcquisitions());
    }

    [Fact]
    public async Task LosslessImportReplacesAllOccurrencesAndHeartAndKeepsOldIdAlias()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp.Path);
        var song = DeezerSong();
        await store.SetHeartAsync("alice", song, true);
        await store.UpsertPlaylistAsync(new ExternalSavedPlaylist
        {
            UserId = "alice", Owner = "alice", Id = "pl-1", Name = "Saved",
            Tracks =
            [
                new() { OccurrenceId = "one", SongId = song.Id, Song = song },
                new() { OccurrenceId = "two", SongId = song.Id, Song = song },
            ],
        });
        var path = Path.Combine(temp.Path, "track.flac");
        await File.WriteAllBytesAsync(path, [0x66, 0x4c, 0x61, 0x43]);

        Assert.Equal(2, await store.MarkImportedAsync("deezer", "42", "local-17", path));

        var playlist = store.GetPlaylist("alice", "pl-1")!;
        Assert.Equal(["one", "two"], playlist.Tracks.Select(track => track.OccurrenceId));
        Assert.All(playlist.Tracks, track => Assert.Equal("local-17", track.SongId));
        Assert.Equal("local-17", Assert.Single(store.GetHearts("alice")).SongId);
        Assert.True(store.IsHearted("alice", song.Id));
        Assert.Equal("local-17", store.CanonicalSongId(song.Id));
        Assert.True(store.GetSong(song.Id)!.IsLocal);
        Assert.Equal(path, store.GetSong(song.Id)!.LocalPath);
        Assert.Empty(store.GetPinnedSongIds());
        Assert.Contains(store.GetHeartMutations("alice"), mutation => mutation.Hearted && mutation.SongId == "local-17");
    }

    [Fact]
    public async Task LossyImportDoesNotReplacePromisedTrack()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp.Path);
        var song = DeezerSong();
        await store.SetHeartAsync("alice", song, true);
        var path = Path.Combine(temp.Path, "track.mp3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        Assert.Equal(0, await store.MarkImportedAsync("deezer", "42", "local-17", path));
        Assert.True(store.IsHearted("alice", song.Id));
        Assert.Equal(song.Id, store.CanonicalSongId(song.Id));
    }

    [Fact]
    public async Task LearnedDisplayMetadataSurvivesRestartAcrossDuplicateReferencesWithoutChangingSaveState()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp.Path);
        var song = DeezerSong();
        song.Album = "";
        await store.SetHeartAsync("alice", song, true);
        await store.SetHeartAsync("bob", song, true);
        await store.UpsertPlaylistAsync(new ExternalSavedPlaylist
        {
            UserId = "alice", Owner = "alice", Id = "pl-1", Name = "Saved",
            Tracks =
            [
                new() { OccurrenceId = "one", SongId = song.Id, Song = song },
                new() { OccurrenceId = "duplicate", SongId = song.Id, Song = song },
            ],
        });
        var before = store.GetPlaylist("alice", "pl-1")!;
        var intentBefore = Assert.Single(store.Snapshot().Acquisitions);
        var mutationBefore = store.GetHeartMutations("alice").Single();

        Assert.True(await store.RememberMetadataAsync(song.Id, deezerId: null, album: "Discovery", duration: 245));
        Assert.False(await store.RememberMetadataAsync(song.Id, deezerId: null, album: "Discovery", duration: 245));

        var restarted = NewStore(temp.Path);
        var after = restarted.GetPlaylist("alice", "pl-1")!;
        Assert.Equal(before.Tracks.Select(t => t.OccurrenceId), after.Tracks.Select(t => t.OccurrenceId));
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
        Assert.All(after.Tracks, track => AssertSongMetadata(track.Song!, "Discovery", 245, null));
        AssertSongMetadata(restarted.GetSong(song.Id)!, "Discovery", 245, null);
        AssertSongMetadata(Assert.Single(restarted.GetHearts("alice")).Song, "Discovery", 245, null);
        AssertSongMetadata(Assert.Single(restarted.GetHearts("bob")).Song, "Discovery", 245, null);
        AssertSongMetadata(Assert.Single(restarted.GetHeartMutations("alice")).Song, "Discovery", 245, null);
        var intentAfter = Assert.Single(restarted.Snapshot().Acquisitions);
        AssertSongMetadata(intentAfter.Song, "Discovery", 245, null);
        Assert.Equal(intentBefore.Status, intentAfter.Status);
        Assert.Equal(intentBefore.CreatedUtc, intentAfter.CreatedUtc);
        Assert.Equal(mutationBefore.UpdatedUtc, restarted.GetHeartMutations("alice").Single().UpdatedUtc);
    }

    [Fact]
    public async Task LearnedMetadataFillsMissingAlbumButPreservesTitleEqualReleaseName()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp.Path);
        var placeholder = DeezerSong();
        placeholder.Album = "";
        var explicitRelease = new Song
        {
            Id = "ext-deezer-43", ExternalProvider = "deezer", ExternalId = "43",
            Artist = "Artist", Title = "Track 2", Album = "Track 2", Duration = 180,
        };
        await store.SetHeartAsync("alice", placeholder, true);
        await store.SetHeartAsync("alice", explicitRelease, true);

        Assert.True(await store.RememberMetadataAsync(placeholder.Id, null, "Discovered Release", 241));
        Assert.True(await store.RememberMetadataAsync(explicitRelease.Id, "deezer-43", "Different Release", 242));

        AssertSongMetadata(store.GetSong(placeholder.Id)!, "Discovered Release", 241, null);
        AssertSongMetadata(store.GetSong(explicitRelease.Id)!, "Track 2", 242, "deezer-43");
        Assert.Equal("deezer-43", store.GetHearts("alice").Single(heart => heart.SongId == explicitRelease.Id).Song.DeezerId);
    }

    [Fact]
    public async Task CacheDisabledWorkerPersistsKnownMetadataWithoutDeezerId()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp.Path);
        var song = DeezerSong();
        song.Album = "";
        await store.SetHeartAsync("alice", song, true);
        await store.MarkAcquisitionScheduledAsync("deezer", "42");

        using var ids = new ExternalIdRegistry();
        var routing = new SoulseekRouting { Artist = song.Artist, Title = song.Title, Album = "Learned Release" };
        SongLength.Remember(routing, 247, LengthSource.Deezer);
        ids.Restore(song.Id, routing);

        var configuration = new ConfigurationBuilder().Build();
        var metadataOptions = new Mock<IOptionsMonitor<MetadataSettings>>();
        metadataOptions.SetupGet(options => options.CurrentValue).Returns(new MetadataSettings());
        var catalog = new DeezerMetadataService(new Mock<IHttpClientFactory>().Object,
            metadataOptions.Object,
            NullLogger<DeezerMetadataService>.Instance);
        using var resolver = new DeezerResolver(new Mock<IHttpClientFactory>().Object, configuration,
            NullLogger<DeezerResolver>.Instance, catalog);
        var cacheSettings = new DeezerSettings { CacheEnabled = false, CachePath = temp.Path };
        var cacheOptions = new Mock<IOptionsMonitor<DeezerSettings>>();
        cacheOptions.SetupGet(options => options.CurrentValue).Returns(cacheSettings);
        using var cache = new DeezerAudioCache(resolver, catalog, ids,
            cacheOptions.Object,
            NullLogger<DeezerAudioCache>.Instance);
        using var worker = new ExternalSaveWorker(store, cache, null!, ids, NullLogger<ExternalSaveWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                var persisted = store.GetSong(song.Id)!;
                if (persisted.Album == "Learned Release" && persisted.Duration == 247) break;
                await Task.Delay(20, timeout.Token);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var saved = NewStore(temp.Path).GetSong(song.Id)!;
        AssertSongMetadata(saved, "Learned Release", 247, null);
    }

    [Fact]
    public async Task LearnedMetadataDoesNotRewriteImportedLocalCopies()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp.Path);
        var song = DeezerSong();
        await store.SetHeartAsync("alice", song, true);
        await store.UpsertPlaylistAsync(new ExternalSavedPlaylist
        {
            UserId = "alice", Owner = "alice", Id = "pl-1", Name = "Saved",
            Tracks = [new() { OccurrenceId = "one", SongId = song.Id, Song = song }],
        });
        var path = Path.Combine(temp.Path, "track.flac");
        await File.WriteAllBytesAsync(path, [0x66, 0x4c, 0x61, 0x43]);
        await store.MarkImportedAsync("deezer", "42", "local-17", path);

        Assert.False(await store.RememberMetadataAsync(song.Id, "deezer-42", "Changed Release", 245));

        var local = store.GetSong(song.Id)!;
        Assert.True(local.IsLocal);
        Assert.Equal("Album", local.Album);
        Assert.Equal(180, local.Duration);
        Assert.Null(local.DeezerId);
        Assert.True(store.GetPlaylist("alice", "pl-1")!.Tracks[0].Song!.IsLocal);
        Assert.True(Assert.Single(store.GetHearts("alice")).Song.IsLocal);
    }

    private static ExternalSaveStore NewStore(string directory) =>
        new(Path.Combine(directory, "external-saves.json"), NullLogger<ExternalSaveStore>.Instance);

    private static Song DeezerSong() => new()
    {
        Id = "ext-deezer-song-42", ExternalProvider = "deezer", ExternalId = "42",
        Artist = "Artist", Title = "Track", Album = "Album", Duration = 180,
    };

    private static void AssertSongMetadata(Song song, string album, int duration, string? deezerId)
    {
        Assert.Equal(album, song.Album);
        Assert.Equal(duration, song.Duration);
        Assert.Equal(deezerId, song.DeezerId);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "octo-external-saves-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
