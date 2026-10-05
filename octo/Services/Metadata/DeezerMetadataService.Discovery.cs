using Octo.Models.Domain;
using Octo.Services.Common;
using Octo.Services.Trackers;

namespace Octo.Services.Metadata;

public enum DiscoveryResolution { Resolved, Ambiguous, Unresolved }
public sealed record DiscoveryAlbumResult(DiscoveryResolution Status, DeezerAlbumManifest? Manifest,
    bool SearchComplete, int CandidatesInspected, string Diagnostic);

public partial class DeezerMetadataService
{
    /// <summary>Discovery only. Does not change Lidarr's acquisition fallback.</summary>
    public async Task<DiscoveryAlbumResult> ResolveDiscoveryAlbumAsync(Song song, CancellationToken ct = default, bool refresh = false)
    {
        // Authoritative provider membership takes precedence over title search.
        var id = song.DeezerId ?? (song.ExternalProvider == "deezer" ? song.ExternalId : null);
        if (id is not null)
        {
            using var response = await GetJsonAsync($"{Base}/track/{Uri.EscapeDataString(id)}", ct, background: true);
            if (response.Transient || response.Doc is null) return new(DiscoveryResolution.Unresolved, null, false, 0, "Track membership unavailable");
            var track = response.Doc.RootElement;
            var credit = track.TryGetProperty("artist", out var a) ? Str(a, "name") : null;
            var candidate = new AlbumTrack(TrackTitle(track) ?? "", credit ?? "", Int(track, "duration"),
                Int(track, "track_position"), Int(track, "disk_number"), Str(track, "isrc"), track.TryGetProperty("id", out var trackId) ? trackId.ToString() : null);
            if (candidate.DeezerId != id || !DiscoveryRecordingMatches(song, candidate))
                return new(DiscoveryResolution.Unresolved, null, false, 0, "Saved recording not verified");
            if (track.TryGetProperty("album", out var album) && Int(album, "id") is > 0)
            {
                var detail = await GetAlbumDetailAsync(Int(album, "id")!.Value.ToString(), ct, refresh);
                if (detail is null) return new(DiscoveryResolution.Unresolved, null, false, 1, "Album membership unavailable");
                if (detail.RecordType is "album" or "ep")
                {
                    var manifest = DeezerAlbumManifest.From(detail);
                    return manifest is not null && detail.Tracks.Any(t => DiscoveryRecordingMatches(song, t))
                        ? new(DiscoveryResolution.Resolved, manifest, true, 1, "Authoritative album membership")
                        : new(DiscoveryResolution.Unresolved, null, false, 1, "Authoritative manifest incomplete");
                }
            }
        }
        // ponytail: 25 parent hits bound refresh work; complete pagination is required if this ceiling grows.
        using var page = await GetJsonAsync($"{Base}/search/album?q={Uri.EscapeDataString(song.Artist)}&limit=25", ct, background: true);
        if (page.Transient || page.Doc is null || !page.Doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != System.Text.Json.JsonValueKind.Array)
            return new(DiscoveryResolution.Unresolved, null, false, 0, "Parent search unavailable");
        var root = page.Doc.RootElement;
        var complete = data.GetArrayLength() <= 25 && !(Str(root, "next") is { Length: > 0 })
            && (Int(root, "total") == data.GetArrayLength() || !root.TryGetProperty("total", out _) && data.GetArrayLength() < 25);
        var parents = new List<DeezerAlbumManifest>();
        var inspected = 0;
        foreach (var hit in data.EnumerateArray().Take(25))
        {
            if (hit.ValueKind != System.Text.Json.JsonValueKind.Object || Int(hit, "id") is not > 0)
            { complete = false; continue; }
            // Details, not incomplete search fields, determine parent type and membership.
            var detail = await GetAlbumDetailAsync(Int(hit, "id")!.Value.ToString(), ct, refresh);
            inspected++;
            if (detail is null) { complete = false; continue; }
            if (detail.RecordType is "single" or "compile") continue;
            if (detail.RecordType is not ("album" or "ep") || !detail.Complete) { complete = false; continue; }
            if (SongIdentity.Key(detail.Artist) != SongIdentity.Key(song.AlbumArtist ?? song.Artist)) continue;
            if (detail.Tracks.Any(t => DiscoveryRecordingMatches(song, t))) parents.Add(DeezerAlbumManifest.From(detail)!);
        }
        var identities = parents.GroupBy(p => SongIdentity.Key(p.Artist) + ":" + SongIdentity.Key(p.Title) + ":" + p.AlbumType).ToList();
        // Even equal titles are distinct parents unless ordered recordings agree.
        var consistent = identities.All(g => g.All(p => SameRecordingManifest(g.First(), p)));
        if (identities.Count > 1 || !consistent) return new(DiscoveryResolution.Ambiguous, null, complete, inspected, "Competing parent releases");
        if (!complete) return new(DiscoveryResolution.Unresolved, null, false, inspected, "Parent search incomplete or budget exhausted");
        return identities.Count == 1 ? new(DiscoveryResolution.Resolved, identities[0].First(), true, inspected, "Complete parent search")
            : new(DiscoveryResolution.Unresolved, null, true, inspected, "No verified album/EP parent");
    }

    internal static bool DiscoveryRecordingMatches(Song song, AlbumTrack track)
    {
        var wanted = SongIdentity.ParseTitle(song.Title, song.Artist);
        var actual = SongIdentity.ParseTitle(track.Title, track.Artist);
        if (!wanted.Versions.SequenceEqual(actual.Versions) || !wanted.Extras.SequenceEqual(actual.Extras)
            || song.Duration is > 0 && track.Duration is > 0 && Math.Abs(song.Duration.Value - track.Duration.Value) > SongIdentity.LengthToleranceSeconds)
            return false;
        var isrc = SongIdentity.NormalizeIsrc(song.Isrc);
        var got = SongIdentity.NormalizeIsrc(track.Isrc);
        if (isrc is not null && got is not null) return isrc == got;
        return song.Duration is > 0 && track.Duration is > 0
            && SongIdentity.Key(song.Artist) == SongIdentity.Key(track.Artist)
            && SongIdentity.Key(song.Title) == SongIdentity.Key(track.Title);
    }

    internal static bool SameRecordingManifest(DeezerAlbumManifest a, DeezerAlbumManifest b) =>
        a.Tracks.Count == b.Tracks.Count && a.Tracks.Zip(b.Tracks).All(pair =>
            pair.First.Disc == pair.Second.Disc && pair.First.Track == pair.Second.Track
            && DiscoveryRecordingMatches(new Song { Artist = pair.First.Artist, Title = pair.First.Title,
                Isrc = pair.First.Isrc, Duration = pair.First.Duration }, new AlbumTrack(pair.Second.Title,
                pair.Second.Artist, pair.Second.Duration, pair.Second.Track, pair.Second.Disc, pair.Second.Isrc)));
}
