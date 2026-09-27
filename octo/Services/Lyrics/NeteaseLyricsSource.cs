using System.Text.Json;

namespace Octo.Services.Lyrics;

/// <summary>
/// NetEase Cloud Music: much deeper than LRCLIB on non-Western and older music, and often synced.
/// Its API is undocumented and unlicensed, so it only runs when LYRICS_SOURCES names it. Every
/// lyric opens with contributor credits, which LyricsText.StripCredits removes.
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

    public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct) =>
        await FindInAsync(query, await SearchAsync(query, ct), ct);

    /// <summary>The lyrics of the first entry in a search already made that is this song.</summary>
    internal async Task<LyricsLookup> FindInAsync(LyricsQuery query, LyricsSearch search, CancellationToken ct)
    {
        if (search.Transient) return LyricsLookup.Failed;
        if (search.Candidates.FirstOrDefault(candidate => IsThisSong(candidate, query)) is not { } hit) return LyricsLookup.Miss;

        var lookup = await FetchAsync(hit.Id, ct);
        return lookup.Result is { } result
            ? new LyricsLookup(result with { Doubt = LyricsIdentity.Doubt(query.DurationSeconds, hit.DurationSeconds) }, false)
            : lookup;
    }

    internal static bool IsThisSong(LyricsCandidate candidate, LyricsQuery query) =>
        LyricsIdentity.SameSong(query.Title, query.Artist, candidate.Title, candidate.Artist,
            candidate.Artist.Split(" & ", StringSplitOptions.RemoveEmptyEntries))
        && LyricsIdentity.LengthFits(query.DurationSeconds, candidate.DurationSeconds);

    /// <summary>The songs a search for "artist title" returns, and for the same song written
    /// the ways <see cref="LyricsIdentity.Searches"/> gives, until one of them is this song.</summary>
    public async Task<LyricsSearch> SearchAsync(LyricsQuery query, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient(LrclibLyricsSource.ClientName);
            var candidates = new List<LyricsCandidate>();
            foreach (var variant in LyricsIdentity.Searches(query))
            {
                using var search = await GetJsonAsync(client,
                    $"https://music.163.com/api/search/get?s={Uri.EscapeDataString(variant.Text)}&type=1&limit=8", ct);
                if (search is null) return candidates.Count == 0 ? LyricsSearch.Failed : new LyricsSearch(candidates, true);
                candidates.AddRange(Read(search.RootElement).Where(found => candidates.All(seen => seen.Id != found.Id)));
                if (candidates.Any(candidate => IsThisSong(candidate, query))) break;
            }
            return new LyricsSearch(candidates, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LyricsSearch.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("NetEase search failed: {M}", ex.Message);
            return LyricsSearch.Failed;
        }
    }

    public async Task<LyricsLookup> FetchAsync(string id, CancellationToken ct)
    {
        if (!long.TryParse(id, out var number)) return LyricsLookup.Miss;
        try
        {
            using var lyric = await GetJsonAsync(_http.CreateClient(LrclibLyricsSource.ClientName),
                $"https://music.163.com/api/song/lyric?id={number}&lv=1&kv=1&tv=-1", ct);
            if (lyric is null) return LyricsLookup.Failed;

            var raw = lyric.RootElement.TryGetProperty("lrc", out var lrc) && lrc.TryGetProperty("lyric", out var body)
                && body.ValueKind == JsonValueKind.String ? body.GetString() : null;
            if (string.IsNullOrWhiteSpace(raw)) return LyricsLookup.Miss;

            var clean = LyricsText.StripCredits(raw);
            if (string.IsNullOrWhiteSpace(clean)) return LyricsLookup.Miss;
            return new LyricsLookup((LyricsText.HasTimestamps(clean)
                ? new LyricsResult("NetEase", clean, null, false)
                : new LyricsResult("NetEase", null, clean, false)) with { CandidateId = $"netease:{number}" }, false);
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

    /// <summary>The search's songs, in NetEase's order.</summary>
    internal static List<LyricsCandidate> Read(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array) return [];

        var candidates = new List<LyricsCandidate>();
        foreach (var song in songs.EnumerateArray())
        {
            if (!song.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
            var artists = song.TryGetProperty("artists", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(artist => artist.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "")
                    .Where(artist => artist.Length > 0).ToList()
                : [];
            var album = song.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object
                && al.TryGetProperty("name", out var aln) ? aln.GetString() : null;
            int? seconds = song.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.GetDouble() > 0
                ? (int)Math.Round(d.GetDouble() / 1000) : null;
            candidates.Add(new LyricsCandidate("netease", id.GetInt64().ToString(),
                song.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", string.Join(" & ", artists), album, seconds));
        }
        return candidates;
    }

    /// <summary>The id of the first result that is the same song, by the same artist, of the same length.</summary>
    internal static long? Pick(JsonElement root, LyricsQuery query) =>
        Read(root).FirstOrDefault(candidate => IsThisSong(candidate, query)) is { } hit ? long.Parse(hit.Id) : null;

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        using var response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct)) : null;
    }
}
