using System.Net;
using System.Text.Json;

namespace Octo.Services.Lyrics;

/// <summary>
/// LRCLIB: open, keyless, and wide coverage of synced lyrics for current music. Its docs ask
/// callers to identify themselves, to go one request at a time with a short gap, and to honour
/// Retry-After; it sheds load with 503s often enough that all three matter. A few entries also
/// time their words (hasWordSync); those words live in the entry's Lyricsfile, not its LRC.
/// </summary>
public sealed class LrclibLyricsSource : ILyricsSource
{
    public const string ClientName = "lyrics";
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<LrclibLyricsSource> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;
    private DateTime _coolDownUntilUtc = DateTime.MinValue;

    public LrclibLyricsSource(IHttpClientFactory http, ILogger<LrclibLyricsSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Key => "lrclib";

    /// <summary>Until when the service asked to be left alone, for a caller that would rather
    /// wait than give up.</summary>
    internal DateTime CoolDownUntilUtc => _coolDownUntilUtc;

    public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct)
    {
        if (DateTime.UtcNow < _coolDownUntilUtc) return LyricsLookup.Failed;

        try
        {
            var get = await SendAsync(GetUrl(query), ct);
            if (get.Transient) return LyricsLookup.Failed;
            if (get.Json is { } found)
            {
                // LRCLIB's own lookup is close to exact, but not strict about the kind of
                // recording, so its answer is held to the same rule as a search hit.
                using (found)
                    if (IsThisSong(found.RootElement, query)) return new LyricsLookup(Parse(found.RootElement, query), false);
            }

            var search = await SendAsync(SearchUrl(query), ct);
            if (search.Transient) return LyricsLookup.Failed;
            if (search.Json is not { } results) return LyricsLookup.Miss;
            using (results)
                return new LyricsLookup(Pick(results.RootElement, query) is { } hit ? Parse(hit, query) : null, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LyricsLookup.Failed;
        }
    }

    public async Task<LyricsSearch> SearchAsync(LyricsQuery query, CancellationToken ct)
    {
        if (DateTime.UtcNow < _coolDownUntilUtc) return LyricsSearch.Failed;
        var search = await SendAsync(SearchUrl(query), ct);
        if (search.Transient) return LyricsSearch.Failed;
        if (search.Json is not { } results) return LyricsSearch.Empty;
        using (results)
        {
            if (results.RootElement.ValueKind != JsonValueKind.Array) return LyricsSearch.Empty;
            return new LyricsSearch(results.RootElement.EnumerateArray()
                .Where(row => row.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                .Take(12)
                .Select(row => new LyricsCandidate("lrclib", row.GetProperty("id").GetInt64().ToString(),
                    Str(row, "trackName") ?? Str(row, "name") ?? "", Str(row, "artistName") ?? "", Str(row, "albumName"),
                    Seconds(row) is { } seconds ? (int)Math.Round(seconds) : null)
                { Lyrics = Parse(row, null) })
                .ToList(), false);
        }
    }

    public async Task<LyricsLookup> FetchAsync(string id, CancellationToken ct)
    {
        if (!long.TryParse(id, out var number)) return LyricsLookup.Miss;
        if (DateTime.UtcNow < _coolDownUntilUtc) return LyricsLookup.Failed;
        var get = await SendAsync($"https://lrclib.net/api/get/{number}", ct);
        if (get.Transient) return LyricsLookup.Failed;
        if (get.Json is not { } found) return LyricsLookup.Miss;
        using (found) return new LyricsLookup(Parse(found.RootElement, null), false);
    }

    private static string GetUrl(LyricsQuery query)
    {
        var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(query.Title)}"
            + $"&artist_name={Uri.EscapeDataString(query.Artist)}";
        // An album that is only the title is Octo's own single fallback, not a release LRCLIB knows.
        if (!string.IsNullOrWhiteSpace(query.Album)
            && !string.Equals(query.Album, query.Title, StringComparison.OrdinalIgnoreCase))
            url += $"&album_name={Uri.EscapeDataString(query.Album)}";
        if (query.DurationSeconds is > 0 and <= 3600) url += $"&duration={query.DurationSeconds}";
        return url;
    }

    private static string SearchUrl(LyricsQuery query) =>
        $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(query.Title)}"
        + $"&artist_name={Uri.EscapeDataString(query.Artist)}";

    internal static bool IsThisSong(JsonElement row, LyricsQuery query) =>
        LyricsIdentity.SameSong(query.Title, query.Artist, Str(row, "trackName") ?? Str(row, "name"), Str(row, "artistName"))
        && LyricsIdentity.LengthFits(query.DurationSeconds, Seconds(row));

    /// <summary>The search hit that is the same song, timed ones first, then the same album,
    /// then the closest length. The search is loose and returns the artist's other songs too.</summary>
    internal static JsonElement? Pick(JsonElement results, LyricsQuery query)
    {
        if (results.ValueKind != JsonValueKind.Array) return null;
        return results.EnumerateArray()
            .Where(row => IsThisSong(row, query))
            .Where(row => Str(row, "syncedLyrics") is { Length: > 0 } || Str(row, "plainLyrics") is { Length: > 0 }
                || row.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True)
            .OrderBy(row => Str(row, "syncedLyrics") is { Length: > 0 } ? 0 : 1)
            .ThenBy(row => string.Equals(Str(row, "albumName"), query.Album, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(row => query.DurationSeconds is > 0 && Seconds(row) is { } seconds ? Math.Abs(seconds - query.DurationSeconds.Value) : 0)
            .Cast<JsonElement?>()
            .FirstOrDefault();
    }

    /// <summary>An entry's lyrics. With hasWordSync, the Lyricsfile's words become enhanced LRC;
    /// when that cannot be read, the entry's own LRC is used as before.</summary>
    internal static LyricsResult Parse(JsonElement row, LyricsQuery? query)
    {
        var synced = Str(row, "syncedLyrics");
        if (row.TryGetProperty("hasWordSync", out var word) && word.ValueKind == JsonValueKind.True
            && LyricsfileReader.ReadLines(Str(row, "lyricsfile")) is { } lines && lines.Any(line => line.Words.Count > 0))
            synced = LyricsText.WriteLrc(lines);

        return new LyricsResult("LRCLIB", synced, Str(row, "plainLyrics"),
            row.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True)
        {
            CandidateId = row.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? $"lrclib:{id.GetInt64()}" : null,
            Doubt = query is null ? null : LyricsIdentity.Doubt(query.DurationSeconds, Seconds(row)),
        };
    }

    private static double? Seconds(JsonElement row) =>
        row.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.GetDouble() > 0 ? d.GetDouble() : null;

    private sealed record Answer(JsonDocument? Json, bool Transient);

    private async Task<Answer> SendAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var gap = _lastCallUtc + MinimumGap - DateTime.UtcNow;
            if (gap > TimeSpan.Zero) await Task.Delay(gap, ct);
            _lastCallUtc = DateTime.UtcNow;

            using var response = await _http.CreateClient(ClientName).GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? DefaultCooldown;
                _coolDownUntilUtc = DateTime.UtcNow + wait;
                _logger.LogInformation("LRCLIB asked Octo to wait {Seconds:0}s", wait.TotalSeconds);
                return new(null, true);
            }
            if (!response.IsSuccessStatusCode) return new(null, true);
            return new(JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct)), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("LRCLIB request failed: {M}", ex.Message);
            return new(null, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
