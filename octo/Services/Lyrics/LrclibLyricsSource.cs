using System.Net;
using System.Text.Json;
using Octo.Services.Fingerprint;

namespace Octo.Services.Lyrics;

/// <summary>
/// LRCLIB: open, keyless, and the best coverage of synced lyrics for current music, so it is
/// asked first. Its docs ask callers to identify themselves, to go one request at a time with a
/// short gap, and to honour Retry-After; it sheds load with 503s often enough that all three
/// matter.
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

    public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct)
    {
        if (DateTime.UtcNow < _coolDownUntilUtc) return LyricsLookup.Failed;

        await _gate.WaitAsync(ct);
        try
        {
            var get = await SendAsync(GetUrl(query), ct);
            if (get.Transient) return LyricsLookup.Failed;
            if (get.Json is { } found)
            {
                using (found)
                    return new LyricsLookup(Parse(found.RootElement), false);
            }

            var search = await SendAsync(SearchUrl(query), ct);
            if (search.Transient) return LyricsLookup.Failed;
            if (search.Json is not { } results) return LyricsLookup.Miss;
            using (results)
                return new LyricsLookup(Pick(results.RootElement, query) is { } hit ? Parse(hit) : null, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LyricsLookup.Failed;
        }
        finally
        {
            _gate.Release();
        }
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

    /// <summary>The first result that is the same song and, when the length is known, within the
    /// two seconds LRCLIB itself allows.</summary>
    internal static JsonElement? Pick(JsonElement results, LyricsQuery query)
    {
        if (results.ValueKind != JsonValueKind.Array) return null;
        foreach (var row in results.EnumerateArray())
        {
            var title = Str(row, "trackName") ?? Str(row, "name");
            if (!TrackMatchComparer.TitleMatches(query.Title, title)) continue;
            if (!TrackMatchComparer.ArtistMatches(query.Artist, Str(row, "artistName"), null)) continue;
            if (query.DurationSeconds is > 0 && row.TryGetProperty("duration", out var d)
                && d.ValueKind == JsonValueKind.Number && Math.Abs(d.GetDouble() - query.DurationSeconds.Value) > 2)
                continue;
            return row;
        }
        return null;
    }

    internal static LyricsResult Parse(JsonElement row) => new("LRCLIB",
        Str(row, "syncedLyrics"), Str(row, "plainLyrics"),
        row.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True);

    private sealed record Answer(JsonDocument? Json, bool Transient);

    private async Task<Answer> SendAsync(string url, CancellationToken ct)
    {
        var gap = _lastCallUtc + MinimumGap - DateTime.UtcNow;
        if (gap > TimeSpan.Zero) await Task.Delay(gap, ct);
        _lastCallUtc = DateTime.UtcNow;

        try
        {
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
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
