using System.Text.Json;

namespace Octo.Services.Library;

/// <summary>One song as Navidrome's native song list describes it.</summary>
public sealed record LibrarySongRow(string Id, string Path, string? LibraryPath, long Size, string Suffix,
    int BitRate, string Title, string Artist, int? Duration, string? Album = null, string? AlbumId = null,
    string? AlbumArtist = null, IReadOnlyList<string>? Isrcs = null);

/// <summary>Reads Navidrome's native <c>/api/song</c> list. Upstream keeps this in its quality
/// upgrade worker, which this fork does not have.</summary>
public static class LibrarySongRows
{
    public static (IReadOnlyList<LibrarySongRow> Rows, int Count)? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;
        var rows = new List<LibrarySongRow>();
        var count = 0;
        foreach (var song in root.EnumerateArray())
        {
            count++;
            if (song.TryGetProperty("missing", out var m) && m.ValueKind == JsonValueKind.True) continue;
            var id = Str(song, "id");
            var path = Str(song, "path");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path)) continue;
            rows.Add(new LibrarySongRow(id, path, Str(song, "libraryPath"),
                song.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var bytes) ? bytes : 0,
                Str(song, "suffix") ?? "",
                song.TryGetProperty("bitRate", out var br) && br.TryGetInt32(out var rate) ? rate : 0,
                Str(song, "title") ?? "", Str(song, "artist") ?? "",
                // A float in Navidrome's native API, unlike Subsonic's whole seconds.
                song.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? (int)Math.Round(d.GetDouble()) : null,
                Str(song, "album"), Str(song, "albumId"), Str(song, "albumArtist"), IsrcsOf(song)));
        }
        return (rows, count);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A song's ISRCs: Navidrome files them among its tags, as a list; one written as
    /// a single string is read as a list of one.</summary>
    private static IReadOnlyList<string> IsrcsOf(JsonElement song)
    {
        var found = new List<string>();
        void Read(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } one) found.Add(one);
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Read(item);
        }
        if (song.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
            && tags.TryGetProperty("isrc", out var tagged)) Read(tagged);
        if (song.TryGetProperty("isrc", out var direct)) Read(direct);
        return found;
    }
}
