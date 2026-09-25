using System.Net;
using System.Text.Json;

namespace Octo.Services.Lyrics;

/// <summary>
/// lyrics.ovh: plain text only, no timing, and it has had outages. It is the last resort, reached
/// only when nothing earlier had lyrics at all.
/// </summary>
public sealed class LyricsOvhLyricsSource : ILyricsSource
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<LyricsOvhLyricsSource> _logger;

    public LyricsOvhLyricsSource(IHttpClientFactory http, ILogger<LyricsOvhLyricsSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Key => "lyricsovh";

    public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(query.Artist)}/{Uri.EscapeDataString(query.Title)}";
            using var response = await _http.CreateClient(LrclibLyricsSource.ClientName).GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return LyricsLookup.Miss;
            if (!response.IsSuccessStatusCode) return LyricsLookup.Failed;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            var text = doc.RootElement.TryGetProperty("lyrics", out var lyrics) && lyrics.ValueKind == JsonValueKind.String
                ? lyrics.GetString()?.Replace("\r\n", "\n").Trim()
                : null;
            return string.IsNullOrWhiteSpace(text)
                ? LyricsLookup.Miss
                : new LyricsLookup(new LyricsResult("lyrics.ovh", null, text, false), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LyricsLookup.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("lyrics.ovh request failed: {M}", ex.Message);
            return LyricsLookup.Failed;
        }
    }
}
