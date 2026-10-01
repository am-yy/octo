using System.Text.Json;
using System.Text.Json.Serialization;
using Octo.Models.Domain;

namespace Octo.Services.Subsonic;

/// <summary>A playlist entry Octo keeps beside Navidrome's own ordered playlist.</summary>
public sealed class ExternalPlaylistTrack
{
    public string OccurrenceId { get; set; } = Guid.NewGuid().ToString("N");
    public string SongId { get; set; } = string.Empty;
    public Song? Song { get; set; }
    public string? CanonicalId { get; set; }
}

/// <summary>Durable overlay for one caller's manually saved Navidrome playlist.</summary>
public sealed class ExternalSavedPlaylist
{
    public string UserId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    [JsonIgnore]
    public string PlaylistId { get => Id; set => Id = value; }
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public bool Public { get; set; }
    public bool PendingMirror { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<ExternalPlaylistTrack> Tracks { get; set; } = [];
}

public sealed class ExternalSavedHeart
{
    public string UserId { get; set; } = string.Empty;
    public string SongId { get; set; } = string.Empty;
    public Song Song { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ExternalHeartMutation
{
    public string UserId { get; set; } = string.Empty;
    public string SongId { get; set; } = string.Empty;
    public Song Song { get; set; } = new();
    public bool Hearted { get; set; }
    public bool PendingMirror { get; set; } = true;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ExternalSongAlias
{
    public string AliasId { get; set; } = string.Empty;
    public string CanonicalId { get; set; } = string.Empty;
    public Song? Song { get; set; }
}

/// <summary>Restart-safe work created by a heart or manually saved playlist entry.</summary>
public sealed class ExternalAcquisitionIntent
{
    public string Provider { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public Song Song { get; set; } = new();
    public List<string> RequestedBy { get; set; } = [];
    public string Status { get; set; } = "pending";
    public string? AlbumForeignId { get; set; }
    public int? LidarrAlbumId { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? RetryAfterUtc { get; set; }
}

public sealed class ExternalAlbumSearch
{
    public string ForeignAlbumId { get; set; } = string.Empty;
    public string Status { get; set; } = "in-progress";
    public int? LidarrAlbumId { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ExternalSaveSnapshot
{
    public IReadOnlyList<ExternalSavedPlaylist> Playlists { get; init; } = [];
    public IReadOnlyList<ExternalSavedHeart> Hearts { get; init; } = [];
    public IReadOnlyList<ExternalHeartMutation> HeartMutations { get; init; } = [];
    public IReadOnlyList<ExternalSongAlias> Aliases { get; init; } = [];
    public IReadOnlyList<ExternalAcquisitionIntent> Acquisitions { get; init; } = [];
}

/// <summary>
/// Atomic JSON persistence for virtual playlist occurrences, hearts, metadata aliases and
/// acquisition work. Mutations write a flushed temporary file and rename it before publishing
/// the new in-memory state, so a failed write cannot claim a save succeeded.
/// </summary>
public sealed class ExternalSaveStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InterruptedWorkAge = TimeSpan.FromMinutes(10);

    private readonly string _path;
    private readonly ILogger<ExternalSaveStore> _logger;
    private readonly object _gate = new();
    private State _state;

    public ExternalSaveStore(string path, ILogger<ExternalSaveStore> logger)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A state path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _logger = logger;
        _state = Load();
    }

    public ExternalSaveSnapshot Snapshot()
    {
        lock (_gate)
            return new ExternalSaveSnapshot
            {
                Playlists = _state.Playlists.Select(Clone).ToList(),
                Hearts = _state.Hearts.Select(Clone).ToList(),
                HeartMutations = _state.HeartMutations.Select(Clone).ToList(),
                Aliases = _state.Aliases.Select(Clone).ToList(),
                Acquisitions = _state.Acquisitions.Select(Clone).ToList(),
            };
    }

    public ExternalSavedPlaylist? GetPlaylist(string userId, string playlistId)
    {
        lock (_gate)
            return _state.Playlists.FirstOrDefault(p => Same(p.UserId, userId) && Same(p.PlaylistId, playlistId)) is { } value
                ? Clone(value) : null;
    }

    public IReadOnlyList<ExternalSavedPlaylist> GetPlaylists(string userId)
    {
        lock (_gate)
            return _state.Playlists.Where(p => Same(p.UserId, userId))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(Clone).ToList();
    }

    public Song? GetSong(string songId)
    {
        lock (_gate)
        {
            var alias = _state.Aliases.FirstOrDefault(a => Same(a.AliasId, songId));
            if (alias?.Song is not null) return Clone(alias.Song);
            if (_state.Songs.TryGetValue(songId, out var song)) return Clone(song);
            return _state.Songs.TryGetValue(alias?.CanonicalId ?? string.Empty, out song) ? Clone(song) : null;
        }
    }

    /// <summary>Persist resolved Deezer IDs without changing saved ordering or user state.</summary>
    public Task<bool> RememberDeezerIdAsync(string songId, string deezerId)
    {
        Require(songId, nameof(songId));
        Require(deezerId, nameof(deezerId));
        lock (_gate)
        {
            var next = Clone(_state);
            var canonicalId = CanonicalSongIdLocked(next, songId);
            var changed = false;

            bool Matches(string? id) => !string.IsNullOrWhiteSpace(id)
                && (Same(id, songId) || Same(CanonicalSongIdLocked(next, id), canonicalId));

            bool Update(Song? song)
            {
                if (song is null || !Matches(SongId(song)) || Same(song.DeezerId, deezerId)) return false;
                song.DeezerId = deezerId;
                return true;
            }

            foreach (var (id, song) in next.Songs)
                if (Matches(id) || Matches(SongId(song))) changed |= Update(song);
            foreach (var playlist in next.Playlists)
                foreach (var track in playlist.Tracks)
                    if (Matches(track.SongId)) changed |= Update(track.Song);
            foreach (var heart in next.Hearts)
                if (Matches(heart.SongId)) changed |= Update(heart.Song);
            foreach (var mutation in next.HeartMutations)
                if (Matches(mutation.SongId)) changed |= Update(mutation.Song);
            foreach (var intent in next.Acquisitions)
                if (Matches(SongId(intent.Song)) || Same(intent.Provider, "deezer") && Same(intent.ExternalId, songId))
                    changed |= Update(intent.Song);
            foreach (var alias in next.Aliases)
                if (Matches(alias.AliasId) || Matches(alias.CanonicalId)) changed |= Update(alias.Song);

            if (!changed) return Task.FromResult(false);
            Save(next);
            _state = next;
            return Task.FromResult(true);
        }
    }

    public string CanonicalSongId(string songId)
    {
        lock (_gate)
            return _state.Aliases.FirstOrDefault(a => Same(a.AliasId, songId))?.CanonicalId ?? songId;
    }

    public ExternalSongAlias? ResolveAlias(string songId)
    {
        lock (_gate)
            return _state.Aliases.FirstOrDefault(a => Same(a.AliasId, songId)) is { } alias
                ? Clone(alias) : null;
    }

    public IReadOnlyList<ExternalSavedHeart> GetHearts(string userId)
    {
        lock (_gate)
            return _state.Hearts.Where(h => Same(h.UserId, userId)).Select(Clone).ToList();
    }

    public bool IsHearted(string userId, string songId)
    {
        lock (_gate)
            return _state.Hearts.Any(h => Same(h.UserId, userId)
                && (Same(h.SongId, songId) || Same(h.SongId, CanonicalSongIdLocked(_state, songId))));
    }

    public IReadOnlyList<ExternalHeartMutation> GetHeartMutations(string userId)
    {
        lock (_gate)
            return _state.HeartMutations.Where(m => m.PendingMirror && Same(m.UserId, userId))
                .Select(Clone).ToList();
    }

    /// <summary>Song IDs referenced by manual playlists or hearts; generated lists never enter this store.</summary>
    public IReadOnlyList<string> GetPinnedSongIds()
    {
        lock (_gate)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var heart in _state.Hearts)
                if (IsExternalSong(heart.Song) && !IsImportedLocked(heart.Song)) ids.Add(heart.SongId);
            foreach (var playlist in _state.Playlists)
                foreach (var track in playlist.Tracks)
                    if (track.Song is { } song && IsExternalSong(song) && !IsImportedLocked(song)) ids.Add(track.SongId);
            return ids.ToList();
        }
    }

    /// <summary>Replace ordered contents while retaining caller-supplied stable occurrence IDs.</summary>
    public Task<ExternalSavedPlaylist> UpsertPlaylistAsync(ExternalSavedPlaylist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        Require(playlist.UserId, nameof(playlist.UserId));
        Require(playlist.Id, nameof(playlist.Id));
        ExternalSavedPlaylist? committed = null;
        Mutate(state =>
        {
            var saved = Clone(playlist);
            saved.UpdatedUtc = DateTime.UtcNow;
            saved.PendingMirror = true;
            saved.Tracks ??= [];
            foreach (var track in saved.Tracks)
            {
                if (string.IsNullOrWhiteSpace(track.OccurrenceId)) track.OccurrenceId = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(track.SongId))
                    throw new ArgumentException("Every playlist occurrence needs a song ID.", nameof(playlist));
                var canonical = CanonicalSongIdLocked(state, track.SongId);
                if (!Same(canonical, track.SongId))
                {
                    track.CanonicalId = canonical;
                    track.SongId = canonical;
                    if (state.Songs.TryGetValue(canonical, out var canonicalSong)) track.Song = Clone(canonicalSong);
                }
                if (track.Song is not null)
                {
                    if (string.IsNullOrWhiteSpace(track.Song.Id)) track.Song.Id = track.SongId;
                    RememberSong(state, track.Song);
                    if (IsExternalSong(track.Song))
                        QueueIntentLocked(state, track.Song.ExternalProvider!, track.Song.ExternalId!, track.Song, saved.UserId);
                }
            }
            var index = state.Playlists.FindIndex(p => Same(p.UserId, saved.UserId) && Same(p.PlaylistId, saved.PlaylistId));
            if (index < 0) state.Playlists.Add(saved);
            else state.Playlists[index] = saved;
            committed = Clone(saved);
        });
        return Task.FromResult(committed!);
    }

    public Task RemovePlaylistAsync(string userId, string playlistId)
    {
        Mutate(state => state.Playlists.RemoveAll(p => Same(p.UserId, userId) && Same(p.PlaylistId, playlistId)));
        return Task.CompletedTask;
    }

    public Task MarkPlaylistMirroredAsync(string userId, string playlistId, DateTime? expectedUpdatedUtc = null)
    {
        Mutate(state =>
        {
            var playlist = state.Playlists.FirstOrDefault(p => Same(p.UserId, userId) && Same(p.PlaylistId, playlistId));
            if (playlist is not null && (expectedUpdatedUtc is null || playlist.UpdatedUtc == expectedUpdatedUtc))
                playlist.PendingMirror = false;
        });
        return Task.CompletedTask;
    }

    public Task SetHeartAsync(string userId, Song song, bool hearted)
    {
        Require(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(song);
        var songId = SongId(song);
        Require(songId, nameof(song));
        var savedSong = Clone(song);
        if (string.IsNullOrWhiteSpace(savedSong.Id)) savedSong.Id = songId;
        Mutate(state =>
        {
            var canonicalId = CanonicalSongIdLocked(state, songId);
            var canonicalSong = state.Songs.TryGetValue(canonicalId, out var existingSong)
                ? Clone(existingSong) : Clone(savedSong);
            canonicalSong.Id = canonicalId;
            if (hearted)
            {
                RememberSong(state, canonicalSong);
                if (IsExternalSong(canonicalSong))
                    QueueIntentLocked(state, canonicalSong.ExternalProvider!, canonicalSong.ExternalId!, canonicalSong, userId);
                var existing = state.Hearts.FirstOrDefault(h => Same(h.UserId, userId) && Same(h.SongId, canonicalId));
                if (existing is null)
                    state.Hearts.Add(new ExternalSavedHeart { UserId = userId, SongId = canonicalId, Song = Clone(canonicalSong), UpdatedUtc = DateTime.UtcNow });
                else
                {
                    existing.Song = Clone(canonicalSong);
                    existing.UpdatedUtc = DateTime.UtcNow;
                }
            }
            else
            {
                state.Hearts.RemoveAll(h => Same(h.UserId, userId)
                    && (Same(h.SongId, songId) || Same(h.SongId, canonicalId)));
            }
            var mutation = state.HeartMutations.FirstOrDefault(m => Same(m.UserId, userId) && Same(m.SongId, canonicalId));
            if (mutation is null)
            {
                mutation = new ExternalHeartMutation { UserId = userId, SongId = canonicalId };
                state.HeartMutations.Add(mutation);
            }
            mutation.Song = Clone(canonicalSong);
            mutation.Hearted = hearted;
            mutation.PendingMirror = true;
            mutation.UpdatedUtc = DateTime.UtcNow;
        });
        return Task.CompletedTask;
    }

    public Task MarkHeartMirroredAsync(string userId, string songId, DateTime? expectedUpdatedUtc = null)
    {
        Mutate(state =>
        {
            var mutation = state.HeartMutations.FirstOrDefault(m => Same(m.UserId, userId) && Same(m.SongId, songId));
            if (mutation is not null && (expectedUpdatedUtc is null || mutation.UpdatedUtc == expectedUpdatedUtc))
                mutation.PendingMirror = false;
        });
        return Task.CompletedTask;
    }

    public Task AddAliasAsync(string aliasId, string canonicalId, Song? song = null)
    {
        Require(aliasId, nameof(aliasId));
        Require(canonicalId, nameof(canonicalId));
        if (Same(aliasId, canonicalId)) return Task.CompletedTask;
        Mutate(state =>
        {
            var canonical = CanonicalSongIdLocked(state, canonicalId);
            var alias = state.Aliases.FirstOrDefault(a => Same(a.AliasId, aliasId));
            if (alias is null)
            {
                alias = new ExternalSongAlias { AliasId = aliasId };
                state.Aliases.Add(alias);
            }
            alias.CanonicalId = canonical;
            alias.Song = song is null ? null : Clone(song);
            if (song is not null) RememberSong(state, song);
        });
        return Task.CompletedTask;
    }

    /// <summary>Add/reuse acquisition request without storing caller credentials.</summary>
    public Task QueueAcquisitionAsync(string provider, string externalId, Song song, string? requestedBy)
    {
        Require(provider, nameof(provider));
        Require(externalId, nameof(externalId));
        ArgumentNullException.ThrowIfNull(song);
        Mutate(state =>
        {
            RememberSong(state, song);
            QueueIntentLocked(state, provider, externalId, song, requestedBy);
        });
        return Task.CompletedTask;
    }

    public IReadOnlyList<ExternalAcquisitionIntent> GetPendingAcquisitions()
    {
        lock (_gate)
            return _state.Acquisitions.Where(i => i.Status == "pending"
                    && (i.RetryAfterUtc is null || i.RetryAfterUtc <= DateTime.UtcNow)
                    && HasPinnedReference(_state, i))
                .Select(Clone).ToList();
    }

    public IReadOnlyList<ExternalAcquisitionIntent> GetSubmittedAcquisitions()
    {
        // "in-progress" survives a restart when Lidarr may already have accepted its
        // command but Octo had not yet persisted the response.
        lock (_gate) return _state.Acquisitions.Where(i => i.Status is "submitted" or "in-progress")
            .Select(Clone).ToList();
    }

    public IReadOnlyList<ExternalAcquisitionIntent> GetUnresolvedAcquisitions()
    {
        lock (_gate) return _state.Acquisitions.Where(i => i.Status != "imported" && HasPinnedReference(_state, i))
            .Select(Clone).ToList();
    }

    public Task<ExternalAcquisitionIntent?> TryClaimAcquisitionAsync(string provider, string externalId)
    {
        ExternalAcquisitionIntent? result = null;
        Mutate(state =>
        {
            var intent = FindAcquisition(state, provider, externalId);
            if (intent is null || intent.Status != "pending"
                || intent.RetryAfterUtc is { } retry && retry > DateTime.UtcNow) return;
            intent.Status = "in-progress";
            intent.UpdatedUtc = DateTime.UtcNow;
            intent.RetryAfterUtc = null;
            result = Clone(intent);
        });
        return Task.FromResult(result);
    }

    public Task MarkAcquisitionFailedAsync(string provider, string externalId, string error)
    {
        Mutate(state =>
        {
            var intent = FindAcquisition(state, provider, externalId);
            if (intent is null || intent.Status == "imported") return;
            intent.Status = "pending";
            intent.LastError = error;
            intent.RetryAfterUtc = DateTime.UtcNow + RetryDelay;
            intent.UpdatedUtc = DateTime.UtcNow;
        });
        return Task.CompletedTask;
    }

    /// <summary>Complete non-Lidarr acquisition handoff without losing import reconciliation.</summary>
    public Task MarkAcquisitionScheduledAsync(string provider, string externalId)
    {
        Mutate(state =>
        {
            var intent = FindAcquisition(state, provider, externalId);
            if (intent is null || intent.Status != "pending") return;
            intent.Status = "scheduled";
            intent.UpdatedUtc = DateTime.UtcNow;
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Requeue interrupted metadata resolution only when no durable AlbumSearch claim exists.
    /// An in-progress claim for an album is never replayed, so a restart cannot duplicate it.
    /// </summary>
    public Task<int> RecoverInterruptedAcquisitionsAsync()
    {
        var recovered = 0;
        Mutate(state =>
        {
            var cutoff = DateTime.UtcNow - InterruptedWorkAge;
            foreach (var intent in state.Acquisitions.Where(i => i.Status == "in-progress"
                && i.UpdatedUtc < cutoff))
            {
                var hasAlbumSearchClaim = !string.IsNullOrWhiteSpace(intent.AlbumForeignId)
                    && state.AlbumSearches.Any(s => Same(s.ForeignAlbumId, intent.AlbumForeignId)
                        && s.Status is "in-progress" or "submitted");
                if (hasAlbumSearchClaim) continue;
                intent.Status = "pending";
                intent.UpdatedUtc = DateTime.UtcNow;
                intent.RetryAfterUtc = null;
                recovered++;
            }
        });
        return Task.FromResult(recovered);
    }

    public Task AssociateAcquisitionWithAlbumAsync(string provider, string externalId, string foreignAlbumId)
    {
        Mutate(state =>
        {
            var intent = FindAcquisition(state, provider, externalId);
            if (intent is null) return;
            intent.AlbumForeignId = foreignAlbumId;
            intent.UpdatedUtc = DateTime.UtcNow;
        });
        return Task.CompletedTask;
    }

    /// <summary>Persist in-progress before calling Lidarr. This suppresses duplicate commands after a crash.</summary>
    public Task<bool> TryBeginAlbumSearchAsync(string foreignAlbumId)
    {
        var claimed = false;
        Mutate(state =>
        {
            var search = state.AlbumSearches.FirstOrDefault(s => Same(s.ForeignAlbumId, foreignAlbumId));
            if (search is { Status: "submitted" or "in-progress" }) return;
            if (search is null)
            {
                search = new ExternalAlbumSearch { ForeignAlbumId = foreignAlbumId };
                state.AlbumSearches.Add(search);
            }
            search.Status = "in-progress";
            search.LastError = null;
            search.UpdatedUtc = DateTime.UtcNow;
            claimed = true;
        });
        return Task.FromResult(claimed);
    }

    public ExternalAlbumSearch? GetAlbumSearch(string foreignAlbumId)
    {
        lock (_gate)
            return _state.AlbumSearches.FirstOrDefault(s => Same(s.ForeignAlbumId, foreignAlbumId)) is { } search
                ? Clone(search) : null;
    }

    public Task MarkAlbumSearchSubmittedAsync(string foreignAlbumId, int lidarrAlbumId)
    {
        Mutate(state =>
        {
            var search = GetOrCreateAlbumSearch(state, foreignAlbumId);
            search.Status = "submitted";
            search.LidarrAlbumId = lidarrAlbumId;
            search.LastError = null;
            search.UpdatedUtc = DateTime.UtcNow;
            foreach (var intent in state.Acquisitions.Where(i => Same(i.AlbumForeignId, foreignAlbumId)))
            {
                if (intent.Status == "imported") continue;
                intent.Status = "submitted";
                intent.LidarrAlbumId = lidarrAlbumId;
                intent.UpdatedUtc = DateTime.UtcNow;
            }
        });
        return Task.CompletedTask;
    }

    public Task MarkAlbumSearchFailedAsync(string foreignAlbumId, string error)
    {
        Mutate(state =>
        {
            var search = GetOrCreateAlbumSearch(state, foreignAlbumId);
            search.Status = "failed";
            search.LastError = error;
            search.UpdatedUtc = DateTime.UtcNow;
            foreach (var intent in state.Acquisitions.Where(i => Same(i.AlbumForeignId, foreignAlbumId)))
            {
                if (intent.Status == "imported") continue;
                intent.Status = "pending";
                intent.LastError = error;
                intent.RetryAfterUtc = DateTime.UtcNow + RetryDelay;
                intent.UpdatedUtc = DateTime.UtcNow;
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Replace promised occurrences only after an unambiguous lossless import was found.</summary>
    public Task<int> MarkImportedAsync(string provider, string externalId, string localId, string localPath)
    {
        if (!File.Exists(localPath) || !string.Equals(Path.GetExtension(localPath), ".flac", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(0);
        var replaced = 0;
        Mutate(state =>
        {
            var intent = FindAcquisition(state, provider, externalId);
            var oldSong = intent?.Song ?? state.Songs.Values.FirstOrDefault(s => Same(s.ExternalProvider, provider)
                && Same(s.ExternalId, externalId));
            if (oldSong is null) return;

            var local = Clone(oldSong);
            local.Id = localId;
            local.IsLocal = true;
            local.LocalPath = localPath;
            local.Suffix = Path.GetExtension(localPath).TrimStart('.').ToLowerInvariant();
            local.ExternalProvider = provider;
            local.ExternalId = externalId;
            RememberSong(state, local);
            AddAliasLocked(state, oldSong.Id, localId, local);
            if (!Same(oldSong.Id, externalId)) AddAliasLocked(state, externalId, localId, local);

            foreach (var playlist in state.Playlists)
            foreach (var track in playlist.Tracks)
            {
                if (!Same(track.SongId, oldSong.Id) && !Same(track.SongId, externalId)
                    && !(track.Song is { } song && Same(song.ExternalProvider, provider) && Same(song.ExternalId, externalId)))
                    continue;
                track.CanonicalId = localId;
                track.SongId = localId;
                track.Song = Clone(local);
                playlist.PendingMirror = true;
                playlist.UpdatedUtc = DateTime.UtcNow;
                replaced++;
            }

            foreach (var heart in state.Hearts.Where(h => Same(h.SongId, oldSong.Id) || Same(h.SongId, externalId)))
            {
                heart.SongId = localId;
                heart.Song = Clone(local);
                heart.UpdatedUtc = DateTime.UtcNow;
                var mutation = state.HeartMutations.FirstOrDefault(m => Same(m.UserId, heart.UserId)
                    && (Same(m.SongId, oldSong.Id) || Same(m.SongId, externalId) || Same(m.SongId, localId)));
                if (mutation is null)
                {
                    mutation = new ExternalHeartMutation { UserId = heart.UserId, SongId = localId };
                    state.HeartMutations.Add(mutation);
                }
                mutation.SongId = localId;
                mutation.Song = Clone(local);
                mutation.Hearted = true;
                mutation.PendingMirror = true;
                mutation.UpdatedUtc = DateTime.UtcNow;
            }
            foreach (var mutation in state.HeartMutations.Where(m => Same(m.SongId, oldSong.Id)
                         || Same(m.SongId, externalId)))
            {
                mutation.SongId = localId;
                mutation.Song = Clone(local);
                mutation.PendingMirror = true;
                mutation.UpdatedUtc = DateTime.UtcNow;
            }

            if (intent is not null)
            {
                intent.Status = "imported";
                intent.UpdatedUtc = DateTime.UtcNow;
                intent.LastError = null;
                intent.RetryAfterUtc = null;
            }
        });
        return Task.FromResult(replaced);
    }

    private State Load()
    {
        if (!File.Exists(_path)) return new State();
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Json) ?? new State();
            state.Songs = new Dictionary<string, Song>(state.Songs, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError("External save state could not be read from {Path}: {Message}", _path, ex.Message);
            throw new InvalidDataException($"External save state is invalid: {_path}", ex);
        }
    }

    private void Mutate(Action<State> mutation)
    {
        lock (_gate)
        {
            var next = Clone(_state);
            mutation(next);
            Save(next);
            _state = next;
        }
    }

    private void Save(State state)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { /* preserve original write error */ }
            throw;
        }
    }

    private static void RememberSong(State state, Song song)
    {
        var id = SongId(song);
        if (string.IsNullOrWhiteSpace(id)) return;
        var copy = Clone(song);
        copy.Id = id;
        state.Songs[id] = copy;
    }

    private static string SongId(Song song) => !string.IsNullOrWhiteSpace(song.Id) ? song.Id
        : !string.IsNullOrWhiteSpace(song.ExternalProvider) && !string.IsNullOrWhiteSpace(song.ExternalId)
            ? $"ext-{song.ExternalProvider}-{song.ExternalId}" : string.Empty;

    private static bool IsExternalSong(Song? song) => song is not null
        && !string.IsNullOrWhiteSpace(song.ExternalProvider) && !string.IsNullOrWhiteSpace(song.ExternalId);

    private static string CanonicalSongIdLocked(State state, string songId)
    {
        var current = songId;
        for (var i = 0; i < state.Aliases.Count; i++)
        {
            var next = state.Aliases.FirstOrDefault(a => Same(a.AliasId, current))?.CanonicalId;
            if (string.IsNullOrWhiteSpace(next) || Same(next, current)) break;
            current = next;
        }
        return current;
    }

    private static void AddAliasLocked(State state, string aliasId, string canonicalId, Song? song)
    {
        if (string.IsNullOrWhiteSpace(aliasId) || string.IsNullOrWhiteSpace(canonicalId) || Same(aliasId, canonicalId)) return;
        var alias = state.Aliases.FirstOrDefault(a => Same(a.AliasId, aliasId));
        if (alias is null)
        {
            alias = new ExternalSongAlias { AliasId = aliasId };
            state.Aliases.Add(alias);
        }
        alias.CanonicalId = CanonicalSongIdLocked(state, canonicalId);
        alias.Song = song is null ? null : Clone(song);
    }

    private static ExternalAcquisitionIntent? FindAcquisition(State state, string provider, string externalId) =>
        state.Acquisitions.FirstOrDefault(i => Same(i.Provider, provider) && Same(i.ExternalId, externalId));

    private static void QueueIntentLocked(State state, string provider, string externalId, Song song, string? requestedBy)
    {
        var intent = FindAcquisition(state, provider, externalId);
        if (intent is null)
        {
            intent = new ExternalAcquisitionIntent
            {
                Provider = provider,
                ExternalId = externalId,
                Song = NormalizeSong(song, externalId),
                Status = "pending",
                CreatedUtc = DateTime.UtcNow,
            };
            state.Acquisitions.Add(intent);
        }
        else
        {
            intent.Song = NormalizeSong(song, externalId);
            if (intent.Status == "failed")
            {
                intent.Status = "pending";
                intent.RetryAfterUtc = null;
                intent.LastError = null;
            }
        }
        if (!string.IsNullOrWhiteSpace(requestedBy)
            && !intent.RequestedBy.Contains(requestedBy.Trim(), StringComparer.OrdinalIgnoreCase))
            intent.RequestedBy.Add(requestedBy.Trim());
        intent.UpdatedUtc = DateTime.UtcNow;
    }

    private static bool HasPinnedReference(State state, ExternalAcquisitionIntent intent) =>
        state.Hearts.Any(h => Same(h.Song.ExternalProvider, intent.Provider) && Same(h.Song.ExternalId, intent.ExternalId))
        || state.Playlists.Any(p => p.Tracks.Any(t => IsExternalSong(t.Song)
            && Same(t.Song!.ExternalProvider, intent.Provider) && Same(t.Song.ExternalId, intent.ExternalId)));

    private static ExternalAlbumSearch GetOrCreateAlbumSearch(State state, string foreignAlbumId)
    {
        var value = state.AlbumSearches.FirstOrDefault(s => Same(s.ForeignAlbumId, foreignAlbumId));
        if (value is not null) return value;
        value = new ExternalAlbumSearch { ForeignAlbumId = foreignAlbumId };
        state.AlbumSearches.Add(value);
        return value;
    }

    private static void Require(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", parameter);
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;

    private static State Clone(State value)
    {
        var copy = Clone<State>(value);
        copy.Songs = new Dictionary<string, Song>(copy.Songs, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    private static Song NormalizeSong(Song song, string externalId)
    {
        var copy = Clone(song);
        if (string.IsNullOrWhiteSpace(copy.Id))
            copy.Id = !string.IsNullOrWhiteSpace(copy.ExternalProvider)
                ? $"ext-{copy.ExternalProvider}-{externalId}" : externalId;
        return copy;
    }

    private bool IsImportedLocked(Song song) =>
        FindAcquisition(_state, song.ExternalProvider ?? string.Empty, song.ExternalId ?? string.Empty)?.Status == "imported";

    private sealed class State
    {
        public int Version { get; set; } = 1;
        public List<ExternalSavedPlaylist> Playlists { get; set; } = [];
        public List<ExternalSavedHeart> Hearts { get; set; } = [];
        public List<ExternalHeartMutation> HeartMutations { get; set; } = [];
        public Dictionary<string, Song> Songs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ExternalSongAlias> Aliases { get; set; } = [];
        public List<ExternalAcquisitionIntent> Acquisitions { get; set; } = [];
        public List<ExternalAlbumSearch> AlbumSearches { get; set; } = [];
    }
}
