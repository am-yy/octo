using System.Text;
using System.Text.Json;

namespace Octo.Services.Metadata;

/// <summary>
/// Deezer's own radios, as song radio's Deezer source asks them: the track mix (Deezer's songs
/// like one song, which the public API does not offer) and an artist's radio. Both keyless. The
/// track mix is the web player's gateway call, made with the anonymous session the catalog
/// client's cookie jar keeps; Deezer refuses it without those cookies.
/// </summary>
public partial class DeezerMetadataService
{
    /// <summary>A mix is Deezer's answer for a day; asking again gives about the same songs.</summary>
    private static readonly TimeSpan RadioTtl = TimeSpan.FromHours(24);
    private const string Gateway = "https://www.deezer.com/ajax/gw-light.php";

    private readonly SemaphoreSlim _gatewayTokenGate = new(1, 1);
    private string? _gatewayToken;

    /// <summary>
    /// Deezer's track mix for a catalog track, in Deezer's order; it may start with the track
    /// itself. Null when Deezer did not answer this time, so nothing is cached; empty when it
    /// answered with no songs.
    /// </summary>
    public Task<List<ChartTrack>?> GetTrackMixAsync(string deezerTrackId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deezerTrackId)) return Task.FromResult<List<ChartTrack>?>([]);
        var key = $"mix|{deezerTrackId}";
        if (TryGetCached<List<ChartTrack>>(key, out var cached)) return Task.FromResult(cached);
        return SharedAsync<List<ChartTrack>?>(key, async () =>
        {
            var body = JsonSerializer.Serialize(new { sng_id = deezerTrackId, start_with_input_track = false });
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var token = await GatewayTokenAsync(fresh: attempt > 0, ct);
                if (token is null) return null;
                using var doc = await GatewayAsync("song.getSearchTrackMix", token, body, ct);
                if (doc is null) return null;
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.EnumerateObject().Any())
                {
                    // A stale anonymous session: one fresh one, then give up for this call.
                    if (error.TryGetProperty("VALID_TOKEN_REQUIRED", out _) && attempt == 0) continue;
                    _logger.LogInformation("deezer track mix for {Id} refused: {Error}", deezerTrackId, error.GetRawText());
                    return null;
                }
                var tracks = ReadGatewayTracks(root);
                Put(key, tracks, tracks.Count == 0 ? NegativeTtl : RadioTtl);
                return tracks;
            }
            return null;
        }, ct);
    }

    /// <summary>Deezer's radio for a catalog artist: their songs and similar artists' songs.
    /// Null when Deezer did not answer this time.</summary>
    public Task<List<ChartTrack>?> GetArtistRadioAsync(string deezerArtistId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deezerArtistId)) return Task.FromResult<List<ChartTrack>?>([]);
        return ChartAsync($"artist-radio|{deezerArtistId}",
            $"{Base}/artist/{Uri.EscapeDataString(deezerArtistId)}/radio?limit={ChartLimit}", RadioTtl, ct);
    }

    /// <summary>The anonymous session's CSRF token (checkForm). Deezer sets the session's cookies
    /// on the same answer, and the catalog client keeps them.</summary>
    private async Task<string?> GatewayTokenAsync(bool fresh, CancellationToken ct)
    {
        await _gatewayTokenGate.WaitAsync(ct);
        try
        {
            if (!fresh && _gatewayToken is not null) return _gatewayToken;
            _gatewayToken = null;
            using var doc = await GatewayAsync("deezer.getUserData", "", null, ct);
            if (doc is null || !doc.RootElement.TryGetProperty("results", out var results)
                || Str(results, "checkForm") is not { Length: > 0 } token)
                return null;
            return _gatewayToken = token;
        }
        finally { _gatewayTokenGate.Release(); }
    }

    private async Task<JsonDocument?> GatewayAsync(string method, string token, string? body, CancellationToken ct)
    {
        try
        {
            var url = $"{Gateway}?method={method}&input=3&api_version=1.0&api_token={Uri.EscapeDataString(token)}";
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Client().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug("deezer gateway {Method} failed: {Message}", method, ex.Message);
            return null;
        }
    }

    /// <summary>The gateway's songs (results.data), read as the popularity lists read theirs.</summary>
    internal static List<ChartTrack> ReadGatewayTracks(JsonElement root)
    {
        var tracks = new List<ChartTrack>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object
            || !results.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return tracks;
        foreach (var t in data.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            var id = Str(t, "SNG_ID");
            var title = Str(t, "SNG_TITLE");
            var artist = Str(t, "ART_NAME");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) continue;
            // The gateway keeps "Radio Edit" and the like apart from the title; the public API joins them.
            if (Str(t, "VERSION") is { Length: > 0 } version) title = $"{title} {version}";
            var duration = int.TryParse(Str(t, "DURATION"), out var seconds) && seconds > 0 ? seconds : (int?)null;
            bool? explicitLyrics = Str(t, "EXPLICIT_LYRICS") switch { "1" => true, "0" => false, _ => null };
            tracks.Add(new ChartTrack(id!, title!, artist!, Str(t, "ART_ID"), Str(t, "ALB_TITLE"), Str(t, "ALB_ID"),
                null, duration, explicitLyrics));
        }
        return tracks;
    }
}
