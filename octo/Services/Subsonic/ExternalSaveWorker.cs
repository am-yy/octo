using System.Collections.Concurrent;
using Octo.Services.Common;
using Octo.Services.Deezer;
using Octo.Services.Soulseek;

namespace Octo.Services.Subsonic;

/// <summary>Replays durable intent and protects saved audio without retaining caller sessions.</summary>
public sealed class ExternalSaveWorker(ExternalSaveStore store, DeezerAudioCache cache,
    HeartAcquisitionCoordinator acquisitions, ExternalIdRegistry ids,
    ILogger<ExternalSaveWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<string, Task> _running = new();

    public void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var afterRestart = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.RecoverInterruptedAcquisitionsAsync(afterRestart);
                afterRestart = false;
                var state = store.Snapshot();
                var pins = state.Playlists.SelectMany(p => p.Tracks.Where(t => t.Song is { IsLocal: false })
                    .Select(t => (Song: t.Song!, ReferenceId: $"playlist/{p.UserId}/{p.Id}/{t.OccurrenceId}")))
                    .Concat(state.Hearts.Where(h => !h.Song.IsLocal)
                        .Select(h => (Song: h.Song, ReferenceId: $"heart/{h.UserId}/{h.SongId}"))).ToList();
                foreach (var (song, _) in pins)
                    ids.Restore(song.Id, new SoulseekRouting { Artist = song.Artist, Title = song.Title,
                        Album = song.Album, Duration = song.Duration, DeezerId = song.DeezerId,
                        Isrc = song.Isrc, Track = song.Track, DiscNumber = song.DiscNumber });
                foreach (var intent in store.GetPendingAcquisitions())
                {
                    if (_running.Count >= 2) break;
                    var key = intent.Provider + ":" + intent.ExternalId;
                    if (_running.ContainsKey(key)) continue;
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!_running.TryAdd(key, completion.Task)) continue;
                    _ = AcquireAsync(intent, key, completion);
                }
                if (cache.Enabled)
                    await cache.ReplacePinsAsync(pins, stoppingToken);
                foreach (var song in pins.Select(p => p.Song).DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase))
                {
                    var routing = ids.Lookup(song.Id);
                    var album = routing?.Album;
                    var duration = routing is null ? null : SongLength.Shown(routing).Seconds;
                    var deezerId = string.IsNullOrWhiteSpace(song.DeezerId) ? routing?.DeezerId : song.DeezerId;
                    if (!string.IsNullOrWhiteSpace(deezerId) || !string.IsNullOrWhiteSpace(album) || duration is > 0)
                        await store.RememberMetadataAsync(song.Id, deezerId, album, duration);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Saved audio upkeep deferred: {Reason}", ex.GetType().Name); }
            try { await _wake.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task AcquireAsync(ExternalAcquisitionIntent intent, string key, TaskCompletionSource completion)
    {
        try
        {
            var acquired = await acquisitions.AcquireTrackAsync(intent.Provider, intent.ExternalId, intent.RequestedBy.FirstOrDefault());
            var remaining = store.Snapshot().Acquisitions.FirstOrDefault(i => i.Provider == intent.Provider && i.ExternalId == intent.ExternalId);
            if (acquired && remaining?.Status is "pending" or "in-progress")
                await store.MarkAcquisitionScheduledAsync(intent.Provider, intent.ExternalId);
            else if (!acquired && remaining?.Status != "submitted" && remaining?.Status != "imported")
                await store.MarkAcquisitionFailedAsync(intent.Provider, intent.ExternalId, "Acquisition did not produce an import.");
        }
        catch (Exception ex)
        {
            try { await store.MarkAcquisitionFailedAsync(intent.Provider, intent.ExternalId, ex.GetType().Name); }
            catch (Exception writeError) { logger.LogError("Acquisition state write failed: {Reason}", writeError.GetType().Name); }
        }
        finally { _running.TryRemove(key, out _); completion.TrySetResult(); }
    }
}
