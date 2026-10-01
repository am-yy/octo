using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Domain;
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
    public async Task DeezerHintRefreshUpdatesEveryDurableSongCopyWithoutChangingSaveState()
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
        var before = store.GetPlaylist("alice", "pl-1")!;

        Assert.True(await store.RememberDeezerIdAsync(song.Id, "deezer-42"));
        Assert.False(await store.RememberDeezerIdAsync(song.Id, "deezer-42"));

        var restarted = NewStore(temp.Path);
        Assert.Equal(before.Tracks.Select(t => t.OccurrenceId),
            restarted.GetPlaylist("alice", "pl-1")!.Tracks.Select(t => t.OccurrenceId));
        Assert.Equal(before.UpdatedUtc, restarted.GetPlaylist("alice", "pl-1")!.UpdatedUtc);
        Assert.Equal("deezer-42", restarted.GetPlaylist("alice", "pl-1")!.Tracks[0].Song!.DeezerId);
        Assert.Equal("deezer-42", Assert.Single(restarted.GetHearts("alice")).Song.DeezerId);
        Assert.Equal("deezer-42", Assert.Single(restarted.GetHeartMutations("alice")).Song.DeezerId);
        Assert.Equal("deezer-42", Assert.Single(restarted.Snapshot().Acquisitions).Song.DeezerId);
        Assert.Equal("deezer-42", restarted.GetSong(song.Id)!.DeezerId);
    }

    private static ExternalSaveStore NewStore(string directory) =>
        new(Path.Combine(directory, "external-saves.json"), NullLogger<ExternalSaveStore>.Instance);

    private static Song DeezerSong() => new()
    {
        Id = "ext-deezer-song-42", ExternalProvider = "deezer", ExternalId = "42",
        Artist = "Artist", Title = "Track", Album = "Album", Duration = 180,
    };

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "octo-external-saves-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
