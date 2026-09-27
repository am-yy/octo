using System.Net;
using System.Text;
using System.Text.Json;
using Octo.Services.Common;

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

    public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct) => (await LookUpAsync(query, ct)).Lookup;

    public async Task<LyricsSearch> SearchAsync(LyricsQuery query, CancellationToken ct)
    {
        var (lookup, asked) = await LookUpAsync(query, ct);
        if (lookup.Transient) return LyricsSearch.Failed;
        if (lookup.Result is not { } result) return LyricsSearch.Empty;
        return new LyricsSearch([new LyricsCandidate("lyricsovh", IdOf(asked.Artist, asked.Title), asked.Title, asked.Artist, null, null)
            { Lyrics = result }], false);
    }

    /// <summary>
    /// The song as asked, then written the other ways <see cref="LyricsIdentity.Searches"/> gives
    /// ("suicideboys" for "$uicideboy$", the primary artist alone). lyrics.ovh names nothing back,
    /// so nothing it answers can be checked: only a variant that is the same song by
    /// construction is tried, and none for a title that names a version ("Song (Live)"), whose
    /// cleaned query would ask for the original.
    /// </summary>
    private async Task<(LyricsLookup Lookup, SongQuery Asked)> LookUpAsync(LyricsQuery query, CancellationToken ct)
    {
        var searches = LyricsIdentity.Searches(query);
        if (SongIdentity.DistinctVersions(SongIdentity.ParseTitle(query.Title, query.Artist)).Count > 0)
            searches = [searches[0]];
        (LyricsLookup Lookup, SongQuery Asked) last = (LyricsLookup.Miss, searches[0]);
        foreach (var search in searches)
        {
            last = (await GetAsync(search.Artist, search.Title, ct), search);
            if (last.Lookup.Transient || last.Lookup.Result is not null) break;
        }
        return last;
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
