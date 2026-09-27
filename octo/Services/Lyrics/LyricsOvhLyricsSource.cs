using System.Net;
using System.Text;
using System.Text.Json;

namespace Octo.Services.Lyrics;

/// <summary>
/// lyrics.ovh: plain text only, no timing, and it has had outages. It is the last resort, reached
/// only when nothing earlier had lyrics at all. It looks songs up by exact artist and title and
/// names nothing back, so its one entry is the song as asked.
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

    public Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct) => GetAsync(query.Artist, query.Title, ct);

    public async Task<LyricsSearch> SearchAsync(LyricsQuery query, CancellationToken ct)
    {
        var lookup = await GetAsync(query.Artist, query.Title, ct);
        if (lookup.Transient) return LyricsSearch.Failed;
        if (lookup.Result is not { } result) return LyricsSearch.Empty;
        return new LyricsSearch([new LyricsCandidate("lyricsovh", IdOf(query.Artist, query.Title), query.Title, query.Artist, null, null)
            { Lyrics = result }], false);
    }

    public Task<LyricsLookup> FetchAsync(string id, CancellationToken ct)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(id.Replace('-', '+').Replace('_', '/').PadRight((id.Length + 3) / 4 * 4, '=')))
                .Split('\n', 2);
            return parts.Length == 2 ? GetAsync(parts[0], parts[1], ct) : Task.FromResult(LyricsLookup.Miss);
        }
        catch (FormatException)
        {
            return Task.FromResult(LyricsLookup.Miss);
        }
    }

    /// <summary>The artist and title, which are all lyrics.ovh looks a song up by.</summary>
    private static string IdOf(string artist, string title) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{artist}\n{title}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<LyricsLookup> GetAsync(string artist, string title, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(artist)}/{Uri.EscapeDataString(title)}";
            using var response = await _http.CreateClient(LrclibLyricsSource.ClientName).GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return LyricsLookup.Miss;
            if (!response.IsSuccessStatusCode) return LyricsLookup.Failed;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            var text = doc.RootElement.TryGetProperty("lyrics", out var lyrics) && lyrics.ValueKind == JsonValueKind.String
                ? lyrics.GetString()?.Replace("\r\n", "\n").Trim()
                : null;
            return string.IsNullOrWhiteSpace(text)
                ? LyricsLookup.Miss
                : new LyricsLookup(new LyricsResult("lyrics.ovh", null, text, false)
                {
                    CandidateId = $"lyricsovh:{IdOf(artist, title)}",
                    Doubt = "lyrics.ovh names no song and no length",
                }, false);
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
