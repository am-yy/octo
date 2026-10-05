using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;

namespace Octo.Services.Trackers;

public sealed record HandoffResult(string State, string? Error = null, int? AlbumId = null, int? CommandId = null);

/// <summary>Verifies prepared payload copies, registers official torrents with qBittorrent,
/// and imports a separate listening copy through Lidarr's manual import command.</summary>
public sealed class SalmonMediaHandoff
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> JobLocks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SourceLocks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<LidarrSettings> _lidarrSettings;
    private readonly string _root;

    public SalmonMediaHandoff(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IOptionsMonitor<LidarrSettings> lidarrSettings)
    {
        _httpFactory = httpClientFactory;
        _configuration = configuration;
        _lidarrSettings = lidarrSettings;
        _root = Path.GetFullPath(configuration["Salmon:Root"] ?? "/hangar/torrent-downloads");
    }

    /// <summary>Force a full qBittorrent recheck of a completed source torrent. Restores its prior run state.</summary>
    public async Task<bool> VerifySourceAsync(string infoHash, CancellationToken ct = default) =>
        (await VerifySourceCoreAsync(infoHash, exportTorrent: false, ct)).Verified;

    /// <summary>Rechecks source torrent, then returns qBittorrent's stored metainfo for CLI piece verification.</summary>
    public async Task<byte[]?> GetVerifiedSourceTorrentAsync(string infoHash, CancellationToken ct = default) =>
        (await VerifySourceCoreAsync(infoHash, exportTorrent: true, ct)).Torrent;

    private async Task<(bool Verified, byte[]? Torrent)> VerifySourceCoreAsync(string infoHash, bool exportTorrent, CancellationToken ct)
    {
        if (!ValidHash(infoHash)) return (false, null);
        var stateDir = Path.Combine(_root, "salmon-state", "source-rechecks");
        var statePath = Path.Combine(stateDir, infoHash.ToLowerInvariant() + ".json");
        var sourceLock = SourceLocks.GetOrAdd(statePath, _ => new SemaphoreSlim(1, 1));
        await sourceLock.WaitAsync(ct);
        SourceRecheckState? restoreState = null;
        try
        {
            Directory.CreateDirectory(stateDir);
            SetPrivateDirectory(stateDir);
            restoreState = await ReadSourceRecheckAsync(statePath, ct);
            if (restoreState is not null && (restoreState.Version != 1 || !restoreState.InfoHash.Equals(infoHash, StringComparison.OrdinalIgnoreCase)))
            {
                restoreState = null;
                return (false, null);
            }
            using var qb = await QbSession.ConnectAsync(_httpFactory, _configuration, ct);
            var row = await qb.GetTorrentAsync(infoHash, ct);
            if (row is null) return (false, null);
            var state = restoreState;
            if (state is null)
            {
                if (IsChecking(String(row, "state")) || Number(row, "progress") < 1) return (false, null);
                state = new SourceRecheckState
                {
                    Version = 1,
                    InfoHash = infoHash.ToLowerInvariant(),
                    WasRunning = IsRunning(String(row, "state")),
                    Phase = "pending",
                };
                await WriteSourceRecheckAsync(statePath, state, ct);
            }
            restoreState = state;

            if (state.Phase == "verified")
            {
                if (IsChecking(String(row, "state")) && !await qb.WaitForRecheckAsync(infoHash, ct)) return (false, null);
                row = await qb.GetTorrentAsync(infoHash, ct);
                if (row is null) return (false, null);
                if (Number(row, "progress") < 1 || !await qb.AllFilesCompleteAsync(infoHash, ct))
                {
                    state.Phase = "failed";
                    state.RecheckIssued = false;
                    state.RecheckConfirmed = false;
                    await WriteSourceRecheckAsync(statePath, state, ct);
                    return (false, null);
                }
                return (true, exportTorrent ? await qb.ExportTorrentAsync(infoHash, ct) : null);
            }

            // Incomplete input cannot prepare. A later call retries; finally restores source run intent.
            if (state.Phase == "failed")
            {
                state.Phase = "pending";
                state.RecheckIssued = false;
                state.RecheckConfirmed = false;
                state.WasRunning = IsRunning(String(row, "state"));
                await WriteSourceRecheckAsync(statePath, state, ct);
            }

            var sourceState = String(row, "state");
            var isChecking = IsChecking(sourceState);
            if (isChecking && !state.RecheckConfirmed && IsFullCheckingState(sourceState))
            {
                state.RecheckIssued = true;
                state.RecheckConfirmed = true;
                await WriteSourceRecheckAsync(statePath, state, ct);
            }
            else if (isChecking && !state.RecheckConfirmed)
            {
                if (!await qb.WaitForRecheckAsync(infoHash, ct)) return (false, null);
                row = await qb.GetTorrentAsync(infoHash, ct);
                if (row is null) return (false, null);
                sourceState = String(row, "state");
                isChecking = IsChecking(sourceState);
            }

            if (!isChecking && (!state.RecheckIssued || !state.RecheckConfirmed))
            {
                if (IsRunning(sourceState)) await qb.PostAsync("torrents/stop", ("hashes", infoHash), ct);
                state.Phase = "checking";
                state.RecheckIssued = true;
                state.RecheckConfirmed = false;
                await WriteSourceRecheckAsync(statePath, state, ct);
                await qb.PostAsync("torrents/recheck", ("hashes", infoHash), ct);
                state.RecheckConfirmed = true;
                await WriteSourceRecheckAsync(statePath, state, ct);
            }
            if (!state.RecheckConfirmed) return (false, null);
            if (!await qb.WaitForRecheckAsync(infoHash, ct))
            {
                row = await qb.GetTorrentAsync(infoHash, ct);
                if (row is not null && !IsChecking(String(row, "state")) && Number(row, "progress") < 1)
                {
                    state.Phase = "failed";
                    await WriteSourceRecheckAsync(statePath, state, ct);
                }
                return (false, null);
            }
            if (!await qb.AllFilesCompleteAsync(infoHash, ct))
            {
                state.Phase = "failed";
                await WriteSourceRecheckAsync(statePath, state, ct);
                return (false, null);
            }
            state.Phase = "verified";
            await WriteSourceRecheckAsync(statePath, state, ct);
            return (true, exportTorrent ? await qb.ExportTorrentAsync(infoHash, ct) : null);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (false, null); }
        finally
        {
            if (restoreState?.WasRunning == true)
            {
                try
                {
                    using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var restoreQb = await QbSession.ConnectAsync(_httpFactory, _configuration, restoreTimeout.Token);
                    var current = await restoreQb.GetTorrentAsync(infoHash, restoreTimeout.Token);
                    if (current is not null)
                    {
                        if (!IsRunning(String(current, "state")) || IsChecking(String(current, "state")))
                            await restoreQb.PostAsync("torrents/start", ("hashes", infoHash), restoreTimeout.Token);
                        restoreState.WasRunning = false;
                        await WriteSourceRecheckAsync(statePath, restoreState, restoreTimeout.Token);
                    }
                }
                catch { }
            }
            sourceLock.Release();
        }
    }

    /// <summary>Creates immutable tracker and independent listening copies, then registers and verifies
    /// the official torrent. Repeated calls resume from handoff.json and never blindly repeat add.</summary>
    public async Task<HandoffResult> SeedAsync(
        string jobId,
        string target,
        string rootName,
        string infoHash,
        byte[] officialTorrentBytes,
        IReadOnlyList<SalmonFile> files,
        CancellationToken ct = default)
    {
        if (!ValidComponent(jobId) || !ValidRootName(rootName) || !ValidHash(infoHash)
            || files is null || files.Count == 0 || officialTorrentBytes is null || officialTorrentBytes.Length == 0)
            return new("failed", "invalid_input");
        target = target?.Trim().ToLowerInvariant() ?? "";
        if (target is not ("red" or "ops")) return new("failed", "invalid_target");

        var jobLock = JobLocks.GetOrAdd(JobStatePath(jobId), _ => new SemaphoreSlim(1, 1));
        await jobLock.WaitAsync(ct);
        try
        {
            var normalizedFiles = ValidateManifest(files);
            var payloadDir = Path.Combine(_root, "music-prepared", jobId, rootName);
            var payloadDigest = await VerifyDirectoryAsync(payloadDir, normalizedFiles, ct);
            var torrent = new SalmonTorrent(officialTorrentBytes, target);
            if (!string.Equals(torrent.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(torrent.RootName, rootName, StringComparison.Ordinal)
                || torrent.Files.Count != normalizedFiles.Count
                || torrent.Files.Any(f => !normalizedFiles.Any(m => m.Path == f.Path && m.Size == f.Size)))
                return new("failed", "torrent_manifest_mismatch");

            var stateDir = Path.Combine(_root, "salmon-state", jobId);
            Directory.CreateDirectory(stateDir);
            SetPrivateDirectory(stateDir);
            var manifestPath = Path.Combine(stateDir, "manifest.json");
            await PersistManifestAsync(manifestPath, normalizedFiles, ct);
            var manifestDigest = Digest(JsonSerializer.SerializeToUtf8Bytes(normalizedFiles, JsonOptions));
            var officialPath = Path.Combine(stateDir, "official.torrent");
            await PersistArtifactAsync(officialPath, officialTorrentBytes, ct);

            var seedParent = Path.Combine(_root, "cross-seed", target, jobId);
            var seedDir = Path.Combine(seedParent, rootName);
            if (!await EnsureCopyAsync(payloadDir, seedDir, normalizedFiles, hardlink: true, ct))
                return new("failed", "copy_conflict");

            var savePath = Path.GetFullPath(seedParent);
            var category = target == "red" ? "salmon-red" : "salmon-ops";
            var tag = "salmon-job-" + SafeTag(jobId) + "-" + infoHash[..12].ToLowerInvariant();
            var trackersFingerprint = Digest(Encoding.UTF8.GetBytes(torrent.Announce));
            var handoffPath = Path.Combine(stateDir, "handoff.json");
            var state = await ReadHandoffAsync(handoffPath, ct) ?? new HandoffState
            {
                Version = 1,
                JobId = jobId,
                RootName = rootName,
                ManifestDigest = manifestDigest,
                PayloadDigest = payloadDigest,
            };
            if (state.Version != 1 || state.JobId != jobId || state.RootName != rootName
                || state.ManifestDigest != manifestDigest || state.PayloadDigest != payloadDigest)
                return new("failed", "handoff_state_conflict");
            if (state.Seed is not null && (state.Seed.Target != target || state.Seed.InfoHash != infoHash.ToLowerInvariant()
                || state.Seed.SavePath != savePath || state.Seed.TrackersFingerprint != trackersFingerprint))
                return new("failed", "seed_intent_conflict");
            state.Seed ??= new SeedIntent
            {
                Target = target,
                Category = category,
                InfoHash = infoHash.ToLowerInvariant(),
                SavePath = savePath,
                TrackersFingerprint = trackersFingerprint,
                Tag = tag,
                Phase = "addPending",
                UpdatedUtc = DateTime.UtcNow,
            };
            if (state.Seed.Phase == "unknown") return new("unknown", "add_outcome_unknown");
            var wasSeeded = state.Seed.Phase == "seeded";

            // Intent reaches durable storage before category/tag creation or add POST.
            await WriteHandoffAsync(handoffPath, state, ct);
            using var qb = await QbSession.ConnectAsync(_httpFactory, _configuration, ct);
            await qb.EnsureCategoryAndTagAsync(category, tag, ct);
            var existing = await qb.GetTorrentAsync(infoHash, ct);
            if (existing is null)
            {
                if (state.Seed.AddAttempted)
                    return wasSeeded ? new("failed", "registered_torrent_missing") : new("unknown", "add_outcome_unknown");
                state.Seed.AddAttempted = true;
                state.Seed.UpdatedUtc = DateTime.UtcNow;
                await WriteHandoffAsync(handoffPath, state, ct);
                try
                {
                    await qb.AddStoppedAsync(officialTorrentBytes, savePath, category, tag, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // qBittorrent may commit the add before a proxy drops its response.
                }
                existing = await qb.WaitForTorrentAsync(infoHash, ct);
                if (existing is null) return new("unknown", "add_outcome_unknown");
            }
            if (!await qb.MatchesOwnedTorrentAsync(existing, state.Seed, rootName, [torrent.Announce], ct))
                return new("failed", "duplicate_torrent_conflict");

            if (wasSeeded)
            {
                if (IsChecking(String(existing, "state")))
                {
                    if (!await qb.WaitForRecheckAsync(infoHash, ct)) return new("pending", "recheck_pending");
                    existing = await qb.GetTorrentAsync(infoHash, ct);
                    if (existing is null) return new("failed", "registered_torrent_missing");
                }
                if (Number(existing, "progress") < 1 || !await qb.AllFilesCompleteAsync(infoHash, ct))
                    return new("failed", "registered_torrent_incomplete");
                if (!IsRunning(String(existing, "state")))
                {
                    await qb.PostAsync("torrents/setShareLimits",
                        ("hashes", infoHash), ("ratioLimit", "-1"), ("seedingTimeLimit", "-1"), ("inactiveSeedingTimeLimit", "-1"), ct);
                    await qb.PostAsync("torrents/start", ("hashes", infoHash), ct);
                }
                return new("seeding");
            }

            state.Seed.Phase = "recheckPending";
            state.Seed.UpdatedUtc = DateTime.UtcNow;
            var existingState = String(existing, "state");
            var existingIsChecking = IsChecking(existingState);
            if (existingIsChecking && !state.Seed.RecheckConfirmed && IsFullCheckingState(existingState))
            {
                state.Seed.RecheckIssued = true;
                state.Seed.RecheckConfirmed = true;
                state.Seed.UpdatedUtc = DateTime.UtcNow;
                await WriteHandoffAsync(handoffPath, state, ct);
            }
            else if (existingIsChecking && !state.Seed.RecheckConfirmed)
            {
                // checkingResumeData is not proof of a full piece recheck. Let it finish first.
                if (!await qb.WaitForRecheckAsync(infoHash, ct)) return new("pending", "recheck_pending");
                existing = await qb.GetTorrentAsync(infoHash, ct);
                if (existing is null) return new("failed", "registered_torrent_missing");
                existingState = String(existing, "state");
                existingIsChecking = IsChecking(existingState);
            }

            if (!existingIsChecking && (!state.Seed.RecheckIssued || !state.Seed.RecheckConfirmed))
            {
                await WriteHandoffAsync(handoffPath, state, ct);
                if (IsRunning(existingState))
                    await qb.PostAsync("torrents/stop", ("hashes", infoHash), ct);
                // Persist intent before POST. Retries confirm qB state before repeating ambiguous requests.
                state.Seed.RecheckIssued = true;
                state.Seed.RecheckConfirmed = false;
                state.Seed.UpdatedUtc = DateTime.UtcNow;
                await WriteHandoffAsync(handoffPath, state, ct);
                await qb.PostAsync("torrents/recheck", ("hashes", infoHash), ct);
                state.Seed.RecheckConfirmed = true;
                state.Seed.UpdatedUtc = DateTime.UtcNow;
                await WriteHandoffAsync(handoffPath, state, ct);
            }
            if (!state.Seed.RecheckConfirmed) return new("pending", "recheck_pending");
            if (!await qb.WaitForRecheckAsync(infoHash, ct))
            {
                var checkedTorrent = await qb.GetTorrentAsync(infoHash, ct);
                if (checkedTorrent is null || IsChecking(String(checkedTorrent, "state")))
                    return new("pending", "recheck_pending");
                return await IncompleteRecheckAsync();
            }
            if (!await qb.AllFilesCompleteAsync(infoHash, ct)) return await IncompleteRecheckAsync();
            await qb.PostAsync("torrents/setShareLimits",
                ("hashes", infoHash), ("ratioLimit", "-1"), ("seedingTimeLimit", "-1"), ("inactiveSeedingTimeLimit", "-1"), ct);
            await qb.PostAsync("torrents/start", ("hashes", infoHash), ct);
            state.Seed.Phase = "seeded";
            state.Seed.UpdatedUtc = DateTime.UtcNow;
            await WriteHandoffAsync(handoffPath, state, ct);
            return new("seeding");

            async Task<HandoffResult> IncompleteRecheckAsync()
            {
                state.Seed.RecheckIssued = false;
                state.Seed.RecheckConfirmed = false;
                state.Seed.UpdatedUtc = DateTime.UtcNow;
                await WriteHandoffAsync(handoffPath, state, ct);
                return new("failed", "torrent_files_incomplete");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (RemoteFailure) { return new("pending", "tracker_registration_pending"); }
        catch (InvalidDataException) { return new("failed", "payload_invalid"); }
        catch (IOException) { return new("failed", "filesystem_conflict"); }
        catch { return new("failed", "handoff_failed"); }
        finally { jobLock.Release(); }
    }

    /// <summary>Imports frozen listening copy using Lidarr's selected release and ManualImport command.
    /// A command intent with unknown POST outcome is reconciled and never submitted twice.</summary>
    public async Task<HandoffResult> ImportAsync(
        string jobId,
        string rootName,
        string artist,
        string album,
        string releaseId,
        CancellationToken ct = default)
    {
        if (!ValidComponent(jobId) || !ValidRootName(rootName)
            || string.IsNullOrWhiteSpace(artist) && !Guid.TryParse(releaseId, out _)
            || string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(releaseId) || releaseId.Length > 512
            || releaseId.Any(char.IsControl))
            return new("failed", "invalid_input");
        var jobLock = JobLocks.GetOrAdd(JobStatePath(jobId), _ => new SemaphoreSlim(1, 1));
        await jobLock.WaitAsync(ct);
        try
        {
            var stateDir = Path.Combine(_root, "salmon-state", jobId);
            var manifestPath = Path.Combine(stateDir, "manifest.json");
            var manifest = await ReadManifestAsync(manifestPath, ct);
            if (manifest is null || manifest.Count == 0) return new("failed", "manifest_missing");
            var normalized = ValidateManifest(manifest);
            var importDir = Path.Combine(_root, "salmon-import", jobId, rootName);
            var handoffPath = Path.Combine(stateDir, "handoff.json");
            var state = await ReadHandoffAsync(handoffPath, ct);
            if (state is null || state.JobId != jobId || state.RootName != rootName) return new("failed", "handoff_state_missing");
            if (state.Import is not null)
            {
                if (state.Import.ReleaseId != releaseId || state.Import.Artist != artist || state.Import.Album != album)
                    return new("failed", "import_intent_conflict");
                if (state.Import.Phase == "imported") return new("imported", AlbumId: state.Import.AlbumId);
            }
            var audioFiles = normalized.Where(f => f.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)).ToList();
            if (audioFiles.Count == 0) return new("failed", "audio_manifest_missing");
            if (state.Import is null)
            {
                var payloadDir = Path.Combine(_root, "music-prepared", jobId, rootName);
                if (!await EnsureCopyAsync(payloadDir, importDir, normalized, hardlink: false, ct))
                    return new("failed", "copy_conflict");
                _ = await VerifyDirectoryAsync(importDir, normalized, ct);
            }

            var settings = _lidarrSettings.CurrentValue;
            var baseUrl = settings.BaseUrl?.TrimEnd('/');
            var apiKey = settings.ApiKey;
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return new("failed", "lidarr_not_configured");
            using var lidarr = new LidarrSession(_httpFactory, baseUrl, apiKey);

            if (state.Import is { Phase: "commandPending" or "unknown" } pending)
            {
                var poll = "pending";
                if (pending.CommandId is int commandId)
                {
                    poll = await lidarr.WaitForCommandAsync(commandId, ct);
                    if (poll == "pending") return new("pending", "import_command_pending", pending.AlbumId, commandId);
                }
                var reconciled = await lidarr.VerifyImportedAsync(pending.AlbumId, pending.ArtistPath, pending.TrackIds, audioFiles.Count, ct);
                if (reconciled)
                {
                    pending.Phase = "imported";
                    pending.UpdatedUtc = DateTime.UtcNow;
                    await WriteHandoffAsync(handoffPath, state, ct);
                    return new("imported", AlbumId: pending.AlbumId, CommandId: pending.CommandId);
                }
                if (pending.Phase == "unknown" || pending.CommandId is null)
                    return new("unknown", "import_outcome_unknown", pending.AlbumId);
                var importedIds = await lidarr.GetImportedTrackIdsAsync(pending.AlbumId, pending.ArtistPath, ct);
                if (importedIds.Overlaps(pending.TrackIds)
                    || !await VerifyListedFilesAsync(importDir, audioFiles, ct))
                    return new("failed", poll == "failed" ? "import_command_failed" : "import_not_verified", pending.AlbumId, pending.CommandId);
                // Known finished command, no tracks imported, and every audio file remains intact: safe retry.
                state.Import = null;
                await WriteHandoffAsync(handoffPath, state, ct);
            }

            var resolved = await lidarr.ResolveReleaseAsync(artist, album, releaseId, ct);
            if (resolved is null) return new("pending", "release_unresolved");
            var root = Path.GetFullPath(Path.Combine(_root, "media", "music"));
            if (!Directory.Exists(root) || !await lidarr.RootFolderExistsAsync(root, ct))
                return new("failed", "music_root_unavailable");
            var artistPath = await lidarr.GetArtistPathAsync(resolved.ArtistId, root, ct);
            if (artistPath is null) return new("failed", "artist_path_invalid", resolved.AlbumId);
            var manual = await lidarr.MatchManualFilesAsync(importDir, audioFiles, resolved, ct);
            if (manual is null || manual.Files.Count != audioFiles.Count)
                return new("failed", "manual_import_ambiguous", resolved.AlbumId);

            var intent = new ImportIntent
            {
                Artist = artist,
                Album = album,
                ReleaseId = releaseId,
                ArtistId = resolved.ArtistId,
                AlbumId = resolved.AlbumId,
                AlbumReleaseId = resolved.AlbumReleaseId,
                ArtistPath = artistPath,
                TrackIds = manual.TrackIds.ToList(),
                Phase = "commandPending",
                UpdatedUtc = DateTime.UtcNow,
            };
            state.Import = intent;
            await WriteHandoffAsync(handoffPath, state, ct);
            int? returnedCommandId = null;
            try
            {
                returnedCommandId = await lidarr.StartManualImportAsync(manual.Files, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                intent.Phase = "unknown";
                intent.UpdatedUtc = DateTime.UtcNow;
                await WriteHandoffAsync(handoffPath, state, ct);
                return new("unknown", "import_outcome_unknown", intent.AlbumId);
            }
            if (returnedCommandId is null)
            {
                intent.Phase = "unknown";
                intent.UpdatedUtc = DateTime.UtcNow;
                await WriteHandoffAsync(handoffPath, state, ct);
                return new("unknown", "import_outcome_unknown", intent.AlbumId);
            }
            intent.CommandId = returnedCommandId;
            intent.UpdatedUtc = DateTime.UtcNow;
            await WriteHandoffAsync(handoffPath, state, ct);
            var commandState = await lidarr.WaitForCommandAsync(returnedCommandId.Value, ct);
            if (commandState == "pending") return new("pending", "import_command_pending", intent.AlbumId, returnedCommandId);
            if (commandState == "failed") return new("failed", "import_command_failed", intent.AlbumId, returnedCommandId);
            if (!await lidarr.VerifyImportedAsync(intent.AlbumId, artistPath, intent.TrackIds, audioFiles.Count, ct))
                return new("failed", "import_not_verified", intent.AlbumId, returnedCommandId);
            intent.Phase = "imported";
            intent.UpdatedUtc = DateTime.UtcNow;
            await WriteHandoffAsync(handoffPath, state, ct);
            return new("imported", AlbumId: intent.AlbumId, CommandId: returnedCommandId);
        }
        catch (OperationCanceledException) { throw; }
        catch (RemoteFailure) { return new("pending", "lidarr_request_pending"); }
        catch (InvalidDataException) { return new("failed", "manifest_invalid"); }
        catch (IOException) { return new("failed", "filesystem_conflict"); }
        catch { return new("failed", "import_failed"); }
        finally { jobLock.Release(); }
    }

    private string JobStatePath(string jobId) => Path.Combine(_root, "salmon-state", jobId, "handoff.json");

    private static bool ValidComponent(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 120 && value is not ("." or "..")
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool ValidRootName(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 180 && value is not ("." or "..")
        && !value.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*');

    private static bool ValidHash(string? value) => value is { Length: 40 }
        && value.All(Uri.IsHexDigit);

    private static string SafeTag(string jobId) => new(jobId.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    private static bool IsChecking(string? state) => state?.StartsWith("checking", StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsFullCheckingState(string? state) => state is not null
        && (state.Equals("checkingUP", StringComparison.OrdinalIgnoreCase)
            || state.Equals("checkingDL", StringComparison.OrdinalIgnoreCase));
    private static bool IsRunning(string? state) => state is not null
        && !state.StartsWith("paused", StringComparison.OrdinalIgnoreCase)
        && !state.StartsWith("stopped", StringComparison.OrdinalIgnoreCase)
        && !state.Equals("queuedDL", StringComparison.OrdinalIgnoreCase)
        && !state.Equals("queuedUP", StringComparison.OrdinalIgnoreCase);

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static List<SalmonFile> ValidateManifest(IReadOnlyList<SalmonFile> files)
    {
        var result = new List<SalmonFile>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var path = file.Path?.Replace('\\', '/') ?? "";
            if (path.Length == 0 || path.StartsWith('/') || path.Split('/').Any(p => p is "" or "." or "..")
                || path.Split('/').Any(p => p.Contains(':')) || file.Size <= 0
                || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit)
                || !seen.Add(path))
                throw new InvalidDataException("Invalid release manifest.");
            result.Add(new(path, file.Size, file.Sha256.ToLowerInvariant()));
        }
        return result.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private static bool ManifestEqual(IReadOnlyList<SalmonFile> left, IReadOnlyList<SalmonFile> right) =>
        left.Count == right.Count && left.OrderBy(f => f.Path, StringComparer.Ordinal)
            .Zip(right.OrderBy(f => f.Path, StringComparer.Ordinal))
            .All(pair => pair.First.Path == pair.Second.Path && pair.First.Size == pair.Second.Size
                && pair.First.Sha256.Equals(pair.Second.Sha256, StringComparison.OrdinalIgnoreCase));

    private static async Task<string> VerifyDirectoryAsync(string directory, IReadOnlyList<SalmonFile> files, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new IOException("Payload directory missing.");
        var expected = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var actual = EnumeratePayloadFiles(root).ToList();
        if (actual.Count != expected.Count) throw new InvalidDataException("Payload file set differs from manifest.");
        foreach (var path in actual)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Payload contains link.");
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!expected.Remove(relative)) throw new InvalidDataException("Payload contains unknown file.");
        }
        if (expected.Count != 0) throw new InvalidDataException("Payload is missing manifest files.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = CombineRelative(root, file.Path);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length != file.Size) throw new InvalidDataException("Payload size mismatch.");
            await using var stream = File.OpenRead(fullPath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (!actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Payload hash mismatch.");
            digest.AppendData(Encoding.UTF8.GetBytes(file.Path));
            digest.AppendData([0]);
            digest.AppendData(Encoding.UTF8.GetBytes(file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            digest.AppendData([0]);
            digest.AppendData(Convert.FromHexString(actualHash));
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<bool> VerifyListedFilesAsync(string directory, IReadOnlyList<SalmonFile> files, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var path = CombineRelative(Path.GetFullPath(directory), file.Path);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || new FileInfo(path).Length != file.Size) return false;
            await using var stream = File.OpenRead(path);
            if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static IEnumerable<string> EnumeratePayloadFiles(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Payload contains link.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var nested in EnumeratePayloadFiles(entry)) yield return nested;
            }
            else yield return entry;
        }
    }

    private static string CombineRelative(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(relative.Split('/'))));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Path escaped release root.");
        return full;
    }

    private static async Task<bool> EnsureCopyAsync(string source, string destination, IReadOnlyList<SalmonFile> files, bool hardlink, CancellationToken ct)
    {
        if (Directory.Exists(destination))
        {
            try { await VerifyDirectoryAsync(destination, files, ct); return true; }
            catch { return false; }
        }
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var src = CombineRelative(Path.GetFullPath(source), file.Path);
                var dst = CombineRelative(Path.GetFullPath(stage), file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!hardlink || !TryHardLink(dst, src)) File.Copy(src, dst, overwrite: false);
            }
            await VerifyDirectoryAsync(stage, files, ct);
            try { Directory.Move(stage, destination); }
            catch (IOException) when (Directory.Exists(destination))
            {
                await VerifyDirectoryAsync(destination, files, ct);
                Directory.Delete(stage, recursive: true);
            }
            return true;
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static bool TryHardLink(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try { return NativeLink(targetPath, linkPath) == 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
        return false;
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int NativeLink(string existingPath, string newPath);

    private static async Task PersistManifestAsync(string path, IReadOnlyList<SalmonFile> files, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(files, JsonOptions);
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, ct);
            var parsed = JsonSerializer.Deserialize<List<SalmonFile>>(existing, JsonOptions);
            if (parsed is null || !ManifestEqual(ValidateManifest(parsed), files)) throw new IOException("Manifest conflict.");
            SetPrivate(path);
            return;
        }
        await AtomicWriteAsync(path, bytes, ct);
    }

    private static async Task PersistArtifactAsync(string path, byte[] bytes, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            var old = await File.ReadAllBytesAsync(path, ct);
            if (!old.AsSpan().SequenceEqual(bytes)) throw new IOException("Torrent artifact conflict.");
            SetPrivate(path);
            return;
        }
        await AtomicWriteAsync(path, bytes, ct);
    }

    private static async Task<List<SalmonFile>?> ReadManifestAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<List<SalmonFile>>(await File.ReadAllBytesAsync(path, ct), JsonOptions);
    }

    private static async Task<HandoffState?> ReadHandoffAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<HandoffState>(await File.ReadAllBytesAsync(path, ct), JsonOptions);
    }

    private static async Task WriteHandoffAsync(string path, HandoffState state, CancellationToken ct) =>
        await AtomicWriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), ct);

    private static async Task<SourceRecheckState?> ReadSourceRecheckAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<SourceRecheckState>(await File.ReadAllBytesAsync(path, ct), JsonOptions);
    }

    private static async Task WriteSourceRecheckAsync(string path, SourceRecheckState state, CancellationToken ct) =>
        await AtomicWriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), ct);

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                SetPrivate(temp);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            SetPrivate(path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void SetPrivate(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void SetPrivateDirectory(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string String(JsonObject row, string key) => row[key]?.ToString() ?? "";
    private static double Number(JsonObject row, string key) => double.TryParse(row[key]?.ToString(), out var value) ? value : 0;
    private static int Int(JsonObject row, string key) => int.TryParse(row[key]?.ToString(), out var value) ? value : 0;
    private static bool IsSuccess(HttpResponseMessage response) => response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices;

    private sealed class HandoffState
    {
        public int Version { get; set; }
        public string JobId { get; set; } = "";
        public string RootName { get; set; } = "";
        public string ManifestDigest { get; set; } = "";
        public string PayloadDigest { get; set; } = "";
        public SeedIntent? Seed { get; set; }
        public ImportIntent? Import { get; set; }
    }

    private sealed class SourceRecheckState
    {
        public int Version { get; set; }
        public string InfoHash { get; set; } = "";
        public bool WasRunning { get; set; }
        public bool RecheckIssued { get; set; }
        public bool RecheckConfirmed { get; set; }
        public string Phase { get; set; } = "";
    }

    private sealed class SeedIntent
    {
        public string Target { get; set; } = "";
        public string Category { get; set; } = "";
        public string InfoHash { get; set; } = "";
        public string SavePath { get; set; } = "";
        public string TrackersFingerprint { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Phase { get; set; } = "";
        public bool AddAttempted { get; set; }
        public bool RecheckIssued { get; set; }
        public bool RecheckConfirmed { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class ImportIntent
    {
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string ReleaseId { get; set; } = "";
        public int ArtistId { get; set; }
        public int AlbumId { get; set; }
        public int AlbumReleaseId { get; set; }
        public string ArtistPath { get; set; } = "";
        public List<int> TrackIds { get; set; } = [];
        public int? CommandId { get; set; }
        public string Phase { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class RemoteFailure : Exception { }

    private sealed class QbSession : IDisposable
    {
        private readonly HttpClient _client;
        private readonly Uri _baseUri;
        private string? _cookie;

        private QbSession(HttpClient client, Uri baseUri) { _client = client; _baseUri = baseUri; }

        public static async Task<QbSession> ConnectAsync(IHttpClientFactory factory, IConfiguration configuration, CancellationToken ct)
        {
            var raw = configuration["Salmon:QbittorrentUrl"];
            var user = configuration["Salmon:QbittorrentUser"];
            var password = configuration["Salmon:QbittorrentPassword"];
            var hasCredentials = !string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(password);
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                || (hasCredentials && (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))))
                throw new RemoteFailure();
            var client = factory.CreateClient("SalmonMediaHandoff");
            var session = new QbSession(client, new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"));
            // With no credentials, qBittorrent must authorize API calls via its existing IP bypass.
            if (!hasCredentials) return session;
            using var request = session.NewRequest(HttpMethod.Post, "api/v2/auth/login");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user!, ["password"] = password! });
            using var response = await client.SendAsync(request, ct);
            if (!IsSuccess(response)) throw new RemoteFailure();
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                session._cookie = string.Join("; ", cookies.Select(c => c.Split(';', 2)[0]));
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Trim() != "Ok." || string.IsNullOrWhiteSpace(session._cookie)) throw new RemoteFailure();
            return session;
        }

        public void Dispose() => _client.Dispose();

        private HttpRequestMessage NewRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
            request.Headers.Referrer = _baseUri;
            request.Headers.TryAddWithoutValidation("Origin", _baseUri.GetLeftPart(UriPartial.Authority));
            if (_cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", _cookie);
            return request;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await _client.SendAsync(request, ct);
            if (!IsSuccess(response)) { response.Dispose(); throw new RemoteFailure(); }
            return response;
        }

        public async Task<string> GetTextAsync(string path, CancellationToken ct)
        {
            using var request = NewRequest(HttpMethod.Get, "api/v2/" + path);
            using var response = await SendAsync(request, ct);
            return await response.Content.ReadAsStringAsync(ct);
        }

        public async Task<JsonArray> GetArrayAsync(string path, CancellationToken ct)
        {
            try { return JsonNode.Parse(await GetTextAsync(path, ct)) as JsonArray ?? throw new RemoteFailure(); }
            catch (JsonException) { throw new RemoteFailure(); }
        }

        public async Task<JsonObject> GetObjectAsync(string path, CancellationToken ct)
        {
            try { return JsonNode.Parse(await GetTextAsync(path, ct)) as JsonObject ?? throw new RemoteFailure(); }
            catch (JsonException) { throw new RemoteFailure(); }
        }

        public async Task<byte[]?> ExportTorrentAsync(string hash, CancellationToken ct)
        {
            using var request = NewRequest(HttpMethod.Get, "api/v2/torrents/export?hash=" + Uri.EscapeDataString(hash));
            using var response = await SendAsync(request, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length == 0 ? null : bytes;
        }

        public async Task PostAsync(string endpoint, CancellationToken ct, params (string Key, string Value)[] fields)
        {
            using var request = NewRequest(HttpMethod.Post, "api/v2/" + endpoint);
            request.Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
            using var response = await SendAsync(request, ct);
            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (body.Length > 0 && !body.Equals("Ok.", StringComparison.OrdinalIgnoreCase))
                throw new RemoteFailure();
        }

        public async Task PostAsync(string endpoint, (string Key, string Value) first, CancellationToken ct) =>
            await PostAsync(endpoint, ct, first);

        public async Task PostAsync(string endpoint, (string Key, string Value) first, (string Key, string Value) second, (string Key, string Value) third, (string Key, string Value) fourth, CancellationToken ct) =>
            await PostAsync(endpoint, ct, first, second, third, fourth);

        public async Task<JsonObject?> GetTorrentAsync(string hash, CancellationToken ct)
        {
            var rows = await GetArrayAsync("torrents/info?hashes=" + Uri.EscapeDataString(hash), ct);
            if (rows.Count == 0) return null;
            if (rows.Count != 1 || rows[0] is not JsonObject row
                || !String(row, "hash").Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new RemoteFailure();
            return (JsonObject)row.DeepClone();
        }

        public async Task<JsonObject?> WaitForTorrentAsync(string hash, CancellationToken ct)
        {
            for (var i = 0; i < 8; i++)
            {
                var row = await GetTorrentAsync(hash, ct);
                if (row is not null) return row;
                if (i < 7) await Task.Delay(250, ct);
            }
            return null;
        }

        public async Task EnsureCategoryAndTagAsync(string category, string tag, CancellationToken ct)
        {
            var categories = await GetObjectAsync("torrents/categories", ct);
            if (categories[category] is null)
                await PostAsync("torrents/createCategory", ct, ("category", category));
            var tags = await GetArrayAsync("torrents/tags", ct);
            if (!tags.Any(x => x?.GetValue<string>() == tag))
                await PostAsync("torrents/createTags", ct, ("tags", tag));
        }

        public async Task AddStoppedAsync(byte[] torrent, string savePath, string category, string tag, CancellationToken ct)
        {
            using var request = NewRequest(HttpMethod.Post, "api/v2/torrents/add");
            using var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(torrent);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
            form.Add(content, "torrents", "official.torrent");
            form.Add(new StringContent(savePath), "savepath");
            form.Add(new StringContent(category), "category");
            form.Add(new StringContent(tag), "tags");
            form.Add(new StringContent("false"), "skip_checking");
            form.Add(new StringContent("true"), "stopped");
            form.Add(new StringContent("false"), "autoTMM");
            request.Content = form;
            using var response = await SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!body.Trim().Equals("Ok.", StringComparison.OrdinalIgnoreCase)) throw new RemoteFailure();
        }

        public async Task<bool> MatchesOwnedTorrentAsync(JsonObject row, SeedIntent intent, string rootName, IReadOnlyList<string> officialTrackers, CancellationToken ct)
        {
            if (!String(row, "hash").Equals(intent.InfoHash, StringComparison.OrdinalIgnoreCase)
                || !PathEquals(String(row, "save_path"), intent.SavePath)
                || String(row, "category") != intent.Category
                || !String(row, "name").Equals(rootName, StringComparison.Ordinal)) return false;
            var tags = String(row, "tags").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!tags.Contains(intent.Tag, StringComparer.Ordinal)) return false;
            var trackers = await GetArrayAsync("torrents/trackers?hash=" + Uri.EscapeDataString(intent.InfoHash), ct);
            var urls = new List<string>();
            foreach (var tracker in trackers)
            {
                if (tracker is not JsonObject trackerRow) return false;
                var url = String(trackerRow, "url");
                if (url.Length == 0) return false;
                if (!IsVirtualTracker(url)) urls.Add(url);
            }
            urls = urls.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var fingerprint = Digest(Encoding.UTF8.GetBytes(string.Join("\n", urls)));
            var expected = Digest(Encoding.UTF8.GetBytes(string.Join("\n", officialTrackers.Order(StringComparer.Ordinal))));
            return fingerprint == expected;
        }

        private static bool IsVirtualTracker(string url)
        {
            var value = url.Trim();
            return value.Equals("** [DHT] **", StringComparison.OrdinalIgnoreCase)
                || value.Equals("** [PeX] **", StringComparison.OrdinalIgnoreCase)
                || value.Equals("** [LSD] **", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<bool> WaitForRecheckAsync(string hash, CancellationToken ct)
        {
            for (var i = 0; i < 180; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = await GetTorrentAsync(hash, ct);
                if (row is null) return false;
                var state = String(row, "state");
                // qBittorrent 5.1.4 forceRecheck synchronously clears progress and enters checking
                // before returning; a tiny torrent may finish before this first poll.
                // https://raw.githubusercontent.com/qbittorrent/qBittorrent/release-5.1.4/src/base/bittorrent/torrentimpl.cpp
                if (!IsChecking(state)) return Number(row, "progress") >= 1;
                await Task.Delay(500, ct);
            }
            return false;
        }

        public async Task<bool> AllFilesCompleteAsync(string hash, CancellationToken ct)
        {
            var rows = await GetArrayAsync("torrents/files?hash=" + Uri.EscapeDataString(hash), ct);
            return rows.Count > 0 && rows.All(x => x is JsonObject row && Number(row, "progress") >= 1);
        }

        private static bool PathEquals(string left, string right) =>
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private sealed class LidarrSession : IDisposable
    {
        private readonly HttpClient _client;
        private readonly string _baseUrl;
        private readonly string _apiKey;

        public LidarrSession(IHttpClientFactory factory, string baseUrl, string apiKey)
        {
            _client = factory.CreateClient("SalmonMediaHandoff");
            _baseUrl = baseUrl;
            _apiKey = apiKey;
        }

        public void Dispose() => _client.Dispose();

        private HttpRequestMessage Request(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, _baseUrl + "/api/v1/" + path.TrimStart('/'));
            request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
            return request;
        }

        private async Task<string> GetTextAsync(string path, CancellationToken ct)
        {
            using var request = Request(HttpMethod.Get, path);
            using var response = await _client.SendAsync(request, ct);
            if (!IsSuccess(response)) throw new RemoteFailure();
            return await response.Content.ReadAsStringAsync(ct);
        }

        private async Task<JsonArray> GetArrayAsync(string path, CancellationToken ct)
        {
            try { return JsonNode.Parse(await GetTextAsync(path, ct)) as JsonArray ?? throw new RemoteFailure(); }
            catch (JsonException) { throw new RemoteFailure(); }
        }

        private async Task<JsonObject> GetObjectAsync(string path, CancellationToken ct)
        {
            try { return JsonNode.Parse(await GetTextAsync(path, ct)) as JsonObject ?? throw new RemoteFailure(); }
            catch (JsonException) { throw new RemoteFailure(); }
        }

        public async Task<ReleaseMatch?> ResolveReleaseAsync(string artist, string album, string releaseId, CancellationToken ct)
        {
            var hasGuidReleaseId = Guid.TryParse(releaseId, out var releaseUuid);
            // A release UUID identifies the edition independently of tracker credit names.
            var term = Uri.EscapeDataString(hasGuidReleaseId ? album : artist + " " + album);
            var rows = await GetArrayAsync("album/lookup?term=" + term, ct);
            var candidates = rows.OfType<JsonObject>().Where(row =>
                hasGuidReleaseId
                    ? row["releases"] is JsonArray rels && rels.OfType<JsonObject>().Any(r =>
                        Text(r, "foreignReleaseId").Equals(releaseUuid.ToString(), StringComparison.OrdinalIgnoreCase))
                    : ArtistMatches(row, artist) && (Canonical(Text(row, "title")) == Canonical(album)
                        || row["releases"] is JsonArray nonGuidReleases && nonGuidReleases.OfType<JsonObject>().Any(r =>
                            Canonical(Text(r, "title")) == Canonical(album))))
                .ToList();
            var groups = candidates.GroupBy(x => Text(x, "foreignAlbumId"), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key)).ToList();
            if (groups.Count != 1) return null;
            var foreignAlbumId = groups[0].Key;
            var managed = await GetArrayAsync("album?foreignAlbumId=" + Uri.EscapeDataString(foreignAlbumId), ct);
            var albumRows = managed.OfType<JsonObject>().Where(x => Text(x, "foreignAlbumId").Equals(foreignAlbumId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (albumRows.Count != 1) return null;
            var managedAlbum = albumRows[0];
            var albumId = Int(managedAlbum, "id");
            var artistId = Int(managedAlbum, "artistId");
            if (artistId <= 0 && managedAlbum["artist"] is JsonObject artistObj) artistId = Int(artistObj, "id");
            if (albumId <= 0 || artistId <= 0) return null;
            var detail = await GetObjectAsync("album/" + albumId, ct);
            var releases = (detail["releases"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var matchingReleases = hasGuidReleaseId
                ? releases.Where(x => Text(x, "foreignReleaseId").Equals(releaseUuid.ToString(), StringComparison.OrdinalIgnoreCase)).ToList()
                : releases;
            if (matchingReleases.Count != 1) return null;
            var internalReleaseId = Int(matchingReleases[0], "id");
            var trackCount = Int(matchingReleases[0], "trackCount");
            if (internalReleaseId <= 0 || trackCount <= 0) return null;
            return new(artistId, albumId, internalReleaseId, trackCount, foreignAlbumId);
        }

        public async Task<bool> RootFolderExistsAsync(string root, CancellationToken ct)
        {
            var rows = await GetArrayAsync("rootfolder", ct);
            return rows.OfType<JsonObject>().Any(x => PathEquals(Text(x, "path"), root));
        }

        public async Task<string?> GetArtistPathAsync(int artistId, string root, CancellationToken ct)
        {
            var artist = await GetObjectAsync("artist/" + artistId, ct);
            var path = Text(artist, "path");
            if (!PathWithin(path, root)) return null;
            return Path.GetFullPath(path);
        }

        public async Task<ManualMatch?> MatchManualFilesAsync(string folder, IReadOnlyList<SalmonFile> files, ReleaseMatch release, CancellationToken ct)
        {
            var query = "manualimport?folder=" + Uri.EscapeDataString(folder)
                + "&filterExistingFiles=false&replaceExistingFiles=false";
            var rows = await GetArrayAsync(query, ct);
            if (rows.Count != files.Count) return null;
            var expected = files.ToDictionary(f => Path.GetFullPath(CombineRelative(folder, f.Path)), PathComparer());
            var matches = new List<JsonObject>();
            var trackIds = new List<int>();
            foreach (var node in rows)
            {
                if (node is not JsonObject row) return null;
                var path = Text(row, "path");
                if (path.Length == 0 || !expected.Remove(Path.GetFullPath(path))) return null;
                if (row["rejections"] is JsonArray rejects && rejects.Count > 0) return null;
                if (row["artist"] is not JsonObject artist || Int(artist, "id") != release.ArtistId
                    || row["album"] is not JsonObject album || Int(album, "id") != release.AlbumId
                    || Int(row, "albumReleaseId") != release.AlbumReleaseId) return null;
                if (row["tracks"] is not JsonArray tracks || tracks.Count != 1) return null;
                var track = tracks[0] as JsonObject;
                var id = track is null ? 0 : Int(track, "id");
                if (id <= 0 || trackIds.Contains(id)) return null;
                trackIds.Add(id);
                row["_sourcePath"] = path;
                matches.Add(row);
            }
            if (expected.Count != 0 || matches.Count != release.TrackCount || trackIds.Count != release.TrackCount) return null;
            var updates = new List<JsonObject>();
            foreach (var row in matches)
            {
                var trackId = Int((row["tracks"] as JsonArray)![0] as JsonObject ?? new(), "id");
                var update = new JsonObject
                {
                    ["path"] = Text(row, "path"),
                    ["artistId"] = release.ArtistId,
                    ["albumId"] = release.AlbumId,
                    ["albumReleaseId"] = release.AlbumReleaseId,
                    ["trackIds"] = new JsonArray(trackId),
                    ["quality"] = row["quality"]?.DeepClone(),
                    ["releaseGroup"] = row["releaseGroup"]?.DeepClone(),
                    ["indexerFlags"] = row["indexerFlags"]?.DeepClone() ?? JsonValue.Create(0),
                    ["downloadId"] = row["downloadId"]?.DeepClone(),
                    ["disableReleaseSwitching"] = true,
                };
                updates.Add(update);
            }
            return new(updates, trackIds);
        }

        public async Task<int?> StartManualImportAsync(IReadOnlyList<JsonObject> files, CancellationToken ct)
        {
            using var request = Request(HttpMethod.Post, "command");
            request.Content = JsonContent.Create(new JsonObject
            {
                ["name"] = "ManualImport",
                ["importMode"] = "move",
                ["replaceExistingFiles"] = false,
                ["files"] = new JsonArray(files.Select(x => (JsonNode?)x.DeepClone()).ToArray()),
            });
            using var response = await _client.SendAsync(request, ct);
            if (!IsSuccess(response)) throw new RemoteFailure();
            try
            {
                var obj = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
                var id = obj is null ? 0 : Int(obj, "id");
                return id > 0 ? id : null;
            }
            catch (JsonException) { return null; }
        }

        public async Task<string> WaitForCommandAsync(int commandId, CancellationToken ct)
        {
            for (var i = 0; i < 120; i++)
            {
                var command = await GetObjectAsync("command/" + commandId, ct);
                var status = Text(command, "status");
                if (status.Equals("completed", StringComparison.OrdinalIgnoreCase)) return "completed";
                if (status.Equals("failed", StringComparison.OrdinalIgnoreCase) || status.Equals("aborted", StringComparison.OrdinalIgnoreCase)) return "failed";
                await Task.Delay(500, ct);
            }
            return "pending";
        }

        public async Task<bool> VerifyImportedAsync(int albumId, string artistPath, IReadOnlyList<int> trackIds, int expectedCount, CancellationToken ct)
        {
            var actualIds = await GetImportedTrackIdsAsync(albumId, artistPath, ct);
            return expectedCount > 0 && trackIds.Count == expectedCount && actualIds.Count == expectedCount
                && trackIds.All(actualIds.Contains);
        }

        public async Task<HashSet<int>> GetImportedTrackIdsAsync(int albumId, string artistPath, CancellationToken ct)
        {
            var files = await GetArrayAsync("trackFile?albumId=" + albumId, ct);
            var filesById = new Dictionary<int, string>();
            foreach (var node in files)
            {
                if (node is not JsonObject file) throw new RemoteFailure();
                var fileId = Int(file, "id");
                var path = Text(file, "path");
                if (fileId <= 0 || path.Length == 0) throw new RemoteFailure();
                if (!PathWithin(path, artistPath) || !File.Exists(path)) continue;
                filesById[fileId] = Path.GetFullPath(path);
            }

            // Lidarr TrackFileResource has no nested tracks. TrackResource.trackFileId is authoritative.
            var tracks = await GetArrayAsync("track?albumId=" + albumId, ct);
            var actualIds = new HashSet<int>();
            foreach (var node in tracks)
            {
                if (node is not JsonObject track) throw new RemoteFailure();
                var trackId = Int(track, "id");
                if (trackId <= 0) throw new RemoteFailure();
                var fileId = Int(track, "trackFileId");
                if (fileId > 0 && filesById.ContainsKey(fileId)) actualIds.Add(trackId);
            }
            return actualIds;
        }

        private static bool ArtistMatches(JsonObject row, string wanted)
        {
            var artist = row["artist"] as JsonObject;
            return artist is not null && Canonical(Text(artist, "artistName")) == Canonical(wanted);
        }

        private static string Canonical(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private sealed record ReleaseMatch(int ArtistId, int AlbumId, int AlbumReleaseId, int TrackCount, string ForeignAlbumId);
    private sealed record ManualMatch(IReadOnlyList<JsonObject> Files, IReadOnlyList<int> TrackIds);

    private static bool PathWithin(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool PathEquals(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Text(JsonObject obj, string key) => obj[key]?.ToString() ?? "";

}
