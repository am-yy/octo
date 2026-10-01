using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Download;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Fingerprint;
using Octo.Services.LastFm;
using Octo.Services.Local;
using Octo.Services.Metadata;
using Octo.Services.Notifications;
using Octo.Services.Subsonic;

namespace Octo.Services.Lidarr;

public interface ILidarrHeartAcquisitionService
{
    Task<bool> TryAcquireTrackAsync(
        string provider, string externalId, bool notifyFailure = true,
        string? requestedBy = null);
    Task<bool> TryAcquireAlbumAsync(
        string provider, string externalId, bool notifyFailure = true,
        string? requestedBy = null);
}

/// <summary>
/// Submits Lidarr album searches without occupying Octo's serialized direct-download
/// worker, then reconciles imported files independently.
/// </summary>
public sealed class LidarrHeartAcquisitionService : ILidarrHeartAcquisitionService
{
    private readonly LidarrClient _client;
    private readonly IMusicMetadataService _metadata;
    private readonly DeezerMetadataService _deezer;
    private readonly IOptionsMonitor<LidarrSettings> _settings;
    private readonly IOptionsMonitor<SubsonicSettings> _subsonicSettings;
    private readonly IConfiguration _configuration;
    private readonly NavidromeIdentityService _navIdentity;
    private readonly ILocalLibraryService _library;
    private readonly DownloadHistoryService _history;
    private readonly NotificationService _notifications;
    private readonly ILogger<LidarrHeartAcquisitionService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _albumJobs = new();
    private readonly ConcurrentDictionary<string, byte> _recordedPaths = new(StringComparer.OrdinalIgnoreCase);

    // Who asked for each album, by Lidarr foreign id. Separate from _albumJobs because the
    // same dedup applies: a second user starring an album Lidarr is already working on joins
    // that job, and the import can land minutes later, so the set is read when the file is
    // recorded rather than captured when the job started.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _albumRequesters = new();

    /// <summary>The live progress list. Lidarr reports no bytes, so it only ever hears
    /// accepted, landed, done and failed from here.</summary>
    private readonly AcquisitionTracker? _tracker;
    private readonly MusicBrainzClient? _musicBrainz;
    private readonly ExternalSaveStore? _externalSaves;

    public LidarrHeartAcquisitionService(
        LidarrClient client,
        IMusicMetadataService metadata,
        DeezerMetadataService deezer,
        IOptionsMonitor<LidarrSettings> settings,
        IOptionsMonitor<SubsonicSettings> subsonicSettings,
        IConfiguration configuration,
        NavidromeIdentityService navIdentity,
        ILocalLibraryService library,
        DownloadHistoryService history,
        NotificationService notifications,
        ILogger<LidarrHeartAcquisitionService> logger,
        AcquisitionTracker? tracker = null,
        MusicBrainzClient? musicBrainz = null,
        ExternalSaveStore? externalSaves = null)
    {
        _tracker = tracker;
        _musicBrainz = musicBrainz;
        _externalSaves = externalSaves;
        _client = client;
        _metadata = metadata;
        _deezer = deezer;
        _settings = settings;
        _subsonicSettings = subsonicSettings;
        _configuration = configuration;
        _navIdentity = navIdentity;
        _library = library;
        _history = history;
        _notifications = notifications;
        _logger = logger;
        foreach (var entry in history.GetRecent(int.MaxValue))
            if (!string.IsNullOrWhiteSpace(entry.Path)) _recordedPaths.TryAdd(entry.Path, 0);
    }

    public Task<bool> TryAcquireTrackAsync(
        string provider, string externalId, bool notifyFailure = true,
        string? requestedBy = null) =>
        TryAcquireAsync(async () =>
        {
            var song = _externalSaves?.GetSong(externalId)
                ?? _externalSaves?.Snapshot().Acquisitions.FirstOrDefault(i =>
                    i.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)
                    && i.ExternalId.Equals(externalId, StringComparison.OrdinalIgnoreCase))?.Song
                ?? await _metadata.GetSongAsync(provider, externalId)
                ?? throw new InvalidOperationException("The starred external track is no longer available.");
            song.ExternalProvider ??= provider;
            song.ExternalId ??= externalId;
            // Durable metadata survives registry eviction and restart. Check the recording
            // before claiming acquisition, so an existing import cannot trigger another search.
            var imported = await _library.FindImportedSongAsync(song);
            if (imported is { IsLocal: true, Suffix: { } suffix, LocalPath: { Length: > 0 } path }
                && suffix.Equals("flac", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                if (_externalSaves is not null)
                    await _externalSaves.MarkImportedAsync(provider, externalId, imported.Id, path);
                return;
            }
            if (_externalSaves is not null)
            {
                await _externalSaves.QueueAcquisitionAsync(provider, externalId, song, requestedBy);
                if (await _externalSaves.TryClaimAcquisitionAsync(provider, externalId) is null) return;
            }

            // Deezer names the release a hit came out on first, usually the single, which Lidarr
            // then cannot match. MusicBrainz knows which studio album the song belongs to.
            var studioAlbumId = _musicBrainz is null ? null
                : await _musicBrainz.FindStudioAlbumAsync(song.Artist ?? "", song.Title ?? "", CancellationToken.None);
            var studio = studioAlbumId is null ? null : await _client.ResolveAlbumByForeignIdAsync(studioAlbumId);
            if (studio is not null)
            {
                song.Album = studio.Title;
                await QueueResolvedAlbumAsync(new Album
                {
                    Title = studio.Title,
                    Artist = studio.Artist,
                    Year = studio.Year,
                    Songs = new List<Song> { song },
                }, requestedBy, studio);
                return;
            }

            var album = await ResolveTrackAlbumAsync(song, _deezer);
            await QueueResolvedAlbumAsync(album, requestedBy);
        }, "track", provider, externalId, notifyFailure);

    internal static async Task<Album> ResolveTrackAlbumAsync(Song song, DeezerMetadataService deezer)
    {
        var enriched = await deezer.EnrichTrackAsync(song.Artist, song.Title, includeYear: true);
        var title = enriched?.AlbumTitle;
        if (string.IsNullOrWhiteSpace(title)) title = song.Album;
        var artist = enriched?.ArtistName ?? song.Artist;
        var year = enriched?.Year ?? song.Year;
        var cover = enriched?.AlbumCoverUrl;

        // Track search often reports only a single, even when Deezer lists its studio album.
        // ponytail: inspect first 25 artist album hits; paginate discography if later releases matter.
        var albums = (await deezer.SearchAlbumsAsync(artist, 25))
            .Where(a => SongIdentity.Key(a.Artist) == SongIdentity.Key(artist)
                && (a.RecordType == "album" || a.RecordType == "ep"))
            .ToList();
        if (!albums.Any(a => SongIdentity.Key(a.Title) == SongIdentity.Key(title)))
        {
            foreach (var hit in albums.OrderBy(a => a.RecordType == "album" ? 0 : 1))
            {
                var detail = await deezer.GetAlbumDetailAsync(hit.DeezerId);
                if (detail is null || SongIdentity.Key(detail.Artist) != SongIdentity.Key(artist))
                    continue;
                var track = detail.Tracks.FirstOrDefault(t =>
                    LastFmRadioTrackResolver.IsSameRecording(song.Artist, song.Title, t.Artist, t.Title)
                    && ((enriched?.Duration ?? song.Duration) is not int duration || duration <= 0
                        || t.Duration is not int got || got <= 0 || Math.Abs(duration - got) <= 10));
                if (track is null) continue;
                title = detail.Title;
                artist = detail.Artist;
                year = detail.Year;
                cover = detail.CoverUrl;
                song.Track = track.TrackPosition;
                song.DiscNumber = track.DiscNumber;
                song.TotalTracks = detail.Tracks.Count;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException($"Could not resolve an album for '{song.Artist} - {song.Title}'.");

        song.Album = title;
        song.CoverArtUrl = cover ?? song.CoverArtUrl;
        song.Year = year ?? song.Year;
        return new Album
        {
            Title = title,
            Artist = artist,
            Year = year,
            CoverArtUrl = cover,
            Songs = new List<Song> { song },
        };
    }

    public Task<bool> TryAcquireAlbumAsync(
        string provider, string externalId, bool notifyFailure = true,
        string? requestedBy = null) =>
        TryAcquireAsync(async () =>
        {
            var album = await _metadata.GetAlbumAsync(provider, externalId)
                ?? throw new InvalidOperationException("The starred external album is no longer available.");
            if (_externalSaves is not null)
                foreach (var song in album.Songs.Where(s => !string.IsNullOrWhiteSpace(s.ExternalId)))
                {
                    song.ExternalProvider ??= provider;
                    await _externalSaves.QueueAcquisitionAsync(song.ExternalProvider!, song.ExternalId!, song, requestedBy);
                }
            // No walk runs on this path, so the track list is announced here instead.
            _tracker?.Announce(provider, externalId, null, album.Songs
                .Where(s => !string.IsNullOrEmpty(s.ExternalId))
                .Select(s => (s.ExternalId!, (string?)s.Artist, (string?)s.Title, (string?)album.Title)));
            await QueueResolvedAlbumAsync(album, requestedBy);
        }, "album", provider, externalId, notifyFailure);

    private void AddRequester(string albumKey, string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        _albumRequesters
            .GetOrAdd(albumKey, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))
            .TryAdd(username.Trim(), 0);
    }

    private IReadOnlyList<string>? RequestersFor(string albumKey) =>
        _albumRequesters.TryGetValue(albumKey, out var set) && !set.IsEmpty
            ? set.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList()
            : null;

    private async Task QueueResolvedAlbumAsync(Album album, string? requestedBy = null,
        LidarrAlbumCandidate? resolved = null)
    {
        if (string.IsNullOrWhiteSpace(album.Artist) || string.IsNullOrWhiteSpace(album.Title))
            throw new InvalidOperationException("Lidarr requires an album artist and title.");

        var candidate = resolved ?? await _client.ResolveAlbumAsync(album.Artist, album.Title, album.Year);
        if (_externalSaves is not null)
            foreach (var song in album.Songs.Where(s => !string.IsNullOrWhiteSpace(s.ExternalProvider)
                                                        && !string.IsNullOrWhiteSpace(s.ExternalId)))
                await _externalSaves.AssociateAcquisitionWithAlbumAsync(
                    song.ExternalProvider!, song.ExternalId!, candidate.ForeignAlbumId);
        // Before GetOrAdd, so a caller that joins an existing job is still recorded.
        AddRequester(candidate.ForeignAlbumId, requestedBy);
        var lazy = _albumJobs.GetOrAdd(candidate.ForeignAlbumId,
            _ => new Lazy<Task>(() => SubmitAndStartReconciliationAsync(candidate, album),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            await lazy.Value;
        }
        catch
        {
            _albumJobs.TryRemove(new KeyValuePair<string, Lazy<Task>>(candidate.ForeignAlbumId, lazy));
            throw;
        }
    }

    private static IEnumerable<(string Provider, string Id)> TrackedKeys(Album album) =>
        album.Songs
            .Where(s => !string.IsNullOrWhiteSpace(s.ExternalProvider) && !string.IsNullOrWhiteSpace(s.ExternalId))
            .Select(s => (s.ExternalProvider!, s.ExternalId!));

    private void NoteLanded(Album album, LidarrImportedTrack track, string localPath, Dictionary<Song, string> landed)
    {
        if (_tracker is null) return;
        try
        {
            if (MatchSong(album, track) is not { } song) return;
            landed[song] = localPath;
            if (song is { ExternalProvider: { Length: > 0 } provider, ExternalId: { Length: > 0 } id })
                _tracker.Stage(provider, id, AcquisitionState.Importing);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not match a Lidarr import for progress: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Close the progress entry of every hearted track on this album: the ones Octo can see on
    /// disk are imported, the rest failed with <paramref name="missing"/>. Rows already settled
    /// keep what they said.
    /// </summary>
    private void SettleTracked(Album album, Dictionary<Song, string> landed, string missing)
    {
        if (_tracker is null) return;
        try
        {
            foreach (var song in album.Songs)
            {
                if (string.IsNullOrWhiteSpace(song.ExternalProvider) || string.IsNullOrWhiteSpace(song.ExternalId)) continue;
                if (landed.TryGetValue(song, out var path))
                    _tracker.Imported(song.ExternalProvider, song.ExternalId, song.Artist, song.Title, path);
                else
                    _tracker.Fail(song.ExternalProvider, song.ExternalId, missing);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not settle Lidarr progress for '{Album}': {Message}", album.Title, ex.Message);
        }
    }

    private async Task SubmitAndStartReconciliationAsync(
        LidarrAlbumCandidate candidate, Album album)
    {
        var snapshot = _settings.CurrentValue;
        int albumId;
        var prior = _externalSaves?.GetAlbumSearch(candidate.ForeignAlbumId);
        if (prior is { Status: "submitted", LidarrAlbumId: int submittedId })
        {
            albumId = submittedId;
        }
        else if (_externalSaves is not null && !await _externalSaves.TryBeginAlbumSearchAsync(candidate.ForeignAlbumId))
        {
            // A durable in-progress marker may be all that survived shutdown. Do not issue a
            // second AlbumSearch; the periodic import reconciler keeps checking for its files.
            var current = _externalSaves.GetAlbumSearch(candidate.ForeignAlbumId);
            if (current?.LidarrAlbumId is not int knownId) return;
            albumId = knownId;
        }
        else
        {
            try
            {
                albumId = await _client.EnsureAlbumAndSearchAsync(candidate,
                    beforeSearch: _externalSaves is null ? null : id =>
                        _externalSaves.MarkAlbumSearchSubmittingAsync(candidate.ForeignAlbumId, id));
                if (_externalSaves is not null)
                    await _externalSaves.MarkAlbumSearchSubmittedAsync(candidate.ForeignAlbumId, albumId);
            }
            catch (Exception ex)
            {
                // A failed response after submission began is ambiguous. Keep the claim
                // rather than repeat AlbumSearch; import reconciliation remains active.
                if (_externalSaves?.GetAlbumSearch(candidate.ForeignAlbumId)?.Status == "preparing")
                    await _externalSaves.MarkAlbumSearchFailedAsync(candidate.ForeignAlbumId, ex.Message);
                throw;
            }
        }
        _logger.LogInformation("Lidarr accepted AlbumSearch for '{Artist} - {Album}' ({ForeignId}, local id {Id})",
            album.Artist, album.Title, candidate.ForeignAlbumId, albumId);
        _notifications.Notify(new NotificationEvent
        {
            Type = NotificationEventType.DownloadStarted,
            Artist = album.Artist,
            Title = album.Title,
            Album = album.Title,
            Source = "Lidarr",
            CoverArtUrl = album.CoverArtUrl,
            Detail = "Album search accepted",
        });

        // Accepted. Lidarr says nothing about bytes, so this is a download with no figure on
        // it. Before the reconcile starts, so it can never undo what the reconcile reports.
        foreach (var (provider, id) in TrackedKeys(album))
            _tracker?.Transfer(provider, id, null, null, null, "Lidarr");

        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileImportsAsync(albumId, album, snapshot, candidate.ForeignAlbumId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lidarr import reconciliation failed for '{Artist} - {Album}'",
                    album.Artist, album.Title);
                foreach (var (provider, id) in TrackedKeys(album)) _tracker?.Fail(provider, id, ex.Message);
                if (snapshot.CompletionMode == LidarrCompletionMode.Imported)
                {
                    _notifications.Notify(new NotificationEvent
                    {
                        Type = NotificationEventType.DownloadFailed,
                        Artist = album.Artist,
                        Title = album.Title,
                        Album = album.Title,
                        Source = "Lidarr",
                        CoverArtUrl = album.CoverArtUrl,
                        Detail = ex.Message,
                    });
                }
            }
            finally
            {
                _albumJobs.TryRemove(candidate.ForeignAlbumId, out _);
            }
        });
    }

    private async Task ReconcileImportsAsync(int albumId, Album album, LidarrSettings settings,
        string albumKey)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.ImportTimeoutSeconds));
        var poll = TimeSpan.FromSeconds(Math.Clamp(settings.ImportTimeoutSeconds / 30, 1, 10));
        var deadline = DateTime.UtcNow + timeout;
        var imported = 0;
        var expected = 0;
        var visibleToOcto = 0;
        // Which hearted song each visible file is, for the progress list. Matched on every poll,
        // not only when recorded, so a song Lidarr had imported before still counts as here.
        var landed = new Dictionary<Song, string>(ReferenceEqualityComparer.Instance);

        var octoRoot = _navIdentity.EffectiveDownloadPath(_configuration["Library:DownloadPath"] ?? "/music");

        while (DateTime.UtcNow < deadline)
        {
            var state = await _client.GetAlbumImportStateAsync(albumId);
            var tracks = state.Tracks;
            expected = state.TrackCount;
            visibleToOcto = 0;
            var visible = tracks
                .Where(t => t.HasFile && !string.IsNullOrWhiteSpace(t.Path))
                .GroupBy(t => t.Path!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            foreach (var track in visible)
            {
                var importedPath = TranslateImportedPath(track.Path!, settings.RootFolderPath, octoRoot);
                if (!File.Exists(importedPath)) continue;
                var localPath = NormalizeImportedLayout(importedPath, album, track, octoRoot);
                visibleToOcto++;
                NoteLanded(album, track, localPath, landed);
                if (!_recordedPaths.TryAdd(localPath, 0)) continue;
                try
                {
                    await RecordImportAsync(album, track, localPath, RequestersFor(albumKey));
                    imported++;
                }
                catch
                {
                    _recordedPaths.TryRemove(localPath, out _);
                    throw;
                }
            }

            if (state.IsComplete && visible.Count > 0 && visibleToOcto == visible.Count)
            {
                if (imported > 0) await _library.TriggerLibraryScanAsync(force: true);
                SettleTracked(album, landed, "Lidarr finished the album without this track.");
                if (settings.CompletionMode == LidarrCompletionMode.Imported && imported > 0)
                {
                    _notifications.Notify(new NotificationEvent
                    {
                        Type = NotificationEventType.AlbumCompleted,
                        Artist = album.Artist,
                        Title = album.Title,
                        CoverArtUrl = album.CoverArtUrl,
                        TrackCount = state.TrackCount,
                        LosslessCount = visible.Count(t =>
                            string.Equals(Path.GetExtension(t.Path), ".flac", StringComparison.OrdinalIgnoreCase)),
                        FailedCount = 0,
                    });
                }
                return;
            }

            await Task.Delay(poll);
        }

        if (imported > 0) await _library.TriggerLibraryScanAsync(force: true);
        var detail = $"Lidarr import timed out after {(int)timeout.TotalMinutes} minute(s)"
                     + (expected > 0 ? $" ({visibleToOcto}/{expected} files visible to Octo)" : "");
        SettleTracked(album, landed, $"Lidarr import timed out after {(int)timeout.TotalMinutes} minute(s).");
        _logger.LogWarning("{Detail} for '{Artist} - {Album}'", detail, album.Artist, album.Title);
        if (settings.CompletionMode == LidarrCompletionMode.Imported)
        {
            _notifications.Notify(new NotificationEvent
            {
                Type = NotificationEventType.DownloadFailed,
                Artist = album.Artist,
                Title = album.Title,
                Album = album.Title,
                Source = "Lidarr",
                CoverArtUrl = album.CoverArtUrl,
                Detail = detail,
            });
        }
    }

    private async Task RecordImportAsync(Album album, LidarrImportedTrack imported, string localPath,
        IReadOnlyList<string>? requestedBy = null)
    {
        var song = MatchSong(album, imported) ?? new Song
        {
            Artist = imported.Artist ?? album.Artist,
            Title = imported.Title,
            Album = album.Title,
            Track = imported.TrackNumber,
            Duration = imported.DurationSeconds,
            CoverArtUrl = album.CoverArtUrl,
            IsLocal = false,
        };

        if (!string.IsNullOrWhiteSpace(song.ExternalProvider) && !string.IsNullOrWhiteSpace(song.ExternalId))
            await _library.RegisterDownloadedSongAsync(song, localPath);

        var ext = Path.GetExtension(localPath).TrimStart('.').ToUpperInvariant();
        long size = imported.SizeBytes;
        if (size <= 0) try { size = new FileInfo(localPath).Length; } catch { /* best effort */ }
        _history.Record(new DownloadHistoryEntry
        {
            Artist = song.Artist,
            Title = song.Title,
            Album = album.Title,
            Path = localPath,
            Format = string.IsNullOrEmpty(ext) ? "?" : ext,
            Source = "Lidarr",
            CoverArtUrl = song.CoverArtUrlLarge ?? song.CoverArtUrl ?? album.CoverArtUrl,
            SizeBytes = size,
            DownloadedAt = DateTime.UtcNow.ToString("o"),
            RequestedBy = requestedBy is { Count: > 0 } ? [.. requestedBy] : null,
        });
    }

    /// <summary>The album's song Lidarr imported: by title, read by <see cref="SongIdentity"/> so
    /// Deezer's "Song (feat. X)" is MusicBrainz's "Song" but never its "Song (Live)", then by
    /// track number.</summary>
    internal static Song? MatchSong(Album album, LidarrImportedTrack track)
    {
        var byTitle = album.Songs.Where(s => SongIdentity.SameTitle(track.Title, s.Title, SongIdentity.StrictTitles).IsSame).ToList();
        if (byTitle.Count == 1) return byTitle[0];
        if (track.TrackNumber is int number)
        {
            var byNumber = album.Songs.Where(s => s.Track == number).ToList();
            if (byNumber.Count == 1) return byNumber[0];
        }
        return byTitle.FirstOrDefault();
    }

    internal static string TranslateImportedPath(string lidarrPath, string? lidarrRoot, string octoRoot)
    {
        if (string.IsNullOrWhiteSpace(lidarrRoot))
            throw new InvalidOperationException("Lidarr root folder is not configured.");
        var root = Path.GetFullPath(lidarrRoot);
        var source = Path.GetFullPath(lidarrPath);
        var relative = Path.GetRelativePath(root, source);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Lidarr imported a path outside its configured root: {lidarrPath}");
        var targetRoot = Path.GetFullPath(octoRoot);
        var target = Path.GetFullPath(Path.Combine(targetRoot, relative));
        if (Path.GetRelativePath(targetRoot, target).StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Translated Lidarr path escaped Octo's library root.");
        return target;
    }

    /// <summary>
    /// Lidarr does not always rename an import into an Artist/Album layout: a release with
    /// thin MusicBrainz metadata can land under its raw staging folder name (e.g.
    /// "HELLHOUND {mbid:...} {Album}") with the peer's original filename still attached.
    /// Re-home it into the same layout PathHelper.BuildTrackPath gives direct Soulseek
    /// downloads, so Navidrome never sees Lidarr-sourced tracks organized differently.
    /// </summary>
    private string NormalizeImportedLayout(string importedPath, Album album, LidarrImportedTrack track, string octoRoot)
    {
        try
        {
            var ext = Path.GetExtension(importedPath);
            var title = string.IsNullOrWhiteSpace(track.Title) ? album.Title : track.Title;

            // Follow the SAME setting the Soulseek path follows. Building the Artist/Album
            // layout unconditionally would put Lidarr imports in folders while a Flat
            // library keeps everything in one directory, which is the inconsistency this
            // is here to remove, and Flat is the default.
            var canonicalPath = PathHelper.BuildLayoutPath(
                _subsonicSettings.CurrentValue.FolderStructure, octoRoot,
                album.Artist, album.Title, title, track.TrackNumber, ext);
            if (string.Equals(Path.GetFullPath(canonicalPath), Path.GetFullPath(importedPath), StringComparison.OrdinalIgnoreCase))
                return importedPath;

            var targetDir = Path.GetDirectoryName(canonicalPath);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
            canonicalPath = PathHelper.ResolveUniquePath(canonicalPath);
            File.Move(importedPath, canonicalPath);

            var oldDir = Path.GetDirectoryName(importedPath);
            if (!string.IsNullOrEmpty(oldDir) && Directory.Exists(oldDir)
                && !Directory.EnumerateFileSystemEntries(oldDir).Any())
                Directory.Delete(oldDir);

            return canonicalPath;
        }
        catch
        {
            // Best effort: keep the file registered where Lidarr put it rather than losing it.
            return importedPath;
        }
    }

    private async Task<bool> TryAcquireAsync(
        Func<Task> work, string kind, string provider, string externalId, bool notifyFailure)
    {
        try
        {
            await work();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lidarr {Kind} heart failed for {Id}", kind, externalId);
            if (kind == "track" && _externalSaves is not null)
            {
                try { await _externalSaves.MarkAcquisitionFailedAsync(provider, externalId, ex.Message); }
                catch (Exception storeError)
                {
                    _logger.LogError(storeError, "Could not persist failed acquisition for {Provider}:{Id}", provider, externalId);
                }
            }
            // notifyFailure is true only for the last source in the chain, which is also the
            // only failure the progress list may show.
            if (notifyFailure)
            {
                if (kind == "album") _tracker?.FailAlbum(provider, externalId, ex.Message);
                else _tracker?.Fail(provider, externalId, ex.Message);
            }
            if (notifyFailure) _notifications.Notify(new NotificationEvent
            {
                Type = NotificationEventType.DownloadFailed,
                Source = "Lidarr",
                Detail = ex.Message,
            });
            return false;
        }
    }
}
