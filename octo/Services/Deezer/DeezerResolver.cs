using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Octo.Models.Settings;
using Octo.Services.Metadata;

namespace Octo.Services.Deezer;

/// <summary>
/// Native account-backed media resolution adapted from V1ck3s/octo-fiesta (GPL-3.0).
/// Session tokens stay local to one account attempt; fallback accounts never share cookies.
/// </summary>
public sealed class DeezerResolver(IHttpClientFactory httpFactory, IConfiguration configuration,
    ILogger<DeezerResolver> logger, DeezerMetadataService catalog) : IDisposable
{
    public const string ApiClientName = "deezer-media-api";
    public const string StreamClientName = "deezer-media-stream";
    private readonly MemoryCache _mediaCache = new(new MemoryCacheOptions { SizeLimit = 128 });
    // ponytail: serial media resolution bounds gateway bursts; add per-account gates if contention grows.
    private readonly SemaphoreSlim _mediaGate = new(1);
    private DeezerSettings Settings => configuration.GetSection("Deezer").Get<DeezerSettings>() ?? new();
    public bool IsConfigured => Accounts(Settings).Any();
    public string DownloadQuality => Settings.Quality;

    private sealed record Session(string Cookie, string ApiToken, string LicenseToken);
    private sealed record Media(string TrackId, string Format, string Url, string Account = "");

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        foreach (var arl in Accounts(Settings))
        {
            try { await AuthenticateAsync(arl, ct); return true; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* Try the fallback account without logging credentials. */ }
        }
        return false;
    }

    private static IEnumerable<string> Accounts(DeezerSettings settings) =>
        new[] { settings.Arl, settings.ArlFallback }
            .Where(arl => !string.IsNullOrWhiteSpace(arl)).Select(arl => arl!.Trim()).Distinct();

    private async Task<Session> AuthenticateAsync(string arl, CancellationToken ct)
    {
        if (arl.Length > 1024 || !arl.All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException("Invalid Deezer ARL cookie.");
        var cookie = $"arl={arl}";
        using var request = JsonRequest(Gateway("deezer.getUserData", "null"), new { }, cookie);
        using var response = await httpFactory.CreateClient(ApiClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (HasError(root) || !root.TryGetProperty("results", out var results)
            || !results.TryGetProperty("USER", out var user)
            || Text(user, "USER_ID") is null or "0"
            || Text(results, "checkForm") is not { Length: > 0 } apiToken
            || !user.TryGetProperty("OPTIONS", out var options)
            || Text(options, "license_token") is not { Length: > 0 } license)
            throw new InvalidOperationException("Deezer ARL expired or account has no full-track license.");

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var sid = cookies.Select(value => value.Split(';')[0]).FirstOrDefault(value => value.StartsWith("sid=", StringComparison.Ordinal));
            if (sid is not null) cookie += $"; {sid}";
        }
        return new Session(cookie, apiToken, license);
    }

    private async Task<Media> ResolveMediaAsync(string trackId, string quality, CancellationToken ct,
        IReadOnlySet<string> rejectedAccounts, bool strictFlac = false)
    {
        if (!ValidTrackId(trackId)) throw new ArgumentException("Invalid Deezer track ID.", nameof(trackId));
        var settings = Settings;
        await _mediaGate.WaitAsync(ct);
        try
        {
            foreach (var arl in Accounts(settings).Where(arl => !rejectedAccounts.Contains(arl)))
            {
                var key = strictFlac
                    ? $"{arl}\0{trackId}\0{quality}\0strict-flac"
                    : $"{arl}\0{trackId}\0{quality}";
                if (_mediaCache.TryGetValue(key, out Media? cached)) return cached!;
                try
                {
                    var session = await AuthenticateAsync(arl, ct);
                    var visited = new HashSet<string>();
                    var candidate = trackId;
                    for (var attempt = 0; attempt < 3 && visited.Add(candidate); attempt++)
                    {
                        using var page = await PostAsync(Gateway("deezer.pageTrack", session.ApiToken),
                            new { SNG_ID = candidate }, session.Cookie, ct);
                        if (HasError(page.RootElement)
                            || !page.RootElement.TryGetProperty("results", out var results)
                            || !results.TryGetProperty("DATA", out var data)) break;
                        var actualId = Text(data, "SNG_ID") ?? candidate;
                        if (!ValidTrackId(actualId)) break;
                        if (Text(data, "TRACK_TOKEN") is { Length: > 0 } trackToken)
                        {
                            using var media = await PostAsync("https://media.deezer.com/v1/get_url", new
                            {
                                license_token = session.LicenseToken,
                                media = new[] { new { type = "FULL", formats = Formats(quality, strictFlac)
                                    .Select(format => new { cipher = "BF_CBC_STRIPE", format }).ToArray() } },
                                track_tokens = new[] { trackToken },
                            }, cookie: null, ct: ct);
                            if (SelectMedia(media.RootElement, actualId, quality, strictFlac) is { } found)
                            {
                                found = found with { Account = arl };
                                _mediaCache.Set(key, found, new MemoryCacheEntryOptions
                                    { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });
                                return found;
                            }
                        }
                        // Alternative bytes must use the alternative track's Blowfish key.
                        var fallback = data.TryGetProperty("FALLBACK", out var alt) ? Text(alt, "SNG_ID") : null;
                        if (fallback is not null && ValidTrackId(fallback) && !visited.Contains(fallback))
                        { candidate = fallback; continue; }
                        string? id = null;
                        if (Text(data, "ISRC") is { Length: > 0 } isrc)
                        {
                            using var response = await httpFactory.CreateClient(DeezerRateLimiter.ClientName)
                                .GetAsync($"https://api.deezer.com/track/isrc:{Uri.EscapeDataString(isrc)}", ct);
                            if (response.IsSuccessStatusCode)
                            {
                                using var sameRecording = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                                id = Text(sameRecording.RootElement, "id");
                            }
                        }
                        if (id is null || !ValidTrackId(id) || visited.Contains(id))
                        {
                            id = Text(data, "ART_NAME") is { Length: > 0 } artist
                                && Text(data, "SNG_TITLE") is { Length: > 0 } title
                                ? await catalog.FindAlternativeTrackIdAsync(artist, title, visited, ct) : null;
                        }
                        if (id is null || !ValidTrackId(id) || visited.Contains(id)) break;
                        candidate = id;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogDebug("Deezer media attempt failed: {Type}", ex.GetType().Name); }
            }
            throw new InvalidOperationException("No full-track Deezer media available. Check ARL, subscription and region.");
        }
        finally { _mediaGate.Release(); }
    }

    internal static string[] Formats(string? quality, bool strictFlac = false)
    {
        if (strictFlac) return ["FLAC"];
        return quality?.Trim().ToUpperInvariant() switch
    {
        "MP3_128" => ["MP3_128"],
        "MP3_320" => ["MP3_320", "MP3_128"],
        _ => ["FLAC", "MP3_320", "MP3_128"],
    };
    }

    private static Media? SelectMedia(JsonElement root, string trackId, string quality,
        bool strictFlac = false)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0 || !data[0].TryGetProperty("media", out var media)
            || media.ValueKind != JsonValueKind.Array) return null;
        foreach (var format in Formats(quality, strictFlac))
            foreach (var item in media.EnumerateArray())
                if (Text(item, "format") == format
                    && item.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
                    foreach (var source in sources.EnumerateArray())
                        if (Text(source, "url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                            && uri.Scheme == Uri.UriSchemeHttps)
                            return new Media(trackId, format, url);
        return null;
    }

    public async Task<(Stream stream, string contentType, long? contentLength, int statusCode,
        string? contentRange, HttpResponseMessage owner)?> OpenStreamAsync(string trackId,
        string? rangeHeader = null, CancellationToken ct = default, string quality = "MP3_320")
        => await OpenStreamCoreAsync(trackId, rangeHeader, ct, quality, strictFlac: false);

    /// <summary>Opens only FLAC media. Playback caches must never accept a lossy fallback.</summary>
    public Task<(Stream stream, string contentType, long? contentLength, int statusCode,
        string? contentRange, HttpResponseMessage owner)?> OpenFlacStreamAsync(string trackId,
        CancellationToken ct = default) => OpenStreamCoreAsync(trackId, null, ct, "FLAC", strictFlac: true);

    private async Task<(Stream stream, string contentType, long? contentLength, int statusCode,
        string? contentRange, HttpResponseMessage owner)?> OpenStreamCoreAsync(string trackId,
        string? rangeHeader, CancellationToken ct, string quality, bool strictFlac)
    {
        var rejectedAccounts = new HashSet<string>();
        var refreshed = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            HttpResponseMessage? response = null;
            Media? media = null;
            try
            {
                media = await ResolveMediaAsync(trackId, quality, ct, rejectedAccounts, strictFlac);
                var http = httpFactory.CreateClient(StreamClientName);
                // Read headers first to learn total size, including for suffix ranges.
                response = await http.GetAsync(media.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                long start = 0, skip = 0;
                long? length = total;
                var status = 200;
                string? contentRange = null;
                if (total is > 0 && RangeHeaderValue.TryParse(rangeHeader, out var range)
                    && range.Unit == "bytes" && range.Ranges.Count == 1)
                {
                    var part = range.Ranges.Single();
                    start = part.From ?? Math.Max(0, total.Value - part.To.GetValueOrDefault());
                    var end = part.From.HasValue ? Math.Min(part.To ?? total.Value - 1, total.Value - 1) : total.Value - 1;
                    if (start >= total.Value || start > end)
                    {
                        response.Dispose();
                        response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
                        var owner416 = response; response = null;
                        return (Stream.Null, "audio/mpeg", 0, 416, $"bytes */{total}", owner416);
                    }
                    length = end - start + 1;
                    status = 206;
                    contentRange = $"bytes {start}-{end}/{total}";
                    var aligned = start / 2048 * 2048;
                    if (aligned > 0)
                    {
                        response.Dispose(); response = null;
                        using var request = new HttpRequestMessage(HttpMethod.Get, media.Url);
                        // Fetch to EOF so the final encrypted stripe is never truncated.
                        request.Headers.Range = new RangeHeaderValue(aligned, null);
                        response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            if (response.Content.Headers.ContentRange?.From != aligned)
                                throw new IOException("Deezer CDN returned an unexpected byte range.");
                            skip = start - aligned;
                        }
                        else { aligned = 0; skip = start; }
                    }
                    else skip = start;
                    start = aligned;
                }
                var raw = await response.Content.ReadAsStreamAsync(ct);
                var stream = new DeezerDecryptedStream(raw, media.TrackId, start / 2048, skip, length);
                var owner = response; response = null;
                return (stream, media.Format == "FLAC" ? "audio/flac" : "audio/mpeg", length, status, contentRange, owner);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { response?.Dispose(); throw; }
            catch (Exception ex)
            {
                response?.Dispose();
                logger.LogWarning("Deezer stream unavailable for track {TrackId}: {Type}", trackId, ex.GetType().Name);
                if (media is null) return null;
                var key = strictFlac
                    ? $"{media.Account}\0{trackId}\0{quality}\0strict-flac"
                    : $"{media.Account}\0{trackId}\0{quality}";
                _mediaCache.Remove(key);
                if (refreshed) rejectedAccounts.Add(media.Account);
                refreshed = true;
            }
        }
        return null;
    }

    public async Task<string?> DownloadAsync(string trackId, string destWithoutExt,
        CancellationToken ct = default)
    {
        var opened = await OpenStreamAsync(trackId, ct: ct, quality: DownloadQuality);
        if (opened is null) return null;
        var (stream, type, expected, _, _, owner) = opened.Value;
        using (owner)
        await using (stream)
        {
            var path = destWithoutExt + (type == "audio/flac" ? ".flac" : ".mp3");
            var created = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    await stream.CopyToAsync(file, ct);
                    if (file.Length == 0 || expected is long size && file.Length != size)
                        throw new IOException("Incomplete Deezer download.");
                }
                return path;
            }
            catch { if (created) File.Delete(path); throw; }
        }
    }

    private async Task<JsonDocument> PostAsync(string url, object body, string? cookie, CancellationToken ct)
    {
        using var request = JsonRequest(url, body, cookie);
        using var response = await httpFactory.CreateClient(ApiClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static HttpRequestMessage JsonRequest(string url, object body, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.Add("Cookie", cookie);
        return request;
    }
    private static string Gateway(string method, string token) =>
        $"https://www.deezer.com/ajax/gw-light.php?method={method}&input=3&api_version=1.0&api_token={Uri.EscapeDataString(token)}";
    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString() : null;
    private static bool HasError(JsonElement root) => root.TryGetProperty("error", out var error)
        && error.ValueKind switch
        {
            JsonValueKind.Array => error.GetArrayLength() > 0,
            JsonValueKind.Object => error.EnumerateObject().Any(),
            JsonValueKind.Null => false,
            _ => true,
        };
    private static bool ValidTrackId(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;
    public void Dispose() { _mediaCache.Dispose(); _mediaGate.Dispose(); }
}
