using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Library;
using Octo.Services.Subsonic;

namespace Octo.Services.Lidarr;

/// <summary>
/// The Lidarr side of settling a saved outside song with the album Octo asked Lidarr for.
/// A Navidrome title search misses the song when its file carries other tags ("Anarchy In The
/// UK", a typo, the band's old name), when the album has several cuts of it, or when the request
/// came from a compilation whose cut runs longer. Lidarr's own track list has MusicBrainz titles
/// and each track's file, so the song is found in the album that was imported for it. An album
/// that never arrives, or arrives as an edition without the song, is searched again (for an
/// edition that has it), less often each time, at most <see cref="MaxSearches"/> times.
/// </summary>
public sealed class LidarrImportReconciler(
    LidarrClient client,
    ExternalSaveStore saves,
    NavidromeSongPathResolver paths,
    IOptionsMonitor<LidarrSettings> settings,
    IConfiguration configuration,
    NavidromeIdentityService identity,
    ILogger<LidarrImportReconciler> logger)
{
    internal const int MaxSearches = 5;
    /// <summary>The wait before the first search again; it doubles after each.</summary>
    internal static readonly TimeSpan FirstWait = TimeSpan.FromDays(1);
    /// <summary>How often one album's Lidarr tracks are read; the reconciler runs every minute.</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(15);
    /// <summary>Searches again per pass, so a backlog reaches the indexers a few albums a minute.</summary>
    internal const int SearchesPerPass = 3;

    private readonly ConcurrentDictionary<int, DateTime> _checked = new();
    private readonly ConcurrentDictionary<string, byte> _missingNoted = new();

    public async Task<int> ReconcileAsync(IReadOnlyList<ExternalAcquisitionIntent> intents, CancellationToken ct)
    {
        var lidarr = settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(lidarr.BaseUrl) || string.IsNullOrWhiteSpace(lidarr.ApiKey)) return 0;
        var now = DateTime.UtcNow;
        var octoRoot = identity.EffectiveDownloadPath(configuration["Library:DownloadPath"] ?? "/music");
        var replaced = 0;
        var searches = 0;
        foreach (var album in intents.Where(i => i.Status == "submitted" && i.LidarrAlbumId is > 0)
                     .GroupBy(i => i.LidarrAlbumId!.Value))
        {
            ct.ThrowIfCancellationRequested();
            if (_checked.TryGetValue(album.Key, out var last) && now - last < CheckEvery) continue;
            _checked[album.Key] = now;

            var tracks = await client.GetAlbumTracksAsync(album.Key, ct);
            if (!tracks.Any(t => t.HasFile))
            {
                var waiting = album.ToList();
                if (!SearchDue(waiting, now)) continue;
                if (searches >= SearchesPerPass)
                {
                    // Over this pass's share: look at the album again on the next pass.
                    _checked.TryRemove(album.Key, out _);
                    continue;
                }
                searches++;
                await SearchAgainAsync(album.Key, waiting, ct);
                continue;
            }
            // Songs the imported edition lacks (a bonus track, the other disc): another edition may hold them.
            var lacking = album.Where(intent => !tracks.Any(t =>
                SongIdentity.SameTitle(t.Title, intent.Song.Title, SongIdentity.StrictTitles).IsSame)).ToList();
            if (lacking.Count > 0 && SearchDue(lacking, now) && searches < SearchesPerPass)
            {
                var titles = lacking.Select(i => i.Song.Title).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (await client.PreferReleaseWithAsync(album.Key, titles, ct))
                {
                    searches++;
                    logger.LogInformation("Album {AlbumId} came without {Titles}; monitored an edition that has them",
                        album.Key, string.Join(", ", titles));
                    await SearchAgainAsync(album.Key, lacking, ct);
                }
                else if (_missingNoted.TryAdd(album.Key.ToString(), 0))
                {
                    logger.LogInformation("No edition of album {AlbumId} in Lidarr has {Titles}",
                        album.Key, string.Join(", ", titles));
                }
            }
            foreach (var intent in album)
            {
                // Still downloading, or not in this edition (handled above).
                if (PickTrack(tracks, intent.Song) is not { } track) continue;
                string path;
                try { path = LidarrHeartAcquisitionService.TranslateImportedPath(track.Path!, lidarr.RootFolderPath, octoRoot); }
                catch (InvalidOperationException ex)
                {
                    logger.LogDebug("Lidarr file for '{Title}' is outside Octo's library: {Reason}", intent.Song.Title, ex.Message);
                    continue;
                }
                // Saved songs stand for lossless copies; a lossy import stays unresolved.
                if (!File.Exists(path) || !path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) continue;
                var (artist, title) = Tags(path, track);
                var id = await paths.FindIdByPathAsync(artist, title, path, ct);
                if (id is null)
                {
                    // Navidrome has not scanned the file yet: look again on the next pass.
                    _checked.TryRemove(album.Key, out _);
                    continue;
                }
                replaced += await saves.MarkImportedAsync(intent.Provider, intent.ExternalId, id, path);
            }
        }
        return replaced;
    }

    /// <summary>
    /// The album's track that is the wanted song: same title, read strictly so a "(Live)" or
    /// "(Demo)" cut never stands in for the studio one, then the cut nearest the wanted length,
    /// since a deluxe album can hold several and a compilation's cut can run longer.
    /// </summary>
    internal static LidarrImportedTrack? PickTrack(IReadOnlyList<LidarrImportedTrack> tracks, Song wanted) =>
        tracks.Where(t => t.HasFile && !string.IsNullOrWhiteSpace(t.Path)
                && SongIdentity.SameTitle(t.Title, wanted.Title, SongIdentity.StrictTitles).IsSame)
            .OrderBy(t => wanted.Duration is > 0 && t.DurationSeconds is > 0
                ? Math.Abs(t.DurationSeconds.Value - wanted.Duration.Value) : int.MaxValue)
            .ThenBy(t => t.TrackNumber ?? int.MaxValue)
            .FirstOrDefault();

    /// <summary>Whether an album that has not arrived is due another search: a day after the last
    /// one, then two, four and eight days, and never after <see cref="MaxSearches"/>.</summary>
    internal static bool SearchDue(IReadOnlyList<ExternalAcquisitionIntent> waiting, DateTime now)
    {
        var attempts = waiting.Max(i => i.SearchAttempts);
        var since = waiting.Max(i => i.UpdatedUtc);
        return attempts < MaxSearches && now - since >= FirstWait * Math.Pow(2, attempts);
    }

    private async Task SearchAgainAsync(int albumId, IReadOnlyList<ExternalAcquisitionIntent> waiting,
        CancellationToken ct)
    {
        var attempt = waiting.Max(i => i.SearchAttempts) + 1;
        string? error = attempt >= MaxSearches ? $"Lidarr found no release after {attempt} more searches." : null;
        try
        {
            await client.SearchAlbumAgainAsync(albumId, ct);
            logger.LogInformation("Asked Lidarr to search again for album {AlbumId} (try {Attempt} of {Max})",
                albumId, attempt, MaxSearches);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            error = $"Searching Lidarr again failed: {ex.Message}";
            logger.LogWarning("Could not ask Lidarr to search again for album {AlbumId}: {Reason}", albumId, ex.Message);
        }
        await saves.NoteSearchedAgainAsync(albumId, error);
    }

    /// <summary>The file's own artist and title, which Navidrome indexes; Lidarr's when unreadable.</summary>
    private static (string Artist, string Title) Tags(string path, LidarrImportedTrack track)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return (file.Tag.FirstPerformer ?? file.Tag.FirstAlbumArtist ?? track.Artist ?? "",
                file.Tag.Title ?? track.Title);
        }
        catch
        {
            return (track.Artist ?? "", track.Title);
        }
    }
}
