using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Metadata;
using Octo.Services.Soulseek;

namespace Octo.Services.Deezer;

public enum DeezerCachePriority
{
    Speculative = 0,
    Pinned = 1,
    Playback = 2,
}

/// <summary>A durable reference keeping one decoded Deezer FLAC in cache.</summary>
public sealed record DeezerCachePin(string TrackId, string ReferenceId,
    string Artist = "", string Title = "", string? Album = null, int? Duration = null);

/// <summary>One open cache reader. Dispose it after the HTTP response finishes.</summary>
public sealed class DeezerAudioLease : IDisposable, IAsyncDisposable
{
    private Action? _release;

    internal DeezerAudioLease(string path, FileStream stream, Action release)
    {
        Path = path;
        Stream = stream;
        _release = release;
    }

    public string Path { get; }
    public FileStream Stream { get; }

    public void Dispose()
    {
        try { Stream.Dispose(); }
        finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Stream.DisposeAsync(); }
        finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
    }
}

/// <summary>
/// Persistent, strict-FLAC playback cache. Caller cancellation only ends that caller's wait;
/// shared downloads have their own deadline and continue for other callers and future plays.
/// </summary>
public sealed class DeezerAudioCache : BackgroundService
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(5);
    private const string StateFileName = "cache-index.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true };

    private readonly DeezerResolver _resolver;
    private readonly DeezerMetadataService _catalog;
    private readonly ExternalIdRegistry _ids;
    private readonly IOptionsMonitor<DeezerSettings> _options;
    private readonly ILogger<DeezerAudioCache> _logger;
    private readonly string _root;
    private readonly string _staging;
    private readonly string _statePath;
    private readonly SingleFlight<string, string?> _downloads = new();
    private readonly PriorityTransferGate _transfers = new(2);
    private readonly ConcurrentDictionary<string, int> _activeReaders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activeTransfers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _queuedPins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _pinAttempts = new(StringComparer.Ordinal);
    private readonly Channel<string> _pinQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _stateWrite = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _readLock = new();
    private CacheDocument _state;
    private DateTime _lastCleanupUtc = DateTime.MinValue;

    public DeezerAudioCache(DeezerResolver resolver, DeezerMetadataService catalog,
        ExternalIdRegistry ids, IOptionsMonitor<DeezerSettings> options,
        ILogger<DeezerAudioCache> logger)
    {
        _resolver = resolver;
        _catalog = catalog;
        _ids = ids;
        _options = options;
        _logger = logger;
        var configured = options.CurrentValue.CachePath;
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "octo-cache", "deezer") : configured);
        _staging = Path.Combine(_root, ".staging");
        _statePath = Path.Combine(_root, StateFileName);
        _state = LoadState(_statePath);
    }

    public bool IsEnabled => _options.CurrentValue.CacheEnabled;
    public bool Enabled => IsEnabled;

    /// <summary>Finds a completed FLAC without changing its retention timestamp.</summary>
    public string? GetReadyPath(Song song)
    {
        if (!IsEnabled || !TryTrackId(song, out var trackId)) return null;
        var path = TrackPath(trackId);
        return IsValidFlac(path) ? path : null;
    }

    /// <summary>Gets a completed copy or waits for one shared strict-FLAC download.</summary>
    public async Task<string?> EnsureAsync(Song song,
        DeezerCachePriority priority = DeezerCachePriority.Playback,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) return null;
        var trackId = await ResolveTrackIdAsync(song, cancellationToken);
        return trackId is null ? null : await EnsureByTrackIdAsync(trackId, Snapshot(song), priority, cancellationToken);
    }

    /// <summary>Warms only the requested prefix. Known Deezer IDs are used directly.</summary>
    public async Task PrewarmAsync(IEnumerable<Song> songs, int topN,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || topN <= 0) return;
        // Search rows are shared and frozen; resolving speculative IDs must not mutate them.
        var selected = songs.Take(topN).Select(song => new Song
        {
            Id = song.Id, ExternalId = song.ExternalId, ExternalProvider = song.ExternalProvider,
            DeezerId = song.DeezerId, Artist = song.Artist, Title = song.Title,
            Album = song.Album, Duration = song.Duration,
        }).ToList();
        await Task.WhenAll(selected.Select(async song =>
        {
            try { await EnsureAsync(song, DeezerCachePriority.Speculative, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogDebug("Deezer cache prewarm failed for {Title}: {Type}",
                    song.Title, ex.GetType().Name);
            }
        }));
    }

    /// <summary>Opens a ready file and protects it from eviction until the lease is disposed.</summary>
    public DeezerAudioLease? OpenRead(Song song)
    {
        if (!IsEnabled || !TryTrackId(song, out var trackId)) return null;
        var path = TrackPath(trackId);
        lock (_readLock)
        {
            if (!IsValidFlac(path)) return null;
            _activeReaders.AddOrUpdate(trackId, 1, static (_, count) => count + 1);
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                return new DeezerAudioLease(path, stream, () => ReleaseReader(trackId));
            }
            catch
            {
                ReleaseReader(trackId);
                throw;
            }
        }
    }

    /// <summary>Stores real playback time. Cache reads, prefetch and range probes do not call this.</summary>
    public async Task MarkPlayedAsync(Song song, CancellationToken cancellationToken = default)
    {
        var trackId = await ResolveTrackIdAsync(song, cancellationToken);
        if (trackId is null) return;
        var metadata = Snapshot(song);
        await MutateStateAsync(document =>
        {
            var entry = GetEntry(document, trackId);
            entry.Metadata = metadata;
            entry.LastPlayedUtc = DateTime.UtcNow;
        }, cancellationToken);
    }

    /// <summary>Persists a heart or manually saved playlist reference, then queues protected fill.</summary>
    public async Task PinAsync(Song song, string referenceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(referenceId)) throw new ArgumentException("Reference ID is required.", nameof(referenceId));
        var trackId = await ResolveTrackIdAsync(song, cancellationToken)
            ?? throw new InvalidOperationException("No Deezer track ID is available to pin.");
        var metadata = Snapshot(song);
        await MutateStateAsync(document =>
        {
            var entry = GetEntry(document, trackId);
            entry.Metadata = metadata;
            entry.Pins[referenceId] = referenceId;
        }, cancellationToken);
        QueuePinnedFill(trackId);
    }

    public async Task UnpinAsync(string trackId, string referenceId,
        CancellationToken cancellationToken = default)
    {
        if (!ValidTrackId(trackId) || string.IsNullOrWhiteSpace(referenceId)) return;
        await MutateStateAsync(document =>
        {
            if (document.Tracks.TryGetValue(trackId, out var entry))
                entry.Pins.Remove(referenceId);
        }, cancellationToken);
    }

    /// <summary>Replaces all persisted external references from the durable playlist/heart store.</summary>
    public async Task ReplacePinsAsync(IEnumerable<DeezerCachePin> pins,
        CancellationToken cancellationToken = default)
    {
        var canonical = pins.Where(pin => ValidTrackId(pin.TrackId)
                && !string.IsNullOrWhiteSpace(pin.ReferenceId))
            .GroupBy(pin => pin.TrackId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        await MutateStateAsync(document =>
        {
            foreach (var entry in document.Tracks.Values) entry.Pins.Clear();
            foreach (var (trackId, references) in canonical)
            {
                var entry = GetEntry(document, trackId);
                foreach (var pin in references)
                {
                    entry.Pins[pin.ReferenceId] = pin.ReferenceId;
                    entry.Metadata = new SongSnapshot(pin.Artist, pin.Title, pin.Album, pin.Duration);
                }
            }
        }, cancellationToken);
        foreach (var trackId in canonical.Keys) QueuePinnedFill(trackId);
    }

    public async Task ReplacePinsAsync(IEnumerable<(Song Song, string ReferenceId)> pins,
        CancellationToken cancellationToken = default)
    {
        var resolved = new List<DeezerCachePin>();
        foreach (var (song, referenceId) in pins)
        {
            var trackId = await ResolveTrackIdAsync(song, cancellationToken);
            if (trackId is null) continue;
            var snapshot = Snapshot(song);
            resolved.Add(new DeezerCachePin(trackId, referenceId, snapshot.Artist,
                snapshot.Title, snapshot.Album, snapshot.Duration));
        }
        await ReplacePinsAsync(resolved, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RetryMissingPinsAsync(stoppingToken);
        await CleanupAsync(stoppingToken);
        using var timer = new PeriodicTimer(MaintenanceInterval);
        Task<bool>? nextTick = null;
        Task<bool>? nextPin = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            nextPin ??= _pinQueue.Reader.WaitToReadAsync(stoppingToken).AsTask();
            nextTick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
            var completed = await Task.WhenAny(nextPin, nextTick);
            if (completed == nextTick)
            {
                if (!await nextTick) break;
                nextTick = null;
                await RetryMissingPinsAsync(stoppingToken);
                if (DateTime.UtcNow - _lastCleanupUtc >= TimeSpan.FromHours(1))
                    await CleanupAsync(stoppingToken);
                continue;
            }

            if (!await nextPin) break;
            nextPin = null;
            while (_pinQueue.Reader.TryRead(out var trackId))
                _ = FillPinnedAsync(trackId, stoppingToken);
        }
    }

    private async Task FillPinnedAsync(string trackId, CancellationToken ct)
    {
        try
        {
            if (!HasPin(trackId) || IsValidFlac(TrackPath(trackId))) return;
            _pinAttempts[trackId] = DateTime.UtcNow;
            await EnsureByTrackIdAsync(trackId, GetMetadata(trackId), DeezerCachePriority.Pinned, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Pinned Deezer cache fill failed for {TrackId}: {Type}", trackId, ex.GetType().Name);
        }
        finally { _queuedPins.TryRemove(trackId, out _); }
    }

    private async Task RetryMissingPinsAsync(CancellationToken ct)
    {
        foreach (var trackId in PinnedTrackIds()) QueuePinnedFill(trackId);
        // Let queued work begin before maintenance starts its potentially larger disk scan.
        await Task.Yield();
    }

    private async Task<string?> EnsureByTrackIdAsync(string trackId, SongSnapshot? metadata,
        DeezerCachePriority priority, CancellationToken waiterToken)
    {
        if (!IsEnabled || !ValidTrackId(trackId)) return null;
        var path = TrackPath(trackId);
        if (IsValidFlac(path)) return path;

        if (priority > DeezerCachePriority.Speculative) _transfers.Promote(trackId, priority);
        var shared = _downloads.RunAsync(trackId,
            token => DownloadAsync(trackId, path, metadata, priority, token), DownloadTimeout);
        try { return await shared.WaitAsync(waiterToken); }
        catch (OperationCanceledException) when (waiterToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Deezer FLAC cache unavailable for {TrackId}: {Type}",
                trackId, ex.GetType().Name);
            return null;
        }
    }

    private async Task<string?> DownloadAsync(string trackId, string path, SongSnapshot? metadata,
        DeezerCachePriority priority, CancellationToken workToken)
    {
        if (IsValidFlac(path)) return path;
        Directory.CreateDirectory(_staging);
        try
        {
            using var transfer = await _transfers.AcquireAsync(trackId, priority, workToken);
            if (IsValidFlac(path)) return path;
            _activeTransfers.TryAdd(trackId, 0);
            var temporary = Path.Combine(_staging, $"{trackId}.{Guid.NewGuid():N}.tmp");
            try
            {
                var opened = await _resolver.OpenFlacStreamAsync(trackId, workToken);
                if (opened is null) return null;
                var (stream, contentType, expectedLength, _, _, owner) = opened.Value;
                using (owner)
                await using (stream)
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (!string.Equals(contentType, "audio/flac", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Deezer returned non-FLAC media for strict cache fill.");
                    await stream.CopyToAsync(file, workToken);
                    await file.FlushAsync(workToken);
                    if (file.Length == 0 || expectedLength is long expected && file.Length != expected)
                        throw new InvalidDataException("Deezer FLAC download was incomplete or invalid.");
                }
                if (!IsValidFlac(temporary) || !await ValidateWithFfmpegAsync(temporary, workToken))
                    throw new InvalidDataException("Deezer FLAC download was incomplete or invalid.");

                File.Move(temporary, path, overwrite: true);
                try
                {
                    await MutateStateAsync(document =>
                    {
                        var entry = GetEntry(document, trackId);
                        entry.Metadata ??= metadata;
                        entry.CompletedUtc = DateTime.UtcNow;
                    }, workToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Could not persist Deezer cache completion for {TrackId}: {Type}",
                        trackId, ex.GetType().Name);
                }
                await CleanupAsync(workToken);
                return path;
            }
            finally
            {
                _activeTransfers.TryRemove(trackId, out _);
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { /* staging cleanup is best effort; startup cleanup removes old partials */ }
            }
        }
        finally
        {
            _transfers.Forget(trackId);
        }
    }

    private async Task<string?> ResolveTrackIdAsync(Song song, CancellationToken ct)
    {
        var externalId = song.ExternalId ?? song.Id;
        if (ValidTrackId(song.DeezerId)) return song.DeezerId;
        if (IsDeezerProvider(song.ExternalProvider) && ValidTrackId(externalId))
        {
            song.DeezerId = externalId;
            return externalId;
        }

        var routing = _ids.Lookup(externalId) ?? SoulseekMetadataService.TryDecodeExternalId(externalId);
        if (ValidTrackId(routing?.DeezerId))
        {
            song.DeezerId = routing!.DeezerId;
            return routing.DeezerId;
        }

        if (string.IsNullOrWhiteSpace(song.Artist) && string.IsNullOrWhiteSpace(song.Title)) return null;
        var hit = await _catalog.EnrichTrackAsync(song.Artist, song.Title, includeYear: false, ct: ct);
        if (!ValidTrackId(hit?.DeezerId)) return null;
        song.DeezerId = hit!.DeezerId;
        _ids.RememberDeezerTrack(externalId, hit.DeezerId);
        return hit.DeezerId;
    }

    private static bool IsDeezerProvider(string? provider) =>
        string.Equals(provider, "deezer", StringComparison.OrdinalIgnoreCase);

    private static bool TryTrackId(Song song, out string trackId)
    {
        trackId = song.DeezerId ?? "";
        if (ValidTrackId(trackId)) return true;
        var externalId = song.ExternalId ?? song.Id;
        if (IsDeezerProvider(song.ExternalProvider) && ValidTrackId(externalId))
        {
            trackId = externalId;
            return true;
        }
        trackId = "";
        return false;
    }

    private void QueuePinnedFill(string trackId)
    {
        if (!_options.CurrentValue.CacheEnabled || !HasPin(trackId)
            || IsValidFlac(TrackPath(trackId))
            || _pinAttempts.TryGetValue(trackId, out var attempted) && DateTime.UtcNow - attempted < MaintenanceInterval
            || !_queuedPins.TryAdd(trackId, 0)) return;
        if (!_pinQueue.Writer.TryWrite(trackId)) _queuedPins.TryRemove(trackId, out _);
    }

    internal async Task CleanupAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return;
        var settings = _options.CurrentValue;
        var retention = TimeSpan.FromDays(Math.Max(0, settings.CacheRetentionDays));
        var maximumBytes = settings.CacheMaxGiB <= 0 ? 0L
            : settings.CacheMaxGiB >= long.MaxValue / (1024d * 1024 * 1024)
                ? long.MaxValue : (long)(settings.CacheMaxGiB * 1024d * 1024 * 1024);
        var now = DateTime.UtcNow;
        var files = Directory.EnumerateFiles(_root, "*.flac", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path)).Where(file => file.Exists)
            .ToList();

        var items = files.Select(file =>
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            var entry = GetEntrySnapshot(id);
            var completed = entry?.CompletedUtc ?? file.LastWriteTimeUtc;
            var lastPlayed = entry?.LastPlayedUtc ?? completed;
            var pinned = entry is { Pins.Count: > 0 };
            return new CacheFile(id, file.FullName, file.Length, lastPlayed, pinned);
        }).ToList();

        var expired = items.Where(item => !item.Pinned && item.LastPlayedUtc < now - retention)
            .OrderBy(item => item.LastPlayedUtc).ToList();
        foreach (var item in expired) DeleteIfUnleased(item.TrackId, item.Path);

        items = items.Where(item => File.Exists(item.Path)).ToList();
        var unpinned = items.Where(item => !item.Pinned).OrderBy(item => item.LastPlayedUtc).ToList();
        var total = items.Where(item => !item.Pinned).Sum(item => item.Length);
        foreach (var item in unpinned)
        {
            ct.ThrowIfCancellationRequested();
            if (total <= maximumBytes) break;
            if (DeleteIfUnleased(item.TrackId, item.Path)) total -= item.Length;
        }

        var partials = Directory.Exists(_staging)
            ? Directory.EnumerateFiles(_staging, "*.tmp", SearchOption.TopDirectoryOnly)
            : Enumerable.Empty<string>();
        foreach (var partial in partials)
        {
            ct.ThrowIfCancellationRequested();
            try { if (File.GetLastWriteTimeUtc(partial) < now.AddHours(-1)) File.Delete(partial); }
            catch { /* orphan cleanup is best effort */ }
        }
        _lastCleanupUtc = now;
        await Task.CompletedTask;
    }

    private bool DeleteIfUnleased(string trackId, string path)
    {
        lock (_readLock)
        {
            if (_activeReaders.ContainsKey(trackId) || _activeTransfers.ContainsKey(trackId)
                || HasPin(trackId)) return false;
            try { File.Delete(path); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    private void ReleaseReader(string trackId)
    {
        lock (_readLock)
        {
            if (!_activeReaders.TryGetValue(trackId, out var count)) return;
            if (count <= 1) _activeReaders.TryRemove(trackId, out _);
            else _activeReaders[trackId] = count - 1;
        }
    }

    private string TrackPath(string trackId) => Path.Combine(_root, trackId + ".flac");

    internal static bool IsValidFlac(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (input.Length < 44) return false;
            Span<byte> header = stackalloc byte[8];
            if (input.Read(header) != header.Length || !header[..4].SequenceEqual("fLaC"u8)) return false;
            var type = header[4] & 0x7f;
            var streamInfoLength = header[5] << 16 | header[6] << 8 | header[7];
            if (type != 0 || streamInfoLength != 34 || input.Length < 4 + 4 + streamInfoLength + 2) return false;
            Span<byte> streamInfo = stackalloc byte[34];
            if (input.Read(streamInfo) != streamInfo.Length) return false;
            var minBlock = streamInfo[0] << 8 | streamInfo[1];
            var maxBlock = streamInfo[2] << 8 | streamInfo[3];
            var packed = (ulong)streamInfo[10] << 56 | (ulong)streamInfo[11] << 48
                | (ulong)streamInfo[12] << 40 | (ulong)streamInfo[13] << 32
                | (ulong)streamInfo[14] << 24 | (ulong)streamInfo[15] << 16
                | (ulong)streamInfo[16] << 8 | streamInfo[17];
            var sampleRate = (packed >> 44) & 0xfffff;
            var bitsPerSample = (int)((packed >> 36) & 0x1f) + 1;
            if (minBlock <= 0 || maxBlock < minBlock || sampleRate == 0 || bitsPerSample < 4) return false;

            // Skip remaining metadata blocks by their declared sizes. A playable stream must
            // begin with a frame sync after the final block, so a truncated header cannot pass.
            var last = (header[4] & 0x80) != 0;
            Span<byte> block = stackalloc byte[4];
            while (!last)
            {
                if (input.Read(block) != block.Length) return false;
                last = (block[0] & 0x80) != 0;
                var length = block[1] << 16 | block[2] << 8 | block[3];
                if (length > input.Length - input.Position) return false;
                input.Seek(length, SeekOrigin.Current);
            }
            var firstFrame = input.ReadByte();
            var secondFrame = input.ReadByte();
            return firstFrame == 0xff && secondFrame >= 0 && (secondFrame & 0xfc) == 0xf8;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task<bool> ValidateWithFfmpegAsync(string path, CancellationToken workToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(workToken, timeout.Token);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in new[]
        {
            "-nostdin", "-hide_banner", "-v", "error", "-xerror", "-err_detect", "crccheck+explode", "-i", path,
            "-map", "0:a:0", "-f", "null", "-",
        }) process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start()) return false;
            var errorTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            var error = await errorTask;
            if (process.ExitCode == 0) return true;
            _logger.LogDebug("ffmpeg rejected Deezer FLAC cache file (exit {Code}): {Error}",
                process.ExitCode, error.Trim());
            return false;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !workToken.IsCancellationRequested)
        {
            Kill(process);
            _logger.LogWarning("Timed out validating completed Deezer FLAC cache file");
            return false;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning("Could not validate Deezer FLAC cache file with ffmpeg: {Type}",
                ex.GetType().Name);
            return false;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* process may exit between the check and kill */ }
    }

    private static bool ValidTrackId(string? id) => id is not null
        && long.TryParse(id, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0;

    private static SongSnapshot Snapshot(Song song) => new(song.Artist, song.Title,
        string.IsNullOrEmpty(song.Album) ? null : song.Album, song.Duration);

    private static CacheDocument LoadState(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path), Json)
                ?? new CacheDocument() : new CacheDocument();
        }
        catch { return new CacheDocument(); }
    }

    private async Task MutateStateAsync(Action<CacheDocument> mutation, CancellationToken ct)
    {
        await _stateWrite.WaitAsync(ct);
        try
        {
            CacheDocument next;
            lock (_stateLock) next = Clone(_state);
            mutation(next);
            Directory.CreateDirectory(_root);
            var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next, Json), ct);
                lock (_readLock)
                {
                    File.Move(temporary, _statePath, overwrite: true);
                    lock (_stateLock) _state = next;
                }
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
        finally { _stateWrite.Release(); }
    }

    private bool HasPin(string trackId)
    {
        lock (_stateLock) return _state.Tracks.TryGetValue(trackId, out var entry) && entry.Pins.Count > 0;
    }

    private SongSnapshot? GetMetadata(string trackId)
    {
        lock (_stateLock) return _state.Tracks.TryGetValue(trackId, out var entry) ? entry.Metadata : null;
    }

    private CacheEntry? GetEntrySnapshot(string trackId)
    {
        lock (_stateLock)
            return _state.Tracks.TryGetValue(trackId, out var entry) ? Clone(entry) : null;
    }

    private List<string> PinnedTrackIds()
    {
        lock (_stateLock) return _state.Tracks.Where(pair => pair.Value.Pins.Count > 0)
            .Select(pair => pair.Key).ToList();
    }

    private static CacheEntry GetEntry(CacheDocument document, string trackId)
    {
        if (!document.Tracks.TryGetValue(trackId, out var entry))
            document.Tracks[trackId] = entry = new CacheEntry();
        return entry;
    }

    private static CacheDocument Clone(CacheDocument document) => new()
    {
        Tracks = document.Tracks.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal),
    };

    private static CacheEntry Clone(CacheEntry entry) => new()
    {
        CompletedUtc = entry.CompletedUtc,
        LastPlayedUtc = entry.LastPlayedUtc,
        Metadata = entry.Metadata,
        Pins = new Dictionary<string, string>(entry.Pins, StringComparer.Ordinal),
    };

    private sealed class CacheDocument
    {
        public Dictionary<string, CacheEntry> Tracks { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CacheEntry
    {
        public DateTime? CompletedUtc { get; set; }
        public DateTime? LastPlayedUtc { get; set; }
        public SongSnapshot? Metadata { get; set; }
        public Dictionary<string, string> Pins { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record SongSnapshot(string Artist, string Title, string? Album, int? Duration);
    private sealed record CacheFile(string TrackId, string Path, long Length,
        DateTime LastPlayedUtc, bool Pinned);

    private sealed class PriorityTransferGate(int limit)
    {
        private readonly object _lock = new();
        private readonly List<Waiter> _waiting = [];
        private readonly Dictionary<string, DeezerCachePriority> _promoted = new(StringComparer.Ordinal);
        private int _active;
        private long _sequence;

        public Task<IDisposable> AcquireAsync(string key, DeezerCachePriority priority, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_promoted.TryGetValue(key, out var promoted)) priority = Max(priority, promoted);
                if (_active < limit && _waiting.Count == 0)
                {
                    _active++;
                    return Task.FromResult<IDisposable>(new TransferLease(this));
                }

                var waiter = new Waiter(key, priority, _sequence++);
                _waiting.Add(waiter);
                waiter.Registration = ct.Register(() => Cancel(waiter, ct));
                return waiter.Source.Task;
            }
        }

        public void Promote(string key, DeezerCachePriority priority)
        {
            lock (_lock)
            {
                if (!_promoted.TryGetValue(key, out var old) || priority > old) _promoted[key] = priority;
                foreach (var waiter in _waiting.Where(waiter => waiter.Key == key))
                    waiter.Priority = Max(waiter.Priority, priority);
            }
        }

        public void Forget(string key)
        {
            lock (_lock) _promoted.Remove(key);
        }

        private void Release()
        {
            Waiter? next = null;
            lock (_lock)
            {
                _active--;
                if (_waiting.Count > 0)
                {
                    next = _waiting.OrderByDescending(waiter => waiter.Priority)
                        .ThenBy(waiter => waiter.Sequence).First();
                    _waiting.Remove(next);
                    _active++;
                }
            }
            if (next is not null)
            {
                next.Registration.Dispose();
                next.Source.TrySetResult(new TransferLease(this));
            }
        }

        private void Cancel(Waiter waiter, CancellationToken ct)
        {
            var removed = false;
            lock (_lock) removed = _waiting.Remove(waiter);
            if (removed) waiter.Source.TrySetCanceled(ct);
        }

        private static DeezerCachePriority Max(DeezerCachePriority first, DeezerCachePriority second) =>
            first > second ? first : second;

        private sealed class Waiter(string key, DeezerCachePriority priority, long sequence)
        {
            public string Key { get; } = key;
            public DeezerCachePriority Priority { get; set; } = priority;
            public long Sequence { get; } = sequence;
            public TaskCompletionSource<IDisposable> Source { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenRegistration Registration { get; set; }
        }

        private sealed class TransferLease(PriorityTransferGate owner) : IDisposable
        {
            private PriorityTransferGate? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
