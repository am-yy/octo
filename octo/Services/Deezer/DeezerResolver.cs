using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Octo.Models.Settings;
using Octo.Services.Metadata;

namespace Octo.Services.Deezer;

/// <summary>
/// Native account-backed media resolution adapted from V1ck3s/octo-fiesta (GPL-3.0).
/// One account, one cached session. A rejected ARL latches Deezer off until the ARL changes, and
/// failed logins and unavailable tracks are remembered, so background retries never reach Deezer.
/// </summary>
public sealed class DeezerResolver(IHttpClientFactory httpFactory, IConfiguration configuration,
    ILogger<DeezerResolver> logger, DeezerMetadataService catalog) : IDisposable
{
    public const string ApiClientName = "deezer-media-api";
    public const string StreamClientName = "deezer-media-stream";
    /// <summary>Most tracks one song.getListData or get_url call resolves.</summary>
    public const int MaxBatch = 25;
    // ponytail: mirrors the Deezer desktop release this flow was read from (7.1.340); bump with the client.
    public const string UserAgent = "Deezer/7.1.340 (Electron; osx/15.1.0; Desktop; en)";
    public const string Origin = "https://www.deezer.com";
    private const string DefaultMediaService = "https://media.deezer.com";
    private static readonly TimeSpan FallbackSessionLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan FallbackMediaLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MissLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaxLoginBackoff = TimeSpan.FromHours(1);
    // Room for batched resolves: entries are a few URLs each.
    private readonly MemoryCache _mediaCache = new(new MemoryCacheOptions { SizeLimit = 1024 });
    // Unbounded on purpose: a size-limited cache drops new entries when full, and a dropped miss is a retry storm.
    private readonly MemoryCache _misses = new(new MemoryCacheOptions());
    // Session state below is only touched under _mediaGate.
    private (string Arl, Session Session)? _session;
    private string? _rejectedArl;
    private (string Arl, DateTime UntilUtc, TimeSpan Backoff)? _loginCooldown;
    // ponytail: serial media resolution bounds gateway bursts; add per-account gates if contention grows.
    private readonly SemaphoreSlim _mediaGate = new(1);
    private DeezerSettings Settings => configuration.GetSection("Deezer").Get<DeezerSettings>() ?? new();
    public bool IsConfigured => Arl(Settings) is not null;
    /// <summary>True while Deezer has rejected the configured ARL. Saving a different ARL clears it.</summary>
    public bool ArlRejected => Arl(Settings) is { } arl && arl == _rejectedArl;
    public string DownloadQuality => Settings.Quality;
    public string CacheQuality => NormalizeStrictQuality(Settings.CacheQuality);

    private sealed record Session(string Cookie, string ApiToken, string LicenseToken, string MediaService,
        DateTime ExpiresUtc);
    private sealed record Media(string TrackId, string Format, string[] Urls, DateTime ExpiresUtc,
        string Account = "");

    /// <summary>Requests look like the Deezer desktop client, which sets these on every Deezer request.</summary>
    public static void ApplyClientHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Add("Origin", Origin);
    }

    /// <summary>Reuses the cached session; never logs in again while the ARL is rejected or cooling down.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (Arl(Settings) is not { } arl) return false;
        await _mediaGate.WaitAsync(ct);
        try { await GetSessionAsync(arl, ct); return true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
        finally { _mediaGate.Release(); }
    }

    private static string? Arl(DeezerSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Arl) ? null : settings.Arl.Trim();

    private sealed class ArlRejectedException(string message) : InvalidOperationException(message);

    /// <summary>Caller holds _mediaGate. Fresh is true when this call performed the login.</summary>
    private async Task<(Session Session, bool Fresh)> GetSessionAsync(string arl, CancellationToken ct,
        bool forceFresh = false)
    {
        if (arl == _rejectedArl)
            throw new ArlRejectedException("Deezer rejected the configured ARL. Set a new one.");
        if (!forceFresh && _session is { } cached && cached.Arl == arl && DateTime.UtcNow < cached.Session.ExpiresUtc)
            return (cached.Session, false);
        if (_loginCooldown is { } cooldown && cooldown.Arl == arl && DateTime.UtcNow < cooldown.UntilUtc)
            throw new InvalidOperationException("Deezer login is cooling down after a failure.");
        _session = null;
        try
        {
            var session = await AuthenticateAsync(arl, ct);
            _session = (arl, session);
            _loginCooldown = null;
            logger.LogInformation("Deezer session established");
            return (session, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ArlRejectedException ex)
        {
            _rejectedArl = arl;
            logger.LogError("{Reason} Deezer is disabled until the ARL changes.", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            var backoff = _loginCooldown is { } previous && previous.Arl == arl
                ? TimeSpan.FromTicks(Math.Min(previous.Backoff.Ticks * 2, MaxLoginBackoff.Ticks))
                : TimeSpan.FromMinutes(1);
            _loginCooldown = (arl, DateTime.UtcNow + backoff, backoff);
            logger.LogWarning("Deezer login failed ({Detail}); no login attempts for {Backoff}",
                Describe(ex), backoff);
            throw;
        }
    }

    private async Task<Session> AuthenticateAsync(string arl, CancellationToken ct)
    {
        if (arl.Length > 1024 || !arl.All(char.IsAsciiLetterOrDigit))
            throw new ArlRejectedException("Invalid Deezer ARL cookie.");
        var cookie = $"arl={arl}";
        using var request = JsonRequest(Gateway("deezer.getUserData", "null"), new { }, cookie);
        using var response = await httpFactory.CreateClient(ApiClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (HasError(root))
            throw new InvalidOperationException($"Deezer getUserData error {ErrorText(root)}");
        if (!root.TryGetProperty("results", out var results)
            || !results.TryGetProperty("USER", out var user)
            || Text(user, "USER_ID") is null or "0"
            || Text(results, "checkForm") is not { Length: > 0 } apiToken
            || !user.TryGetProperty("OPTIONS", out var options)
            || Text(options, "license_token") is not { Length: > 0 } license)
            throw new ArlRejectedException("Deezer ARL expired or account has no full-track license.");

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var sid = cookies.Select(value => value.Split(';')[0]).FirstOrDefault(value => value.StartsWith("sid=", StringComparison.Ordinal));
            if (sid is not null) cookie += $"; {sid}";
        }
        // The license token goes only to a Deezer media service.
        var mediaService = Text(results, "URL_MEDIA") is { } advertised
            && Uri.TryCreate(advertised, UriKind.Absolute, out var media) && media.Scheme == Uri.UriSchemeHttps
            && media.Host.EndsWith(".deezer.com", StringComparison.OrdinalIgnoreCase)
                ? advertised.TrimEnd('/') : DefaultMediaService;
        // Keep the session until the license expires, as the desktop client does.
        var expires = Expiry(options, "expiration_timestamp") is { } until && until > DateTime.UtcNow
            ? until : DateTime.UtcNow + FallbackSessionLifetime;
        return new Session(cookie, apiToken, license, mediaService, expires);
    }

    /// <summary>A Unix-seconds field less the expiry margin, or null when absent. May already be past.</summary>
    private static DateTime? Expiry(JsonElement element, string name) =>
        Text(element, name) is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && seconds < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime - ExpiryMargin : null;

    /// <summary>Caller holds _mediaGate. A stale cached session gets one fresh login, then the request repeats.</summary>
    private async Task<(JsonDocument Doc, Session Session, bool Fresh)> WithSessionAsync(string arl,
        Session session, bool fresh, Func<Session, Task<JsonDocument>> send, Func<JsonElement, bool> stale,
        CancellationToken ct)
    {
        var doc = await send(session);
        if (fresh || !stale(doc.RootElement)) return (doc, session, fresh);
        doc.Dispose();
        (session, _) = await GetSessionAsync(arl, ct, forceFresh: true);
        return (await send(session), session, true);
    }

    private Task<JsonDocument> GetListDataAsync(Session session, IReadOnlyList<string> ids, CancellationToken ct) =>
        PostAsync(Gateway("song.getListData", session.ApiToken), new { sng_ids = ids }, session.Cookie, ct);

    private Task<JsonDocument> GetUrlAsync(Session session, IReadOnlyList<string> trackTokens, string quality,
        string? strictFormat, CancellationToken ct) =>
        PostAsync($"{session.MediaService}/v1/get_url", new
        {
            license_token = session.LicenseToken,
            media = new[] { new { type = "FULL", formats = Formats(quality, strictFormat)
                .Select(format => new { cipher = "BF_CBC_STRIPE", format }).ToArray() } },
            track_tokens = trackTokens,
        }, cookie: null, ct: ct);

    /// <summary>Rows follow request order; when Deezer drops unavailable rows, match by SNG_ID instead.</summary>
    private static Dictionary<string, JsonElement> SongRows(JsonElement root, IReadOnlyList<string> ids)
    {
        var rows = new Dictionary<string, JsonElement>();
        if (!root.TryGetProperty("results", out var results) || !results.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array) return rows;
        var list = data.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object).ToList();
        if (list.Count == ids.Count)
            for (var i = 0; i < list.Count; i++) rows[ids[i]] = list[i];
        else
            foreach (var row in list)
                if (Text(row, "SNG_ID") is { } id) rows.TryAdd(id, row);
        return rows;
    }

    private static string MediaKey(string arl, string trackId, string quality, string? strictFormat) =>
        strictFormat is not null ? $"{arl}\0{trackId}\0{quality}\0strict-{strictFormat}" : $"{arl}\0{trackId}\0{quality}";

    private void Remember(string key, Media media)
    {
        if (media.ExpiresUtc > DateTime.UtcNow)
            _mediaCache.Set(key, media, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpiration = media.ExpiresUtc });
    }

    /// <summary>
    /// Resolves many strict-quality tracks with one song.getListData and one get_url, as the desktop client batches.
    /// Best effort: tracks without a direct source are left to the per-track path.
    /// </summary>
    public async Task PrefetchAsync(IEnumerable<string> trackIds, string quality, CancellationToken ct = default)
    {
        if (Arl(Settings) is not { } arl) return;
        var selected = NormalizeStrictQuality(quality);
        var pending = trackIds.Where(ValidTrackId).Distinct()
            .Where(id => !_mediaCache.TryGetValue(MediaKey(arl, id, selected, selected), out _)
                && !_misses.TryGetValue(MediaKey(arl, id, selected, selected), out _))
            .ToList();
        // One track gains nothing from batching and would ask twice if it needs a substitute.
        if (pending.Count < 2) return;
        await _mediaGate.WaitAsync(ct);
        try
        {
            // Another worker's batch may have resolved these while this one waited.
            pending.RemoveAll(id => _mediaCache.TryGetValue(MediaKey(arl, id, selected, selected), out _));
            if (pending.Count < 2) return;
            var (session, fresh) = await GetSessionAsync(arl, ct);
            foreach (var chunk in pending.Chunk(MaxBatch))
            {
                JsonDocument rowsDoc;
                (rowsDoc, session, fresh) = await WithSessionAsync(arl, session, fresh,
                    s => GetListDataAsync(s, chunk, ct), IsSessionError, ct);
                using var rows = rowsDoc;
                if (HasError(rows.RootElement)) return;
                var direct = SongRows(rows.RootElement, chunk)
                    .Where(row => Text(row.Value, "SNG_ID") == row.Key && Offers(row.Value, selected, selected))
                    .Select(row => (Id: row.Key, Token: Text(row.Value, "TRACK_TOKEN")))
                    .Where(row => row.Token is { Length: > 0 })
                    .ToList();
                if (direct.Count == 0) continue;
                JsonDocument mediaDoc;
                (mediaDoc, session, fresh) = await WithSessionAsync(arl, session, fresh,
                    s => GetUrlAsync(s, direct.Select(row => row.Token!).ToArray(), selected, selected, ct),
                    IsLicenseError, ct);
                using var media = mediaDoc;
                for (var i = 0; i < direct.Count; i++)
                    if (SelectMedia(media.RootElement, i, direct[i].Id, selected, selected) is { } found)
                        Remember(MediaKey(arl, direct[i].Id, selected, selected), found with { Account = arl });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogDebug("Deezer batch resolve failed: {Detail}", Describe(ex)); }
        finally { _mediaGate.Release(); }
    }

    private async Task<Media> ResolveMediaAsync(string trackId, string quality, CancellationToken ct,
        string? strictFormat = null)
    {
        if (!ValidTrackId(trackId)) throw new ArgumentException("Invalid Deezer track ID.", nameof(trackId));
        var arl = Arl(Settings) ?? throw new InvalidOperationException("Deezer ARL is not set.");
        var key = MediaKey(arl, trackId, quality, strictFormat);
        if (_mediaCache.TryGetValue(key, out Media? cached)) return cached!;
        if (_misses.TryGetValue(key, out _))
            throw new InvalidOperationException("Deezer recently had no full-track media for this track.");
        await _mediaGate.WaitAsync(ct);
        try
        {
            if (_mediaCache.TryGetValue(key, out cached)) return cached!;
            // Latch and cooldown failures propagate: they say nothing about the track.
            var (session, fresh) = await GetSessionAsync(arl, ct);
            try
            {
                var visited = new HashSet<string>();
                var candidate = trackId;
                var candidateReason = "requested";
                (string Artist, string Title)? originalIdentity = null;
                for (var attempt = 0; attempt < 3 && visited.Add(candidate); attempt++)
                {
                    JsonDocument rowsDoc;
                    (rowsDoc, session, fresh) = await WithSessionAsync(arl, session, fresh,
                        s => GetListDataAsync(s, [candidate], ct), IsSessionError, ct);
                    using var rows = rowsDoc;
                    if (HasError(rows.RootElement))
                    {
                        logger.LogWarning("Deezer song.getListData error for track {TrackId}: {Error}",
                            candidate, ErrorText(rows.RootElement));
                        break;
                    }
                    if (!SongRows(rows.RootElement, [candidate]).TryGetValue(candidate, out var data)) break;
                    var actualId = Text(data, "SNG_ID") ?? candidate;
                    if (!ValidTrackId(actualId)) break;

                    var candidateIdentity = PageTrackIdentity(data);
                    if (candidate == trackId && actualId == trackId)
                        originalIdentity ??= candidateIdentity;
                    else if (candidate == trackId && originalIdentity is null)
                        originalIdentity = await catalog.GetTrackIdentityByIdAsync(trackId, ct);

                    var substituted = candidate != trackId || actualId != candidate;
                    var identityMatches = !substituted
                        || RecordingMatches(originalIdentity, candidateIdentity);
                    var substitutionReason = actualId == candidate
                        ? candidateReason : $"{candidateReason}+sng-id";
                    if (substituted)
                        logger.LogDebug(
                            "Deezer source identity check requested {RequestedId}, candidate {CandidateId}, actual {ActualId}, reason {Reason}, matched {Matched}",
                            trackId, candidate, actualId, substitutionReason, identityMatches);
                    if (!identityMatches) break;

                    if (Text(data, "TRACK_TOKEN") is { Length: > 0 } trackToken
                        && Offers(data, quality, strictFormat))
                    {
                        JsonDocument mediaDoc;
                        (mediaDoc, session, fresh) = await WithSessionAsync(arl, session, fresh,
                            s => GetUrlAsync(s, [trackToken], quality, strictFormat, ct), IsLicenseError, ct);
                        using var media = mediaDoc;
                        if (SelectMedia(media.RootElement, 0, actualId, quality, strictFormat) is { } found)
                        {
                            found = found with { Account = arl };
                            logger.LogDebug(
                                "Deezer source selected requested {RequestedId}, candidate {CandidateId}, actual {ActualId}, reason {Reason}, format {Format}",
                                trackId, candidate, actualId, substitutionReason, found.Format);
                            Remember(key, found);
                            return found;
                        }
                        if (MediaErrorText(media.RootElement) is { } mediaError)
                            logger.LogWarning("Deezer get_url error for track {TrackId}: {Error}",
                                actualId, mediaError);
                    }
                    // Alternative bytes must use the alternative track's Blowfish key.
                    var fallback = data.TryGetProperty("FALLBACK", out var alt) ? Text(alt, "SNG_ID") : null;
                    if (fallback is not null && ValidTrackId(fallback) && !visited.Contains(fallback))
                    {
                        candidate = fallback;
                        candidateReason = "provider-fallback";
                        continue;
                    }
                    string? id = null;
                    var reason = "isrc";
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
                        reason = "catalog-alternative";
                        id = candidateIdentity is { } identity
                            ? await catalog.FindAlternativeTrackIdAsync(identity.Artist, identity.Title, visited, ct)
                            : null;
                    }
                    if (id is null || !ValidTrackId(id) || visited.Contains(id)) break;
                    candidate = id;
                    candidateReason = reason;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Transient: not remembered as a miss, so the next request may try again.
                logger.LogWarning("Deezer media resolution failed for track {TrackId}: {Detail}",
                    trackId, Describe(ex));
                throw new InvalidOperationException("Deezer media resolution failed.", ex);
            }
            // Deezer answered and has nothing playable: remember it so background work stops asking.
            _misses.Set(key, true, MissLifetime);
            throw new InvalidOperationException("No full-track Deezer media available. Check ARL, subscription and region.");
        }
        finally { _mediaGate.Release(); }
    }

    private static (string Artist, string Title)? PageTrackIdentity(JsonElement data)
    {
        var artist = Text(data, "ART_NAME");
        var title = Text(data, "SNG_TITLE");
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;
        var version = data.TryGetProperty("VERSION", out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return (artist, string.IsNullOrWhiteSpace(version) ? title : $"{title} {version}");
    }

    private static bool RecordingMatches((string Artist, string Title)? expected,
        (string Artist, string Title)? candidate) =>
        expected is { } original && candidate is { } resolved
        && DeezerMetadataService.RecordingMatches(original.Artist, original.Title,
            resolved.Artist, resolved.Title);

    /// <summary>
    /// Whether song.getListData lists a file for any format the request would ask for. Asking
    /// get_url for a format the track lacks earns error 2002, which the desktop client never
    /// provokes; Deezer may count it against the ARL. A row without the size field is assumed to have it.
    /// </summary>
    internal static bool Offers(JsonElement row, string? quality, string? strictFormat = null) =>
        Formats(quality, strictFormat).Any(format => Text(row, $"FILESIZE_{format}") is not { Length: > 0 } size
            || long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes > 0);

    internal static string[] Formats(string? quality, string? strictFormat = null)
    {
        if (strictFormat is not null) return [strictFormat];
        return quality?.Trim().ToUpperInvariant() switch
    {
        "MP3_128" => ["MP3_128"],
        "MP3_320" => ["MP3_320", "MP3_128"],
        _ => ["FLAC", "MP3_320", "MP3_128"],
    };
    }

    /// <summary>Picks the best format for the index-th track token; keeps every https source, in Deezer's order.</summary>
    private static Media? SelectMedia(JsonElement root, int index, string trackId, string quality,
        string? strictFormat = null)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() <= index || !data[index].TryGetProperty("media", out var media)
            || media.ValueKind != JsonValueKind.Array) return null;
        foreach (var format in Formats(quality, strictFormat))
            foreach (var item in media.EnumerateArray())
            {
                if (Text(item, "format") != format || !item.TryGetProperty("sources", out var sources)
                    || sources.ValueKind != JsonValueKind.Array) continue;
                var urls = sources.EnumerateArray()
                    .Select(source => Text(source, "url"))
                    .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    .Select(url => url!)
                    .ToArray();
                // Keep the URL until Deezer says it expires, as the desktop client does.
                if (urls.Length > 0)
                    return new Media(trackId, format, urls,
                        Expiry(item, "exp") ?? DateTime.UtcNow + FallbackMediaLifetime);
            }
        return null;
    }

    /// <summary>Tries each CDN source in Deezer's order, as the desktop client does, before the caller re-resolves.</summary>
    private async Task<(HttpResponseMessage Response, string Url)> GetFromSourcesAsync(IEnumerable<string> urls,
        (long From, long? To)? range, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (range is { } r) request.Headers.Range = new RangeHeaderValue(r.From, r.To);
            HttpResponseMessage? response = null;
            try
            {
                response = await httpFactory.CreateClient(StreamClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                return (response, url);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { response?.Dispose(); throw; }
            catch (Exception ex) { response?.Dispose(); last = ex; }
        }
        throw last ?? new IOException("Deezer returned no media sources.");
    }

    public async Task<(Stream stream, string contentType, long? contentLength, int statusCode,
        string? contentRange, HttpResponseMessage owner)?> OpenStreamAsync(string trackId,
        string? rangeHeader = null, CancellationToken ct = default, string quality = "MP3_320")
        => await OpenStreamCoreAsync(trackId, rangeHeader, ct, quality, strictFormat: null);

    /// <summary>Opens only FLAC media. Playback caches must never accept a lossy fallback.</summary>
    public Task<(Stream stream, string contentType, long? contentLength, int statusCode,
        string? contentRange, HttpResponseMessage owner)?> OpenFlacStreamAsync(string trackId,
        CancellationToken ct = default) => OpenStreamCoreAsync(trackId, null, ct, "FLAC", strictFormat: "FLAC");

    private async Task<(Stream stream, string contentType, long? contentLength, int statusCode,
        string? contentRange, HttpResponseMessage owner)?> OpenStreamCoreAsync(string trackId,
        string? rangeHeader, CancellationToken ct, string quality, string? strictFormat)
    {
        // One refresh: a CDN URL can expire between resolve and open.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            HttpResponseMessage? response = null;
            Media? media = null;
            try
            {
                media = await ResolveMediaAsync(trackId, quality, ct, strictFormat);
                // Read headers first to learn total size, including for suffix ranges.
                string url;
                (response, url) = await GetFromSourcesAsync(media.Urls, null, ct);
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
                        // Fetch to EOF so the final encrypted stripe is never truncated.
                        (response, _) = await GetFromSourcesAsync([url], (aligned, null), ct);
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
                InvalidateMedia(media, trackId, quality, strictFormat);
            }
        }
        return null;
    }

    public async Task<DeezerSourceInfo?> ProbeSourceAsync(string trackId, string? quality = null,
        CancellationToken ct = default)
    {
        var selected = NormalizeStrictQuality(quality ?? CacheQuality);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Media? media = null;
            try
            {
                media = await ResolveMediaAsync(trackId, selected, ct, selected);
                // The desktop client never sends HEAD; one ranged stripe carries the same validators.
                var (response, url) = await GetFromSourcesAsync(media.Urls, (0, 2047), ct);
                using (response)
                {
                    var length = response.StatusCode == HttpStatusCode.PartialContent
                        ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;
                    return SourceInfo(media, url, selected, length,
                        response.Headers.ETag, response.Content.Headers.LastModified);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogDebug("Deezer source probe attempt {Attempt} failed for track {TrackId}: {Type}",
                    attempt + 1, trackId, ex.GetType().Name);
                // No media means resolution failed; a second resolve would ask Deezer the same question.
                if (media is null) break;
                InvalidateMedia(media, trackId, selected, selected);
            }
        }
        return null;
    }

    /// <summary>
    /// Resolve, probe and open one strict-quality source under one three-attempt establishment budget.
    /// The returned descriptor is immutable and identifies the exact bytes opened by this method.
    /// </summary>
    public async Task<(DeezerSourceInfo info,
        (Stream stream, string contentType, long? contentLength, int statusCode,
            string? contentRange, HttpResponseMessage owner) opened)?> EstablishSourceAsync(
        string trackId, string? quality = null, CancellationToken ct = default)
    {
        var selected = NormalizeStrictQuality(quality ?? CacheQuality);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Media? media = null;
            HttpResponseMessage? response = null;
            try
            {
                media = await ResolveMediaAsync(trackId, selected, ct, selected);
                // One GET, no HEAD: the descriptor comes from the response that carries the bytes.
                string url;
                (response, url) = await GetFromSourcesAsync(media.Urls, null, ct);
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("Deezer CDN returned an unexpected partial source.");
                var length = response.Content.Headers.ContentLength;
                var info = SourceInfo(media, url, selected, length,
                    response.Headers.ETag, response.Content.Headers.LastModified);
                var stream = await response.Content.ReadAsStreamAsync(ct);
                var owner = response;
                response = null;
                return (info, (new DeezerDecryptedStream(stream, media.TrackId),
                    ContentType(media.Format), length, (int)owner.StatusCode, null, owner));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                response?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                response?.Dispose();
                logger.LogWarning("Deezer source establishment attempt {Attempt} failed for track {TrackId}: {Type}",
                    attempt + 1, trackId, ex.GetType().Name);
                if (media is null) break;
                InvalidateMedia(media, trackId, selected, selected);
            }
        }
        return null;
    }

    /// <summary>Open plaintext from offset using exact source descriptor resolved by ProbeSourceAsync.</summary>
    public async Task<(Stream stream, string contentType, long? contentLength, int statusCode,
        string? contentRange, HttpResponseMessage owner)?> OpenSourceRangeAsync(DeezerSourceInfo source,
        long offset, CancellationToken ct = default)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (source.Media is not { } media) return null;
        if (source.ExpectedLength is long total && offset >= total)
        {
            var unsatisfied = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            return (Stream.Null, source.ContentType, 0, 416, $"bytes */{total}", unsatisfied);
        }

        var aligned = offset / 2048 * 2048;
        HttpResponseMessage response;
        try { (response, _) = await GetFromSourcesAsync([media.Url], (aligned, null), ct); }
        catch (HttpRequestException)
        {
            // The URL may have expired: renew it once, as the desktop client does.
            // ValidateRepresentation below still requires the same bytes.
            _mediaCache.Remove(MediaKey(media.Account, media.TrackId, media.Quality, media.Quality));
            var renewed = await ResolveMediaAsync(media.TrackId, media.Quality, ct, media.Quality);
            (response, _) = await GetFromSourcesAsync(renewed.Urls, (aligned, null), ct);
        }
        try
        {
            ValidateRepresentation(response, media, partial: response.StatusCode == HttpStatusCode.PartialContent);
            var start = 0L;
            var stripe = 0L;
            var skip = offset;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange?.From != aligned)
                    throw new IOException("Deezer CDN returned an unexpected byte range.");
                start = aligned;
                stripe = aligned / 2048;
                skip = offset - aligned;
            }
            var raw = await response.Content.ReadAsStreamAsync(ct);
            long? length = source.ExpectedLength is long expected ? expected - offset : null;
            return (new DeezerDecryptedStream(raw, media.TrackId, stripe, skip, length),
                ContentType(media.Format), length, 206,
                source.ExpectedLength is long fullLength ? $"bytes {offset}-{fullLength - 1}/{fullLength}" : null,
                response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public static string NormalizeStrictQuality(string quality) => quality.Trim().ToUpperInvariant() switch
    {
        "FLAC" => "FLAC",
        "MP3_320" => "MP3_320",
        _ => throw new ArgumentException("Cache quality must be FLAC or MP3_320.", nameof(quality)),
    };

    internal static string ContentType(string format) => format == "FLAC" ? "audio/flac" : "audio/mpeg";

    private static string SourceFingerprint(Media media, string url, string quality, long? length,
        string? strongETag, DateTimeOffset? modifiedUtc)
    {
        var uri = new Uri(url);
        var representation = strongETag ?? modifiedUtc?.ToUniversalTime().ToString("O") ?? "";
        var identity = $"{media.TrackId}\0{quality}\0{media.Format}\0{uri.GetLeftPart(UriPartial.Path)}\0{representation}\0{length?.ToString(CultureInfo.InvariantCulture) ?? ""}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(identity);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static DeezerSourceInfo SourceInfo(Media media, string url, string quality, long? length,
        EntityTagHeaderValue? entityTag, DateTimeOffset? modifiedUtc)
    {
        var etag = entityTag is { IsWeak: false } strong ? strong.Tag : null;
        var fingerprint = SourceFingerprint(media, url, quality, length, etag, modifiedUtc);
        var descriptor = new DeezerMediaSource(media.TrackId, quality, media.Format, url,
            media.Account, fingerprint, length, etag, modifiedUtc);
        return new DeezerSourceInfo(ContentType(media.Format), length, fingerprint,
            modifiedUtc, media: descriptor, etag: etag);
    }

    private void InvalidateMedia(Media media, string trackId, string quality, string? strictFormat) =>
        _mediaCache.Remove(MediaKey(media.Account, trackId, quality, strictFormat));

    private static void ValidateRepresentation(HttpResponseMessage response,
        DeezerMediaSource source, bool partial)
    {
        if (source.StrongETag is { Length: > 0 } expectedTag
            && response.Headers.ETag is { } returnedTag
            && !string.Equals(returnedTag.Tag, expectedTag, StringComparison.Ordinal))
            throw new IOException("Deezer CDN changed source representation during transfer.");
        if (source.ModifiedUtc is { } expectedModified
            && response.Content.Headers.LastModified is { } returnedModified
            && returnedModified != expectedModified)
            throw new IOException("Deezer CDN changed source modification time during transfer.");
        var returnedLength = partial ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        if (source.ExpectedLength is long expectedLength && returnedLength is long actualLength
            && actualLength != expectedLength)
            throw new IOException("Deezer CDN changed source length during transfer.");
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
        $"https://www.deezer.com/ajax/gw-light.php?method={method}&input=3&api_version=1.0&api_token={Uri.EscapeDataString(token)}&cid={Random.Shared.Next(1_000_000_000)}";
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
    // Gateway errors that mean the session, not the track, is bad.
    private static bool IsSessionError(JsonElement root) =>
        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.EnumerateObject().Any(entry => entry.Name is "VALID_TOKEN_REQUIRED" or "NEED_API_AUTH_REQUIRED"
            || entry.Value.ValueKind == JsonValueKind.String
               && entry.Value.GetString()!.Contains("CSRF", StringComparison.OrdinalIgnoreCase));

    // get_url codes 1000-1002: invalid, expired or unauthorized license token.
    private static bool IsLicenseError(JsonElement root) =>
        root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
        && errors.EnumerateArray().Any(error => error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) && value is >= 1000 and <= 1002);

    // Deezer's error payloads carry codes and messages, never the caller's tokens.
    private static string ErrorText(JsonElement root) =>
        Truncate(root.TryGetProperty("error", out var error) ? error.GetRawText() : "(none)");

    private static string? MediaErrorText(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
        && data.GetArrayLength() > 0 && data[0].TryGetProperty("errors", out var errors)
            ? Truncate(errors.GetRawText()) : null;

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        InvalidOperationException => ex.Message,
        _ => ex.GetType().Name,
    };

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private static bool ValidTrackId(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;
    public void Dispose() { _mediaCache.Dispose(); _misses.Dispose(); _mediaGate.Dispose(); }
}
