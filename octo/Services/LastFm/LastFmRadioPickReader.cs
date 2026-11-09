using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Radio;
using Octo.Models.Settings;
using Octo.Services.Subsonic;

namespace Octo.Services.LastFm;

/// <summary>A playlist as radio reads it: whose it is, when it changed, and whether it is smart.</summary>
public sealed record RadioPlaylist(string Id, string Name, string Owner, DateTime ChangedUtc, bool Smart);

/// <summary>A song in a playlist as radio reads it.</summary>
public sealed record RadioPlaylistSong(string MediaFileId, string Title, string Artist, string? Album, string? Genre, int? Duration);

/// <summary>Where radio reads every listener's playlists from: Navidrome, as the admin.</summary>
public interface IRadioPlaylistSource
{
    Task<IReadOnlyList<RadioPlaylist>?> ListRadioPlaylistsAsync(CancellationToken ct);
    Task<IReadOnlyList<RadioPlaylistSong>?> ListRadioPlaylistSongsAsync(string playlistId, CancellationToken ct);
}

/// <summary>Navidrome's native playlist rows, as radio reads them.</summary>
public static class RadioPlaylistRows
{
    public static IReadOnlyList<RadioPlaylist> ParsePlaylists(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];
        var rows = new List<RadioPlaylist>();
        foreach (var item in root.EnumerateArray())
        {
            if (Str(item, "id") is not { Length: > 0 } id || Str(item, "name") is not { Length: > 0 } name) continue;
            var changed = DateTime.TryParse(Str(item, "updatedAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : default;
            var smart = item.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Object;
            rows.Add(new RadioPlaylist(id, name, Str(item, "ownerName") ?? "", changed, smart));
        }
        return rows;
    }

    public static IReadOnlyList<RadioPlaylistSong> ParseSongs(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];
        var rows = new List<RadioPlaylistSong>();
        foreach (var item in root.EnumerateArray())
        {
            if (Str(item, "mediaFileId") is not { Length: > 0 } id) continue;
            int? duration = item.TryGetProperty("duration", out var seconds) && seconds.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(seconds.GetDouble()) : null;
            rows.Add(new RadioPlaylistSong(id, Str(item, "title") ?? "", Str(item, "artist") ?? "",
                Str(item, "album"), Str(item, "genre"), duration));
        }
        return rows;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Reads what each listener has chosen into radio's history before a build: the songs in their
/// own playlists, and Octo's record of their hearts on outside songs. Playlists are read as the
/// admin, whatever app they were edited in, so no client has to report an add; only a playlist
/// that changed since the last reading is read again. Octo's own action and notice playlists are
/// left out: what lands there is Octo's doing or a command, not a pick.
/// </summary>
public sealed class LastFmRadioPickReader(IRadioPlaylistSource navidrome, LastFmRadioStateStore state,
    IOptionsMonitor<LibraryActionSettings> actions, ILogger<LastFmRadioPickReader> logger, ExternalSaveStore? saves = null)
{
    public async Task<int> SyncAsync(string username, CancellationToken ct)
    {
        var marks = state.PlaylistMarks(username);
        // Octo's own copy of a playlist it keeps for the listener also holds the outside songs
        // that Navidrome has not got yet, so it is the whole playlist.
        var saved = (saves?.Snapshot().Playlists ?? [])
            .Where(playlist => Same(playlist.UserId, username))
            .GroupBy(playlist => playlist.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var listed = await navidrome.ListRadioPlaylistsAsync(ct);
        List<LastFmRadioStateStore.PlaylistRead>? reads = null;
        if (listed is not null)
        {
            var managed = ManagedNames(actions.CurrentValue);
            var own = listed.Where(playlist => Same(playlist.Owner, username) && !playlist.Smart).ToList();
            // A saved playlist Navidrome does not list yet, its copy still being made, is theirs too.
            own.AddRange(saved.Values.Where(playlist => listed.All(row => row.Id != playlist.Id))
                .Select(playlist => new RadioPlaylist(playlist.Id, playlist.Name, username, playlist.UpdatedUtc, false)));
            reads = [];
            foreach (var playlist in own.Where(playlist => !managed.Contains(playlist.Name)))
            {
                var copy = saved.GetValueOrDefault(playlist.Id);
                var changed = copy is not null && copy.UpdatedUtc > playlist.ChangedUtc ? copy.UpdatedUtc : playlist.ChangedUtc;
                IReadOnlyList<LastFmRadioPick>? songs = null;
                if (!marks.TryGetValue(playlist.Id, out var mark) || mark != changed)
                    songs = copy is not null
                        ? copy.Tracks.Where(track => track.Song is not null)
                            .Select(track => ToPick(track.Song!, track.CanonicalId ?? track.SongId)).ToList()
                        : (await navidrome.ListRadioPlaylistSongsAsync(playlist.Id, ct))?.Select(ToPick).ToList();
                reads.Add(new LastFmRadioStateStore.PlaylistRead(playlist.Id, playlist.Name,
                    songs is null && marks.TryGetValue(playlist.Id, out var last) ? last : changed, songs));
            }
        }
        var hearts = saves?.GetHearts(username).Select(heart =>
        {
            var pick = ToPick(heart.Song, heart.SongId);
            pick.PickedAtUtc = heart.UpdatedUtc;
            return pick;
        }).ToList();
        var added = state.ApplyPicks(username, reads, hearts);
        if (added > 0)
            logger.LogInformation("Radio picks for {User}: {Added} new from {Playlists} playlists and {Hearts} hearts",
                username, added, reads?.Count ?? 0, hearts?.Count ?? 0);
        return added;
    }

    /// <summary>The titles of Octo's action and notice playlists, while library actions are on.</summary>
    internal static IReadOnlySet<string> ManagedNames(LibraryActionSettings settings)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!settings.Enabled) return names;
        foreach (var action in settings.EffectiveActions().Where(action => action.Enabled)) names.Add(settings.PlaylistTitle(action));
        foreach (var kind in settings.EnabledNoticeKinds()) names.Add(settings.NoticeTitle(kind));
        return names;
    }

    private static LastFmRadioPick ToPick(Song song, string id) => new()
    {
        SongId = id, Artist = song.Artist, Title = song.Title, Album = song.Album,
        Genre = song.Genre, Duration = song.Duration, IsLocal = song.IsLocal,
    };

    private static LastFmRadioPick ToPick(RadioPlaylistSong song) => new()
    {
        SongId = song.MediaFileId, Artist = song.Artist, Title = song.Title, Album = song.Album,
        Genre = song.Genre, Duration = song.Duration, IsLocal = true,
    };

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
