using System.Security.Cryptography;
using System.Text.Json;
using Octo.Services.Metadata;

namespace Octo.Services.Trackers;

public sealed record DeezerManifestTrack(string TrackId, int Disc, int Track, string Title,
    string Artist, string? Isrc, int? Duration);

/// <summary>Catalog completeness and exact source edition are independent of saved recordings.</summary>
public sealed record DeezerAlbumManifest(string AlbumId, string Revision, string AlbumType,
    int DeclaredCount, string Artist, string Title, bool Complete, List<DeezerManifestTrack> Tracks)
{
    public static string? Validate(DeezerMetadataService.AlbumDetail album)
    {
        if (!long.TryParse(album.DeezerId, out var id) || id <= 0 || string.IsNullOrWhiteSpace(album.Title)
            || string.IsNullOrWhiteSpace(album.Artist) || album.DeclaredTrackCount is not > 0
            || album.DeclaredTrackCount != album.Tracks.Count
            || album.ReturnedTotal is int total && total != album.Tracks.Count) return "Declared track count mismatch";
        var ids = new HashSet<string>();
        var positions = new HashSet<(int, int)>();
        foreach (var track in album.Tracks)
            if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)
                || !long.TryParse(track.DeezerId, out var tid) || tid <= 0 || !ids.Add(track.DeezerId!)
                || track.DiscNumber is not > 0 || track.TrackPosition is not > 0
                || !positions.Add((track.DiscNumber.Value, track.TrackPosition.Value))) return "Missing or duplicate track identity/position";
        var discs = album.Tracks.GroupBy(t => t.DiscNumber!.Value).OrderBy(g => g.Key).ToList();
        if (!discs.Select(g => g.Key).SequenceEqual(Enumerable.Range(1, discs.Count))
            || discs.Any(g => !g.Select(t => t.TrackPosition!.Value).Order().SequenceEqual(Enumerable.Range(1, g.Count()))))
            return "Non-contiguous disc/track positions";
        return null;
    }

    public static DeezerAlbumManifest? From(DeezerMetadataService.AlbumDetail album)
    {
        if (!album.Complete || Validate(album) is not null || album.RecordType is not ("album" or "ep")) return null;
        var tracks = album.Tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackPosition)
            .Select(t => new DeezerManifestTrack(t.DeezerId!, t.DiscNumber!.Value, t.TrackPosition!.Value,
                t.Title, t.Artist, t.Isrc, t.Duration)).ToList();
        var content = new { album.DeezerId, album.RecordType, album.DeclaredTrackCount, album.Artist, album.Title, tracks };
        var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content))).ToLowerInvariant();
        return new(album.DeezerId, revision, album.RecordType, tracks.Count, album.Artist, album.Title, true, tracks);
    }
}
