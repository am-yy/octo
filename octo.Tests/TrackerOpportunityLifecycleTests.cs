using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Deezer;
using Octo.Services.Lidarr;
using Octo.Services.Metadata;
using Octo.Services.Subsonic;
using Octo.Services.Trackers;

namespace Octo.Tests;

public sealed class TrackerOpportunityLifecycleTests
{
    [Fact]
    public async Task AuthoritativeParentReplacesSavedSingleAndNeverSearchesTrackTitle()
    {
        using var f = new Fixture();
        await f.SaveAsync(); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("Crisps", row.Album); Assert.Equal("resolved", row.IdentityStatus);
        Assert.Equal("candidate", row.Ops.Status); Assert.True(row.Manifest!.Complete);
        Assert.Equal(2, f.ProbeIds.Count); Assert.All(f.MediaRequests, r => Assert.Contains("FLAC", r));
        Assert.All(f.Searches, q => { Assert.Contains("Crisps", q); Assert.DoesNotContain("Get Back Jamie", q); });
        Assert.DoesNotContain(row.Names, n => n.Album == "Get Back Jamie");
        Assert.NotNull(await f.Service.GetManifestAsync("5", row.Manifest.Revision));
    }

    [Fact]
    public async Task CompletedAcquisitionGetsOneCrossUploadThenRestartAndReaddDoNothing()
    {
        using var f = new Fixture { Acquired = true };
        await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("complete", row.Acquisition); Assert.Equal("candidate", row.Ops.Status);
        Assert.Equal("complete", Assert.Single(row.Assessments).Progress);
        Assert.Equal(["red"], f.HashTrackers); Assert.Empty(f.ProbeIds);
        var requests = f.TrackerRequests;
        f.Clock.Advance(TimeSpan.FromHours(1)); f.Restart(); await f.Service.RefreshAsync();
        await f.Saves.SetHeartAsync("user", f.Song(album: "Crisps"), false); await f.Service.RefreshAsync();
        await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        Assert.Equal(requests, f.TrackerRequests);
        await f.Service.RecheckAsync(row.Key); await f.Service.RefreshAsync();
        Assert.Equal(2, f.HashTrackers.Count); Assert.Empty(f.ProbeIds);
    }

    [Fact]
    public async Task SourceLookupFailureCompletesUnknownAndNeverQueriesOppositeHash()
    {
        using var f = new Fixture { Acquired = true, SourceLookupFails = true };
        await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("unknown", row.Ops.Status); Assert.NotNull(Assert.Single(row.Assessments).CompletedUtc);
        Assert.Equal(["red"], f.HashTrackers); Assert.Empty(f.Searches);
        f.Restart(); await f.Service.RefreshAsync(); Assert.Single(f.HashTrackers);
    }

    [Theory]
    [InlineData("Redacted", true)]
    [InlineData("Unknown", true)]
    [InlineData("Redacted (Prowlarr)", false)]
    public async Task UnmappedIndexerAndMissingFileRemainRecoverableWithoutTrackerRequests(string indexer, bool fileExists)
    {
        using var f = new Fixture { Acquired = true, Indexer = indexer };
        if (!fileExists) File.Delete(f.AudioPath);
        await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal(0, f.TrackerRequests); Assert.Equal("unknown", row.Ops.Status);
        Assert.True(await f.Service.RecheckAsync(row.Key));
        Assert.Equal("complete", row.Acquisition);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("43")]
    public async Task IdentityChangeHidesCandidateAndSendsNothingUntilExplicitRecheck(string? deezerId)
    {
        using var f = new Fixture();
        await f.SaveAsync(); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync()); var requests = f.TrackerRequests;
        await f.Saves.RememberMetadataAsync("ext-deezer-42",deezerId,duration:181); f.Clock.Advance(TimeSpan.FromDays(2));
        await f.Service.RefreshAsync();
        var stale = Assert.Single(await f.Service.ListAsync());
        Assert.True(stale.IdentityChanged); Assert.Equal("unknown", stale.Ops.Status); Assert.Equal(requests, f.TrackerRequests);
        await f.Service.RecheckAsync(row.Key); await f.Service.RefreshAsync();
        Assert.False(Assert.Single(await f.Service.ListAsync()).IdentityChanged); Assert.True(f.TrackerRequests > requests);
    }

    [Fact]
    public async Task PendingAssessmentResumesAfterRestartUsingPersistedSourceProof()
    {
        using var f = new Fixture { Acquired = true, CancelDestination = true };
        await f.SaveAsync(album: "Crisps");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.RefreshAsync(f.Cancel.Token));
        var row = Assert.Single(await f.Service.ListAsync()); var assessment = Assert.Single(row.Assessments);
        Assert.Equal("destination-search", assessment.Progress); Assert.Null(assessment.CompletedUtc);
        f.CancelDestination = false; f.Restart(); await f.Service.RefreshAsync();
        Assert.NotNull(Assert.Single(Assert.Single(await f.Service.ListAsync()).Assessments).CompletedUtc);
        Assert.Single(f.HashTrackers);
    }

    [Fact]
    public async Task UnsavedMissingSourceCanRecoverThroughExplicitRecheck()
    {
        using var f = new Fixture { Acquired = true };
        File.Delete(f.AudioPath);
        await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync()); Assert.Empty(row.Assessments);
        await f.Saves.SetHeartAsync("user", f.Song(album: "Crisps"), false); await f.Service.RefreshAsync();
        File.WriteAllBytes(f.AudioPath, [1]);
        await f.Service.RecheckAsync(row.Key); await f.Service.RefreshAsync();
        var recovered = Assert.Single(await f.Service.ListAsync());
        Assert.False(recovered.Saved); Assert.Equal("candidate", recovered.Ops.Status);
        Assert.NotNull(Assert.Single(recovered.Assessments).CompletedUtc); Assert.Single(f.HashTrackers);
    }

    [Fact]
    public async Task IdentityChangeInvalidatesPendingWorkWithoutResettingAutomaticAllowance()
    {
        using var f = new Fixture { Acquired = true, CancelDestination = true };
        await f.SaveAsync(album: "Crisps");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.RefreshAsync(f.Cancel.Token));
        var requests = f.TrackerRequests;
        await f.Saves.RememberMetadataAsync("ext-deezer-42", null, duration: 181);
        f.CancelDestination = false; f.Restart(); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync()); var stale = Assert.Single(row.Assessments);
        Assert.True(row.IdentityChanged); Assert.Equal("invalidated", stale.Progress); Assert.NotNull(stale.CompletedUtc);
        Assert.Equal("identity changed — Recheck", stale.Result!.Error); Assert.Equal(requests, f.TrackerRequests);
    }

    [Fact]
    public async Task RecheckSupersedesPendingPassAndKeepsFreshAllowance()
    {
        using var f = new Fixture { Acquired = true, CancelDestination = true };
        await f.SaveAsync(album: "Crisps");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.RefreshAsync(f.Cancel.Token));
        var prior = Assert.Single(await f.Service.ListAsync());
        await f.Service.RecheckAsync(prior.Key);
        Assert.True(Assert.Single(await f.Service.ListAsync()).RecheckRequested);
        f.CancelDestination = false; f.Restart(); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal(2, row.Assessments.Count); Assert.Equal("invalidated", row.Assessments[0].Progress);
        Assert.Equal(row.RecheckVersion, row.Assessments[1].RecheckVersion);
        Assert.NotNull(row.Assessments[1].CompletedUtc); Assert.False(row.RecheckRequested); Assert.Equal(2, f.HashTrackers.Count);
    }

    [Fact]
    public async Task GuardCancelsHashRequestWhenIdentityChangesWhileQueued()
    {
        using var f = new Fixture { Acquired = true };
        await f.Queue.GetAsync("red", "browse", new Dictionary<string,string>(), CancellationToken.None);
        f.TrackerRequests = 0; f.Searches.Clear();
        await f.SaveAsync(album: "Crisps");
        f.Clock.OnDelay = () => { f.Clock.OnDelay = null; f.Saves.RememberMetadataAsync("ext-deezer-42",null,duration:181).GetAwaiter().GetResult(); };
        await f.Service.RefreshAsync();
        Assert.Empty(f.HashTrackers); Assert.Equal(0, f.TrackerRequests);
    }

    [Fact]
    public async Task FailedProbeStopsAlbumAndBackoffSurvivesRestartThenRecheckResetsIt()
    {
        using var f = new Fixture { SubstituteTrack = true };
        await f.SaveAsync(); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("unavailable", row.DeezerAvailability); Assert.Equal(0, f.TrackerRequests);
        Assert.Equal(["42"], f.ProbeIds); Assert.NotNull(row.NextAttemptUtc);
        var probes = f.ProbeIds.Count;
        f.Restart(); await f.Service.RefreshAsync(); Assert.Equal(probes,f.ProbeIds.Count);
        f.SubstituteTrack = false; await f.Service.RecheckAsync(row.Key); await f.Service.RefreshAsync();
        Assert.Equal("candidate", Assert.Single(await f.Service.ListAsync()).Ops.Status);
    }

    [Fact]
    public async Task ExpiredProofHidesCandidatesAndPreservesIdentityAndRecheck()
    {
        using var f = new Fixture(); await f.SaveAsync(); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync()); var probes = f.ProbeIds.Count;
        f.Clock.Advance(TimeSpan.FromHours(13));
        var expired = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("resolved", expired.IdentityStatus); Assert.Equal("unknown", expired.Ops.Status);
        Assert.Null(await f.Service.GetManifestAsync("5", row.Manifest!.Revision));
        await f.Service.RefreshAsync(); Assert.Equal(probes, f.ProbeIds.Count);
        Assert.True(await f.Service.RecheckAsync(row.Key));
    }

    [Fact]
    public async Task ExplicitRecheckRefreshesCachedAuthoritativeManifest()
    {
        using var f = new Fixture(); await f.SaveAsync(); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync()); var requests = f.TrackerRequests;
        f.DeclaredCount = 3;
        await f.Service.RecheckAsync(row.Key); await f.Service.RefreshAsync();
        var changed = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("unresolved", changed.IdentityStatus); Assert.Equal("unknown", changed.Ops.Status);
        Assert.Equal(requests, f.TrackerRequests); Assert.Null(await f.Service.GetManifestAsync("5", row.Manifest!.Revision));
    }

    [Fact]
    public async Task ResolveAndProbeBudgetsDeduplicateSavedProviderReferences()
    {
        using var f = new Fixture();
        for (var i = 42; i < 54; i++) await f.SaveAsync(id: i.ToString());
        await f.Service.RefreshAsync();
        Assert.Equal(10, f.TrackDetailRequests); Assert.Equal(2, f.ProbeIds.Count);
        Assert.Single(await f.Service.ListAsync(), r => r.IdentityStatus == "resolved");
    }

    [Fact]
    public async Task LegacyPresenceAndSingleAliasesCannotEstablishAlbumPresence()
    {
        using var f = new Fixture { Acquired = true };
        await File.WriteAllTextAsync(f.StatePath, JsonSerializer.Serialize(new[] { new TrackerOpportunity {
            Artist = "Artist", Album = "Crisps", Acquisition = "complete", ForeignAlbumId = "group", LidarrAlbumId = 7,
            ReferenceIds = ["ext-deezer-42"], Names = [new("Artist", "We Make Hits")], Red = new() { Status = "present" }
        } }));
        f.Restart(); await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync()); Assert.Equal("complete", row.Acquisition);
        Assert.DoesNotContain(row.Names, n => n.Album == "We Make Hits"); Assert.Equal("unknown", row.Red.Status);
    }

    [Fact]
    public async Task EditionSearchMigrationInvalidatesCandidatesWithoutResettingAssessmentAllowance()
    {
        using var f = new Fixture { Acquired = true };
        await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        var prior = Assert.Single(await f.Service.ListAsync());
        var assessment = Assert.Single(prior.Assessments);
        prior.SchemaVersion = 2;
        await File.WriteAllTextAsync(f.StatePath, JsonSerializer.Serialize(new[] { prior }));
        var requests = f.TrackerRequests;

        f.Restart();
        var migrated = Assert.Single(await f.Service.ListAsync());
        Assert.Equal(3, migrated.SchemaVersion);
        Assert.Equal("complete", migrated.Acquisition);
        Assert.Equal(prior.IdentityVersion, migrated.IdentityVersion);
        Assert.Equal(prior.Sources.Single().Hash, migrated.Sources.Single().Hash);
        Assert.Equal(prior.References.Single().Id, migrated.References.Single().Id);
        Assert.Equal(prior.RecheckVersion, migrated.RecheckVersion);
        Assert.Equal(prior.CheckedVersion, migrated.CheckedVersion);
        Assert.Equal(TrackerDecision.Unknown, migrated.Ops.Decision);
        Assert.Equal("unknown", migrated.Ops.Status);
        Assert.Equal("none", migrated.Ops.UploadMode);
        Assert.Contains("Recheck", migrated.Ops.Error);
        Assert.Equal(prior.Ops.CheckedUtc, migrated.Ops.CheckedUtc);
        Assert.Equal(prior.Ops.QueryCoverage, migrated.Ops.QueryCoverage);
        var retained = Assert.Single(migrated.Assessments);
        Assert.Equal(assessment.Id, retained.Id);
        Assert.Equal(assessment.CompletedUtc, retained.CompletedUtc);
        Assert.Equal("unknown", retained.Result!.Status);
        await f.Service.RefreshAsync(); f.Restart(); await f.Service.RefreshAsync();
        Assert.Equal(requests, f.TrackerRequests);

        Assert.True(await f.Service.RecheckAsync(prior.Key));
        await f.Service.RefreshAsync(); f.Restart();
        var refreshed = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("candidate", refreshed.Ops.Status);
        Assert.Equal(2, refreshed.Assessments.Count);
        Assert.False(refreshed.RecheckRequested);
    }

    [Fact]
    public async Task LegacySingleClosureStaysOnSingleWhenRecheckResolvesParent()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(f.StatePath, JsonSerializer.Serialize(new[] { new TrackerOpportunity {
            Artist = "Artist", Album = "Get Back Jamie", Acquisition = "complete", ReferenceIds = ["ext-deezer-42"]
        } }));
        f.Restart(); await f.SaveAsync(); var key = Assert.Single(await f.Service.ListAsync()).Key;
        await f.Service.RecheckAsync(key); await f.Service.RefreshAsync();
        var rows = await f.Service.ListAsync(); Assert.Equal(2, rows.Count);
        Assert.Equal("complete", rows.Single(r => r.Album == "Get Back Jamie").Acquisition);
        Assert.Equal("unresolved", rows.Single(r => r.Album == "Crisps").Acquisition);
    }

    [Fact]
    public async Task AcquisitionIntentCannotCloseNewSavedDisplayParent()
    {
        using var f = new Fixture { Acquired = true, ManagedTitle = "We Make Hits", ManagedType = "Single" };
        var song = f.Song(album: "Crisps"); song.MusicBrainzReleaseGroupId = "group";
        await f.Saves.SetHeartAsync("user", song, true); await f.Service.RefreshAsync();
        var old = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("We Make Hits", old.Album); Assert.Equal("complete", old.Acquisition);
        await f.Service.RecheckAsync(old.Key); await f.Service.RefreshAsync();
        var rows = await f.Service.ListAsync();
        Assert.Equal("complete", rows.Single(r => r.Album == "We Make Hits").Acquisition);
        Assert.Equal("unresolved", rows.Single(r => r.Album == "Crisps").Acquisition);
    }

    [Fact]
    public void SourceMappingRequiresExactNonoverlappingNames()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Trackers:red:LidarrIndexerNames:0"]="RED", ["Trackers:ops:LidarrIndexerNames:0"]="OPS" }).Build();
        Assert.Equal("red", TrackerSourceMapping.Resolve(config,"RED")); Assert.Null(TrackerSourceMapping.Resolve(config,"red"));
        Assert.Null(TrackerSourceMapping.Resolve(config,"RED (Prowlarr)")); config["Trackers:ops:LidarrIndexerNames:0"]="RED";
        Assert.Null(TrackerSourceMapping.Resolve(config,"RED"));
    }


    [Fact]
    public async Task Adv_RecheckWithIncompleteDeezerManifestDoesNotStripLidarrIdentityOrShowStaleCandidate()
    {
        using var f = new Fixture { Acquired = true };
        await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("resolved", row.IdentityStatus); Assert.Equal("candidate", row.Ops.Status);
        var lookups = f.HashTrackers.Count;
        f.DeclaredCount = 3;
        await f.Service.RecheckAsync(row.Key); await f.Service.RefreshAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(20)); await f.Service.RefreshAsync();
        var after = Assert.Single(await f.Service.ListAsync());
        var observed = $"identity={after.IdentityStatus} ops={after.Ops.Status} recheckPending={after.RecheckRequested} lookups={f.HashTrackers.Count - lookups}";
        Assert.True(after.IdentityStatus == "resolved" || after.Ops.Status != "candidate", "stale candidate on non-resolved identity: " + observed);
        Assert.True(after.IdentityStatus == "resolved" && f.HashTrackers.Count > lookups, "Recheck stripped verified identity / no reassessment: " + observed);
    }

    [Fact]
    public async Task Adv_RecheckDuringInFlightProbeStillGetsFreshProbe()
    {
        using var f = new Fixture();
        await f.SaveAsync(); await f.Service.RefreshAsync();
        var key = Assert.Single(await f.Service.ListAsync()).Key;
        f.Clock.Advance(TimeSpan.FromHours(25));
        var injected = false;
        f.OnMedia = () => { if (!injected) { injected = true; f.Service.RecheckAsync(key).GetAwaiter().GetResult(); } };
        var before = f.Heads;
        await f.Service.RefreshAsync();
        f.OnMedia = null;
        var probesDuringRace = f.Heads - before;
        await f.Service.RefreshAsync(); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.True(injected, $"no probe request in refresh; probes={f.Heads - before} searches={f.Searches.Count}");
        Assert.True(f.Heads - before > probesDuringRace,
            $"no probe after Recheck; probes={f.Heads - before} race={probesDuringRace} recheckPending={row.RecheckRequested} availability={row.DeezerAvailability}");
    }

    [Fact]
    public void Adv_EditionCollapseRejectsDifferentIsrcs()
    {
        DeezerAlbumManifest M(string id, string isrc) => new(id, id, "album", 1, "Artist", "Crisps", true,
            [new DeezerManifestTrack(id + "1", 1, 1, "Get Back Jamie", "Artist", isrc, 180)]);
        Assert.False(DeezerMetadataService.SameRecordingManifest(M("5", "GBAAA2400001"), M("6", "USXYZ1900009")),
            "different recordings (ISRCs) treated as one edition");
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adv_LegacyManagedAlbumDoesNotReplaceSavedSongParentNomination(bool explicitRecheck)
    {
        using var f = new Fixture { Acquired = true, ManagedTitle = "We Make Hits", ManagedType = "EP" };
        await File.WriteAllTextAsync(f.StatePath, JsonSerializer.Serialize(new[] { new TrackerOpportunity {
            Artist = "Artist", Album = "We Make Hits", Acquisition = "complete", ReferenceIds = ["ext-deezer-42"]
        } }));
        f.Restart(); await f.SaveAsync(album: "Crisps");
        if (explicitRecheck) await f.Service.RecheckAsync(Assert.Single(await f.Service.ListAsync()).Key);
        await f.Service.RefreshAsync(); f.Clock.Advance(TimeSpan.FromDays(2)); await f.Service.RefreshAsync();
        var rows = await f.Service.ListAsync();
        var observed = string.Join("; ", rows.Select(r => $"{r.Album}/{r.IdentityStatus}/{r.IdentityDiagnostic}/saved={r.Saved}/acq={r.Acquisition}"));
        Assert.True(rows.Any(r => r.Album == "Crisps" && r.Saved), "saved song never nominates its album: " + observed);
        Assert.Equal("unresolved", rows.Single(r => r.Album == "Crisps").Acquisition);
        var old = rows.Single(r => r.Album == "We Make Hits");
        Assert.Equal("complete", old.Acquisition); Assert.False(old.Saved); Assert.Empty(old.References);
        if (explicitRecheck) Assert.True(old.ManagedIdentityVerified);
    }

    [Fact]
    public async Task LegacyAcquiredAlbumRecoversOnRecheckWhenDeezerManifestIsIncomplete()
    {
        using var f = new Fixture { Acquired = true, DeclaredCount = 3 };
        await File.WriteAllTextAsync(f.StatePath, JsonSerializer.Serialize(new[] { new TrackerOpportunity {
            Artist = "Artist", Album = "Crisps", Acquisition = "complete", ReferenceIds = ["ext-deezer-42"]
        } }));
        f.Restart(); await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        var legacy = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("legacy", legacy.IdentityStatus); Assert.Empty(f.HashTrackers);

        await f.Service.RecheckAsync(legacy.Key); await f.Service.RefreshAsync();
        var recovered = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("resolved", recovered.IdentityStatus); Assert.True(recovered.ManagedIdentityVerified);
        Assert.Equal("complete", recovered.Acquisition); Assert.Equal("candidate", recovered.Ops.Status);
        Assert.False(recovered.RecheckRequested); Assert.Null(recovered.Manifest); Assert.Empty(f.ProbeIds);
        Assert.Equal(["red"], f.HashTrackers);
        Assert.NotNull(Assert.Single(recovered.Assessments).CompletedUtc);

        f.Restart(); f.Clock.Advance(TimeSpan.FromHours(52)); await f.Service.RefreshAsync();
        var retained = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("resolved", retained.IdentityStatus); Assert.Equal("candidate", retained.Ops.Status);
        Assert.False(retained.RecheckRequested); Assert.Single(retained.Assessments); Assert.Single(f.HashTrackers);
    }

    [Fact]
    public async Task ManagedIdentitySurvivesSuccessfulThenFailedProviderRecheck()
    {
        using var f = new Fixture { Acquired = true };
        await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        var first = Assert.Single(await f.Service.ListAsync());
        await f.Service.RecheckAsync(first.Key); await f.Service.RefreshAsync();
        var qualified = Assert.Single(await f.Service.ListAsync());
        Assert.NotNull(qualified.Manifest); Assert.True(qualified.ManagedIdentityVerified);
        f.Restart(); f.DeclaredCount = 3;
        await f.Service.RecheckAsync(first.Key); await f.Service.RefreshAsync();
        var after = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("resolved", after.IdentityStatus); Assert.True(after.ManagedIdentityVerified);
        Assert.Equal(qualified.IdentityVersion, after.IdentityVersion);
        Assert.Equal("candidate", after.Ops.Status); Assert.False(after.RecheckRequested);
        Assert.Equal(3, f.HashTrackers.Count);
        Assert.Null(await f.Service.GetManifestAsync("5", qualified.Manifest!.Revision));
    }

    [Fact]
    public async Task UnresolvedIdentityMasksCandidatesAndManagedPromotionClearsStaleManifest()
    {
        using var f = new Fixture { Acquired = true };
        var manifest = new DeezerAlbumManifest("5", "old", "album", 1, "Artist", "Crisps", true,
            [new("42", 1, 1, "Get Back Jamie", "Artist", "GBAAA2400001", 180)]);
        await File.WriteAllTextAsync(f.StatePath, JsonSerializer.Serialize(new[] { new TrackerOpportunity {
            SchemaVersion = 2, Artist = "Artist", Album = "Crisps", IdentityVersion = 7,
            IdentityStatus = "unresolved", Manifest = manifest, DeezerAvailability = "available",
            DeezerProofExpiresUtc = f.Clock.GetUtcNow().UtcDateTime.AddHours(12),
            Ops = new() { Status = "candidate", IdentityVersion = 7, SourceManifestRevision = "old" }
        } }));
        f.Restart(); Assert.Equal("unknown", Assert.Single(await f.Service.ListAsync()).Ops.Status);
        await f.Service.RefreshAsync();
        var promoted = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("resolved", promoted.IdentityStatus); Assert.True(promoted.ManagedIdentityVerified);
        Assert.Equal(8, promoted.IdentityVersion); Assert.Null(promoted.Manifest);
        Assert.Null(promoted.DeezerProofExpiresUtc);
    }

    [Fact]
    public async Task LegacyAssociationRetriesFailedParentResolutionWithoutManagedPromotion()
    {
        using var f = new Fixture { Acquired = true, ManagedTitle = "We Make Hits", ManagedType = "EP", DeclaredCount = 3 };
        await File.WriteAllTextAsync(f.StatePath, JsonSerializer.Serialize(new[] { new TrackerOpportunity {
            Artist = "Artist", Album = "We Make Hits", Acquisition = "complete", ReferenceIds = ["ext-deezer-42"]
        } }));
        f.Restart(); await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        Assert.Equal("legacy", Assert.Single(await f.Service.ListAsync()).IdentityStatus);
        f.DeclaredCount = 2; f.Clock.Advance(TimeSpan.FromMinutes(20));
        f.ExpireMetadataCache(); await f.Service.RefreshAsync();
        var rows = await f.Service.ListAsync();
        Assert.Equal("unresolved", rows.Single(r => r.Album == "Crisps").Acquisition);
        Assert.True(rows.Single(r => r.Album == "Crisps").Saved);
        Assert.Equal("complete", rows.Single(r => r.Album == "We Make Hits").Acquisition);
    }

    [Fact]
    public async Task UnknownIndexerDoesNotConsumeAllowanceAndNewMappingGetsAutomaticPass()
    {
        using var f = new Fixture { Acquired = true, Indexer = "Historical RED" };
        await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync();
        Assert.Empty(Assert.Single(await f.Service.ListAsync()).Assessments); Assert.Empty(f.HashTrackers);
        f.MapRedIndexer("Historical RED"); f.Clock.Advance(TimeSpan.FromMinutes(16)); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.Equal("candidate", row.Ops.Status); Assert.Equal("ops", Assert.Single(row.Assessments).Destination);
        Assert.Equal(["red"], f.HashTrackers);
    }

    [Fact]
    public async Task UnknownIndexerCannotOverwriteQualifiedDeezerCandidate()
    {
        using var f = new Fixture(); await f.SaveAsync(); await f.Service.RefreshAsync();
        var row = Assert.Single(await f.Service.ListAsync()); Assert.Equal("candidate", row.Ops.Status);
        row.Sources = [new() { Hash = new string('1', 40), Indexer = "Unknown", Complete = true,
            Error = "Source indexer unknown", ObservedUtc = f.Clock.GetUtcNow().UtcDateTime }];
        await File.WriteAllTextAsync(f.StatePath, JsonSerializer.Serialize(new[] { row }));
        f.Restart(); await f.Service.RefreshAsync();
        var after = Assert.Single(await f.Service.ListAsync());
        Assert.Empty(after.Assessments); Assert.Equal("candidate", after.Ops.Status); Assert.Empty(f.HashTrackers);
    }

    [Fact]
    public async Task RecheckCompletesAllPreviouslyAssessedSourceDestinationPairs()
    {
        using var f = new Fixture { Acquired = true, SecondSource = true };
        await f.SaveAsync(album: "Crisps"); await f.Service.RefreshAsync(); await f.Service.RefreshAsync();
        var prior = Assert.Single(await f.Service.ListAsync()); Assert.Equal(2, prior.Assessments.Count);
        await f.Service.RecheckAsync(prior.Key); await f.Service.RefreshAsync();
        var partial = Assert.Single(await f.Service.ListAsync()); Assert.True(partial.RecheckRequested);
        Assert.Single(partial.Assessments, a => a.RecheckVersion == partial.RecheckVersion);
        await f.Service.RefreshAsync();
        var done = Assert.Single(await f.Service.ListAsync()); Assert.False(done.RecheckRequested);
        Assert.Equal(2, done.Assessments.Count(a => a.RecheckVersion == done.RecheckVersion && a.CompletedUtc is not null));
        Assert.Equal(["red", "ops", "red", "ops"], f.HashTrackers);
        var requests = f.TrackerRequests; f.Restart(); await f.Service.RefreshAsync(); Assert.Equal(requests, f.TrackerRequests);
    }

    [Fact]
    public async Task RecheckDuringFinalBrowseCannotStoreFindingOrCompleteNewPass()
    {
        using var f = new Fixture();
        await f.SaveAsync(); var injected = false;
        f.OnBrowse = (tracker, query) =>
        {
            if (injected || tracker != "ops" || query != "Crisps") return;
            injected = true;
            f.Service.RecheckAsync(Assert.Single(f.Service.ListAsync().GetAwaiter().GetResult()).Key).GetAwaiter().GetResult();
        };
        await f.Service.RefreshAsync(); f.OnBrowse = null;
        var row = Assert.Single(await f.Service.ListAsync());
        Assert.True(injected); Assert.True(row.RecheckRequested); Assert.Null(row.Ops.CheckedUtc);
        Assert.Null(row.DeezerProofExpiresUtc);
        var probes = f.Heads; await f.Service.RefreshAsync();
        Assert.True(f.Heads > probes); Assert.False(Assert.Single(await f.Service.ListAsync()).RecheckRequested);
    }

    private sealed class Fixture : HttpMessageHandler, IHttpClientFactory
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-opportunities-" + Guid.NewGuid().ToString("N"));
        private readonly IConfiguration _config;
        private readonly LidarrClient _lidarr;
        private readonly DeezerMetadataService _metadata;
        private readonly DeezerResolver _resolver;
        private readonly SalmonMediaHandoff _handoff;
        public string StatePath => Path.Combine(_root,"opportunities.json");
        public string AudioPath => Path.Combine(_root,"source","Crisps","01.flac");
        public TrackerDiscoveryTests.AdvancingClock Clock { get; } = new();
        public ExternalSaveStore Saves { get; }
        public TrackerOpportunityService Service { get; private set; } = null!;
        public TrackerDirectQueue Queue { get; private set; } = null!;
        public bool Acquired { get; init; }
        public bool SecondSource { get; init; }
        public string ManagedTitle { get; init; } = "Crisps";
        public string ManagedType { get; init; } = "Album";
        public string Indexer { get; init; } = "Redacted (Prowlarr)";
        public bool SourceLookupFails { get; init; }
        public int DeclaredCount { get; set; } = 2;
        public bool SubstituteTrack { get; set; }
        public bool CancelDestination { get; set; }
        public Action? OnMedia { get; set; }
        public Action<string, string>? OnBrowse { get; set; }
        public int Heads { get; set; }
        public CancellationTokenSource Cancel { get; } = new();
        public int TrackerRequests { get; set; }
        public int TrackDetailRequests { get; private set; }
        public List<string> Searches { get; }=[];
        public List<string> ProbeIds { get; }=[];
        public List<string> HashTrackers { get; }=[];
        public List<string> MediaRequests { get; }=[];
        private const string Hash = "1111111111111111111111111111111111111111";
        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AudioPath)!); File.WriteAllBytes(AudioPath,[1]);
            _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Trackers:red:ApiKey"]="fake", ["Trackers:ops:ApiKey"]="fake", ["Deezer:Arl"]="fake",
                ["Trackers:red:LidarrIndexerNames:0"]="Redacted (Prowlarr)", ["Trackers:ops:LidarrIndexerNames:0"]="Orpheus (Prowlarr)",
                ["Salmon:Root"]=_root, ["Salmon:QbittorrentUrl"]="http://qbt.invalid"
            }).Build();
            var lidarr = TestOptions.Monitor(new LidarrSettings { BaseUrl="http://lidarr.invalid", ApiKey="fake" });
            _lidarr = new(this,lidarr); _metadata = new(this,TestOptions.Monitor(new MetadataSettings()),NullLogger<DeezerMetadataService>.Instance);
            _resolver = new(this,_config,NullLogger<DeezerResolver>.Instance,_metadata); _handoff = new(this,_config,lidarr);
            Saves=new(Path.Combine(_root,"saves.json"),NullLogger<ExternalSaveStore>.Instance); Restart();
        }
        // Metadata cache uses wall-clock time; emulate its expiry alongside the service clock.
        public void ExpireMetadataCache() => _metadata.ClearCaches();
        public void MapRedIndexer(string indexer) => _config["Trackers:red:LidarrIndexerNames:1"] = indexer;
        public Song Song(string id="42", string album="Get Back Jamie") => new() { Id="ext-deezer-"+id, ExternalProvider="deezer", ExternalId=id,
            Artist="Artist", Title="Get Back Jamie", Album=album, Duration=180, Isrc="GBAAA2400001" };
        public Task SaveAsync(string id="42", string album="Get Back Jamie") => Saves.SetHeartAsync("user",Song(id,album),true);
        public void Restart() { Service?.Dispose(); Queue=new(Path.Combine(_root,"cooldowns.json"),_config,this,Clock);
            Service=new(Saves,_lidarr,Queue,StatePath,NullLogger<TrackerOpportunityService>.Instance,Clock,_metadata,_resolver,_handoff,_config); }
        public HttpClient CreateClient(string name) => new(this,false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var uri=request.RequestUri!; var query=System.Web.HttpUtility.ParseQueryString(uri.Query);
            if(uri.Host=="lidarr.invalid") return Json(uri.AbsolutePath switch {
                "/api/v1/album" => Acquired ? new[] {new {id=7,foreignAlbumId="group",title=ManagedTitle,albumType=ManagedType,artist=new{artistName="Artist"}}} : (object)Array.Empty<object>(),
                "/api/v1/album/7" => new {statistics=new{trackCount=1,trackFileCount=1}},
                "/api/v1/track" => new[]{new{id=1,title="Get Back Jamie",trackNumber="1",trackFileId=1,hasFile=true}},
                "/api/v1/trackFile" => new[]{new{id=1,path=AudioPath,size=1}},
                "/api/v1/history" => new {totalRecords=SecondSource ? 4 : 2,records=SourceHistory()},
                _=>Array.Empty<object>() });
            if(uri.Host=="qbt.invalid") return Json(uri.AbsolutePath.EndsWith("/info") ? new[] {new {hash=query["hashes"] ?? Hash,name="Crisps",save_path=Path.Combine(_root,"source"),content_path=Path.Combine(_root,"source","Crisps"),progress=1.0,state="uploading"}} : (object)new[] {new{name="Crisps/01.flac",size=1,progress=1.0,priority=0}});
            if(uri.Host=="api.deezer.com")
            {
                if(uri.AbsolutePath.StartsWith("/track/")) {TrackDetailRequests++; return Json(new{id=long.Parse(uri.Segments.Last()),title="Get Back Jamie",duration=180,isrc="GBAAA2400001",artist=new{name="Artist"},album=new{id=5,title="Crisps"}});}
                if(uri.AbsolutePath.EndsWith("/tracks")) return Json(new {total=2,data=new[]{
                    new{id=42,title="Get Back Jamie",duration=180,track_position=1,disk_number=1,isrc="GBAAA2400001",artist=new{name="Artist"}},
                    new{id=43,title="Closing",duration=181,track_position=2,disk_number=1,isrc="GBAAA2400002",artist=new{name="Artist"}} }});
                return Json(new{id=5,title="Crisps",record_type="album",nb_tracks=DeclaredCount,artist=new{name="Artist"}});
            }
            if(uri.Host=="www.deezer.com")
            {
                if(query["method"]=="deezer.getUserData") return Json(new{results=new{checkForm="fake",USER=new{USER_ID=1,OPTIONS=new{license_token="fake"}}}});
                using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var id=body.RootElement.GetProperty("SNG_ID").GetString()!;
                ProbeIds.Add(id); OnMedia?.Invoke(); return Json(new{results=new{DATA=new{SNG_ID=SubstituteTrack?"49":id,TRACK_TOKEN="fake",SNG_TITLE=id=="42"?"Get Back Jamie":"Closing",ART_NAME="Artist"}}});
            }
            if(uri.Host=="media.deezer.com") {OnMedia?.Invoke();MediaRequests.Add(await request.Content!.ReadAsStringAsync(ct));return Json(new{data=new[]{new{media=new[]{new{format="FLAC",sources=new[]{new{url="https://cdn.invalid/audio"}}}}}}});}
            if(uri.Host=="cdn.invalid") { Heads++; OnMedia?.Invoke(); } if(uri.Host=="cdn.invalid") return new(HttpStatusCode.OK){Content=new ByteArrayContent([1])};
            TrackerRequests++; var tracker=uri.Host=="redacted.sh"?"red":"ops";
            if(query["action"]=="torrent") {HashTrackers.Add(tracker);return Json(SourceLookupFails ? new{status="failure",response=(object)new{}} : new{status="success",response=(object)new{group=new{id=41,name="Crisps",categoryId=1,releaseType=1,musicInfo=new{artists=new[]{new{name="Artist"}}}},torrent=new{id=7,hash=query["hash"] ?? Hash,media="WEB",format="FLAC"}}});}
            OnBrowse?.Invoke(tracker, query["searchstr"] ?? "");
            Searches.Add(query["searchstr"]??"");
            if(CancelDestination && tracker=="ops") {Cancel.Cancel();ct.ThrowIfCancellationRequested();}
            return Json(new{status="success",response=new{results=Array.Empty<object>()}});
        }
        private object[] SourceHistory()
        {
            var records = new List<object> { new {albumId=7,eventType="downloadImported",downloadId=Hash},
                new {albumId=7,eventType="grabbed",downloadId=Hash,data=new{indexer=Indexer}} };
            if (SecondSource)
            {
                const string second = "2222222222222222222222222222222222222222";
                records.Add(new {albumId=7,eventType="downloadImported",downloadId=second});
                records.Add(new {albumId=7,eventType="grabbed",downloadId=second,data=new{indexer="Orpheus (Prowlarr)"}});
            }
            return records.ToArray();
        }
        private static HttpResponseMessage Json(object body)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(body))};
        protected override void Dispose(bool disposing) {Service?.Dispose();_resolver.Dispose();_metadata.Dispose();Cancel.Dispose();if(Directory.Exists(_root))Directory.Delete(_root,true);base.Dispose(disposing);}
    }
}
