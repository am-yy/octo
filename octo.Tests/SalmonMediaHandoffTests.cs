using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Settings;
using Octo.Services.Trackers;

namespace Octo.Tests;

public sealed class SalmonMediaHandoffTests
{
    private const string RootName = "Test Album (2024)";
    private const string Announce = "https://flacsfor.me/AAAAAAAAAAAAAAAAAAAAAAAA/announce";

    [Fact]
    public async Task LocalSourceInventoryIsReadOnlyAndChecksSkippedFilesOnDisk()
    {
        using var temp = new TempDirectory();
        var hash = new string('a', 40);
        var filePath = Path.Combine(temp.Path, RootName, "01.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var bytes = Encoding.UTF8.GetBytes("verified local file");
        await File.WriteAllBytesAsync(filePath, bytes);
        var handler = new FakeQbHandler(hash, RootName, temp.Path, "", "", Announce)
        {
            HasTorrent = true,
            FileName = RootName + "/01.flac",
            FileSize = bytes.Length,
            FilePriority = 0,
        };

        var complete = await Build(temp.Path, handler).InspectLocalSourceAsync(hash);
        Assert.True(complete.Complete);
        Assert.Null(complete.Error);
        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/torrents/recheck", StringComparison.Ordinal)
            || r.Path.EndsWith("/torrents/export", StringComparison.Ordinal)
            || r.Path.EndsWith("/torrents/add", StringComparison.Ordinal));

        File.Delete(filePath);
        var missing = await Build(temp.Path, handler).InspectLocalSourceAsync(hash);
        Assert.False(missing.Complete);
        Assert.Equal("local_file_missing_or_linked", missing.Error);
    }

    [Fact]
    public async Task LocalSourceInventoryRejectsIncompleteSkippedFile()
    {
        using var temp = new TempDirectory();
        var hash = new string('b', 40);
        var filePath = Path.Combine(temp.Path, RootName, "01.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var bytes = Encoding.UTF8.GetBytes("verified local file");
        await File.WriteAllBytesAsync(filePath, bytes);
        var handler = new FakeQbHandler(hash, RootName, temp.Path, "", "", Announce)
        {
            HasTorrent = true,
            FileName = RootName + "/01.flac",
            FileSize = bytes.Length,
            FileProgressOverride = 0.99,
            FilePriority = 0,
        };

        var observation = await Build(temp.Path, handler).InspectLocalSourceAsync(hash);

        Assert.False(observation.Complete);
        Assert.Equal("file_incomplete", observation.Error);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SeedAddsStoppedWithExplicitPathThenRechecksSetsUnlimitedAndStarts(bool useCredentials, bool allowAnonymous)
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("test audio bytes");
        var file = FileRecord("01 - Song.flac", bytes);
        var (torrent, hash) = MakeTorrent(RootName, file.Path, bytes);
        var payload = Path.Combine(temp.Path, "music-prepared", "job-1", RootName);
        Directory.CreateDirectory(payload);
        await File.WriteAllBytesAsync(Path.Combine(payload, file.Path), bytes);
        var savePath = Path.Combine(temp.Path, "cross-seed", "red", "job-1");
        var handler = new FakeQbHandler(hash, RootName, savePath, "salmon-red", "salmon-job-job-1-" + hash[..12], Announce)
        {
            ExportBytes = torrent,
            AllowAnonymous = allowAnonymous,
        };

        var result = await Build(temp.Path, handler, useCredentials).SeedAsync("job-1", "red", RootName, hash, torrent, [file]);

        Assert.Equal(useCredentials ? 1 : 0, handler.Requests.Count(r => r.Path == "/api/v2/auth/login"));
        if (!useCredentials && !allowAnonymous)
        {
            Assert.Equal("pending", result.State);
            Assert.Equal("tracker_registration_pending", result.Error);
            Assert.DoesNotContain(handler.Requests, r => r.Method == "POST");
            return;
        }

        Assert.Equal("seeding", result.State);
        Assert.True(File.Exists(Path.Combine(savePath, RootName, file.Path)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(savePath, RootName, file.Path)));
        var add = Assert.Single(handler.Requests, r => r.Path == "/api/v2/torrents/add");
        Assert.True(HasMultipartField(add.Body, "stopped", "true"));
        Assert.True(HasMultipartField(add.Body, "autoTMM", "false"));
        Assert.Contains(savePath, add.Body);
        Assert.Contains("salmon-red", add.Body);
        Assert.True(handler.Requests.FindIndex(r => r.Path == "/api/v2/torrents/add")
            < handler.Requests.FindIndex(r => r.Path == "/api/v2/torrents/recheck"));
        Assert.True(handler.Requests.FindIndex(r => r.Path == "/api/v2/torrents/recheck")
            < handler.Requests.FindIndex(r => r.Path == "/api/v2/torrents/setShareLimits"));
        Assert.True(handler.Requests.FindIndex(r => r.Path == "/api/v2/torrents/setShareLimits")
            < handler.Requests.FindIndex(r => r.Path == "/api/v2/torrents/start"));
        var limits = Assert.Single(handler.Requests, r => r.Path == "/api/v2/torrents/setShareLimits").Body;
        Assert.Contains("ratioLimit=-1", limits);
        Assert.Contains("seedingTimeLimit=-1", limits);
        Assert.Contains("inactiveSeedingTimeLimit=-1", limits);

        Assert.Equal("seeded", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "salmon-state", "job-1", "handoff.json")))!["seed"]!["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExistingInfohashWithDifferentPathIsRejectedWithoutAdd()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("test audio bytes");
        var file = FileRecord("01 - Song.flac", bytes);
        var (torrent, hash) = MakeTorrent(RootName, file.Path, bytes);
        var payload = Path.Combine(temp.Path, "music-prepared", "job-2", RootName);
        Directory.CreateDirectory(payload);
        await File.WriteAllBytesAsync(Path.Combine(payload, file.Path), bytes);
        var handler = new FakeQbHandler(hash, RootName, "/unrelated/torrent", "other", "other-tag", Announce)
        {
            HasTorrent = true,
        };

        var result = await Build(temp.Path, handler).SeedAsync("job-2", "red", RootName, hash, torrent, [file]);

        Assert.Equal("failed", result.State);
        Assert.Equal("duplicate_torrent_conflict", result.Error);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/v2/torrents/add");
    }

    [Fact]
    public async Task ExistingOwnedPathWithExtraRealTrackerIsRejected()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("test audio bytes");
        var file = FileRecord("01 - Song.flac", bytes);
        var (torrent, hash) = MakeTorrent(RootName, file.Path, bytes);
        var payload = Path.Combine(temp.Path, "music-prepared", "job-extra-tracker", RootName);
        Directory.CreateDirectory(payload);
        await File.WriteAllBytesAsync(Path.Combine(payload, file.Path), bytes);
        var savePath = Path.Combine(temp.Path, "cross-seed", "red", "job-extra-tracker");
        var handler = new FakeQbHandler(hash, RootName, savePath, "salmon-red", "salmon-job-job-extra-tracker-" + hash[..12], Announce)
        {
            HasTorrent = true,
            ExtraTrackerUrls = ["https://unexpected.example/announce"],
        };

        var result = await Build(temp.Path, handler).SeedAsync("job-extra-tracker", "red", RootName, hash, torrent, [file]);

        Assert.Equal("failed", result.State);
        Assert.Equal("duplicate_torrent_conflict", result.Error);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/v2/torrents/add");
    }

    [Fact]
    public async Task AmbiguousAddIsPersistedAndNeverRepeated()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("test audio bytes");
        var file = FileRecord("01 - Song.flac", bytes);
        var (torrent, hash) = MakeTorrent(RootName, file.Path, bytes);
        var payload = Path.Combine(temp.Path, "music-prepared", "job-3", RootName);
        Directory.CreateDirectory(payload);
        await File.WriteAllBytesAsync(Path.Combine(payload, file.Path), bytes);
        var savePath = Path.Combine(temp.Path, "cross-seed", "red", "job-3");
        var tag = "salmon-job-job-3-" + hash[..12];
        var handler = new FakeQbHandler(hash, RootName, savePath, "salmon-red", tag, Announce) { HideAddedTorrent = true };
        var handoff = Build(temp.Path, handler);

        var first = await handoff.SeedAsync("job-3", "red", RootName, hash, torrent, [file]);
        var second = await handoff.SeedAsync("job-3", "red", RootName, hash, torrent, [file]);

        Assert.Equal("unknown", first.State);
        Assert.Equal("unknown", second.State);
        Assert.Single(handler.Requests, r => r.Path == "/api/v2/torrents/add");
    }

    [Fact]
    public async Task AddReconcilesTorrentThatAppearsAfterInitialInfoMisses()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("test audio bytes");
        var file = FileRecord("01 - Song.flac", bytes);
        var (torrent, hash) = MakeTorrent(RootName, file.Path, bytes);
        var payload = Path.Combine(temp.Path, "music-prepared", "job-lag", RootName);
        Directory.CreateDirectory(payload);
        await File.WriteAllBytesAsync(Path.Combine(payload, file.Path), bytes);
        var savePath = Path.Combine(temp.Path, "cross-seed", "red", "job-lag");
        var handler = new FakeQbHandler(hash, RootName, savePath, "salmon-red", "salmon-job-job-lag-" + hash[..12], Announce)
        {
            InfoMissesAfterAdd = 2,
        };

        var result = await Build(temp.Path, handler).SeedAsync("job-lag", "red", RootName, hash, torrent, [file]);

        Assert.Equal("seeding", result.State);
        Assert.Single(handler.Requests, r => r.Path == "/api/v2/torrents/add");
        Assert.Contains(handler.Requests, r => r.Path == "/api/v2/torrents/recheck");
    }

    [Fact]
    public async Task TimedOutSeedRecheckResumesWithoutIssuingSecondCheck()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("test audio bytes");
        var file = FileRecord("01 - Song.flac", bytes);
        var (torrent, hash) = MakeTorrent(RootName, file.Path, bytes);
        var payload = Path.Combine(temp.Path, "music-prepared", "job-recheck", RootName);
        Directory.CreateDirectory(payload);
        await File.WriteAllBytesAsync(Path.Combine(payload, file.Path), bytes);
        var savePath = Path.Combine(temp.Path, "cross-seed", "red", "job-recheck");
        var handler = new FakeQbHandler(hash, RootName, savePath, "salmon-red", "salmon-job-job-recheck-" + hash[..12], Announce);
        var handoff = Build(temp.Path, handler);
        using var timeout = new CancellationTokenSource();

        handler.CheckingObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = handoff.SeedAsync("job-recheck", "red", RootName, hash, torrent, [file], timeout.Token);
        await handler.CheckingObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var retried = await handoff.SeedAsync("job-recheck", "red", RootName, hash, torrent, [file]);

        Assert.Equal("seeding", retried.State);
        Assert.Single(handler.Requests, r => r.Path == "/api/v2/torrents/recheck");
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.0)]
    public async Task CompletedIncompleteDestinationCheckCanRetryAfterRepairAndRestart(double progress)
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("test audio bytes");
        var file = FileRecord("01 - Song.flac", bytes);
        var (torrent, hash) = MakeTorrent(RootName, file.Path, bytes);
        const string job = "job-repair";
        var payload = Path.Combine(temp.Path, "music-prepared", job, RootName);
        Directory.CreateDirectory(payload);
        await File.WriteAllBytesAsync(Path.Combine(payload, file.Path), bytes);
        var savePath = Path.Combine(temp.Path, "cross-seed", "red", job);
        var handler = new FakeQbHandler(hash, RootName, savePath, "salmon-red", "salmon-job-" + job + "-" + hash[..12], Announce)
        { RecheckProgress = progress, FileProgressOverride = 0.75 };

        var failed = await Build(temp.Path, handler).SeedAsync(job, "red", RootName, hash, torrent, [file]);

        Assert.Equal("failed", failed.State);
        Assert.Equal("torrent_files_incomplete", failed.Error);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/v2/torrents/start");
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "salmon-state", job, "handoff.json")))!["seed"]!;
        Assert.False(receipt["recheckIssued"]!.GetValue<bool>());
        Assert.False(receipt["recheckConfirmed"]!.GetValue<bool>());
        handler.RecheckProgress = 1;
        handler.FileProgressOverride = null;

        var repaired = await Build(temp.Path, handler).SeedAsync(job, "red", RootName, hash, torrent, [file]);

        Assert.Equal("seeding", repaired.State);
        Assert.Single(handler.Requests, r => r.Path == "/api/v2/torrents/add");
        Assert.Equal(2, handler.Requests.Count(r => r.Path == "/api/v2/torrents/recheck"));
        Assert.Single(handler.Requests, r => r.Path == "/api/v2/torrents/start");
    }

    [Fact]
    public async Task TimedOutSourceRecheckResumesAndRestoresOriginalRunState()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("source audio");
        var file = FileRecord("01 - Source.flac", bytes);
        var (_, hash) = MakeTorrent(RootName, file.Path, bytes);
        var handler = new FakeQbHandler(hash, RootName, "/source", "", "", Announce)
        {
            HasTorrent = true,
            State = "uploading",
        };
        var handoff = Build(temp.Path, handler);
        using var timeout = new CancellationTokenSource();

        handler.CheckingObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = handoff.VerifySourceAsync(hash, timeout.Token);
        await handler.CheckingObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(await handoff.VerifySourceAsync(hash));

        Assert.Single(handler.Requests, r => r.Path == "/api/v2/torrents/recheck");
        Assert.Equal("uploading", handler.State);
    }

    [Fact]
    public async Task SourceRecheckPreservesRunStateAndCanReturnStoredTorrent()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("source audio");
        var file = FileRecord("01 - Source.flac", bytes);
        var (torrent, hash) = MakeTorrent(RootName, file.Path, bytes);
        var handler = new FakeQbHandler(hash, RootName, "/source", "", "", Announce)
        {
            HasTorrent = true,
            State = "uploading",
            ExportBytes = torrent,
        };
        var handoff = Build(temp.Path, handler);

        Assert.True(await handoff.VerifySourceAsync(hash));
        Assert.Equal("uploading", handler.State);
        var exported = await handoff.GetVerifiedSourceTorrentAsync(hash);

        Assert.Equal(torrent, exported);
        Assert.Equal("uploading", handler.State);
        Assert.Contains(handler.Requests, r => r.Path == "/api/v2/torrents/recheck");
        Assert.Contains(handler.Requests, r => r.Path == "/api/v2/torrents/export");
    }

    [Fact]
    public async Task FailedSourceRecheckRestoresPriorRunState()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("source audio");
        var file = FileRecord("01 - Source.flac", bytes);
        var (_, hash) = MakeTorrent(RootName, file.Path, bytes);
        var handler = new FakeQbHandler(hash, RootName, "/source", "", "", Announce)
        {
            HasTorrent = true,
            State = "uploading",
            RecheckProgress = 0.75,
        };

        Assert.False(await Build(temp.Path, handler).VerifySourceAsync(hash));

        Assert.Equal("uploading", handler.State);
        Assert.Contains(handler.Requests, r => r.Path == "/api/v2/torrents/start");
    }

    [Fact]
    public async Task SourceRecheckAcceptsCompletionBeforeFirstPoll()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("tiny source audio");
        var file = FileRecord("01 - Tiny.flac", bytes);
        var (_, hash) = MakeTorrent(RootName, file.Path, bytes);
        var handler = new FakeQbHandler(hash, RootName, "/source", "", "", Announce)
        {
            HasTorrent = true,
            State = "uploading",
            FastRecheck = true,
        };

        Assert.True(await Build(temp.Path, handler).VerifySourceAsync(hash));

        Assert.Equal("uploading", handler.State);
    }

    [Fact]
    public async Task ResumeDataCheckDoesNotReplaceRequiredFullSourceRecheck()
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("source audio");
        var file = FileRecord("01 - Source.flac", bytes);
        var (_, hash) = MakeTorrent(RootName, file.Path, bytes);
        var stateDir = Path.Combine(temp.Path, "salmon-state", "source-rechecks");
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, hash + ".json"),
            $$"""{"version":1,"infoHash":"{{hash}}","wasRunning":true,"recheckIssued":false,"recheckConfirmed":false,"phase":"pending"}""");
        var handler = new FakeQbHandler(hash, RootName, "/source", "", "", Announce)
        {
            HasTorrent = true,
            State = "checkingResumeData",
            ResumeDataReads = 1,
        };

        Assert.True(await Build(temp.Path, handler).VerifySourceAsync(hash));

        var resumeDataRead = handler.Requests.FindIndex(r => r.Path == "/api/v2/torrents/info");
        var fullRecheck = handler.Requests.FindIndex(r => r.Path == "/api/v2/torrents/recheck");
        Assert.True(resumeDataRead >= 0 && fullRecheck > resumeDataRead);
        Assert.Single(handler.Requests, r => r.Path == "/api/v2/torrents/recheck");
    }

    [Theory]
    [InlineData("Band")]
    [InlineData("Band & Guest")]
    [InlineData("Different credit name")]
    [InlineData("")]
    public async Task ImportUsesExactReleaseUuidRegardlessOfArtistCredit(string creditedArtist)
    {
        using var temp = new TempDirectory();
        var (jobId, releaseId, file, importDir) = await PrepareImportState(temp.Path);
        var root = Path.Combine(temp.Path, "media", "music");
        Directory.CreateDirectory(Path.Combine(root, "Band"));
        var handler = new FakeLidarrHandler(root, Path.Combine(root, "Band"), importDir, releaseId)
        { MoveAudioOnCommand = true, RequiredLookupTerm = RootName };
        handler.AddUnrelatedLookupGroup = true;

        var result = await BuildImport(temp.Path, handler).ImportAsync(jobId, RootName, creditedArtist, RootName, releaseId);

        Assert.Equal("imported", result.State);
        Assert.Equal(42, result.AlbumId);
        var command = Assert.Single(handler.Requests, r => r.Path == "/api/v1/command");
        Assert.Contains("\"name\":\"ManualImport\"", command.Body);
        Assert.Contains("\"importMode\":\"move\"", command.Body);
        Assert.Contains("\"replaceExistingFiles\":false", command.Body);
        Assert.Contains("\"albumReleaseId\":11", command.Body);
        Assert.Contains("\"trackIds\":[9]", command.Body);
        Assert.Equal("api-secret", command.ApiKey);
        Assert.Equal("imported", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "salmon-state", jobId, "handoff.json")))!["import"]!["phase"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(importDir, file.Path)));
        Assert.Equal(file.Size, new FileInfo(Path.Combine(root, "Band", RootName, file.Path)).Length);
        Assert.Equal(file.Size, new FileInfo(Path.Combine(temp.Path, "music-prepared", jobId, RootName, file.Path)).Length);
    }

    [Fact]
    public async Task NonGuidSourceIdImportsWhenManagedAlbumHasOneRelease()
    {
        using var temp = new TempDirectory();
        var (jobId, _, _, importDir) = await PrepareImportState(temp.Path);
        var root = Path.Combine(temp.Path, "media", "music");
        Directory.CreateDirectory(Path.Combine(root, "Band"));
        var handler = new FakeLidarrHandler(root, Path.Combine(root, "Band"), importDir, "dd145441-06f5-4420-81a7-92ded3d4e734")
        {
            MoveAudioOnCommand = true,
            RequiredLookupTerm = "Band " + RootName,
        };
        var credits = new SalmonSubmission { Fields = new()
        {
            ["artists[]"] = new JsonArray("Guest", "Band", "Producer", "Remixer", "Composer"),
            ["importance[]"] = new JsonArray(2, 1, 7, 3, 4),
        } };
        var artist = string.Join(" & ", SalmonPayload.Artists(credits, mainOnly: true));

        var result = await BuildImport(temp.Path, handler).ImportAsync(jobId, RootName, artist, RootName, "official-store:download-42");

        Assert.Equal("imported", result.State);
        Assert.Contains(handler.Requests, r => r.Path == "/api/v1/command");
    }

    [Fact]
    public async Task UnmatchedExplicitUuidCannotFallBackToArtistAndTitle()
    {
        using var temp = new TempDirectory();
        var (jobId, releaseId, _, importDir) = await PrepareImportState(temp.Path);
        var root = Path.Combine(temp.Path, "media", "music");
        Directory.CreateDirectory(Path.Combine(root, "Band"));
        var handler = new FakeLidarrHandler(root, Path.Combine(root, "Band"), importDir, releaseId);

        var result = await BuildImport(temp.Path, handler).ImportAsync(jobId, RootName, "Band", RootName,
            "00000000-0000-0000-0000-000000000001");

        Assert.Equal("release_unresolved", result.Error);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/v1/command");
    }

    [Fact]
    public async Task NonGuidSourceIdPausesWhenManagedAlbumHasMultipleReleases()
    {
        using var temp = new TempDirectory();
        var (jobId, _, _, importDir) = await PrepareImportState(temp.Path);
        var root = Path.Combine(temp.Path, "media", "music");
        Directory.CreateDirectory(Path.Combine(root, "Band"));
        var handler = new FakeLidarrHandler(root, Path.Combine(root, "Band"), importDir, "dd145441-06f5-4420-81a7-92ded3d4e734")
        {
            MultipleReleases = true,
        };

        var result = await BuildImport(temp.Path, handler).ImportAsync(jobId, RootName, "Band", RootName, "official-store:download-42");

        Assert.Equal("pending", result.State);
        Assert.Equal("release_unresolved", result.Error);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/v1/manualimport");
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/v1/command");
    }

    [Fact]
    public async Task ImportRejectionBlocksCommandAndAmbiguousPostIsNeverRepeated()
    {
        using var temp = new TempDirectory();
        var (jobId, releaseId, _, importDir) = await PrepareImportState(temp.Path);
        var root = Path.Combine(temp.Path, "media", "music");
        Directory.CreateDirectory(Path.Combine(root, "Band"));
        var rejected = new FakeLidarrHandler(root, Path.Combine(root, "Band"), importDir, releaseId) { RejectManual = true };
        var rejectedResult = await BuildImport(temp.Path, rejected).ImportAsync(jobId, RootName, "Band", RootName, releaseId);
        Assert.Equal("failed", rejectedResult.State);
        Assert.DoesNotContain(rejected.Requests, r => r.Path == "/api/v1/command");

        using var secondTemp = new TempDirectory();
        (jobId, releaseId, _, importDir) = await PrepareImportState(secondTemp.Path);
        root = Path.Combine(secondTemp.Path, "media", "music");
        Directory.CreateDirectory(Path.Combine(root, "Band"));
        var ambiguous = new FakeLidarrHandler(root, Path.Combine(root, "Band"), importDir, releaseId) { FailCommandPost = true };
        var handoff = BuildImport(secondTemp.Path, ambiguous);
        Assert.Equal("unknown", (await handoff.ImportAsync(jobId, RootName, "Band", RootName, releaseId)).State);
        Assert.Equal("unknown", (await handoff.ImportAsync(jobId, RootName, "Band", RootName, releaseId)).State);
        Assert.Single(ambiguous.Requests, r => r.Path == "/api/v1/command");
    }

    [Fact]
    public async Task ImportRecoveryMapsTrackFileIdsAfterLidarrMovesAudio()
    {
        using var temp = new TempDirectory();
        var (jobId, releaseId, _, importDir) = await PrepareImportState(temp.Path);
        var root = Path.Combine(temp.Path, "media", "music");
        Directory.CreateDirectory(Path.Combine(root, "Band"));
        var handler = new FakeLidarrHandler(root, Path.Combine(root, "Band"), importDir, releaseId)
        {
            MoveAudioOnCommand = true,
            HiddenTrackLists = 2,
        };
        var handoff = BuildImport(temp.Path, handler);

        var first = await handoff.ImportAsync(jobId, RootName, "Band", RootName, releaseId);
        var retry = await handoff.ImportAsync(jobId, RootName, "Band", RootName, releaseId);
        var recovered = await handoff.ImportAsync(jobId, RootName, "Band", RootName, releaseId);

        Assert.Equal("failed", first.State);
        Assert.Equal("failed", retry.State);
        Assert.Equal("import_not_verified", retry.Error);
        Assert.Equal("imported", recovered.State);
        Assert.Single(handler.Requests, r => r.Path == "/api/v1/command");
        Assert.Contains(handler.Requests, r => r.Path == "/api/v1/track?albumId=42");
    }

    private static SalmonFile FileRecord(string path, byte[] bytes) =>
        new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private static bool HasMultipartField(string body, string name, string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(body,
            $"name=\\\"?{System.Text.RegularExpressions.Regex.Escape(name)}\\\"?\\r\\n\\r\\n{System.Text.RegularExpressions.Regex.Escape(value)}\\r\\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static SalmonMediaHandoff Build(string root, FakeQbHandler handler, bool useCredentials = true)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Salmon:Root"] = root,
            ["Salmon:QbittorrentUrl"] = "http://qbt:8080/",
            ["Salmon:QbittorrentUser"] = useCredentials ? "user" : null,
            ["Salmon:QbittorrentPassword"] = useCredentials ? "password" : null,
        }).Build();
        var options = new Mock<IOptionsMonitor<LidarrSettings>>();
        options.SetupGet(x => x.CurrentValue).Returns(new LidarrSettings());
        return new SalmonMediaHandoff(factory.Object, config, options.Object);
    }

    private static SalmonMediaHandoff BuildImport(string root, FakeLidarrHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Salmon:Root"] = root }).Build();
        var options = new Mock<IOptionsMonitor<LidarrSettings>>();
        options.SetupGet(x => x.CurrentValue).Returns(new LidarrSettings { BaseUrl = "http://lidarr:8686", ApiKey = "api-secret" });
        return new SalmonMediaHandoff(factory.Object, config, options.Object);
    }

    private static async Task<(string JobId, string ReleaseId, SalmonFile File, string ImportDir)> PrepareImportState(string root)
    {
        var jobId = "import-job";
        var releaseId = "dd145441-06f5-4420-81a7-92ded3d4e734";
        var bytes = Encoding.UTF8.GetBytes("test audio bytes");
        var file = FileRecord("01 - Song.flac", bytes);
        var payload = Path.Combine(root, "music-prepared", jobId, RootName);
        Directory.CreateDirectory(payload);
        await File.WriteAllBytesAsync(Path.Combine(payload, file.Path), bytes);
        var stateDir = Path.Combine(root, "salmon-state", jobId);
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new[] { file }));
        await File.WriteAllTextAsync(Path.Combine(stateDir, "handoff.json"),
            $$"""{"version":1,"jobId":"{{jobId}}","rootName":"{{RootName}}","manifestDigest":"m","payloadDigest":"p"}""");
        return (jobId, releaseId, file, Path.Combine(root, "salmon-import", jobId, RootName));
    }

    private static (byte[] Torrent, string Hash) MakeTorrent(string rootName, string path, byte[] bytes)
    {
        var pieces = SHA1.HashData(bytes);
        var file = Dict(("length", Integer(bytes.Length)), ("path", List(Bytes(path))));
        var info = Dict(("files", List(file)), ("name", Bytes(rootName)), ("piece length", Integer(16384)),
            ("pieces", Bytes(pieces)), ("private", Integer(1)), ("source", Bytes("RED")));
        var tracker = Bytes(Announce);
        var torrent = Dict(("announce", tracker), ("announce-list", List(List(tracker))), ("info", info));
        var hash = Convert.ToHexStringLower(SHA1.HashData(info));
        return (torrent, hash);
    }

    private static byte[] Dict(params (string Key, byte[] Value)[] entries)
    {
        using var output = new MemoryStream();
        output.WriteByte((byte)'d');
        foreach (var (key, value) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            output.Write(Bytes(key));
            output.Write(value);
        }
        output.WriteByte((byte)'e');
        return output.ToArray();
    }
    private static byte[] List(params byte[][] items)
    {
        using var output = new MemoryStream();
        output.WriteByte((byte)'l');
        foreach (var item in items) output.Write(item);
        output.WriteByte((byte)'e');
        return output.ToArray();
    }
    private static byte[] Integer(long value) => Encoding.ASCII.GetBytes("i" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "e");
    private static byte[] Bytes(string value) => Bytes(Encoding.UTF8.GetBytes(value));
    private static byte[] Bytes(byte[] value) => Encoding.ASCII.GetBytes(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":").Concat(value).ToArray();

    private sealed class FakeLidarrHandler(string rootPath, string artistPath, string importDir, string releaseId) : HttpMessageHandler
    {
        public sealed record Request(string Method, string Path, string Body, string? ApiKey);
        public List<Request> Requests { get; } = [];
        public bool RejectManual { get; set; }
        public bool FailCommandPost { get; set; }
        public bool MoveAudioOnCommand { get; set; }
        public bool MultipleReleases { get; set; }
        public bool AddUnrelatedLookupGroup { get; set; }
        public string? RequiredLookupTerm { get; set; }
        public int HiddenTrackLists { get; set; }
        public bool Imported { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("X-Api-Key", out var keys);
            Requests.Add(new(request.Method.Method, request.RequestUri.PathAndQuery, body, keys?.SingleOrDefault()));
            if (path == "/api/v1/album/lookup")
            {
                if (RequiredLookupTerm is not null
                    && System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["term"] != RequiredLookupTerm)
                    return Json(new JsonArray());
                var lookup = new JsonArray(new JsonObject
                {
                    ["id"] = 0,
                    ["title"] = RootName,
                    ["foreignAlbumId"] = "group-1",
                    ["artist"] = new JsonObject { ["artistName"] = "Band" },
                    ["releases"] = new JsonArray(new JsonObject
                    {
                        ["id"] = 0, ["foreignReleaseId"] = releaseId, ["title"] = RootName,
                    }),
                });
                if (AddUnrelatedLookupGroup)
                    lookup.Add(new JsonObject
                    {
                        ["id"] = 0,
                        ["title"] = RootName,
                        ["foreignAlbumId"] = "group-2",
                        ["artist"] = new JsonObject { ["artistName"] = "Band" },
                        ["releases"] = new JsonArray(new JsonObject
                        {
                            ["id"] = 0, ["foreignReleaseId"] = "another-release", ["title"] = RootName,
                        }),
                    });
                return Json(lookup);
            }
            if (path == "/api/v1/album" && request.RequestUri.Query.Contains("foreignAlbumId=group-1", StringComparison.Ordinal))
                return Json(new JsonArray(new JsonObject { ["id"] = 42, ["artistId"] = 7, ["foreignAlbumId"] = "group-1" }));
            if (path == "/api/v1/album/42")
                return Json(new JsonObject
                {
                    ["releases"] = MultipleReleases
                        ? new JsonArray(
                            new JsonObject { ["id"] = 11, ["foreignReleaseId"] = releaseId, ["trackCount"] = 1 },
                            new JsonObject { ["id"] = 12, ["foreignReleaseId"] = "another-release", ["trackCount"] = 1 })
                        : new JsonArray(new JsonObject { ["id"] = 11, ["foreignReleaseId"] = releaseId, ["trackCount"] = 1 }),
                });
            if (path == "/api/v1/rootfolder") return Json(new JsonArray(new JsonObject { ["id"] = 1, ["path"] = rootPath }));
            if (path == "/api/v1/artist/7") return Json(new JsonObject { ["id"] = 7, ["path"] = artistPath });
            if (path == "/api/v1/manualimport")
            {
                var row = new JsonObject
                {
                    ["path"] = Path.Combine(importDir, "01 - Song.flac"),
                    ["artist"] = new JsonObject { ["id"] = 7 },
                    ["album"] = new JsonObject { ["id"] = 42 },
                    ["albumReleaseId"] = RejectManual ? 12 : 11,
                    ["tracks"] = new JsonArray(new JsonObject { ["id"] = 9 }),
                    ["rejections"] = RejectManual ? new JsonArray("wrong release") : new JsonArray(),
                    ["quality"] = new JsonObject { ["quality"] = new JsonObject { ["id"] = 1 } },
                    ["releaseGroup"] = "",
                    ["indexerFlags"] = 0,
                    ["downloadId"] = "",
                };
                return Json(new JsonArray(row));
            }
            if (path == "/api/v1/command" && request.Method == HttpMethod.Post)
            {
                if (FailCommandPost) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                if (MoveAudioOnCommand)
                {
                    var source = Path.Combine(importDir, "01 - Song.flac");
                    var destinationDirectory = Path.Combine(artistPath, RootName);
                    Directory.CreateDirectory(destinationDirectory);
                    File.Move(source, Path.Combine(destinationDirectory, "01 - Song.flac"));
                }
                Imported = true;
                return Json(new JsonObject { ["id"] = 77, ["status"] = "queued" });
            }
            if (path == "/api/v1/command/77") return Json(new JsonObject { ["id"] = 77, ["status"] = "completed" });
            if (path == "/api/v1/trackFile")
                return Imported
                    ? Json(new JsonArray(new JsonObject
                    {
                        ["id"] = 101,
                        ["path"] = Path.Combine(artistPath, RootName, "01 - Song.flac"),
                    }))
                    : Json(new JsonArray());
            if (path == "/api/v1/track")
            {
                if (HiddenTrackLists > 0)
                {
                    HiddenTrackLists--;
                    return Json(new JsonArray());
                }
                return Imported
                    ? Json(new JsonArray(new JsonObject { ["id"] = 9, ["trackFileId"] = 101 }))
                    : Json(new JsonArray());
            }
            return Json(new JsonArray());
        }

        private static HttpResponseMessage Json(JsonNode value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private sealed class FakeQbHandler(string hash, string name, string savePath, string category, string tag, string announce) : HttpMessageHandler
    {
        public sealed record Request(string Method, string Path, string Body);
        public List<Request> Requests { get; } = [];
        public bool AllowAnonymous { get; set; }
        public bool HasTorrent { get; set; }
        public bool HideAddedTorrent { get; set; }
        public string State { get; set; } = "pausedUP";
        public byte[]? ExportBytes { get; set; }
        public double RecheckProgress { get; set; } = 1;
        public double? FileProgressOverride { get; set; }
        public string? FileName { get; set; }
        public long? FileSize { get; set; }
        public int FilePriority { get; set; } = 1;
        public bool FastRecheck { get; set; }
        public int InfoMissesAfterAdd { get; set; }
        public IReadOnlyList<string> ExtraTrackerUrls { get; set; } = [];
        public TaskCompletionSource<bool>? CheckingObserved { get; set; }
        public int ResumeDataReads { get; set; }
        private bool _rechecked;
        private int _checkingReads;
        private int _remainingInfoMisses;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new(request.Method.Method, path, body));
            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                var login = Ok("Ok.");
                login.Headers.Add("Set-Cookie", "SID=abc; HttpOnly");
                return login;
            }
            if (!AllowAnonymous && (!request.Headers.TryGetValues("Cookie", out var cookies) || !cookies.Contains("SID=abc")))
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (path.EndsWith("/torrents/info", StringComparison.Ordinal))
            {
                var state = _checkingReads > 0 ? "checkingUP"
                    : State.Equals("checkingResumeData", StringComparison.OrdinalIgnoreCase) && ResumeDataReads > 0
                        ? "checkingResumeData" : State.Equals("checkingResumeData", StringComparison.OrdinalIgnoreCase) ? "pausedUP" : State;
                if (state.Equals("checkingResumeData", StringComparison.OrdinalIgnoreCase)) ResumeDataReads--;
                if (_checkingReads > 0) _checkingReads--;
                if (state.StartsWith("checking", StringComparison.OrdinalIgnoreCase))
                    CheckingObserved?.TrySetResult(true);
                var progress = _rechecked ? RecheckProgress : 1.0;
                if (HasTorrent && _remainingInfoMisses > 0)
                {
                    _remainingInfoMisses--;
                    return Ok("[]");
                }
                return Ok(HasTorrent && !HideAddedTorrent ? JsonSerializerObject(new
                {
                    hash, name, save_path = savePath, category, tags = tag, state, progress,
                }) : "[]");
            }
            if (path.EndsWith("/torrents/categories", StringComparison.Ordinal)) return Ok("{}");
            if (path.EndsWith("/torrents/tags", StringComparison.Ordinal)) return Ok("[]");
            if (path.EndsWith("/torrents/trackers", StringComparison.Ordinal))
            {
                var urls = new JsonArray(
                    new JsonObject { ["url"] = announce },
                    new JsonObject { ["url"] = "** [DHT] **" },
                    new JsonObject { ["url"] = "** [PeX] **" },
                    new JsonObject { ["url"] = "** [LSD] **" });
                foreach (var extra in ExtraTrackerUrls) urls.Add(new JsonObject { ["url"] = extra });
                return Ok(urls.ToJsonString());
            }
            if (path.EndsWith("/torrents/files", StringComparison.Ordinal)) return Ok(System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { name = FileName ?? "01.flac", size = FileSize ?? 1, progress = FileProgressOverride ?? (_rechecked ? RecheckProgress : 1.0), priority = FilePriority },
            }));
            if (path.EndsWith("/torrents/export", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ExportBytes ?? []) };
            if (path.EndsWith("/torrents/add", StringComparison.Ordinal))
            {
                HasTorrent = true;
                _remainingInfoMisses = InfoMissesAfterAdd;
            }
            if (path.EndsWith("/torrents/stop", StringComparison.Ordinal)) State = "pausedUP";
            if (path.EndsWith("/torrents/recheck", StringComparison.Ordinal)) { State = "pausedUP"; _rechecked = true; _checkingReads = FastRecheck ? 0 : 1; }
            if (path.EndsWith("/torrents/start", StringComparison.Ordinal)) State = "uploading";
            if (path.EndsWith("/torrents/createCategory", StringComparison.Ordinal)
                || path.EndsWith("/torrents/createTags", StringComparison.Ordinal)
                || path.EndsWith("/torrents/stop", StringComparison.Ordinal)
                || path.EndsWith("/torrents/recheck", StringComparison.Ordinal)
                || path.EndsWith("/torrents/start", StringComparison.Ordinal)
                || path.EndsWith("/torrents/setShareLimits", StringComparison.Ordinal)) return Ok("");
            return Ok("Ok.");
        }

        private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
        private static string JsonSerializerObject<T>(T value) => System.Text.Json.JsonSerializer.Serialize(new[] { value });
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "salmon-handoff-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
