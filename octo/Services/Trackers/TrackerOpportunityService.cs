using System.Security.Cryptography;
using System.Text.Json;
using Octo.Models.Domain;
using Octo.Services.Common;
using Octo.Services.Deezer;
using Octo.Services.Lidarr;
using Octo.Services.Metadata;
using Octo.Services.Subsonic;

namespace Octo.Services.Trackers;

public sealed class TrackerSavedReference
{
    public string Id { get; set; } = "";
    public string MetadataRevision { get; set; } = "";
    public Song Song { get; set; } = new();
}
public sealed class TrackerLocalSource
{
    public string Hash { get; set; } = "";
    public string? Indexer { get; set; }
    public string? Tracker { get; set; }
    public bool Complete { get; set; }
    public DateTime ObservedUtc { get; set; }
    public string? Error { get; set; }
}
public sealed class TrackerCrossUploadAssessment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceHash { get; set; } = "";
    public string? SourceTracker { get; set; }
    public string Destination { get; set; } = "";
    public long IdentityVersion { get; set; }
    public long RecheckVersion { get; set; }
    public string Progress { get; set; } = "pending";
    public string? Medium { get; set; }
    public DateTime? SourceVerifiedUtc { get; set; }
    public TrackerSourceLookup? SourceLookup { get; set; }
    public TrackerFinding? Result { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
public sealed class TrackerOpportunity
{
    public int SchemaVersion { get; set; }
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string? AlbumType { get; set; }
    public string? ForeignAlbumId { get; set; }
    public int? LidarrAlbumId { get; set; }
    public List<string> SourceLinks { get; set; } = [];
    public List<string> ReferenceIds { get; set; } = [];
    public List<TrackerSavedReference> References { get; set; } = [];
    public List<string> Aliases { get; set; } = [];
    public List<ReleaseName> Names { get; set; } = [];
    public bool Saved { get; set; }
    public string IdentityStatus { get; set; } = "unresolved";
    public long IdentityVersion { get; set; } = 1;
    public bool IdentityChanged { get; set; }
    public bool ManagedIdentityVerified { get; set; }
    public string? IdentityDiagnostic { get; set; }
    public bool ParentSearchComplete { get; set; }
    public int ParentsInspected { get; set; }
    public DeezerAlbumManifest? Manifest { get; set; }
    public string DeezerAvailability { get; set; } = "unknown";
    public DateTime? DeezerCheckedUtc { get; set; }
    public DateTime? DeezerProofExpiresUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public int Failures { get; set; }
    public string Acquisition { get; set; } = "unresolved";
    public string? ReconciliationError { get; set; }
    public long RecheckVersion { get; set; }
    public long CheckedVersion { get; set; }
    public bool RecheckRequested => RecheckVersion > CheckedVersion;
    public List<TrackerLocalSource> Sources { get; set; } = [];
    public List<TrackerCrossUploadAssessment> Assessments { get; set; } = [];
    public DateTime? InventoryCheckedUtc { get; set; }
    public TrackerFinding Red { get; set; } = new();
    public TrackerFinding Ops { get; set; } = new();
}

/// <summary>Identity, source qualification, acquisition closure, and assessments have separate lifetimes.</summary>
public sealed class TrackerOpportunityService : BackgroundService
{
    private readonly ExternalSaveStore _saves;
    private readonly LidarrClient _lidarr;
    private readonly TrackerCatalogClient _catalog;
    private readonly ILogger<TrackerOpportunityService> _logger;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly DeezerMetadataService? _deezer;
    private readonly DeezerResolver? _resolver;
    private readonly SalmonMediaHandoff? _handoff;
    private readonly IConfiguration _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private List<TrackerOpportunity> _rows;
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public TrackerOpportunityService(ExternalSaveStore saves, LidarrClient lidarr, TrackerDirectQueue queue,
        string path, ILogger<TrackerOpportunityService> logger, TimeProvider? time = null, DeezerMetadataService? deezer = null,
        DeezerResolver? resolver = null, SalmonMediaHandoff? handoff = null, IConfiguration? config = null)
    {
        _saves = saves; _lidarr = lidarr; _path = path; _logger = logger; _time = time ?? TimeProvider.System;
        _deezer = deezer; _resolver = resolver; _handoff = handoff; _config = config ?? new ConfigurationBuilder().Build();
        _catalog = new TrackerCatalogClient(queue, _time);
        _rows = File.Exists(path) ? JsonSerializer.Deserialize<List<TrackerOpportunity>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Tracker opportunity state is invalid.") : [];
        foreach (var row in _rows.Where(r => r.SchemaVersion < 2))
        {
            // Old aliases may be singles. Preserve closure on the old album; never treat those names as destination evidence.
            row.Red = new(); row.Ops = new(); row.Names = [new(row.Artist, row.Album)];
            row.IdentityStatus = "legacy"; row.IdentityDiagnostic = "Legacy album association needs reconciliation";
            row.SchemaVersion = 2;
        }
    }

    public async Task<IReadOnlyList<TrackerOpportunity>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var rows = Clone(_rows);
            foreach (var row in rows)
            {
                if (row.Manifest is not null && row.DeezerProofExpiresUtc <= Now) row.DeezerAvailability = "expired-proof";
                foreach (var finding in new[] { row.Red, row.Ops })
                    if (finding.Status == "candidate" && (row.IdentityStatus != "resolved" || row.ReconciliationError is not null
                        || row.IdentityChanged || finding.IdentityVersion != row.IdentityVersion
                        || finding.SourceManifestRevision is not null && (finding.SourceManifestRevision != row.Manifest?.Revision || !(row.DeezerProofExpiresUtc > Now))
                        || finding.SourceHash is not null && !row.Sources.Any(s => s.Hash == finding.SourceHash && SourceEligible(s) && s.ObservedUtc >= Now.AddMinutes(-15))))
                    { finding.Status = "unknown"; finding.Error = row.IdentityChanged ? "identity changed — Recheck"
                        : row.IdentityStatus != "resolved" ? "Release identity unresolved — Recheck"
                        : row.ReconciliationError ?? "Source proof expired — Recheck"; }
            }
            return rows.OrderBy(r => r.Artist).ThenBy(r => r.Album).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<DeezerAlbumManifest?> GetManifestAsync(string albumId, string revision, CancellationToken ct = default) =>
        (await ListAsync(ct)).Where(r => r.IdentityStatus == "resolved" && !r.IdentityChanged
            && r.Manifest?.AlbumId == albumId && r.Manifest.Revision == revision && r.Manifest.Complete
            && r.DeezerAvailability == "available" && r.DeezerProofExpiresUtc > Now).Select(r => r.Manifest).FirstOrDefault();

    public async Task<bool> RecheckAsync(string key, CancellationToken ct = default)
    {
        var found = false;
        await MutateAsync(rows =>
        {
            if (Find(rows, key) is not { } row) return;
            row.RecheckVersion++; row.NextAttemptUtc = null; row.Failures = 0;
            InvalidatePending(row, "Superseded by explicit Recheck");
            row.DeezerProofExpiresUtc = null; row.DeezerAvailability = "unknown";
            row.InventoryCheckedUtc = null;
            // Completed assessments stay as history. A new explicit pass carries its own persisted intent.
            found = true;
        }, ct);
        return found;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RefreshAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning("Tracker discovery deferred: {Reason}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), _time, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            await ReconcileReferencesAsync(ct);
            IReadOnlyList<LidarrAlbumCandidate> managed;
            try { managed = await _lidarr.GetManagedAlbumsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { await MutateAsync(rows => rows.ForEach(r => r.ReconciliationError = "Lidarr reconciliation unavailable"), ct); return; }
            foreach (var row in await ListAsync(ct)) await ReconcileAcquisitionAsync(row.Key, managed, ct);
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in (await ListAsync(ct)).Where(r => r.Saved && !r.IdentityChanged || r.RecheckRequested)
                .Where(r => r.IdentityStatus != "resolved" || r.RecheckRequested)
                .Where(r => r.Acquisition == "unresolved" || r.IdentityStatus == "legacy" || r.RecheckRequested)
                .Where(r => r.NextAttemptUtc is null || r.NextAttemptUtc <= Now))
            {
                if (attempted.Count >= 10 || _deezer is null) break;
                var reference = row.References.FirstOrDefault();
                if (reference is null || !attempted.Add(reference.Id)) continue;
                var answer = await _deezer.ResolveDiscoveryAlbumAsync(reference.Song, ct, refresh: row.RecheckRequested);
                await ApplyIdentityAsync(row.Key, row.IdentityVersion, row.RecheckVersion, answer, ct);
            }
            foreach (var row in (await ListAsync(ct)).Where(r => r.Saved || r.RecheckRequested || r.Assessments.Any(a => a.CompletedUtc is null)))
                if (row.InventoryCheckedUtc is null || row.InventoryCheckedUtc <= Now.AddMinutes(-15))
                    await RefreshSourcesAsync(row.Key, ct);
            await AssessOneSourceAsync(ct);
            var probed = false;
            foreach (var row in (await ListAsync(ct)).Where(OrdinaryDue).OrderByDescending(r => r.RecheckRequested))
            {
                if (!await EligibleAtDispatchAsync(row.Key, row.IdentityVersion, row.RecheckVersion, null, false, ct)) continue;
                if (row.Manifest is null) continue;
                if (row.DeezerProofExpiresUtc is null || row.DeezerProofExpiresUtc <= Now)
                {
                    if (probed || _resolver is null || row.NextAttemptUtc > Now) continue;
                    probed = true;
                    var success = true;
                    foreach (var track in row.Manifest.Tracks)
                    {
                        var source = await _resolver.ProbeSourceAsync(track.TrackId, "FLAC", ct, requireExactTrack: true);
                        if (source?.Media?.TrackId != track.TrackId || source.Media.Format != "FLAC") { success = false; break; }
                    }
                    await MutateAsync(rows =>
                    {
                        if (Find(rows, row.Key) is not { } current || current.IdentityVersion != row.IdentityVersion
                            || current.RecheckVersion != row.RecheckVersion || current.IdentityStatus != "resolved" || current.IdentityChanged) return;
                        current.DeezerCheckedUtc = Now; current.DeezerAvailability = success ? "available" : "unavailable";
                        current.DeezerProofExpiresUtc = success ? Now.AddHours(12) : null;
                        if (success) { current.Failures = 0; current.NextAttemptUtc = null; } else Backoff(current);
                    }, ct);
                    if (!success) continue;
                }
                var complete = true;
                foreach (var target in new[] { "red", "ops" })
                {
                    var result = await _catalog.SearchAsync(target, row.Names, "WEB", row.IdentityVersion, row.AlbumType ?? "unknown", ct,
                        token => EligibleAtDispatchAsync(row.Key, row.IdentityVersion, row.RecheckVersion, null, true, token));
                    if (result is null) { complete = false; break; }
                    result.SourceManifestRevision = row.Manifest.Revision;
                    await StoreFindingAsync(row.Key, row.IdentityVersion, row.RecheckVersion, target, result, ct);
                }
                if (complete) await MutateAsync(rows =>
                {
                    if (Find(rows, row.Key) is { } r && r.IdentityVersion == row.IdentityVersion
                        && r.RecheckVersion == row.RecheckVersion && r.IdentityStatus == "resolved" && !r.IdentityChanged
                        && !HasOutstandingAssessments(rows, r)) r.CheckedVersion = row.RecheckVersion;
                }, ct);
            }
        }
        finally { _refresh.Release(); }
    }

    private async Task ReconcileReferencesAsync(CancellationToken ct)
    {
        var snapshot = _saves.Snapshot();
        var songs = snapshot.Hearts.Select(h => h.Song).Concat(snapshot.Playlists.SelectMany(p => p.Tracks)
            .Where(t => t.Song is not null).Select(t => t.Song!)).DistinctBy(ReferenceKey).ToList();
        await MutateAsync(rows =>
        {
            rows.ForEach(r => r.Saved = false);
            foreach (var song in songs)
            {
                var id = ReferenceKey(song);
                var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { song.Id };
                bool changed;
                do
                {
                    changed = false;
                    foreach (var alias in snapshot.Aliases)
                        if (references.Contains(alias.AliasId) || references.Contains(alias.CanonicalId))
                            changed |= references.Add(alias.AliasId) | references.Add(alias.CanonicalId);
                } while (changed);
                var row = rows.FirstOrDefault(r => r.References.Any(s => s.Id == id) || r.ReferenceIds.Intersect(references).Any());
                if (row is null)
                {
                    row = new() { SchemaVersion = 2, Artist = song.AlbumArtist ?? song.Artist, Album = song.Album };
                    rows.Add(row);
                }
                var revision = ReferenceRevision(song);
                var existing = row.References.FirstOrDefault(s => s.Id == id || references.Contains(s.Song.Id));
                if (existing is not null && (existing.Id != id || existing.MetadataRevision != revision) && row.IdentityStatus == "resolved")
                {
                    row.IdentityChanged = true; row.IdentityVersion++; row.CheckedVersion = row.RecheckVersion;
                    row.IdentityDiagnostic = "identity changed — Recheck";
                    InvalidatePending(row, row.IdentityDiagnostic);
                }
                row.References.RemoveAll(s => s.Id == id || references.Contains(s.Song.Id));
                row.References.Add(new() { Id = id, MetadataRevision = revision, Song = song });
                row.ReferenceIds = row.ReferenceIds.Concat(references).Distinct().ToList(); row.Saved = true;
                if (row.Manifest is null && row.IdentityStatus is "unresolved" or "legacy")
                {
                    var intent = snapshot.Acquisitions.FirstOrDefault(i => references.Contains(i.Song.Id));
                    row.ForeignAlbumId ??= song.MusicBrainzReleaseGroupId ?? intent?.AlbumForeignId;
                    row.LidarrAlbumId ??= intent?.LidarrAlbumId;
                }
            }
        }, ct);
    }

    private async Task ApplyIdentityAsync(string key, long version, long recheck, DiscoveryAlbumResult answer, CancellationToken ct)
    {
        await MutateAsync(rows =>
        {
            if (Find(rows, key) is not { } row || row.IdentityVersion != version || row.RecheckVersion != recheck) return;
            row.ParentSearchComplete = answer.SearchComplete; row.ParentsInspected = answer.CandidatesInspected;
            row.IdentityDiagnostic = answer.Diagnostic;
            if (answer.Status != DiscoveryResolution.Resolved || answer.Manifest is not { } manifest)
            {
                // Failed provider resolution cannot invalidate independent, verified Lidarr album evidence.
                if (row.IdentityStatus != "resolved" || !row.ManagedIdentityVerified)
                {
                    if (row.IdentityStatus == "resolved" || row.Manifest is not null)
                    { row.IdentityVersion++; InvalidatePending(row, "Release identity unresolved — Recheck"); }
                    if (row.IdentityStatus != "legacy")
                        row.IdentityStatus = answer.Status == DiscoveryResolution.Ambiguous ? "ambiguous" : "unresolved";
                    row.Manifest = null; row.ManagedIdentityVerified = false;
                    row.DeezerAvailability = "unknown"; row.DeezerProofExpiresUtc = null;
                }
                Backoff(row); return;
            }
            var same = NameKey(row.Artist, row.Album) == NameKey(manifest.Artist, manifest.Title);
            if (!same && row.Acquisition != "unresolved")
            {
                // A single's acquisition closure cannot close its newly discovered parent album.
                var parent = new TrackerOpportunity { SchemaVersion = 2, References = row.References,
                    ReferenceIds = row.ReferenceIds, Saved = row.Saved, RecheckVersion = row.RecheckVersion,
                    CheckedVersion = row.CheckedVersion, IdentityVersion = row.IdentityVersion + 1 };
                row.References = []; row.ReferenceIds = []; row.Saved = false;
                row.ReconciliationError = "Historical acquisition retained on previous album";
                rows.Add(parent); row = parent;
            }
            if (row.Manifest?.Revision != manifest.Revision)
            { row.IdentityVersion++; InvalidatePending(row, "identity changed — Recheck"); }
            if (!same || row.AlbumType != manifest.AlbumType) row.ManagedIdentityVerified = false;
            row.Artist = manifest.Artist; row.Album = manifest.Title; row.AlbumType = manifest.AlbumType;
            row.Manifest = manifest; row.IdentityStatus = "resolved"; row.IdentityChanged = false;
            row.Names = [new(manifest.Artist, manifest.Title)]; row.Failures = 0; row.NextAttemptUtc = null;
            if (!same) { row.ForeignAlbumId = null; row.LidarrAlbumId = null; }
            row.SourceLinks = ["https://www.deezer.com/album/" + manifest.AlbumId];
            var duplicate = rows.FirstOrDefault(r => r.Key != row.Key && r.IdentityStatus == "resolved"
                && NameKey(r.Artist, r.Album) == NameKey(row.Artist, row.Album) && r.Manifest is not null
                && DeezerMetadataService.SameRecordingManifest(r.Manifest, manifest));
            if (duplicate is null) return;
            duplicate.References = duplicate.References.Concat(row.References).DistinctBy(r => r.Id).ToList();
            duplicate.ReferenceIds = duplicate.ReferenceIds.Concat(row.ReferenceIds).Distinct().ToList();
            duplicate.Aliases = duplicate.Aliases.Concat(row.Aliases).Append("key:" + row.Key).Distinct().ToList();
            duplicate.Saved |= row.Saved;
            duplicate.Assessments.AddRange(row.Assessments);
            if (row.RecheckRequested) duplicate.RecheckVersion = duplicate.CheckedVersion + 1;
            rows.Remove(row);
        }, ct);
    }

    private async Task<bool> ReconcileAcquisitionAsync(string key, IReadOnlyList<LidarrAlbumCandidate> managed, CancellationToken ct)
    {
        var row = Find(await ListAsync(ct), key); if (row is null) return false;
        try
        {
            var candidates = managed.Where(a => !string.IsNullOrWhiteSpace(row.ForeignAlbumId)
                ? Same(a.ForeignAlbumId, row.ForeignAlbumId) : NameKey(a.Artist, a.Title) == NameKey(row.Artist, row.Album)).ToList();
            if (candidates.Count > 1) throw new InvalidDataException();
            var album = candidates.SingleOrDefault();
            var evidence = album is not null && (row.Acquisition != "complete" || row.RecheckRequested || !row.ManagedIdentityVerified)
                ? await _lidarr.GetAcquisitionEvidenceAsync(album.Id, ct) : new LidarrAcquisitionEvidence(false, false, false);
            await MutateAsync(rows =>
            {
                if (Find(rows, key) is not { } current || current.IdentityVersion != row.IdentityVersion
                    || current.RecheckVersion != row.RecheckVersion) return;
                current.ReconciliationError = null;
                var previouslyVerified = current.ManagedIdentityVerified && current.LidarrAlbumId == album?.Id;
                current.ManagedIdentityVerified = false;
                if (album is not null)
                {
                    current.ForeignAlbumId = album.ForeignAlbumId; current.LidarrAlbumId = album.Id;
                    if (current.Manifest is null && current.IdentityStatus != "resolved" && (evidence.Complete || evidence.Grabbed))
                    {
                        // Acquisition belongs to its verified managed album, not a saved song's newer display album.
                        current.Artist = album.Artist; current.Album = album.Title;
                        current.Names = [new(album.Artist, album.Title)];
                    }
                    // Managed identity is useful for already acquired local sources, without Deezer resolution at dispatch.
                    // Legacy associations require explicit Recheck before this promotion.
                    if ((current.IdentityStatus == "unresolved" || current.IdentityStatus == "legacy" && current.RecheckRequested)
                        && (evidence.Complete || evidence.Grabbed)
                        && NameKey(row.Artist, row.Album) == NameKey(album.Artist, album.Title))
                    {
                        var type = album.Resource["albumType"]?.ToString().ToLowerInvariant();
                        if (type is "album" or "ep")
                        {
                            current.IdentityVersion++; InvalidatePending(current, "Managed album identity replaced provider identity");
                            current.Manifest = null; current.DeezerAvailability = "unknown"; current.DeezerProofExpiresUtc = null;
                            current.Artist = album.Artist; current.Album = album.Title; current.AlbumType = type;
                            current.Names = [new(album.Artist, album.Title)]; current.IdentityStatus = "resolved";
                            current.IdentityDiagnostic = "Verified managed album association";
                        }
                    }
                }
                if (album is not null && current.IdentityStatus == "resolved"
                    && NameKey(current.Artist, current.Album) == NameKey(album.Artist, album.Title)
                    && current.AlbumType == album.Resource["albumType"]?.ToString().ToLowerInvariant())
                    current.ManagedIdentityVerified = evidence.Complete || evidence.Grabbed || previouslyVerified;
                if (evidence.Complete) current.Acquisition = "complete";
                else if (evidence.Grabbed) current.Acquisition = evidence.Failed ? "failed" : "grabbed";
            }, ct); return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { await MutateAsync(rows => { if (Find(rows, key) is { } r && r.IdentityVersion == row.IdentityVersion
            && r.RecheckVersion == row.RecheckVersion) r.ReconciliationError = "Album acquisition needs reconciliation"; }, ct); return false; }
    }

    public async Task RefreshSourcesAsync(string key, CancellationToken ct = default)
    {
        var row = Find(await ListAsync(ct), key); if (row is null || row.LidarrAlbumId is not int id || _handoff is null) return;
        try
        {
            var observations = new List<TrackerLocalSource>();
            foreach (var evidence in await _lidarr.GetAlbumSourcesAsync(id, ct))
            {
                var local = await _handoff.InspectLocalSourceAsync(evidence.Hash, ct);
                var tracker = TrackerSourceMapping.Resolve(_config, evidence.Indexer);
                observations.Add(new() { Hash = evidence.Hash, Indexer = evidence.Indexer, Tracker = tracker,
                    Complete = evidence.CompleteRelease && local.Complete, ObservedUtc = Now,
                    Error = !evidence.CompleteRelease ? "Complete release not verified" : local.Error ?? (tracker is null ? "Source indexer unknown" : null) });
            }
            await MutateAsync(rows => { if (Find(rows, key) is { } r && r.IdentityVersion == row.IdentityVersion
                && r.RecheckVersion == row.RecheckVersion && r.LidarrAlbumId == id) { r.Sources = observations; r.InventoryCheckedUtc = Now; } }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { await MutateAsync(rows => { if (Find(rows, key) is { } r && r.IdentityVersion == row.IdentityVersion
            && r.RecheckVersion == row.RecheckVersion && r.LidarrAlbumId == id) { r.InventoryCheckedUtc = Now; r.Sources.ForEach(s => { s.Complete = false; s.Error = "Local source inventory unavailable"; }); } }, ct); }
    }

    private async Task AssessOneSourceAsync(CancellationToken ct)
    {
        var rows = await ListAsync(ct);
        var row = rows.FirstOrDefault(r => !r.IdentityChanged && r.IdentityStatus == "resolved"
            && r.Assessments.Any(a => a.CompletedUtc is null && a.IdentityVersion == r.IdentityVersion && a.RecheckVersion == r.RecheckVersion));
        var assessment = row?.Assessments.First(a => a.CompletedUtc is null && a.IdentityVersion == row.IdentityVersion && a.RecheckVersion == row.RecheckVersion);
        if (row is null)
        {
            row = rows.FirstOrDefault(r => (r.Saved || r.RecheckRequested) && !r.IdentityChanged && r.IdentityStatus == "resolved"
                && r.Sources.Any(s => SourceEligible(s) && Destinations(s.Tracker).Any(t => AssessmentAllowed(rows, r, s, t))));
            if (row is null) return;
            var source = row.Sources.First(s => SourceEligible(s) && Destinations(s.Tracker).Any(t => AssessmentAllowed(rows, row, s, t)));
            var destination = Destinations(source.Tracker).First(t => AssessmentAllowed(rows, row, source, t));
            assessment = NewAssessment(row, source, destination);
            var added = false;
            await MutateAsync(state =>
            {
                if (Find(state, row.Key) is not { } current || current.IdentityVersion != row.IdentityVersion
                    || current.RecheckVersion != row.RecheckVersion || current.IdentityStatus != "resolved" || current.IdentityChanged) return;
                current.Assessments.Add(assessment); added = true;
            }, ct);
            if (!added) return;
        }
        var key = row.Key; var version = row.IdentityVersion; var intent = assessment!;
        var unknown = new TrackerFinding { IdentityVersion = version, CheckedUtc = Now, Error = "Source evidence unknown" };
        if (intent.SourceTracker is null || !await EligibleAtDispatchAsync(key, version, row.RecheckVersion, intent.Id, false, ct))
        { await CompleteAssessmentAsync(key, intent.Id, unknown, ct); return; }
        await MutateAsync(state =>
        {
            if (Find(state, key) is not { } current) return;
            var a = current.Assessments.First(a => a.Id == intent.Id);
            if (current.IdentityVersion == version && current.RecheckVersion == intent.RecheckVersion
                && current.IdentityStatus == "resolved" && !current.IdentityChanged && a.CompletedUtc is null)
                a.Progress = "source-identification";
        }, ct);
        var lookup = intent.SourceLookup is { Verified: true } && intent.SourceVerifiedUtc >= Now.AddMinutes(-15)
            ? intent.SourceLookup
            : await _catalog.LookupSourceByHashAsync(intent.SourceTracker, intent.SourceHash, new(row.Artist, row.Album), ct,
                token => EligibleAtDispatchAsync(key, version, row.RecheckVersion, intent.Id, false, token), row.AlbumType);
        if (lookup is null) return;
        if (!lookup.Verified) { await CompleteAssessmentAsync(key, intent.Id, unknown, ct); return; }
        await MutateAsync(state =>
        {
            var current = Find(state, key)!; var a = current.Assessments.First(a => a.Id == intent.Id);
            if (current.IdentityVersion != version || current.RecheckVersion != intent.RecheckVersion
                || current.IdentityStatus != "resolved" || current.IdentityChanged || a.CompletedUtc is not null) return;
            a.SourceLookup = lookup; a.Medium = lookup.SourceMedium; a.SourceVerifiedUtc = Now; a.Progress = "destination-search";
        }, ct);
        var result = await _catalog.SearchAsync(intent.Destination, row.Names, lookup.SourceMedium!, version, row.AlbumType ?? "unknown", ct,
            token => EligibleAtDispatchAsync(key, version, row.RecheckVersion, intent.Id, true, token));
        if (result is not null) await CompleteAssessmentAsync(key, intent.Id, result, ct);
    }

    private async Task CompleteAssessmentAsync(string key, string id, TrackerFinding result, CancellationToken ct) =>
        await MutateAsync(rows =>
        {
            if (Find(rows, key) is not { } r) return;
            var a = r.Assessments.First(x => x.Id == id);
            if (r.IdentityStatus != "resolved" || r.IdentityChanged || r.IdentityVersion != a.IdentityVersion || r.RecheckVersion != a.RecheckVersion || a.CompletedUtc is not null) return;
            result.SourceHash = a.SourceHash;
            a.Result = result; a.CompletedUtc = Now; a.Progress = "complete";
            if (a.Destination == "red") r.Red = result; else r.Ops = result;
            if (!HasOutstandingAssessments(rows, r)) r.CheckedVersion = Math.Max(r.CheckedVersion, a.RecheckVersion);
        }, ct);

    private async Task<bool> EligibleAtDispatchAsync(string key, long version, long recheck, string? assessmentId, bool destination, CancellationToken ct)
    {
        await ReconcileReferencesAsync(ct);
        var row = Find(await ListAsync(ct), key);
        if (row is null || row.IdentityChanged || row.IdentityStatus != "resolved" || row.IdentityVersion != version || row.RecheckVersion != recheck || row.ReconciliationError is not null) return false;
        if (assessmentId is not null)
        {
            var a = row.Assessments.FirstOrDefault(a => a.Id == assessmentId);
            var source = row.Sources.FirstOrDefault(s => s.Hash == a?.SourceHash);
            return a is not null && a.CompletedUtc is null && a.IdentityVersion == version && a.RecheckVersion == row.RecheckVersion
                && source is { Complete: true } && source.ObservedUtc >= Now.AddMinutes(-15)
                && source.Tracker == a.SourceTracker && source.Tracker == TrackerSourceMapping.Resolve(_config, source.Indexer)
                && source.Tracker != a.Destination && (!destination || a.SourceVerifiedUtc >= Now.AddMinutes(-15));
        }
        // Reconcile acquisition at the transport boundary, never resolve Deezer parents here.
        try { if (!await ReconcileAcquisitionAsync(key, await _lidarr.GetManagedAlbumsAsync(ct), ct)) return false; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        row = Find(await ListAsync(ct), key);
        return row is not null && row.IdentityStatus == "resolved" && !row.IdentityChanged && row.IdentityVersion == version
            && row.RecheckVersion == recheck
            && (recheck > row.CheckedVersion || row.Saved && row.Acquisition == "unresolved")
            && (!destination || row.DeezerAvailability == "available" && row.DeezerProofExpiresUtc > Now);
    }

    private void InvalidatePending(TrackerOpportunity row, string reason)
    {
        foreach (var assessment in row.Assessments.Where(a => a.CompletedUtc is null))
        {
            assessment.Progress = "invalidated"; assessment.CompletedUtc = Now;
            assessment.Result = new() { IdentityVersion = assessment.IdentityVersion, SourceHash = assessment.SourceHash,
                CheckedUtc = Now, Error = reason };
        }
    }

    private bool OrdinaryDue(TrackerOpportunity row) => row.IdentityStatus == "resolved" && !row.IdentityChanged
        && row.ReconciliationError is null && (row.NextAttemptUtc is null || row.NextAttemptUtc <= Now)
        && (row.RecheckRequested || row.Saved && row.Acquisition == "unresolved"
            && (row.Red.CheckedUtc is null || row.Ops.CheckedUtc is null || row.Red.CheckedUtc < Now.AddHours(-24) || row.Ops.CheckedUtc < Now.AddHours(-24)));
    private Task StoreFindingAsync(string key, long version, long recheck, string target, TrackerFinding finding, CancellationToken ct) =>
        MutateAsync(rows => { if (Find(rows, key) is { } r && r.IdentityVersion == version && r.RecheckVersion == recheck
            && r.IdentityStatus == "resolved" && !r.IdentityChanged) { if (target == "red") r.Red = finding; else r.Ops = finding; } }, ct);
    private bool SourceEligible(TrackerLocalSource source) => source.Complete && source.Error is null
        && source.Tracker is not null && source.Tracker == TrackerSourceMapping.Resolve(_config, source.Indexer);
    private bool HasOutstandingAssessments(IReadOnlyList<TrackerOpportunity> rows, TrackerOpportunity row) =>
        row.Assessments.Any(a => a.CompletedUtc is null && a.IdentityVersion == row.IdentityVersion && a.RecheckVersion == row.RecheckVersion)
        || row.Sources.Any(s => SourceEligible(s) && Destinations(s.Tracker).Any(t => AssessmentAllowed(rows, row, s, t)));
    private void Backoff(TrackerOpportunity row)
    { row.Failures++; row.NextAttemptUtc = Now.AddMinutes(Math.Min(720, 10 * Math.Pow(2, Math.Min(row.Failures - 1, 7)))); }
    private static IEnumerable<string> Destinations(string? tracker) => new[] { "red", "ops" }.Where(t => t != tracker);
    private static TrackerCrossUploadAssessment NewAssessment(TrackerOpportunity row, TrackerLocalSource source, string target) =>
        new() { SourceHash = source.Hash, SourceTracker = source.Tracker, Destination = target, IdentityVersion = row.IdentityVersion, RecheckVersion = row.RecheckVersion };
    private static bool AssessmentAllowed(IReadOnlyList<TrackerOpportunity> rows, TrackerOpportunity row, TrackerLocalSource source, string target) =>
        row.RecheckRequested ? !row.Assessments.Any(a => a.SourceHash == source.Hash && a.Destination == target
            && a.IdentityVersion == row.IdentityVersion && a.RecheckVersion == row.RecheckVersion)
        : !rows.SelectMany(x => x.Assessments).Any(a => a.SourceHash == source.Hash && a.Destination == target);
    private async Task MutateAsync(Action<List<TrackerOpportunity>> mutation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var next = Clone(_rows); mutation(next); Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, next); stream.Flush(true); }
            File.Move(temp, _path, true); _rows = next;
        }
        finally { _gate.Release(); }
    }
    private static TrackerOpportunity? Find(IEnumerable<TrackerOpportunity> rows, string key) => rows.FirstOrDefault(r => r.Key == key || r.Aliases.Contains("key:" + key));
    private static string ReferenceKey(Song song) => !string.IsNullOrWhiteSpace(song.DeezerId) ? "deezer:" + song.DeezerId
        : !string.IsNullOrWhiteSpace(song.ExternalProvider) && !string.IsNullOrWhiteSpace(song.ExternalId) ? song.ExternalProvider.ToLowerInvariant() + ":" + song.ExternalId : song.Id;
    private static string ReferenceRevision(Song song) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { song.Artist, song.Title, song.Album, song.AlbumArtist, song.Duration, song.Isrc, song.MusicBrainzReleaseGroupId, song.MusicBrainzAlbumTitle })));
    private static string NameKey(string artist, string album) => SongIdentity.Key(artist) + ":" + SongIdentity.Key(album);
    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static List<TrackerOpportunity> Clone(List<TrackerOpportunity> rows) => JsonSerializer.Deserialize<List<TrackerOpportunity>>(JsonSerializer.Serialize(rows))!;
}
