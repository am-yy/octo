using Octo.Services.Local;

namespace Octo.Services.Subsonic;

/// <summary>
/// Rechecks unresolved saved songs after Lidarr imports and Octo restarts. It uses the durable
/// metadata snapshot, so a small in-memory routing registry cannot make a saved occurrence vanish.
/// </summary>
public sealed class ExternalSaveReconciler(
    ExternalSaveStore saves,
    ILocalLibraryService library,
    ILogger<ExternalSaveReconciler> logger) : BackgroundService
{
    internal static readonly TimeSpan BurstInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan BurstLength = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private long _burstUntilTicks;

    internal bool Bursting => DateTime.UtcNow.Ticks < Interlocked.Read(ref _burstUntilTicks);

    /// <summary>
    /// Lidarr just imported: check now, then every few seconds while Navidrome scans the files.
    /// ponytail: a fixed two-minute window, not Navidrome's scan status, which needs admin auth;
    /// a scan that outlasts it is still found by the minute poll.
    /// </summary>
    public void Wake()
    {
        Interlocked.Exchange(ref _burstUntilTicks, (DateTime.UtcNow + BurstLength).Ticks);
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task<int> ReconcileImportedAsync(CancellationToken cancellationToken = default)
    {
        var replaced = 0;
        var intents = saves.GetUnresolvedAcquisitions()
            .DistinctBy(intent => (intent.Provider.ToUpperInvariant(), intent.ExternalId.ToUpperInvariant()));
        foreach (var intent in intents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var imported = await library.FindImportedSongAsync(intent.Song, cancellationToken);
                if (imported is not { IsLocal: true, Suffix: { } suffix, LocalPath: { Length: > 0 } path }
                    || !suffix.Equals("flac", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path)) continue;
                replaced += await saves.MarkImportedAsync(intent.Provider, intent.ExternalId, imported.Id, path);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug("External import reconciliation for {Provider}:{Id} will retry: {Reason}",
                    intent.Provider, intent.ExternalId, ex.Message);
            }
        }
        return replaced;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileImportedAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("External save scan will retry: {Reason}", ex.Message); }

            try { await _wake.WaitAsync(Bursting ? BurstInterval : TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

}
