using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
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
    Download = 3,
}

/// <summary>A durable reference keeping the selected Deezer source in cache.</summary>
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

public sealed class DeezerAdmissionException() : IOException("Deezer source admission is full.");
public sealed class DeezerStoppingException() : IOException("Deezer delivery is stopping.");

/// <summary>
/// Persistent, strict-source playback cache. Caller cancellation only ends that caller's wait;
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
    private readonly string _sourceQuality;
    private readonly PriorityTransferGate _transfers;
    private readonly PriorityTransferGate _backgroundTransfers;
    private readonly ConcurrentDictionary<string, SourceJob> _sourceJobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _activeReaders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activeTransfers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _activeEncodedReaders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _activeEncodedWork = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _queuedPins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _pinAttempts = new(StringComparer.Ordinal);
    private readonly Channel<string> _pinQueue;
    private readonly int _admissionLimit;
    private readonly int _backgroundAdmissionLimit;
    private readonly int _pinWorkerCount;
    private readonly object _jobsLock = new();
    private readonly HashSet<SourceJob> _producers = [];
    private readonly CancellationTokenSource _shutdown = new();
    private bool _stopping;
    private readonly CancellationTokenRegistration _hostStopping;
    private readonly SemaphoreSlim _stateWrite = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _readLock = new();
    private CacheDocument _state;
    private DateTime _lastCleanupUtc = DateTime.MinValue;

    public DeezerAudioCache(DeezerResolver resolver, DeezerMetadataService catalog,
        ExternalIdRegistry ids, IOptionsMonitor<DeezerSettings> options,
        ILogger<DeezerAudioCache> logger, IHostApplicationLifetime? lifetime = null)
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
        _sourceQuality = DeezerResolver.NormalizeStrictQuality(options.CurrentValue.CacheQuality);
        var maxDownloads = Math.Clamp(options.CurrentValue.MaxConcurrentDownloads, 1, 32);
        _transfers = new PriorityTransferGate(maxDownloads);
        _pinWorkerCount = Math.Clamp(options.CurrentValue.MaxConcurrentBackgroundDownloads, 1, maxDownloads);
        _backgroundTransfers = new PriorityTransferGate(_pinWorkerCount);
        _admissionLimit = 4 * maxDownloads;
        _backgroundAdmissionLimit = _admissionLimit - maxDownloads;
        _pinQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(_admissionLimit)
            { SingleReader = _pinWorkerCount == 1, FullMode = BoundedChannelFullMode.Wait });
        _hostStopping = lifetime?.ApplicationStopping.Register(BeginStopping) ?? default;
    }

    public bool IsEnabled => _options.CurrentValue.CacheEnabled;
    public bool Enabled => IsEnabled;
    public string RootPath => _root;
    public string SourceQuality => _sourceQuality;

    /// <summary>Finds a completed FLAC without changing its retention timestamp.</summary>
    public string? GetReadyPath(Song song)
    {
        if (!IsEnabled || !TryTrackId(song, out var trackId)) return null;
        var quality = SourceQuality;
        var path = TrackPath(trackId, quality);
        return IsValidSource(path, quality) ? path : null;
    }

    public string? SelectedContentType(Song song) =>
        TryTrackId(song, out _) ? ContentType(SourceQuality) : null;

    /// <summary>Gets a completed copy or waits for one shared selected-quality download.</summary>
    public async Task<string?> EnsureAsync(Song song,
        DeezerCachePriority priority = DeezerCachePriority.Playback,
        CancellationToken cancellationToken = default)
    {
        lock (_jobsLock) if (_stopping) throw new DeezerStoppingException();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;
        if (!IsEnabled) return null;
        var trackId = await ResolveTrackIdAsync(song, cancellationToken);
        return trackId is null ? null : await EnsureByTrackIdAsync(trackId, Snapshot(song), priority, cancellationToken);
    }

    /// <summary>Returns source metadata without starting or waiting for a cache fill.</summary>
    public async Task<DeezerSourceInfo?> ProbeAsync(Song song, CancellationToken cancellationToken = default)
    {
        lock (_jobsLock) if (_stopping) throw new DeezerStoppingException();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;
        var trackId = await ResolveTrackIdAsync(song, cancellationToken);
        if (trackId is null) return null;
        var quality = SourceQuality;
        var path = IsEnabled ? TrackPath(trackId, quality) : null;
        if (path is not null && IsValidSource(path, quality))
        {
            var file = new FileInfo(path);
            var fingerprint = GetSourceFingerprint(trackId, quality);
            return new DeezerSourceInfo(ContentType(quality), file.Length, fingerprint,
                file.LastWriteTimeUtc, path);
        }
        if (_sourceJobs.TryGetValue(SourceKey(trackId, quality), out var active)
            && active.Info is { } activeInfo) return activeInfo;
        return await _resolver.ProbeSourceAsync(trackId, quality, cancellationToken);
    }

    /// <summary>Open shared growing source, including temporary staging when durable cache is disabled.</summary>
    public async Task<DeezerProgressiveLease?> OpenProgressiveAsync(Song song,
        DeezerCachePriority priority = DeezerCachePriority.Playback,
        CancellationToken cancellationToken = default)
    {
        lock (_jobsLock) if (_stopping) throw new DeezerStoppingException();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;
        var trackId = await ResolveTrackIdAsync(song, cancellationToken);
        if (trackId is null) return null;
        return await OpenProgressiveByTrackIdAsync(trackId, Snapshot(song), SourceQuality, priority,
            cancellationToken);
    }

    private async Task<DeezerProgressiveLease> OpenProgressiveByTrackIdAsync(string trackId,
        SongSnapshot? metadata, string quality, DeezerCachePriority priority, CancellationToken ct,
        bool consumer = true)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        ct = linked.Token;
        var key = SourceKey(trackId, quality);
        SourceJob job;
        DeezerProgressiveLease lease;
        lock (_jobsLock)
        {
            if (_stopping) throw new DeezerStoppingException();
            ct.ThrowIfCancellationRequested();
            if (IsEnabled)
            {
                var ready = TrackPath(trackId, quality);
                if (IsValidSource(ready, quality)) return OpenReadyLease(trackId, quality, ready);
            }
            if (_sourceJobs.TryGetValue(key, out var existing))
            {
                job = existing;
                lease = OpenJobLease(job, consumer, priority);
                job.Promote(priority);
                _transfers.Promote(key, priority);
                _backgroundTransfers.Promote(key, priority);
            }
            else
            {
                if (_producers.Count >= _admissionLimit || priority < DeezerCachePriority.Playback
                    && _producers.Count(item => item.BackgroundAdmission) >= _backgroundAdmissionLimit)
                    throw new DeezerAdmissionException();
                var temporary = Path.Combine(_staging, $"{trackId}.{quality}.{Guid.NewGuid():N}.tmp");
                job = new SourceJob(key, trackId, quality, metadata, priority,
                    new ProgressiveFile(temporary), persist: IsEnabled);
                _sourceJobs[key] = job;
                _producers.Add(job);
                lease = OpenJobLease(job, consumer, priority);
                job.Worker = RunProducerAsync(job);
            }
        }
        try
        {
            await job.WaitForOpenAsync(ct);
            return lease;
        }
        catch
        {
            try
            {
                if (!ct.IsCancellationRequested && job.Completion.Task.IsCompleted)
                    await job.Worker.WaitAsync(ct);
            }
            finally { await lease.DisposeAsync(); }
            throw;
        }
    }

    private async Task RunProducerAsync(SourceJob job)
    {
        await Task.Yield();
        try { await ProduceSourceAsync(job); }
        catch (Exception ex)
        {
            job.Buffer.Fail(ex);
            job.Completion.TrySetException(ex);
            job.OpenReady.TrySetResult();
            job.SourceReady.TrySetResult();
        }
        finally
        {
            lock (_jobsLock)
            {
                _producers.Remove(job);
                CleanupSourceJob(job);
            }
        }
    }

    private DeezerProgressiveLease OpenReadyLease(string trackId, string quality, string path)
    {
        lock (_readLock)
        {
            var file = new FileInfo(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
            _activeReaders.AddOrUpdate(trackId, 1, static (_, count) => count + 1);
            var fingerprint = GetSourceFingerprint(trackId, quality, path);
            return new DeezerProgressiveLease(stream, Task.CompletedTask, ContentType(quality),
                () => file.Exists ? file.Length : null, () => fingerprint, () => path,
                (offset, length, ct) => OpenFileRangeAsync(path, offset, length, ct),
                () => ReleaseReader(trackId), () => file.LastWriteTimeUtc);
        }
    }

    private DeezerProgressiveLease OpenJobLease(SourceJob job, bool consumer, DeezerCachePriority priority)
    {
        job.AddLease(consumer, priority);
        if (consumer)
        {
            lock (_readLock) _activeReaders.AddOrUpdate(job.TrackId, 1, static (_, count) => count + 1);
        }
        var stream = new SourceJobReadStream(job);
        return new DeezerProgressiveLease(stream, job.Completion.Task, ContentType(job.Quality),
            () => job.ExpectedLength,
            () => job.Info?.Fingerprint ?? StableSourceFingerprint(job.TrackId, job.Quality),
            () => job.ReadyPath,
            (offset, length, ct) => OpenJobRangeAsync(job, offset, length, ct),
            () =>
            {
                if (consumer) ReleaseReader(job.TrackId);
                job.RemoveLease(consumer, priority);
                if (Volatile.Read(ref job.Readers) == 0 && job.Completion.Task.IsCompleted)
                    CleanupSourceJob(job);
            }, () => job.Info?.ModifiedUtc);
    }

    private async Task<Stream> OpenJobRangeAsync(SourceJob job, long offset, long? length,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        ct = linked.Token;
        ct.ThrowIfCancellationRequested();
        if (offset < 0 || length < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (job.ReadyPath is { } ready && job.Buffer.Completion.IsCompletedSuccessfully)
            return await OpenFileRangeAsync(ready, offset, length, ct);
        if (offset <= job.Buffer.Length) return job.Buffer.OpenRead(offset, length);
        await job.SourceReady.Task.WaitAsync(ct);
        var info = job.Info ?? throw new IOException("Deezer source descriptor unavailable.");
        if (job.ExpectedLength is long total && offset >= total) return Stream.Null;
        var transfer = await AcquireTransferAsync(job.Key, DeezerCachePriority.Playback, ct);
        try
        {
            var opened = await _resolver.OpenSourceRangeAsync(info, offset, ct)
                ?? throw new IOException("Deezer range source unavailable.");
            var (stream, _, _, status, _, owner) = opened;
            if (status == 416)
            {
                owner.Dispose(); await stream.DisposeAsync(); transfer.Dispose(); return Stream.Null;
            }
            var owned = new OwnedStream(stream, new CompositeLease(owner, transfer), _shutdown.Token);
            return length is long requested ? new LengthLimitedStream(owned, requested) : owned;
        }
        catch (Exception ex)
        {
            transfer.Dispose();
            if (!ct.IsCancellationRequested) job.Abort(ex);
            throw;
        }
    }

    private static async Task<Stream> OpenFileRangeAsync(string path, long offset, long? length,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (offset >= file.Length)
        {
            await file.DisposeAsync();
            return Stream.Null;
        }
        file.Position = offset;
        return length is long requested ? new LengthLimitedStream(file, Math.Min(requested, file.Length - offset)) : file;
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
        var quality = SourceQuality;
        var path = TrackPath(trackId, quality);
        lock (_readLock)
        {
            if (!IsValidSource(path, quality)) return null;
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

    public IDisposable ProtectEncoded(string path)
    {
        var key = Path.GetFullPath(path);
        _activeEncodedWork.AddOrUpdate(key, 1, static (_, count) => checked(count + 1));
        return new ActionLease(() => ReleaseEncodedWork(key));
    }

    public DeezerEncodedLease? OpenEncodedRead(string path)
    {
        var key = Path.GetFullPath(path);
        lock (_readLock)
        {
            if (!File.Exists(key)) return null;
            _activeEncodedReaders.AddOrUpdate(key, 1, static (_, count) => count + 1);
            try
            {
                var stream = new FileStream(key, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 81920,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
                var contentHash = GetEncodedContentHash(key);
                if (string.IsNullOrWhiteSpace(contentHash))
                {
                    contentHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                    stream.Position = 0;
                }
                return new DeezerEncodedLease(key, stream, "sha256-" + contentHash,
                    () => ReleaseEncodedReader(key));
            }
            catch
            {
                ReleaseEncodedReader(key);
                throw;
            }
        }
    }

    public async Task RegisterEncodedVariantAsync(string key, string trackId, string path,
        DateTime completedUtc, CancellationToken cancellationToken = default)
    {
        // Library-backed encodes have no Deezer ID; they remain evictable unpinned variants.
        if (string.IsNullOrWhiteSpace(key) || trackId is null
            || trackId.Length > 0 && !ValidTrackId(trackId))
            throw new ArgumentException("Encoded cache identity is invalid.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Encoded cache file is missing.", fullPath);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var contentHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        await MutateStateAsync(document => document.EncodedVariants[key] = new EncodedVariantEntry
        {
            TrackId = trackId,
            Path = fullPath,
            CompletedUtc = completedUtc,
            ContentHash = contentHash,
        }, cancellationToken);
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdown.Token);
        var ct = linked.Token;
        CleanOrphanStagingOnStartup();
        var workers = Enumerable.Range(0, _pinWorkerCount).Select(_ => RunPinnedWorkerAsync(ct)).ToArray();
        try
        {
            await RetryMissingPinsAsync(ct);
            await CleanupAsync(ct);
            using var timer = new PeriodicTimer(MaintenanceInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RetryMissingPinsAsync(ct);
                if (DateTime.UtcNow - _lastCleanupUtc >= TimeSpan.FromHours(1))
                    await CleanupAsync(ct);
            }
        }
        finally
        {
            linked.Cancel();
            await Task.WhenAll(workers);
        }
    }

    private async Task RunPinnedWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var trackId in _pinQueue.Reader.ReadAllAsync(ct))
                await FillPinnedAsync(trackId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void BeginStopping()
    {
        lock (_jobsLock) _stopping = true;
        _pinQueue.Writer.TryComplete();
        _shutdown.Cancel();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginStopping();
        Task[] workers;
        lock (_jobsLock) workers = _producers.Select(job => job.Worker).ToArray();
        await Task.WhenAll(base.StopAsync(cancellationToken), Task.WhenAll(workers)).WaitAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _hostStopping.Dispose();
        BeginStopping();
        base.Dispose();
    }

    private async Task FillPinnedAsync(string trackId, CancellationToken ct)
    {
        try
        {
            if (!HasPin(trackId) || IsValidSource(TrackPath(trackId, SourceQuality), SourceQuality)) return;
            await EnsureByTrackIdAsync(trackId, GetMetadata(trackId), DeezerCachePriority.Pinned, ct);
            _pinAttempts[trackId] = DateTime.UtcNow;
        }
        catch (DeezerAdmissionException) { }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _pinAttempts[trackId] = DateTime.UtcNow;
            _logger.LogWarning("Pinned Deezer cache fill failed for {TrackId}: {Type}", trackId, ex.GetType().Name);
        }
        finally { _queuedPins.TryRemove(trackId, out _); }
    }

    private async Task RetryMissingPinsAsync(CancellationToken ct)
    {
        foreach (var trackId in PinnedTrackIds().OrderBy(id => _pinAttempts.GetValueOrDefault(id)))
        {
            ct.ThrowIfCancellationRequested();
            QueuePinnedFill(trackId);
        }
        // Let queued work begin before maintenance starts its potentially larger disk scan.
        await Task.Yield();
    }

    private async Task<string?> EnsureByTrackIdAsync(string trackId, SongSnapshot? metadata,
        DeezerCachePriority priority, CancellationToken waiterToken)
    {
        if (!IsEnabled || !ValidTrackId(trackId)) return null;
        var quality = SourceQuality;
        var path = TrackPath(trackId, quality);
        if (IsValidSource(path, quality)) return path;
        try
        {
            await using var lease = await OpenProgressiveByTrackIdAsync(trackId, metadata, quality, priority,
                waiterToken, consumer: false);
            await lease.Completion.WaitAsync(waiterToken);
            return lease.ReadyPath;
        }
        catch (DeezerAdmissionException) { throw; }
        catch (DeezerStoppingException) { throw; }
        catch (OperationCanceledException) when (waiterToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Deezer {Quality} cache unavailable for {TrackId}: {Type}",
                quality, trackId, ex.GetType().Name);
            return null;
        }
    }

    private async Task ProduceSourceAsync(SourceJob job)
    {
        using var timeout = new CancellationTokenSource(DownloadTimeout);
        while (!timeout.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            IDisposable? transfer = null;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _shutdown.Token);
            job.SetAttempt(attempt);
            var ct = attempt.Token;
            var retry = false;
            try
            {
                Directory.CreateDirectory(_staging);
                transfer = await AcquireTransferForJobAsync(job, ct);
                job.TransferActive = true;
                _activeTransfers.TryAdd(job.Key, 0);
                var established = await _resolver.EstablishSourceAsync(job.TrackId, job.Quality, ct)
                    ?? throw new InvalidOperationException("No matching Deezer source quality is available.");
                var (info, opened) = established;
                job.Info = info;
                job.ExpectedLength = info.ExpectedLength;
                job.SourceReady.TrySetResult();
                var (source, contentType, contentLength, _, _, owner) = opened;
                if (!string.Equals(contentType, ContentType(job.Quality), StringComparison.OrdinalIgnoreCase))
                {
                    owner.Dispose();
                    await source.DisposeAsync();
                    throw new InvalidDataException("Deezer returned a different source quality.");
                }
                job.ExpectedLength = contentLength ?? job.ExpectedLength;
                job.OpenReady.TrySetResult();
                using (owner)
                await using (source)
                {
                    var bytes = new byte[64 * 1024];
                    while (true)
                    {
                        var read = await source.ReadAsync(bytes, ct);
                        if (read == 0) break;
                        await job.Buffer.AppendAsync(bytes.AsMemory(0, read), ct);
                    }
                }

                if (job.Buffer.Length == 0 || job.ExpectedLength is long expected && job.Buffer.Length != expected)
                    throw new InvalidDataException("Deezer source was incomplete or invalid.");
                if (!IsValidSource(job.Buffer.Path, job.Quality)
                    || !await ValidateWithFfmpegAsync(job.Buffer.Path, ct))
                    throw new InvalidDataException("Deezer source was incomplete or invalid.");

                ct.ThrowIfCancellationRequested();
                var completedUtc = DateTime.UtcNow;
                if (job.Persist)
                {
                    var ready = TrackPath(job.TrackId, job.Quality);
                    job.Buffer.Publish(ready);
                    job.ReadyPath = ready;
                    try
                    {
                        await MutateStateAsync(document =>
                        {
                            var entry = GetEntry(document, job.TrackId);
                            entry.Metadata ??= job.Metadata;
                            entry.Sources[job.Quality] = new SourceEntry
                            {
                                CompletedUtc = completedUtc,
                                Fingerprint = info.Fingerprint,
                            };
                            if (job.Quality == "FLAC") entry.CompletedUtc = completedUtc;
                        }, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning("Could not persist Deezer cache completion for {TrackId}: {Type}",
                            job.TrackId, ex.GetType().Name);
                    }
                }
                else job.ReadyPath = job.Buffer.Path;

                job.Buffer.Complete();
                job.Completion.TrySetResult();
                await CleanupAsync(ct);
            }
            catch (OperationCanceledException) when (job.IsPreempting && !timeout.IsCancellationRequested && !_shutdown.IsCancellationRequested)
            {
                job.ResetForRetry(Path.Combine(_staging,
                    $"{job.TrackId}.{job.Quality}.{Guid.NewGuid():N}.tmp"));
                retry = true;
            }
            catch (Exception ex)
            {
                job.Buffer.Fail(ex);
                job.Completion.TrySetException(ex);
                job.OpenReady.TrySetResult();
                job.SourceReady.TrySetResult();
                _logger.LogWarning("Deezer {Quality} source fill failed for {TrackId}: {Type}",
                    job.Quality, job.TrackId, ex.GetType().Name);
            }
            finally
            {
                transfer?.Dispose();
                job.TransferActive = false;
                job.ClearAttempt(attempt);
                _activeTransfers.TryRemove(job.Key, out _);
                _transfers.Forget(job.Key);
                _backgroundTransfers.Forget(job.Key);
            }

            if (retry) continue;
            if (!job.Completion.Task.IsCompleted)
            {
                Exception failure = timeout.IsCancellationRequested
                    ? new TimeoutException("Deezer source download timed out.")
                    : new IOException("Deezer source download ended without validation.");
                job.Buffer.Fail(failure);
                job.Completion.TrySetException(failure);
            }
            job.OpenReady.TrySetResult();
            job.SourceReady.TrySetResult();
            if (job.Completion.Task.IsCompleted && Volatile.Read(ref job.Readers) == 0)
                CleanupSourceJob(job);
            return;
        }
        Exception timeoutFailure = _shutdown.IsCancellationRequested
            ? new OperationCanceledException(_shutdown.Token)
            : new TimeoutException("Deezer source download timed out.");
        job.Completion.TrySetException(timeoutFailure);
        job.Buffer.Fail(timeoutFailure);
        job.OpenReady.TrySetResult();
        job.SourceReady.TrySetResult();
        if (Volatile.Read(ref job.Readers) == 0) CleanupSourceJob(job);
    }

    private async ValueTask<IDisposable> AcquireTransferAsync(string key, DeezerCachePriority priority,
        CancellationToken ct)
    {
        if (priority >= DeezerCachePriority.Playback && _transfers.IsSaturated)
            TryPreemptBackground();
        IDisposable? background = null;
        try
        {
            if (priority < DeezerCachePriority.Playback)
            {
                background = await _backgroundTransfers.AcquireAsync(key, priority, ct);
                var all = await _transfers.AcquireAsync(key, priority, ct);
                return new CompositeLease(background, all);
            }
            return await _transfers.AcquireAsync(key, priority, ct);
        }
        catch
        {
            background?.Dispose();
            throw;
        }
    }

    private async ValueTask<IDisposable> AcquireTransferForJobAsync(SourceJob job, CancellationToken ct)
    {
        while (true)
        {
            if (job.Priority >= DeezerCachePriority.Playback)
            {
                if (_transfers.IsSaturated) TryPreemptBackground();
                return await _transfers.AcquireAsync(job.Key, job.Priority, ct);
            }

            using var backgroundWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var backgroundTask = _backgroundTransfers.AcquireAsync(job.Key, job.Priority, backgroundWait.Token);
            if (!await WaitForBackgroundPriorityAsync(backgroundTask, job))
            {
                await CancelAndReleaseAsync(backgroundTask, backgroundWait);
                _backgroundTransfers.Forget(job.Key);
                continue;
            }

            var background = await backgroundTask;
            if (job.Priority >= DeezerCachePriority.Playback)
            {
                background.Dispose();
                _backgroundTransfers.Forget(job.Key);
                continue;
            }

            try
            {
                using var totalWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var totalTask = _transfers.AcquireAsync(job.Key, job.Priority, totalWait.Token);
                if (!await WaitForBackgroundPriorityAsync(totalTask, job))
                {
                    await CancelAndReleaseAsync(totalTask, totalWait);
                    background.Dispose();
                    _backgroundTransfers.Forget(job.Key);
                    continue;
                }

                var all = await totalTask;
                if (job.Priority >= DeezerCachePriority.Playback)
                {
                    all.Dispose();
                    background.Dispose();
                    _backgroundTransfers.Forget(job.Key);
                    continue;
                }
                return new JobTransferLease(job, background, all);
            }
            catch
            {
                background.Dispose();
                throw;
            }
        }
    }

    private static async Task<bool> WaitForBackgroundPriorityAsync(Task<IDisposable> pending, SourceJob job)
    {
        while (!pending.IsCompleted)
        {
            var changed = job.PriorityChanged;
            if (job.Priority >= DeezerCachePriority.Playback) return false;
            await Task.WhenAny(pending, changed);
        }
        return job.Priority < DeezerCachePriority.Playback;
    }

    private static async Task CancelAndReleaseAsync(Task<IDisposable> pending,
        CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        try { (await pending).Dispose(); }
        catch (OperationCanceledException) { }
    }

    private void TryPreemptBackground()
    {
        var candidate = _sourceJobs.Values
            .Where(job => job.TransferActive && job.Consumers == 0
                && job.Priority < DeezerCachePriority.Playback)
            .OrderBy(job => job.Priority).FirstOrDefault();
        candidate?.TryPreempt();
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
        _ids.RememberDeezerTrack(externalId, hit.DeezerId, hit.AlbumTitle, hit.Duration);
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
            || IsValidSource(TrackPath(trackId, SourceQuality), SourceQuality)
            || _pinAttempts.TryGetValue(trackId, out var attempted) && DateTime.UtcNow - attempted < MaintenanceInterval
            || !_queuedPins.TryAdd(trackId, 0)) return;
        if (!_pinQueue.Writer.TryWrite(trackId)) _queuedPins.TryRemove(trackId, out _);
    }

    private void CleanOrphanStagingOnStartup()
    {
        if (!Directory.Exists(_staging)) return;
        // ponytail: assumes one cache owner per CachePath; multi-process use needs a filesystem lock.
        foreach (var path in Directory.EnumerateFiles(_staging, "*.tmp", SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(path);
            if (_sourceJobs.Values.Any(job => string.Equals(job.Buffer.Path, fullPath, StringComparison.Ordinal))
                || _activeEncodedWork.ContainsKey(fullPath)) continue;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
        var items = Directory.EnumerateFiles(_root, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".flac" or ".mp3")
            .Select(path => new FileInfo(path)).Where(file => file.Exists)
            .Select(file =>
            {
                var id = Path.GetFileNameWithoutExtension(file.Name);
                var quality = Path.GetExtension(file.Name).Equals(".flac", StringComparison.OrdinalIgnoreCase)
                    ? "FLAC" : "MP3_320";
                var entry = GetEntrySnapshot(id);
                var completed = entry?.Sources.TryGetValue(quality, out var source) == true
                    ? source.CompletedUtc : quality == "FLAC" ? entry?.CompletedUtc : null;
                var lastPlayed = entry?.LastPlayedUtc ?? completed ?? file.LastWriteTimeUtc;
                return new CacheFile(id, file.FullName, file.Length, lastPlayed,
                    entry is { Pins.Count: > 0 }, quality, null);
            }).ToList();

        lock (_stateLock)
        {
            items.AddRange(_state.EncodedVariants.Select(pair => (pair.Key, pair.Value))
                .Where(pair => File.Exists(pair.Value.Path))
                .Select(pair =>
                {
                    var file = new FileInfo(pair.Value.Path);
                    var lastPlayed = _state.Tracks.TryGetValue(pair.Value.TrackId, out var track)
                        ? track.LastPlayedUtc ?? pair.Value.CompletedUtc : pair.Value.CompletedUtc;
                    return new CacheFile(pair.Value.TrackId, file.FullName, file.Length,
                        lastPlayed, false, "ENCODED", pair.Key);
                }));
        }

        var expired = items.Where(item => !item.Pinned && item.LastPlayedUtc < now - retention)
            .OrderBy(item => item.LastPlayedUtc).ToList();
        var removedEncoded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in expired)
            if (DeleteCacheFile(item))
            {
                items.Remove(item);
                if (item.EncodedKey is not null) removedEncoded.Add(item.EncodedKey);
            }

        var unpinned = items.Where(item => !item.Pinned).OrderBy(item => item.LastPlayedUtc).ToList();
        var total = unpinned.Sum(item => item.Length);
        foreach (var item in unpinned)
        {
            ct.ThrowIfCancellationRequested();
            if (total <= maximumBytes) break;
            if (DeleteCacheFile(item))
            {
                total -= item.Length;
                if (item.EncodedKey is not null) removedEncoded.Add(item.EncodedKey);
            }
        }
        if (removedEncoded.Count > 0)
            await MutateStateAsync(document =>
            {
                foreach (var key in removedEncoded) document.EncodedVariants.Remove(key);
            }, ct);

        var partials = Directory.Exists(_staging)
            ? Directory.EnumerateFiles(_staging, "*.tmp", SearchOption.TopDirectoryOnly)
            : Enumerable.Empty<string>();
        foreach (var partial in partials)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(partial);
            var activeSource = _sourceJobs.Values.Any(job => string.Equals(job.Buffer.Path,
                fullPath, StringComparison.Ordinal));
            if (activeSource || _activeEncodedWork.ContainsKey(fullPath)) continue;
            try { if (File.GetLastWriteTimeUtc(partial) < now.AddHours(-1)) File.Delete(partial); }
            catch { /* orphan cleanup is best effort */ }
        }
        _lastCleanupUtc = now;
        await Task.CompletedTask;
    }

    private bool DeleteCacheFile(CacheFile item) => item.EncodedKey is null
        ? DeleteIfUnleased(item.TrackId, item.Path, item.Quality)
        : DeleteEncodedIfUnleased(item.Path);

    private bool DeleteIfUnleased(string trackId, string path, string quality)
    {
        lock (_readLock)
        {
            if (_activeReaders.ContainsKey(trackId)
                || _activeTransfers.ContainsKey(SourceKey(trackId, quality))
                || HasPin(trackId)) return false;
            try { File.Delete(path); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    private bool DeleteEncodedIfUnleased(string path)
    {
        var key = Path.GetFullPath(path);
        lock (_readLock)
        {
            if (_activeEncodedReaders.ContainsKey(key) || _activeEncodedWork.ContainsKey(key)) return false;
            try { File.Delete(key); return true; }
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

    private void ReleaseEncodedReader(string path)
    {
        lock (_readLock)
        {
            if (!_activeEncodedReaders.TryGetValue(path, out var count)) return;
            if (count <= 1) _activeEncodedReaders.TryRemove(path, out _);
            else _activeEncodedReaders[path] = count - 1;
        }
    }

    private void ReleaseEncodedWork(string path)
    {
        if (!_activeEncodedWork.TryGetValue(path, out var count)) return;
        if (count <= 1) _activeEncodedWork.TryRemove(path, out _);
        else _activeEncodedWork[path] = count - 1;
    }

    private string? GetEncodedContentHash(string path)
    {
        lock (_stateLock)
            return _state.EncodedVariants.Values.FirstOrDefault(entry =>
                string.Equals(entry.Path, path, StringComparison.Ordinal))?.ContentHash;
    }

    private string TrackPath(string trackId, string quality) =>
        Path.Combine(_root, trackId + (quality == "FLAC" ? ".flac" : ".mp3"));

    private static string SourceKey(string trackId, string quality) => $"{trackId}\0{quality}";
    private static string ContentType(string quality) => DeezerResolver.ContentType(quality);

    private static string StableSourceFingerprint(string trackId, string quality)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{trackId}\0{quality}\0{quality}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private string GetSourceFingerprint(string trackId, string quality, string? path = null)
    {
        var entry = GetEntrySnapshot(trackId);
        if (entry?.Sources.TryGetValue(quality, out var source) == true
            && !string.IsNullOrWhiteSpace(source.Fingerprint)) return source.Fingerprint;
        path ??= TrackPath(trackId, quality);
        try
        {
            using var input = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }
        catch { return StableSourceFingerprint(trackId, quality); }
    }

    private static bool IsValidSource(string path, string quality) => quality == "FLAC"
        ? IsValidFlac(path) : IsValidMp3(path);

    private static bool IsValidMp3(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (input.Length < 128) return false;
            Span<byte> header = stackalloc byte[10];
            var count = input.Read(header);
            return count >= 3 && (header[..3].SequenceEqual("ID3"u8)
                || header[0] == 0xff && (header[1] & 0xe0) == 0xe0);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void CleanupSourceJob(SourceJob job)
    {
        lock (_jobsLock)
        {
            if (_producers.Contains(job) || !job.Completion.Task.IsCompleted || Volatile.Read(ref job.Readers) != 0) return;
            if (_sourceJobs.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job)
                && _sourceJobs.TryRemove(job.Key, out _))
            {
                var temp = job.Buffer.Path;
                job.Buffer.Dispose();
                if (!job.Persist || job.ReadyPath is null) TryDelete(temp);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* staging cleanup is best effort */ }
    }

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
            _logger.LogDebug("ffmpeg rejected Deezer {Quality} cache file (exit {Code}): {Error}",
                _sourceQuality, process.ExitCode, error.Trim());
            return false;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !workToken.IsCancellationRequested)
        {
            await KillAsync(process);
            _logger.LogWarning("Timed out validating completed Deezer {Quality} cache file", _sourceQuality);
            return false;
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process);
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning("Could not validate Deezer {Quality} cache file with ffmpeg: {Type}",
                _sourceQuality, ex.GetType().Name);
            return false;
        }
    }

    private static async Task KillAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* process may exit between the check and kill */ }
        try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
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
        EncodedVariants = document.EncodedVariants.ToDictionary(pair => pair.Key,
            pair => Clone(pair.Value), StringComparer.Ordinal),
    };

    private static CacheEntry Clone(CacheEntry entry) => new()
    {
        CompletedUtc = entry.CompletedUtc,
        LastPlayedUtc = entry.LastPlayedUtc,
        Metadata = entry.Metadata,
        Pins = new Dictionary<string, string>(entry.Pins, StringComparer.Ordinal),
        Sources = entry.Sources.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal),
    };

    private static SourceEntry Clone(SourceEntry entry) => new()
    {
        CompletedUtc = entry.CompletedUtc,
        Fingerprint = entry.Fingerprint,
    };

    private static EncodedVariantEntry Clone(EncodedVariantEntry entry) => new()
    {
        TrackId = entry.TrackId,
        Path = entry.Path,
        CompletedUtc = entry.CompletedUtc,
        ContentHash = entry.ContentHash,
    };

    private sealed class CacheDocument
    {
        public Dictionary<string, CacheEntry> Tracks { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, EncodedVariantEntry> EncodedVariants { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CacheEntry
    {
        public DateTime? CompletedUtc { get; set; }
        public DateTime? LastPlayedUtc { get; set; }
        public SongSnapshot? Metadata { get; set; }
        public Dictionary<string, string> Pins { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, SourceEntry> Sources { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class SourceEntry
    {
        public DateTime? CompletedUtc { get; set; }
        public string Fingerprint { get; set; } = "";
    }

    private sealed class EncodedVariantEntry
    {
        public string TrackId { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTime CompletedUtc { get; set; }
        public string ContentHash { get; set; } = "";
    }

    private sealed record SongSnapshot(string Artist, string Title, string? Album, int? Duration);
    private sealed record CacheFile(string TrackId, string Path, long Length,
        DateTime LastPlayedUtc, bool Pinned, string Quality, string? EncodedKey);

    private sealed class SourceJob(string key, string trackId, string quality, SongSnapshot? metadata,
        DeezerCachePriority priority, ProgressiveFile buffer, bool persist)
    {
        private readonly object _sync = new();
        private int _priority = (int)priority;
        private DeezerCachePriority _backgroundPriority = priority;
        private int _foregroundLeases;
        private CancellationTokenSource? _attempt;
        private bool _preempting;
        private int _transferActive;
        private TaskCompletionSource _attemptChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _priorityChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _releaseBackground;
        public string Key { get; } = key;
        public string TrackId { get; } = trackId;
        public string Quality { get; } = quality;
        public SongSnapshot? Metadata { get; } = metadata;
        public ProgressiveFile Buffer { get; private set; } = buffer;
        public bool Persist { get; } = persist;
        public bool BackgroundAdmission { get; } = priority < DeezerCachePriority.Playback;
        public Task Worker { get; set; } = Task.CompletedTask;
        public int Readers;
        public int Consumers;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long? ExpectedLength { get; set; }
        public DeezerSourceInfo? Info { get; set; }
        public string? ReadyPath { get; set; }
        public bool TransferActive
        {
            get => Volatile.Read(ref _transferActive) != 0;
            set => Volatile.Write(ref _transferActive, value ? 1 : 0);
        }
        public TaskCompletionSource SourceReady { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OpenReady { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DeezerCachePriority Priority => (DeezerCachePriority)Volatile.Read(ref _priority);
        public bool IsPreempting { get { lock (_sync) return _preempting; } }
        public Task PriorityChanged { get { lock (_sync) return _priorityChanged.Task; } }
        public void Promote(DeezerCachePriority priority)
        {
            TaskCompletionSource? changed = null;
            Action? releaseBackground = null;
            lock (_sync)
            {
                if (priority < DeezerCachePriority.Playback && priority > _backgroundPriority)
                    _backgroundPriority = priority;
                var current = (DeezerCachePriority)_priority;
                if (current >= priority) return;
                Volatile.Write(ref _priority, (int)priority);
                changed = _priorityChanged;
                _priorityChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (priority >= DeezerCachePriority.Playback)
                    releaseBackground = _releaseBackground;
            }
            changed.TrySetResult();
            releaseBackground?.Invoke();
        }

        public void AddLease(bool consumer, DeezerCachePriority priority)
        {
            lock (_sync)
            {
                if (priority >= DeezerCachePriority.Playback) _foregroundLeases++;
                Interlocked.Increment(ref Readers);
                if (consumer) Interlocked.Increment(ref Consumers);
            }
        }

        public void RemoveLease(bool consumer, DeezerCachePriority priority)
        {
            lock (_sync)
            {
                if (priority >= DeezerCachePriority.Playback && --_foregroundLeases == 0
                    && _backgroundPriority < DeezerCachePriority.Playback)
                    Volatile.Write(ref _priority, (int)_backgroundPriority);
                if (consumer) Interlocked.Decrement(ref Consumers);
                Interlocked.Decrement(ref Readers);
            }
        }

        public void SetAttempt(CancellationTokenSource attempt)
        {
            lock (_sync) { _attempt = attempt; _preempting = false; }
        }

        public void ClearAttempt(CancellationTokenSource attempt)
        {
            lock (_sync) if (ReferenceEquals(_attempt, attempt)) _attempt = null;
        }

        public void SetBackgroundRelease(Action release)
        {
            var releaseNow = false;
            lock (_sync)
            {
                if (Priority >= DeezerCachePriority.Playback) releaseNow = true;
                else _releaseBackground = release;
            }
            if (releaseNow) release();
        }

        public void ClearBackgroundRelease(Action release)
        {
            lock (_sync)
                if (ReferenceEquals(_releaseBackground, release)) _releaseBackground = null;
        }

        public void Abort(Exception error)
        {
            Completion.TrySetException(error);
            lock (_sync)
            {
                try { _attempt?.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        public bool TryPreempt()
        {
            CancellationTokenSource? attempt;
            lock (_sync)
            {
                if (_transferActive == 0 || Volatile.Read(ref Consumers) > 0 || Priority >= DeezerCachePriority.Playback
                    || _attempt is null || _preempting) return false;
                _preempting = true;
                attempt = _attempt;
            }
            try { attempt?.Cancel(); }
            catch (ObjectDisposedException) { }
            return true;
        }

        public void ResetForRetry(string path)
        {
            var previous = Buffer;
            previous.Fail(new OperationCanceledException("Unused background transfer preempted."));
            previous.Dispose();
            TryDelete(previous.Path);
            Buffer = new ProgressiveFile(path);
            Info = null;
            ExpectedLength = null;
            ReadyPath = null;
            lock (_sync)
            {
                _preempting = false;
                _releaseBackground = null;
                OpenReady.TrySetResult();
                SourceReady.TrySetResult();
                var changed = _attemptChanged;
                _attemptChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                OpenReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                SourceReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.TrySetResult();
            }
        }

        public async Task WaitForOpenAsync(CancellationToken ct)
        {
            while (true)
            {
                Task open;
                Task changed;
                lock (_sync) { open = OpenReady.Task; changed = _attemptChanged.Task; }
                await open.WaitAsync(ct);
                var retry = false;
                var failed = false;
                lock (_sync)
                {
                    retry = _preempting || !ReferenceEquals(open, OpenReady.Task);
                    failed = !retry && Completion.Task.IsCompleted;
                }
                if (retry) { await changed.WaitAsync(ct); continue; }
                if (failed) await Completion.Task.WaitAsync(ct);
                return;
            }
        }
    }

    private sealed class SourceJobReadStream(SourceJob job) : Stream
    {
        private Stream? _reader;
        private ProgressiveFile? _buffer;
        private long _position;
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => job.ExpectedLength ?? job.Buffer.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SourceJobReadStream));
            while (true)
            {
                var current = job.Buffer;
                if (!ReferenceEquals(current, _buffer))
                {
                    if (_reader is not null) await _reader.DisposeAsync();
                    _buffer = current;
                    _reader = current.OpenRead(_position);
                }
                try
                {
                    var read = await _reader!.ReadAsync(buffer, cancellationToken);
                    _position += read;
                    return read;
                }
                catch (IOException) when (!ReferenceEquals(job.Buffer, _buffer)) { }
            }
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing) _reader?.Dispose();
            _disposed = true;
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (!_disposed && _reader is not null) await _reader.DisposeAsync();
            _disposed = true;
            await base.DisposeAsync();
        }
    }

    private sealed class CompositeLease(params IDisposable[] leases) : IDisposable
    {
        private IDisposable[]? _leases = leases;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _leases, null);
            if (current is null) return;
            foreach (var lease in current.Reverse()) lease.Dispose();
        }
    }

    private sealed class JobTransferLease : IDisposable
    {
        private readonly SourceJob _job;
        private IDisposable? _background;
        private IDisposable? _all;
        private readonly Action _releaseBackground;

        public JobTransferLease(SourceJob job, IDisposable background, IDisposable all)
        {
            _job = job;
            _background = background;
            _all = all;
            _releaseBackground = () => Interlocked.Exchange(ref _background, null)?.Dispose();
            job.SetBackgroundRelease(_releaseBackground);
        }

        public void Dispose()
        {
            _job.ClearBackgroundRelease(_releaseBackground);
            Interlocked.Exchange(ref _all, null)?.Dispose();
            _releaseBackground();
        }
    }

    private sealed class ActionLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class OwnedStream : Stream
    {
        private Stream? _stream;
        private IDisposable? _owner;
        private readonly CancellationTokenRegistration _shutdownRegistration;
        public OwnedStream(Stream stream, IDisposable owner, CancellationToken shutdown)
        {
            _stream = stream;
            _owner = owner;
            _shutdownRegistration = shutdown.Register(Dispose);
        }
        private Stream Inner => _stream ?? throw new ObjectDisposedException(nameof(OwnedStream));
        public override bool CanRead => _stream?.CanRead ?? false;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _shutdownRegistration.Dispose();
                Interlocked.Exchange(ref _stream, null)?.Dispose();
                Interlocked.Exchange(ref _owner, null)?.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            _shutdownRegistration.Dispose();
            var current = Interlocked.Exchange(ref _stream, null);
            if (current is not null) await current.DisposeAsync();
            Interlocked.Exchange(ref _owner, null)?.Dispose();
            await base.DisposeAsync();
        }
    }

    private sealed class LengthLimitedStream : Stream
    {
        private readonly Stream _source;
        private readonly long _limit;
        private long _remaining;

        public LengthLimitedStream(Stream source, long limit)
        {
            _source = source;
            _limit = limit;
            _remaining = limit;
        }

        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _limit;
        public override long Position { get => _limit - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining == 0) return 0;
            var read = _source.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0) return 0;
            var read = await _source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _source.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await _source.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private sealed class PriorityTransferGate(int limit)
    {
        private readonly object _lock = new();
        private readonly List<Waiter> _waiting = [];
        private readonly Dictionary<string, DeezerCachePriority> _promoted = new(StringComparer.Ordinal);
        private int _active;
        private long _sequence;

        public bool IsSaturated { get { lock (_lock) return _active >= limit; } }

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
