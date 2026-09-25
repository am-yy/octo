using System.Text.Json;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Local;
using Octo.Services.Subsonic;

namespace Octo.Services.Library;

/// <summary>
/// Watches the action playlists and applies what people put in them.
///
/// A BackgroundService rather than a request hook, because applying an action can involve a
/// download and nothing that removes a file should run inside a request.
/// </summary>
public sealed class LibraryActionPlaylistWorker : BackgroundService
{
    private readonly LibraryActionExecutor _executor;
    private readonly LibraryActionJournal _journal;
    private readonly LibraryActionQuarantine _quarantine;
    private readonly NavidromeSongPathResolver _resolver;
    private readonly NavidromeIdentityService _identity;
    private readonly NavidromePlaylistApi _api;
    private readonly IOptionsMonitor<LibraryActionSettings> _settings;
    private readonly ILogger<LibraryActionPlaylistWorker> _logger;

    public LibraryActionPlaylistWorker(LibraryActionExecutor executor, LibraryActionJournal journal,
        LibraryActionQuarantine quarantine, NavidromeSongPathResolver resolver,
        NavidromeIdentityService identity, NavidromePlaylistApi api,
        IOptionsMonitor<LibraryActionSettings> settings, ILogger<LibraryActionPlaylistWorker> logger)
    {
        _executor = executor;
        _journal = journal;
        _quarantine = quarantine;
        _resolver = resolver;
        _identity = identity;
        _api = api;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _settings.CurrentValue;

        // Feature-gated by early return, which leaves the service registered but idle. Turning
        // file removal on is a deliberate act, so it is not worth a restart-free toggle.
        if (!settings.Enabled || !settings.PlaylistsEnabled)
        {
            _logger.LogInformation("Library actions are off; the playlist worker is idle");
            return;
        }

        if (!_identity.HasAdminIdentity)
        {
            // One clear line at startup rather than one silent failure per action.
            _logger.LogWarning(
                "Library actions are enabled but Octo has no Navidrome admin credential. Set "
                + "Subsonic:AdminUsername and AdminPassword, or sign in through Octo once as a "
                + "Navidrome admin. Until then no action can find its file, so none will run.");
            return;
        }

        // Decide what half-finished actions meant before doing anything new.
        _journal.Reconcile(path => _quarantine.Restore(path).Moved);

        using var timer = new PeriodicTimer(settings.EffectivePollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Library action sweep failed"); }

            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var settings = _settings.CurrentValue;
        if (!settings.Enabled || !settings.PlaylistsEnabled) return;

        var wanted = WantedPlaylists(settings);
        if (wanted.Count == 0) return;

        var budget = settings.EffectiveMaxActionsPerCycle;

        foreach (var playlist in await _api.ListPlaylistsAsync(ct))
        {
            if (budget <= 0) break;
            if (!wanted.TryGetValue(playlist.Name, out var action)) continue;

            // The owner comes from Navidrome's own record, not from a request parameter. That
            // is the strongest form of the allowlist check available.
            if (!settings.IsAllowed(playlist.Owner))
            {
                _logger.LogDebug("Skipping '{Playlist}': {Owner} is not on the allowlist",
                    playlist.Name, playlist.Owner);
                continue;
            }

            var applied = await ApplyPlaylistAsync(playlist, action.Action, budget, ct);
            budget -= applied;
        }

        _quarantine.Sweep(_resolver.MusicRoot());
    }

    private async Task<int> ApplyPlaylistAsync(PlaylistRow playlist, LibraryAction action,
        int budget, CancellationToken ct)
    {
        var tracks = await _api.ListTracksAsync(playlist.Id, ct);
        if (tracks.Count == 0) return 0;

        var consumed = new List<string>();
        var applied = 0;

        foreach (var track in tracks.Take(budget))
        {
            if (ct.IsCancellationRequested) break;

            // Per-item catch is mandatory: BackgroundServiceExceptionBehavior defaults to
            // StopHost, so one unhandled exception here would take Octo down.
            try
            {
                var outcome = await _executor.ApplyAsync(
                    new LibraryActionRequest(action, track.MediaFileId, playlist.Owner), ct);

                _logger.LogInformation("Library action {Action} for {Id} by {User}: {State} - {Detail}",
                    action, track.MediaFileId, playlist.Owner, outcome.State, outcome.Detail);

                if (outcome.Consumed) consumed.Add(track.MediaFileId);
                applied++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Library action {Action} threw for {Id}", action, track.MediaFileId);
            }
        }

        if (consumed.Count > 0) await RemoveTracksAsync(playlist.Id, consumed, ct);
        return applied;
    }

    /// <summary>
    /// Remove the tracks whose action landed.
    ///
    /// PlaylistTrack.ID is the 1-based POSITION, reassigned on every mutation, so the list is
    /// re-read immediately before deleting and positions are mapped from the media file ids that
    /// were actually applied. A track the user removed in the meantime is simply not there and
    /// is skipped rather than deleting whatever now sits at its old position. The delete is one
    /// bulk call because two sequential single deletes renumber between them.
    /// </summary>
    private async Task RemoveTracksAsync(string playlistId, List<string> mediaFileIds, CancellationToken ct)
    {
        try
        {
            var current = await _api.ListTracksAsync(playlistId, ct);
            var positions = current
                .Where(track => mediaFileIds.Contains(track.MediaFileId, StringComparer.Ordinal))
                .Select(track => track.Position)
                .Where(position => !string.IsNullOrEmpty(position))
                .ToList();
            if (positions.Count == 0) return;

            if (!await _api.RemovePositionsAsync(playlistId, positions, ct))
                _logger.LogWarning(
                    "Could not clear {Count} applied track(s) from playlist {Id}. They stay put; the "
                    + "journal stops the action running twice.", positions.Count, playlistId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not clear applied tracks from playlist {Id}: {M}", playlistId, ex.Message);
        }
    }

    /// <summary>The playlists whose tracks are commands, by title.</summary>
    internal static IReadOnlyDictionary<string, LibraryActionDefinition> WantedPlaylists(LibraryActionSettings settings)
    {
        var wanted = settings.EffectiveActions()
            .Where(action => action.Enabled)
            .ToDictionary(settings.PlaylistTitle, action => action, StringComparer.OrdinalIgnoreCase);
        // A notice playlist named like an action playlist would have every track Octo asked about
        // acted on. Octo's own questions are never commands, whatever they are called.
        foreach (var kind in Enum.GetValues<NoticeKind>()) wanted.Remove(settings.NoticeTitle(kind));
        return wanted;
    }

    public sealed record PlaylistRow(string Id, string Name, string Owner);
    public sealed record PlaylistTrackRow(string Position, string MediaFileId);

    internal static IReadOnlyList<PlaylistRow> ParsePlaylists(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];

        var rows = new List<PlaylistRow>();
        foreach (var item in root.EnumerateArray())
        {
            var id = Str(item, "id");
            var name = Str(item, "name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
            rows.Add(new PlaylistRow(id, name, Str(item, "ownerName") ?? ""));
        }
        return rows;
    }

    internal static IReadOnlyList<PlaylistTrackRow> ParseTracks(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];

        var rows = new List<PlaylistTrackRow>();
        foreach (var item in root.EnumerateArray())
        {
            var mediaFileId = Str(item, "mediaFileId");
            if (string.IsNullOrEmpty(mediaFileId)) continue;
            rows.Add(new PlaylistTrackRow(Str(item, "id") ?? "", mediaFileId));
        }
        return rows;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            }
            : null;
}
