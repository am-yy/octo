using System.Text.Json;
using Octo.Models.Domain;
using Octo.Services.Common;
using Octo.Services.Lidarr;
using Octo.Services.Subsonic;
using Octo.Services.Metadata;

namespace Octo.Services.Trackers;

public sealed class TrackerFinding
{
    public string Status { get; set; } = "unknown";
    public DateTime? CheckedUtc { get; set; }
    public List<TrackerMatch> Matches { get; set; } = [];
    public string? Error { get; set; }
}
public sealed record TrackerMatch(int GroupId, string Artist, string Title, int Seeders, string Url);

public sealed class TrackerOpportunity
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string? ForeignAlbumId { get; set; }
    public int? LidarrAlbumId { get; set; }
    public List<string> SourceLinks { get; set; } = [];
    public List<string> ReferenceIds { get; set; } = [];
    public List<string> Aliases { get; set; } = [];
    public List<ReleaseName> Names { get; set; } = [];
    public bool Saved { get; set; }
    public string DeezerAvailability { get; set; } = "unknown";
    public DateTime? DeezerCheckedUtc { get; set; }
    public string Acquisition { get; set; } = "unresolved";
    public string? ReconciliationError { get; set; }
    public long RecheckVersion { get; set; }
    public long CheckedVersion { get; set; }
    public bool RecheckRequested => RecheckVersion > CheckedVersion;
    public TrackerFinding Red { get; set; } = new();
    public TrackerFinding Ops { get; set; } = new();
}

/// <summary>Saved release identities and acquisition closure survive references, metadata, and restarts.</summary>
public sealed class TrackerOpportunityService : BackgroundService
{
    private readonly ExternalSaveStore _saves;
    private readonly LidarrClient _lidarr;
    private readonly TrackerCatalogClient _catalog;
    private readonly ILogger<TrackerOpportunityService> _logger;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly DeezerMetadataService? _deezer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private List<TrackerOpportunity> _rows;

    public TrackerOpportunityService(ExternalSaveStore saves, LidarrClient lidarr, TrackerDirectQueue queue,
        string path, ILogger<TrackerOpportunityService> logger, TimeProvider? time = null, DeezerMetadataService? deezer = null)
    {
        _saves = saves; _lidarr = lidarr; _path = path; _logger = logger;
        _time = time ?? TimeProvider.System;
        _deezer = deezer;
        _catalog = new TrackerCatalogClient(queue, _time);
        _rows = File.Exists(path)
            ? JsonSerializer.Deserialize<List<TrackerOpportunity>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Tracker opportunity state is invalid.") : [];
    }

    public async Task<IReadOnlyList<TrackerOpportunity>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return Clone(_rows).OrderBy(r => r.Artist).ThenBy(r => r.Album).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> RecheckAsync(string key, CancellationToken ct = default)
    {
        var found = false;
        await MutateAsync(rows =>
        {
            if (Find(rows, key) is not { } row) return;
            row.RecheckVersion++;
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
            {
                await MutateAsync(rows => rows.ForEach(r => r.ReconciliationError = "Lidarr reconciliation unavailable"), ct);
                return; // No initial discovery before existing acquisitions have been reconciled.
            }
            foreach (var row in await ListAsync(ct)) await ReconcileAcquisitionAsync(row.Key, managed, ct);
            // Fair, bounded batches. Older unchecked rows precede refreshed rows.
            var due = (await ListAsync(ct)).Where(Due).OrderByDescending(r => r.RecheckRequested)
                .ThenBy(r => r.Red.CheckedUtc).Take(10).ToList();
            foreach (var row in due)
            {
                if (!await EligibleAtDispatchAsync(row.Key, row.RecheckVersion, ct)) continue;
                if (_deezer is not null)
                {
                    // Catalog presence is useful context, not proof of playback access or upload provenance.
                    var albumId = await _deezer.FindAlbumIdAsync(row.Artist, row.Album, ct);
                    var detail = albumId is null ? null : await _deezer.GetAlbumDetailAsync(albumId, ct);
                    var matched = detail is not null && NameKey(detail.Artist, detail.Title) == NameKey(row.Artist, row.Album);
                    await MutateAsync(rows =>
                    {
                        if (Find(rows, row.Key) is not { } current) return;
                        current.DeezerAvailability = matched ? "catalog-present" : "unknown";
                        current.DeezerCheckedUtc = _time.GetUtcNow().UtcDateTime;
                        if (matched && albumId!.All(char.IsAsciiDigit)) current.SourceLinks = current.SourceLinks
                            .Append("https://www.deezer.com/album/" + albumId).Distinct().ToList();
                    }, ct);
                }
                var skipped = false;
                foreach (var target in new[] { "red", "ops" })
                {
                    var result = await _catalog.SearchAsync(target, row.Names, ct,
                        token => EligibleAtDispatchAsync(row.Key, row.RecheckVersion, token));
                    if (result is null) { skipped = true; break; }
                    await MutateAsync(rows =>
                    {
                        if (Find(rows, row.Key) is not { } current) return;
                        if (target == "red") current.Red = result; else current.Ops = result;
                    }, ct);
                }
                if (!skipped) await MutateAsync(rows =>
                {
                    if (Find(rows, row.Key) is { } current) current.CheckedVersion = row.RecheckVersion;
                }, ct);
            }
        }
        finally { _refresh.Release(); }
    }

    private async Task ReconcileReferencesAsync(CancellationToken ct)
    {
        var snapshot = _saves.Snapshot();
        var songs = snapshot.Hearts.Select(h => h.Song).Concat(snapshot.Playlists.SelectMany(p => p.Tracks)
            .Where(t => t.Song is not null).Select(t => t.Song!)).DistinctBy(s => s.Id).ToList();
        await MutateAsync(rows =>
        {
            rows.ForEach(r => r.Saved = false);
            foreach (var song in songs)
            {
                var artist = string.IsNullOrWhiteSpace(song.AlbumArtist) ? song.Artist : song.AlbumArtist;
                if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(song.Album)) continue;
                var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { song.Id };
                // Follow both directions: imported local IDs and recovered provider aliases refer to one save.
                bool changed;
                do
                {
                    changed = false;
                    foreach (var alias in snapshot.Aliases)
                        if (references.Contains(alias.AliasId) || references.Contains(alias.CanonicalId))
                            changed |= references.Add(alias.AliasId) | references.Add(alias.CanonicalId);
                } while (changed);
                var intent = snapshot.Acquisitions.FirstOrDefault(i => references.Contains(i.Song.Id)
                    || Same(i.Provider, song.ExternalProvider) && Same(i.ExternalId, song.ExternalId));
                var foreign = song.MusicBrainzReleaseGroupId ?? intent?.AlbumForeignId;
                var identity = NameKey(artist, song.Album);
                var aliases = new List<string> { identity };
                if (!string.IsNullOrWhiteSpace(foreign)) aliases.Add("mbid:" + foreign.ToLowerInvariant());
                var matches = rows.Where(r => r.Aliases.Intersect(aliases).Any()
                    || r.ReferenceIds.Intersect(references, StringComparer.OrdinalIgnoreCase).Any()
                    || Same(r.ForeignAlbumId, foreign)).OrderByDescending(r => Rank(r.Acquisition)).ThenBy(r => r.Key).ToList();
                var row = matches.FirstOrDefault();
                if (row is null) { row = new TrackerOpportunity(); rows.Add(row); }
                foreach (var other in matches.Skip(1))
                {
                    row.Aliases.AddRange(other.Aliases.Append("key:" + other.Key));
                    row.ReferenceIds.AddRange(other.ReferenceIds);
                    row.Names.AddRange(other.Names);
                    row.SourceLinks.AddRange(other.SourceLinks);
                    if (other.Red.CheckedUtc is not null && (row.Red.CheckedUtc is null || other.Red.CheckedUtc > row.Red.CheckedUtc)) row.Red = other.Red;
                    if (other.Ops.CheckedUtc is not null && (row.Ops.CheckedUtc is null || other.Ops.CheckedUtc > row.Ops.CheckedUtc)) row.Ops = other.Ops;
                    // Preserve a pending explicit recheck even when another alias owns the surviving row.
                    if (other.RecheckRequested && !row.RecheckRequested) row.RecheckVersion = row.CheckedVersion + 1;
                    rows.Remove(other);
                }
                row.Artist = artist;
                row.Album = song.Album;
                row.ForeignAlbumId ??= foreign;
                row.LidarrAlbumId ??= intent?.LidarrAlbumId;
                row.Saved = true;
                row.Aliases = row.Aliases.Concat(aliases).Distinct().ToList();
                row.ReferenceIds = row.ReferenceIds.Concat(references).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                row.Names = row.Names.Append(new ReleaseName(artist, song.Album)).Distinct().ToList();
                var deezer = song.DeezerId ?? (Same(song.ExternalProvider, "deezer") ? song.ExternalId : null);
                if (!string.IsNullOrEmpty(deezer) && deezer.All(char.IsAsciiDigit))
                    row.SourceLinks = row.SourceLinks.Append("https://www.deezer.com/track/" + deezer).Distinct().ToList();
            }
        }, ct);
    }

    private async Task<bool> ReconcileAcquisitionAsync(string key, IReadOnlyList<LidarrAlbumCandidate> managed, CancellationToken ct)
    {
        var row = Find(await ListAsync(ct), key);
        if (row is null) return false;
        if (row.Acquisition == "complete")
        {
            if (row.ReconciliationError is not null)
                await MutateAsync(rows => { if (Find(rows, key) is { } current) current.ReconciliationError = null; }, ct);
            return true;
        }
        try
        {
            var candidates = managed.Where(a => Same(a.ForeignAlbumId, row.ForeignAlbumId)
                || row.Names.Any(n => NameKey(a.Artist, a.Title) == NameKey(n.Artist, n.Album))).ToList();
            if (row.ForeignAlbumId is { Length: > 0 })
                candidates = candidates.Where(a => Same(a.ForeignAlbumId, row.ForeignAlbumId)).ToList();
            if (candidates.Count > 1) throw new InvalidDataException("Ambiguous managed album.");
            var album = candidates.SingleOrDefault();
            var id = album?.Id ?? row.LidarrAlbumId;
            var evidence = id is int albumId ? await _lidarr.GetAcquisitionEvidenceAsync(albumId, ct) : new(false, false, false);
            await MutateAsync(rows =>
            {
                if (Find(rows, key) is not { } current) return;
                current.ReconciliationError = null;
                if (album is not null)
                {
                    current.ForeignAlbumId = album.ForeignAlbumId;
                    current.LidarrAlbumId = album.Id;
                    current.Aliases = current.Aliases.Append("mbid:" + album.ForeignAlbumId.ToLowerInvariant())
                        .Append(NameKey(album.Artist, album.Title)).Distinct().ToList();
                    current.Names = current.Names.Append(new ReleaseName(album.Artist, album.Title)).Distinct().ToList();
                    current.Artist = album.Artist; current.Album = album.Title;
                }
                if (evidence.Complete) current.Acquisition = "complete";
                else if (evidence.Grabbed) current.Acquisition = evidence.Failed ? "failed" : "grabbed";
                // A disappearing queue/history/file never reopens a persisted acquisition closure.
            }, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MutateAsync(rows => { if (Find(rows, key) is { } current) current.ReconciliationError = "Album acquisition needs reconciliation"; }, ct);
            return false;
        }
    }

    private async Task<bool> EligibleAtDispatchAsync(string key, long recheckVersion, CancellationToken ct)
    {
        await ReconcileReferencesAsync(ct);
        try
        {
            if (!await ReconcileAcquisitionAsync(key, await _lidarr.GetManagedAlbumsAsync(ct), ct)) return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        var row = Find(await ListAsync(ct), key);
        return row is not null && row.ReconciliationError is null &&
            (recheckVersion > row.CheckedVersion || row.Saved && row.Acquisition == "unresolved");
    }

    private bool Due(TrackerOpportunity row) => row.ReconciliationError is null && (row.RecheckRequested ||
        row.Saved && row.Acquisition == "unresolved" &&
        (row.Red.CheckedUtc is null || row.Ops.CheckedUtc is null
            || row.Red.CheckedUtc < _time.GetUtcNow().UtcDateTime.AddHours(-24)
            || row.Ops.CheckedUtc < _time.GetUtcNow().UtcDateTime.AddHours(-24)));

    private async Task MutateAsync(Action<List<TrackerOpportunity>> mutation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var next = Clone(_rows);
            mutation(next);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, next);
                stream.Flush(true);
            }
            File.Move(temp, _path, true);
            _rows = next;
        }
        finally { _gate.Release(); }
    }

    private static TrackerOpportunity? Find(IEnumerable<TrackerOpportunity> rows, string key) =>
        rows.FirstOrDefault(r => r.Key == key || r.Aliases.Contains("key:" + key));
    private static int Rank(string state) => state == "complete" ? 2 : state == "unresolved" ? 0 : 1;
    private static string NameKey(string artist, string album) => "name:" + SongIdentity.Key(artist) + ":" + SongIdentity.Key(album);
    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static List<TrackerOpportunity> Clone(List<TrackerOpportunity> rows) => JsonSerializer.Deserialize<List<TrackerOpportunity>>(JsonSerializer.Serialize(rows))!;
}
