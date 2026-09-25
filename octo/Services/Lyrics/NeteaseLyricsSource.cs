using System.Text.Json;
using Octo.Services.Fingerprint;

namespace Octo.Services.Lyrics;

/// <summary>
/// NetEase Cloud Music: much deeper than LRCLIB on non-Western and older music, and often synced.
/// Its API is undocumented and unlicensed, so it only runs when LYRICS_SOURCES names it. Every
/// lyric opens with contributor credits, which LyricsText.StripNeteaseCredits removes.
/// </summary>
public sealed class NeteaseLyricsSource : ILyricsSource
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NeteaseLyricsSource> _logger;

    public NeteaseLyricsSource(IHttpClientFactory http, ILogger<NeteaseLyricsSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Key => "netease";

    public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient(LrclibLyricsSource.ClientName);
            var search = await GetJsonAsync(client,
                $"https://music.163.com/api/search/get?s={Uri.EscapeDataString($"{query.Artist} {query.Title}")}&type=1&limit=5", ct);
            if (search is null) return LyricsLookup.Failed;

            long? id;
            using (search) id = Pick(search.RootElement, query);
            if (id is null) return LyricsLookup.Miss;

            using var lyric = await GetJsonAsync(client,
                $"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1", ct);
            if (lyric is null) return LyricsLookup.Failed;

            var raw = lyric.RootElement.TryGetProperty("lrc", out var lrc) && lrc.TryGetProperty("lyric", out var body)
                && body.ValueKind == JsonValueKind.String ? body.GetString() : null;
            if (string.IsNullOrWhiteSpace(raw)) return LyricsLookup.Miss;

            var clean = LyricsText.StripNeteaseCredits(raw);
            if (string.IsNullOrWhiteSpace(clean)) return LyricsLookup.Miss;
            return new LyricsLookup(LyricsText.HasTimestamps(clean)
                ? new LyricsResult("NetEase", clean, null, false)
                : new LyricsResult("NetEase", null, clean, false), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LyricsLookup.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("NetEase lyrics request failed: {M}", ex.Message);
            return LyricsLookup.Failed;
        }
    }

    /// <summary>The first result that is the same song, by the same artist, of the same length.</summary>
    internal static long? Pick(JsonElement root, LyricsQuery query)
    {
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array) return null;

        foreach (var song in songs.EnumerateArray())
        {
            var name = song.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!TrackMatchComparer.TitleMatches(query.Title, name)) continue;

            var artists = song.TryGetProperty("artists", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(artist => artist.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "")
                    .Where(artist => artist.Length > 0).ToList()
                : [];
            if (!TrackMatchComparer.ArtistMatches(query.Artist, string.Join(" & ", artists), artists)) continue;

            if (query.DurationSeconds is > 0 && song.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                && Math.Abs(d.GetDouble() / 1000 - query.DurationSeconds.Value) > 3) continue;

            if (song.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number) return id.GetInt64();
        }
        return null;
    }

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        using var response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct)) : null;
    }
}
