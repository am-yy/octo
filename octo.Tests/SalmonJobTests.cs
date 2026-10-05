using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Lidarr;
using Octo.Services.Trackers;

namespace Octo.Tests;

public sealed class SalmonJobTests
{
    [Fact]
    public async Task ChangedPayloadOrCancelledApprovalNeverSubmits()
    {
        using var f = new Fixture();
        await f.PrepareAsync();
        await f.Jobs.ReviewAsync(f.Id, default);
        Assert.Equal(0, f.Uploads); // Review alone is not approval.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SubmitAsync(f.Id, "wrong-revision", default));
        await File.AppendAllTextAsync(f.FrozenFile, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal(0, f.Uploads);
    }

    [Fact]
    public async Task FreshDuplicateStopsPreviouslyReviewedUpload()
    {
        using var f = new Fixture();
        await f.PrepareAsync();
        var review = JsonSerializer.SerializeToNode(await f.Jobs.ReviewAsync(f.Id, default));
        Assert.True(review!["canSubmit"]!.GetValue<bool>(), review.ToJsonString());
        f.Duplicate = true;
        var state = JsonSerializer.SerializeToNode(await f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal("prepared", state!["Status"]!.GetValue<string>());
        Assert.Equal(0, f.Uploads);
    }

    [Fact]
    public async Task BaseTitleGroupAppearingAfterDeluxeReviewPreventsUpload()
    {
        const string title = "Album (Deluxe Version)";
        using var f = new Fixture(title);
        await f.PrepareAsync(f.Submission(title: title));
        var review = JsonSerializer.SerializeToNode(await f.Jobs.ReviewAsync(f.Id, default));
        Assert.True(review!["canSubmit"]!.GetValue<bool>(), review.ToJsonString());

        f.ExistingGroupId = 27;
        var browseCount = f.Requests.Count(r => r.Action == "browse");
        var state = JsonSerializer.SerializeToNode(await f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal("prepared", state!["Status"]!.GetValue<string>());
        Assert.Equal(4, f.Requests.Count(r => r.Action == "browse") - browseCount);
        Assert.Equal(0, f.Uploads);
    }

    [Fact]
    public async Task AmbiguousPostPersistsAndNeverBlindlyRetries()
    {
        using var f = new Fixture { AmbiguousUpload = true };
        await f.PrepareAsync();
        await f.Jobs.ReviewAsync(f.Id, default);
        await f.Jobs.SubmitAsync(f.Id, f.Revision, default);
        Assert.Equal(1, f.Uploads);
        Assert.Equal("submitting", f.StateAtPost);
        f.Restart();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        await f.Jobs.ReconcileAsync(f.Id, default);
        Assert.Equal(1, f.Uploads);
        var status = JsonSerializer.SerializeToNode(await f.Jobs.GetAsync(f.Id, default));
        Assert.Equal("uploaded", status!["Status"]!.GetValue<string>());
        Assert.Equal(f.Torrent, await f.Jobs.TorrentAsync(f.Id, default));
        var reviewJson = JsonSerializer.Serialize(await f.Jobs.ReviewAsync(f.Id, default));
        Assert.DoesNotContain(Fixture.Passkey, reviewJson);
        Assert.DoesNotContain("torrentBase64", reviewJson);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureBeforeDispatchRequiresFreshReviewButCanRetry(bool cancelled)
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await f.PrepareAsync();
        await f.Jobs.ReviewAsync(f.Id, default);
        var searches = 0;
        f.AfterBrowse = () =>
        {
            if (++searches != 2) return; // Interrupt the upload's cooldown after fresh duplicate searches.
            f.Clock.OnDelay = () =>
            {
                f.Clock.OnDelay = null;
                if (cancelled) cancellation.Cancel();
                else File.AppendAllText(f.FrozenFile, "changed while queued");
            };
        };
        var status = JsonSerializer.SerializeToNode(await f.Jobs.SubmitAsync(f.Id, f.Revision, cancellation.Token));
        Assert.Equal("prepared", status!["Status"]!.GetValue<string>());
        Assert.Contains("not sent", status["Error"]!.GetValue<string>());
        Assert.Equal(0, f.Uploads);
        f.AfterBrowse = null;
        if (!cancelled) await File.WriteAllBytesAsync(f.FrozenFile, f.Audio);
        f.Restart();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        await f.Jobs.ReviewAsync(f.Id, default);
        await f.Jobs.SubmitAsync(f.Id, f.Revision, default);
        Assert.Equal(1, f.Uploads);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(429)]
    public async Task ExplicitRejectionAllowsOnlyNewReviewedAttempt(int httpStatus)
    {
        using var f = new Fixture { RejectedUpload = (HttpStatusCode)httpStatus };
        await f.PrepareAsync();
        await f.Jobs.ReviewAsync(f.Id, default);
        var status = JsonSerializer.SerializeToNode(await f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal("prepared", status!["Status"]!.GetValue<string>());
        Assert.Contains("rejected", status["Error"]!.GetValue<string>());
        f.Restart();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        f.RejectedUpload = null;
        await f.Jobs.ReviewAsync(f.Id, default);
        await f.Jobs.SubmitAsync(f.Id, f.Revision, default);
        Assert.Equal(2, f.Uploads);
    }

    [Fact]
    public async Task ServerFailureStillRequiresReconciliation()
    {
        using var f = new Fixture { RejectedUpload = HttpStatusCode.InternalServerError };
        await f.PrepareAsync();
        await f.Jobs.ReviewAsync(f.Id, default);
        var status = JsonSerializer.SerializeToNode(await f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal("ambiguous", status!["Status"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal(1, f.Uploads);
    }

    [Fact]
    public async Task IdempotentPreparationAndInterruptedTransferNeverPublishPartialPayload()
    {
        using var f = new Fixture();
        var first = JsonSerializer.Serialize(await f.Jobs.CreateAsync(f.Id, f.Submission(), default));
        Assert.Equal(first, JsonSerializer.Serialize(await f.Jobs.CreateAsync(f.Id, f.Submission(), default)));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Jobs.PutFileAsync(f.Id, 0, new MemoryStream(f.Audio[..10]), default));
        Assert.False(File.Exists(f.FrozenFile));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Jobs.ReviewAsync(f.Id, default));
        Assert.False(File.Exists(f.FrozenFile));
        var changed = f.Submission(); changed.SourceEvidence = changed.SourceEvidence with { Description = "Changed evidence" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.CreateAsync(f.Id, changed, default));
    }

    [Fact]
    public async Task ImportSelectionCannotChangePayloadOrExistingImportIntent()
    {
        using var f = new Fixture();
        await f.PrepareAsync();
        var release = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SelectImportReleaseAsync(f.Id, release, default));
        await f.Jobs.ReviewAsync(f.Id, default);
        await f.Jobs.SubmitAsync(f.Id, f.Revision, default);
        var selected = JsonSerializer.SerializeToNode(await f.Jobs.SelectImportReleaseAsync(f.Id, release, default));
        Assert.Equal(f.Revision, selected!["Revision"]!.GetValue<string>());
        Assert.Equal(release, selected["ImportReleaseId"]!.GetValue<string>());
        await File.WriteAllTextAsync(Path.Combine(f.Root, "salmon-state", f.Id, "handoff.json"), """{"import":{"phase":"unknown"}}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SelectImportReleaseAsync(f.Id, Guid.NewGuid().ToString(), default));
        Assert.Equal(1, f.Uploads);
    }

    [Theory]
    [InlineData("../escape.flac")]
    [InlineData("a/../../escape.flac")]
    [InlineData("/absolute.flac")]
    [InlineData("a\\escape.flac")]
    public void InvalidPathsCannotEnterJobs(string path) => Assert.Throws<InvalidDataException>(() => SalmonPayload.ValidatePath(path));

    [Fact]
    public void IncompleteReleaseAlteredLogsAndUnsupportedFormatsFailValidation()
    {
        using var f = new Fixture();
        var s = f.Submission(); s.Tracks.Add(new(1, 2, "Missing", "02.flac"));
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
        s = f.Submission(); s.Source = "CD"; s.Fields["media"] = "CD"; s.SourceEvidence = new("cd-rip", null, "Owned CD");
        s.OriginalLogs["rip.log"] = new string('a', 64);
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
        s = f.Submission(); s.Fields["format"] = "MP3";
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
        s = f.Submission(); s.Checks["mqa"] = "unknown"; s.Target = "ops";
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
    }

    [Fact]
    public void TypedNewReleaseRequiresMqaCheckOnRedButLegacyRedPayloadRemainsValid()
    {
        using var f = new Fixture();
        var legacy = f.Submission();
        legacy.Checks["mqa"] = "unknown";
        SalmonPayload.Validate(legacy);

        var typed = f.Submission();
        typed.UploadMode = "new-group";
        typed.ReleaseMetadata = new SalmonReleaseMetadata
        {
            Title = "Album", ReleaseId = typed.ReleaseId, GroupYear = 2026, Year = 2026,
            ReleaseType = "Album", Artists = [new SalmonArtistCredit("Artist", 1)],
        };
        typed.Checks["mqa"] = "unknown";
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(typed));
        typed.Checks["mqa"] = "passed";
        SalmonPayload.Validate(typed);
    }

    [Fact]
    public void MainArtistSelectionPreservesCreditRolesAndValidatesAlignment()
    {
        using var f = new Fixture();
        var s = f.Submission();
        s.Fields["artists[]"] = new JsonArray("Guest", "Artist", "Producer", "Partner", "Composer", "Remixer");
        s.Fields["importance[]"] = new JsonArray(2, 1, 7, "1", 4, 3);
        SalmonPayload.Validate(s);
        Assert.Equal(["Artist", "Partner"], SalmonPayload.Artists(s, mainOnly: true));
        Assert.Equal(["Guest", "Artist", "Producer", "Partner", "Composer", "Remixer"], SalmonPayload.Artists(s));

        s.Fields["importance[]"] = new JsonArray(1);
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
        Assert.Empty(SalmonPayload.Artists(s, mainOnly: true));
        s.Fields["importance[]"] = new JsonArray(2, 1, 7, 1, 4, 99);
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
        s.Fields.Remove("importance[]");
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
    }

    [Fact]
    public async Task SunDrugOpsNewGroupReviewUsesTypedArtistsWithoutArtistId()
    {
        using var f = new Fixture();
        f.SetAlbumArtist("Sun Drug");
        var submission = f.Submission("ops", "Sun Drug", "Album");
        submission.UploadMode = "new-group";
        submission.ReleaseMetadata = new SalmonReleaseMetadata
        {
            Title = "Album", ReleaseId = submission.ReleaseId, GroupYear = 2026, Year = 2026,
            ReleaseType = "Album", Artists = [new SalmonArtistCredit("Sun Drug", 1)],
        };

        await f.PrepareAsync(submission);
        var review = JsonSerializer.SerializeToNode(await f.Jobs.ReviewAsync(f.Id, default));

        Assert.True(review!["canSubmit"]!.GetValue<bool>());
        Assert.Equal("CandidateNewGroup", review["classification"]!["decision"]!.GetValue<string>());
        Assert.Equal("new-group", review["submission"]!["uploadMode"]!.GetValue<string>());
        Assert.Equal("Sun Drug", review["submission"]!["ReleaseMetadata"]!["Artists"]![0]!["Name"]!.GetValue<string>());
        Assert.DoesNotContain("artistid", submission.Fields.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingGroupPayloadContainsOnlyExistingGroupReleaseFields()
    {
        using var f = new Fixture();
        var s = f.Submission();
        s.UploadMode = "existing-group";
        s.GroupId = 7;
        s.ReleaseMetadata = new SalmonReleaseMetadata
        {
            Title = "Album", ReleaseId = s.ReleaseId, GroupYear = 2026, Year = 2026,
            ReleaseType = "Album", Artists = [new SalmonArtistCredit("Artist", 1)],
        };
        s.Fields = new()
        {
            ["submit"] = true, ["type"] = 0, ["groupid"] = 7, ["remaster"] = true,
            ["remaster_year"] = 2026, ["format"] = "FLAC", ["bitrate"] = "Lossless",
            ["vbr"] = false, ["media"] = "WEB", ["release_desc"] = "Release details",
        };

        SalmonPayload.Validate(s);
        s.Fields["title"] = "Album";
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
    }

    [Fact]
    public async Task ExistingGroupReceiptMismatchPreservesIdsAndBlocksHandoffAndRetry()
    {
        using var f = new Fixture { ExistingGroupId = 7 };
        var submission = f.Submission();
        submission.UploadMode = "existing-group";
        submission.GroupId = 7;
        submission.ReleaseMetadata = new SalmonReleaseMetadata
        {
            Title = "Album", ReleaseId = submission.ReleaseId, GroupYear = 2026, Year = 2026,
            ReleaseType = "Album", Artists = [new SalmonArtistCredit("Artist", 1)],
        };
        submission.Fields = new()
        {
            ["submit"] = true, ["type"] = 0, ["groupid"] = 7, ["remaster"] = true,
            ["remaster_year"] = 2026, ["format"] = "FLAC", ["bitrate"] = "Lossless",
            ["vbr"] = false, ["media"] = "WEB", ["release_desc"] = "Release details",
        };
        await f.PrepareAsync(submission);
        var review = JsonSerializer.SerializeToNode(await f.Jobs.ReviewAsync(f.Id, default));
        Assert.True(review!["canSubmit"]!.GetValue<bool>(), review.ToJsonString());
        Assert.Equal("CandidateExistingGroup", review["classification"]!["decision"]!.GetValue<string>());

        var result = JsonSerializer.SerializeToNode(await f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal("receipt-mismatch", result!["Status"]!.GetValue<string>());
        Assert.Equal(5, result["TorrentId"]!.GetValue<int>());
        Assert.Equal(6, result["GroupId"]!.GetValue<int>());
        Assert.Equal(1, f.Uploads);
        Assert.DoesNotContain(f.Requests, r => r.Action == "download");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.HandoffAsync(f.Id, default));
        var recovered = JsonSerializer.SerializeToNode(await f.Jobs.ReconcileAsync(f.Id, default));
        Assert.Equal("receipt-mismatch", recovered!["Status"]!.GetValue<string>());
        Assert.Equal(5, recovered["TorrentId"]!.GetValue<int>());
        Assert.Equal(6, recovered["GroupId"]!.GetValue<int>());
        Assert.Equal(1, f.Uploads);
    }

    [Fact]
    public async Task TrackerPreparationRefreshesIncompleteSourceForUnsavedOpportunity()
    {
        using var f = new Fixture();
        f.EnableSourceOpportunity("red", "Redacted (Prowlarr)", complete: false);
        Assert.False(f.OpportunityRows()[0].Sources[0].Complete);
        Assert.False(f.OpportunityRows()[0].Saved);

        var context = new SalmonPreparationContext("WEB", "Album", ["Artist"], "album", "new-group",
            SourceTorrentHash: Fixture.SourceHash);
        var result = JsonSerializer.SerializeToNode(await f.Jobs.ContextAsync("ops", context, default));

        Assert.Equal("new-group", result!["uploadMode"]!.GetValue<string>());
        Assert.True(f.OpportunityRows()[0].Sources.Single().Complete);
        Assert.Contains(f.LidarrRequests, path => path.StartsWith("/api/v1/history?albumId=42", StringComparison.Ordinal));
        Assert.Contains(f.TrackerRequests, request => request.Tracker == "red" && request.Action == "torrent"
            && string.Equals(request.Query, Fixture.SourceHash, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(f.TrackerRequests, request => request.Tracker == "ops" && request.Action == "torrent");
    }

    [Fact]
    public async Task TrackerPreparationRefreshUsesUniqueHashWhenAlbumNamesRepeat()
    {
        using var f = new Fixture();
        f.EnableSourceOpportunity("red", "Redacted (Prowlarr)", complete: false);
        var rows = f.OpportunityRows();
        rows.Add(new TrackerOpportunity
        {
            SchemaVersion = 2, Key = "cccccccccccccccccccccccccccccccc", Artist = "Artist", Album = "Album",
            AlbumType = "Album", LidarrAlbumId = 43, Names = [new ReleaseName("Artist", "Album")],
            IdentityStatus = "resolved", IdentityVersion = 4,
            Sources = [new TrackerLocalSource { Hash = new string('c', 40), Tracker = "red",
                Indexer = "Redacted (Prowlarr)", Complete = true }],
        });
        await File.WriteAllTextAsync(f.OpportunityPath, JsonSerializer.Serialize(rows));
        f.Restart(f.OpportunityPath);

        var context = new SalmonPreparationContext("WEB", "Album", ["Artist"], "album", "new-group",
            SourceTorrentHash: Fixture.SourceHash);
        await f.Jobs.ContextAsync("ops", context, default);

        Assert.Contains(f.LidarrRequests, path => path.StartsWith("/api/v1/history?albumId=42", StringComparison.Ordinal));
        Assert.DoesNotContain(f.LidarrRequests, path => path.StartsWith("/api/v1/history?albumId=43", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrackerJobCreationRefreshesIncompleteSourceForUnsavedOpportunity()
    {
        using var f = new Fixture();
        f.EnableSourceOpportunity("red", "Redacted (Prowlarr)", complete: false);
        var submission = f.Submission("ops");
        submission.SourceEvidence = submission.SourceEvidence with { Kind = "tracker-download", Description = "Complete source torrent" };
        submission.SourceTorrentHash = Fixture.SourceHash;
        submission.OpportunityKey = Fixture.OpportunityKey;
        submission.IdentityVersion = 4;
        submission.ReleaseMetadata = new SalmonReleaseMetadata
        {
            Title = "Album", ReleaseId = submission.ReleaseId, GroupYear = 2026, Year = 2026,
            ReleaseType = "Album", Artists = [new SalmonArtistCredit("Artist", 1)],
        };
        submission.Checks["sourceTorrent"] = "passed";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.CreateAsync(f.Id, submission, default));

        Assert.Equal("Source torrent must complete a full successful recheck before preparation.", error.Message);
        Assert.True(f.OpportunityRows()[0].Sources.Single().Complete);
        Assert.Contains(f.TrackerRequests, request => request.Tracker == "red" && request.Action == "torrent"
            && string.Equals(request.Query, Fixture.SourceHash, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(f.TrackerRequests, request => request.Tracker == "ops" && request.Action == "torrent");
    }

    [Fact]
    public async Task ExistingPreparationReplaySurvivesStaleOpportunityIdentity()
    {
        using var f = new Fixture();
        f.EnableSourceOpportunity("red", "Redacted (Prowlarr)");
        SalmonSubmission SubmissionWithIdentity()
        {
            var submission = f.Submission();
            submission.IdentityVersion = 4;
            submission.OpportunityKey = Fixture.OpportunityKey;
            submission.ReleaseMetadata = new SalmonReleaseMetadata
            {
                Title = "Album", ReleaseId = submission.ReleaseId, GroupYear = 2026, Year = 2026,
                ReleaseType = "Album", Artists = [new SalmonArtistCredit("Artist", 1)],
            };
            return submission;
        }

        var first = JsonSerializer.Serialize(await f.Jobs.CreateAsync(f.Id, SubmissionWithIdentity(), default));
        var rows = f.OpportunityRows();
        rows[0].IdentityStatus = "unresolved";
        rows[0].IdentityChanged = true;
        await File.WriteAllTextAsync(f.OpportunityPath, JsonSerializer.Serialize(rows));
        f.Restart(f.OpportunityPath);
        Assert.Equal("unresolved", Assert.Single(await f.Opportunities!.ListAsync()).IdentityStatus);

        Assert.Equal(first, JsonSerializer.Serialize(await f.Jobs.CreateAsync(f.Id, SubmissionWithIdentity(), default)));
    }

    [Fact]
    public async Task TrackerSourceContextUsesExactSourceTrackerAndRejectsUnknownOrDestinationMapping()
    {
        using (var f = new Fixture())
        {
            f.EnableSourceOpportunity("red", "Redacted (Prowlarr)");
            var context = new SalmonPreparationContext("WEB", "Album", ["Artist"], "album", "new-group",
                SourceTorrentHash: Fixture.SourceHash);

            var result = JsonSerializer.SerializeToNode(await f.Jobs.ContextAsync("ops", context, default));

            Assert.Equal("new-group", result!["uploadMode"]!.GetValue<string>());
            Assert.Contains(f.TrackerRequests, r => r.Tracker == "red" && r.Action == "torrent"
                && string.Equals(r.Query, Fixture.SourceHash, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(f.TrackerRequests, r => r.Tracker == "ops" && r.Action == "torrent");
            Assert.DoesNotContain(f.TrackerRequests, r => r.Tracker == "red" && r.Action == "browse");
        }

        using (var f = new Fixture())
        {
            f.EnableSourceOpportunity("red", "Redated alias");
            var context = new SalmonPreparationContext("WEB", "Album", ["Artist"], "album", "new-group",
                SourceTorrentHash: Fixture.SourceHash);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.ContextAsync("ops", context, default));
            Assert.Empty(f.TrackerRequests);
        }

        using (var f = new Fixture())
        {
            f.EnableSourceOpportunity("ops", "Orpheus (Prowlarr)");
            var context = new SalmonPreparationContext("WEB", "Album", ["Artist"], "album", "new-group",
                SourceTorrentHash: Fixture.SourceHash);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.ContextAsync("ops", context, default));
            Assert.Empty(f.TrackerRequests);
        }
    }

    [Fact]
    public async Task OversizedFlacPaddingCannotFreeze()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.FrozenFile)!);
        using var output = new MemoryStream();
        output.Write(f.Audio.AsSpan(0, 42)); // STREAMINFO remains the first metadata block.
        output.Write(new byte[] { 1, 16, 0, 1 }); // Non-final padding, 1024 KiB + 1 byte.
        output.Write(new byte[1024 * 1024 + 1]);
        output.Write(f.Audio.AsSpan(42));
        var oversized = output.ToArray();
        await File.WriteAllBytesAsync(f.FrozenFile, oversized);
        var submission = f.Submission();
        submission.Files[0] = new("01.flac", oversized.Length, Convert.ToHexStringLower(SHA256.HashData(oversized)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SalmonPayload.VerifyFilesAsync(
            Path.GetDirectoryName(f.FrozenFile)!, submission, true, default));
        Assert.Contains("1024 KiB", error.Message);
    }

    [Theory]
    [InlineData("Classical")]
    [InlineData("Chamber Music")]
    [InlineData("Modern Classical")]
    public async Task ClassicalComposerIsVerifiedFromPayloadTags(string genre)
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.FrozenFile)!);
        await File.WriteAllBytesAsync(f.FrozenFile, f.Audio);
        using (var file = TagLib.File.Create(f.FrozenFile))
        {
            file.Tag.Genres = [genre]; file.Tag.Composers = []; file.Save();
        }
        var submission = f.Submission();
        async Task Verify()
        {
            var bytes = await File.ReadAllBytesAsync(f.FrozenFile);
            submission.Files[0] = new("01.flac", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            await SalmonPayload.VerifyFilesAsync(Path.GetDirectoryName(f.FrozenFile)!, submission, true, default);
        }
        await Assert.ThrowsAsync<InvalidDataException>(Verify);
        using (var file = TagLib.File.Create(f.FrozenFile))
        {
            file.Tag.Composers = ["Composer"]; file.Save();
        }
        await Verify();
    }

    private sealed class Fixture : HttpMessageHandler, IHttpClientFactory
    {
        public const string Passkey = "abcdef0123456789abcdef0123456789";
        public const string SourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string OpportunityKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "octo-salmon-job-" + Guid.NewGuid().ToString("N"));
        public string OpportunityPath => Path.Combine(Root, "opportunities.json");
        public List<string> LidarrRequests { get; } = [];
        public string LidarrIndexer { get; private set; } = "Redacted (Prowlarr)";
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public const string ReleaseRoot = "Artist - Album (2026)";
        public string FrozenFile => Path.Combine(Root, "music-prepared", Id, ReleaseRoot, "01.flac");
        public byte[] Audio { get; private set; }
        public byte[] Torrent { get; private set; }
        public string Revision { get; private set; } = "";
        public SalmonJobService Jobs { get; private set; } = null!;
        public TrackerOpportunityService? Opportunities { get; private set; }
        private readonly IConfiguration _config;
        public TrackerDiscoveryTests.AdvancingClock Clock { get; } = new();
        public int Uploads { get; private set; }
        public string? StateAtPost { get; private set; }
        public bool Duplicate { get; set; }
        public bool AmbiguousUpload { get; init; }
        public HttpStatusCode? RejectedUpload { get; set; }
        public int? ExistingGroupId { get; set; }
        public Action? AfterBrowse { get; set; }
        public List<(string Action, string? Query)> Requests { get; } = [];
        public List<(string Tracker, string Action, string? Query)> TrackerRequests { get; } = [];
        private TrackerDirectQueue _queue = null!;
        public Fixture(string title = "Album")
        {
            Directory.CreateDirectory(Root);
            var audioPath = Path.Combine(Root, "sample.flac");
            using var p = new Process { StartInfo = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true } };
            foreach (var arg in new[] { "-nostdin", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", "-sample_fmt", "s16", "-metadata", "artist=Artist", "-metadata", "album_artist=Artist", "-metadata", "album=" + title, "-metadata", "title=Track", "-metadata", "track=1", "-metadata", "disc=1", audioPath }) p.StartInfo.ArgumentList.Add(arg);
            p.Start(); var error = p.StandardError.ReadToEnd(); p.WaitForExit();
            Assert.True(p.ExitCode == 0, error);
            Audio = File.ReadAllBytes(audioPath);
            Torrent = MakeTorrent("red");
            _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Salmon:Root"] = Root, ["Trackers:red:ApiKey"] = "fake-key", ["Trackers:ops:ApiKey"] = "fake-ops-key",
                ["Salmon:QbittorrentUrl"] = "http://qbt.invalid",
                ["Lidarr:BaseUrl"] = "http://lidarr.invalid", ["Lidarr:ApiKey"] = "lidarr-key",
            ["Trackers:red:LidarrIndexerNames:0"] = "Redacted (Prowlarr)",
                ["Trackers:ops:LidarrIndexerNames:0"] = "Orpheus (Prowlarr)",
            }).Build();
            Restart();
        }
        public void Restart(string? opportunityPath = null)
        {
            _queue = new TrackerDirectQueue(Path.Combine(Root, "cooldowns.json"), _config, this, Clock);
            var lidarrSettings = TestOptions.Monitor(new LidarrSettings { BaseUrl = "http://lidarr.invalid", ApiKey = "lidarr-key" });
            var handoff = new SalmonMediaHandoff(this, _config, lidarrSettings);
            Opportunities = opportunityPath is null ? null : new TrackerOpportunityService(null!,
                new LidarrClient(this, lidarrSettings), _queue, opportunityPath,
                NullLogger<TrackerOpportunityService>.Instance, Clock, handoff: handoff, config: _config);
            Jobs = new SalmonJobService(_queue, handoff, _config, Clock, Opportunities);
        }
        public void EnableSourceOpportunity(string tracker, string indexer, bool complete = true)
        {
            LidarrIndexer = indexer;
            var sourcePath = Path.Combine(Root, ReleaseRoot, "01.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, Audio);
            var row = new TrackerOpportunity
            {
                SchemaVersion = 2, Key = OpportunityKey, Artist = "Artist", Album = "Album", AlbumType = "Album",
                LidarrAlbumId = 42, Names = [new ReleaseName("Artist", "Album")],
                IdentityStatus = "resolved", IdentityVersion = 4, Saved = false,
                Sources = [new TrackerLocalSource { Hash = SourceHash, Tracker = tracker, Indexer = indexer,
                    Complete = complete, Error = complete ? null : "torrent_incomplete",
                    ObservedUtc = Clock.GetUtcNow().UtcDateTime.AddMinutes(-16) }],
            };
            var path = OpportunityPath;
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { row }));
            Restart(path);
        }
        public SalmonSubmission Submission(string target = "red", string artist = "Artist", string title = "Album") => new()
        {
            Target = target, RootName = ReleaseRoot, ReleaseId = "official-release-id", Source = "WEB",
            SourceEvidence = new("purchased-download", "https://label.example/release", "Receipt and complete official manifest reviewed"),
            Tracks = [new(1, 1, "Track", "01.flac")], Files = [new("01.flac", Audio.Length, Convert.ToHexStringLower(SHA256.HashData(Audio)))],
            TorrentBase64 = Convert.ToBase64String(target == "red" ? Torrent : MakeTorrent(target)),
            Fields = new() { ["submit"] = true, ["type"] = 0, ["artists[]"] = new JsonArray(artist), ["importance[]"] = new JsonArray(1), ["title"] = title, ["year"] = 2026, ["releasetype"] = 1, ["format"] = "FLAC", ["bitrate"] = "Lossless", ["media"] = "WEB", ["tags"] = "ambient", ["remaster"] = true, ["remaster_year"] = 2026, ["release_desc"] = "Release details" },
            Checks = new() { ["integrity"] = "passed", ["trackManifest"] = "passed", ["mqa"] = "passed" },
        };
        private byte[] MakeTorrent(string target)
        {
            var host = target == "ops" ? "home.opsfet.ch" : "flacsfor.me";
            return Bencode(new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["announce"] = "https://" + host + "/" + Passkey + "/announce",
                ["info"] = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["files"] = new object[] { new SortedDictionary<string, object>(StringComparer.Ordinal) { ["length"] = Audio.Length, ["path"] = new object[] { "01.flac" } } },
                    ["name"] = ReleaseRoot, ["piece length"] = 16384, ["pieces"] = SHA1.HashData(Audio), ["private"] = 1, ["source"] = target.ToUpperInvariant(),
                },
            });
        }
        public void SetAlbumArtist(string artist)
        {
            var path = Path.Combine(Root, "sample.flac");
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Performers = [artist];
                file.Tag.AlbumArtists = [artist];
                file.Save();
            }
            Audio = File.ReadAllBytes(path);
            Torrent = MakeTorrent("red");
        }
        public async Task PrepareAsync(SalmonSubmission? submission = null)
        {
            var result = JsonSerializer.SerializeToNode(await Jobs.CreateAsync(Id, submission ?? Submission(), default));
            Revision = result!["Revision"]!.GetValue<string>();
            await Jobs.PutFileAsync(Id, 0, new MemoryStream(Audio), default);
        }
        public List<TrackerOpportunity> OpportunityRows() => JsonSerializer.Deserialize<List<TrackerOpportunity>>(
            File.ReadAllText(OpportunityPath)) ?? [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "lidarr.invalid")
            {
                var path = request.RequestUri.PathAndQuery;
                LidarrRequests.Add(path);
                JsonNode body = path.StartsWith("/api/v1/history?", StringComparison.Ordinal)
                    ? new JsonObject
                    {
                        ["totalRecords"] = 2,
                        ["records"] = new JsonArray(
                            new JsonObject { ["albumId"] = 42, ["eventType"] = "grabbed", ["downloadId"] = SourceHash,
                                ["data"] = new JsonObject { ["indexer"] = LidarrIndexer } },
                            new JsonObject { ["albumId"] = 42, ["eventType"] = "downloadImported", ["downloadId"] = SourceHash }),
                    }
                    : path == "/api/v1/album/42"
                        ? new JsonObject { ["id"] = 42, ["statistics"] = new JsonObject { ["trackCount"] = 1, ["trackFileCount"] = 1 } }
                    : path.StartsWith("/api/v1/track?", StringComparison.Ordinal)
                        ? new JsonArray(new JsonObject { ["id"] = 55, ["title"] = "Track", ["trackNumber"] = "1",
                            ["duration"] = 1000, ["hasFile"] = true, ["trackFileId"] = 77 })
                    : path.StartsWith("/api/v1/trackFile?", StringComparison.Ordinal)
                        ? new JsonArray(new JsonObject { ["id"] = 77, ["path"] = Path.Combine(Root, ReleaseRoot, "01.flac"), ["size"] = Audio.Length })
                    : new JsonArray();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) });
            }
            if (request.RequestUri!.Host == "qbt.invalid")
            {
                var body = request.RequestUri.AbsolutePath.EndsWith("/torrents/info", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new[] { new { hash = SourceHash, name = ReleaseRoot, save_path = Root, progress = 1.0, state = "uploading" } })
                    : JsonSerializer.Serialize(new[] { new { name = ReleaseRoot + "/01.flac", size = Audio.Length, progress = 1.0, priority = 0 } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
            var action = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["action"];
            var parameters = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var query = parameters["hash"] ?? parameters["id"];
            Requests.Add((action ?? "", query));
            var tracker = request.RequestUri.Host == "redacted.sh" ? "red" : "ops";
            TrackerRequests.Add((tracker, action ?? "", query));
            if (action == "upload")
            {
                Uploads++;
                StateAtPost = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "salmon-state", Id, "job.json")))!["status"]!.GetValue<string>();
                if (AmbiguousUpload) throw new HttpRequestException("Mock connection lost after POST");
                if (RejectedUpload is { } status) return Task.FromResult(new HttpResponseMessage(status)
                { Content = new StringContent("""{"status":"failure","error":"mock refusal"}""") });
            }
            if (action == "browse") AfterBrowse?.Invoke();
            if (action == "download") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Torrent) });
            var response = action switch
            {
                "index" => new JsonObject { ["passkey"] = Passkey, ["authkey"] = "fake-auth-key" },
                "upload" => new JsonObject { ["torrentId"] = 5, ["groupId"] = 6 },
                "torrent" when string.Equals(query, SourceHash, StringComparison.OrdinalIgnoreCase) => new JsonObject
                {
                    ["torrent"] = new JsonObject { ["id"] = 8, ["hash"] = SourceHash, ["media"] = "WEB", ["format"] = "FLAC" },
                    ["group"] = new JsonObject
                    {
                        ["id"] = 9, ["name"] = "Album", ["releaseType"] = "Album", ["categoryName"] = "Music", ["categoryId"] = 1,
                        ["musicInfo"] = new JsonObject { ["artists"] = new JsonArray(new JsonObject { ["name"] = "Artist" }) },
                    },
                },
                "torrent" => new JsonObject { ["torrent"] = new JsonObject { ["id"] = 5, ["infoHash"] = new SalmonTorrent(Torrent, "red").InfoHash }, ["group"] = new JsonObject { ["id"] = 6 } },
                "browse" when ExistingGroupId is int groupId && (parameters["searchstr"] is "Artist Album" or "Album") => new JsonObject
                {
                    ["pages"] = 1, ["currentPage"] = 1,
                    ["results"] = new JsonArray(new JsonObject
                    {
                        ["groupId"] = groupId, ["artist"] = "Artist", ["groupName"] = "Album",
                        ["releaseType"] = "Album", ["categoryName"] = "Music", ["categoryId"] = 1,
                    }),
                },
                "torrentgroup" when ExistingGroupId is int groupId => new JsonObject
                {
                    ["group"] = new JsonObject
                    {
                        ["id"] = groupId, ["name"] = "Album", ["releaseType"] = "Album",
                        ["categoryName"] = "Music", ["categoryId"] = 1,
                        ["musicInfo"] = new JsonObject { ["artists"] = new JsonArray(new JsonObject { ["name"] = "Artist" }) },
                    },
                    ["torrents"] = new JsonArray(new JsonObject
                    {
                        ["id"] = 8, ["media"] = "CD", ["format"] = "FLAC", ["encoding"] = "Lossless",
                        ["seeders"] = 0, ["scene"] = false,
                    }),
                },
                _ => JsonNode.Parse(Duplicate ? """{"currentPage":1,"pages":1,"results":[{"groupId":6,"artist":"Artist","groupName":"Album","torrents":[{"seeders":0}]}]}""" : """{"results":[],"youMightLike":[]}""")!,
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["status"] = "success", ["response"] = response }.ToJsonString()) });
        }
        private static byte[] Bencode(object value)
        {
            using var s = new MemoryStream();
            void Write(object v)
            {
                switch (v)
                {
                    case string text: Write(Encoding.UTF8.GetBytes(text)); break;
                    case byte[] bytes: s.Write(Encoding.ASCII.GetBytes(bytes.Length + ":")); s.Write(bytes); break;
                    case int n: s.Write(Encoding.ASCII.GetBytes("i" + n + "e")); break;
                    case SortedDictionary<string, object> dict: s.WriteByte((byte)'d'); foreach (var (k, item) in dict) { Write(k); Write(item); } s.WriteByte((byte)'e'); break;
                    case object[] list: s.WriteByte((byte)'l'); foreach (var item in list) Write(item); s.WriteByte((byte)'e'); break;
                }
            }
            Write(value); return s.ToArray();
        }
        protected override void Dispose(bool disposing) { if (disposing) Directory.Delete(Root, true); base.Dispose(disposing); }
    }
}
