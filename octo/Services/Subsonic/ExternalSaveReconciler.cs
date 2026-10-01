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

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

}
