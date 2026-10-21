using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Octo.Services.Common;

namespace Octo.Services.Metadata;

public partial class DeezerMetadataService
{
    // ponytail: discovery is bounded at 7 sends / 5 seconds, including limiter waits.
    // REST examines at most 3 pages of 100; web search examines 100 more candidates.
    // Exhausting a page/request budget is retryable, never cached as catalog absence.
    private const int TrackPageSize = 100;
    private const int RestSearchRequests = 3;
    private sealed class TrackSearchBudget(CancellationToken stopping, bool background, int? duration = null) : IDisposable
    {
        private readonly CancellationTokenSource _deadline = MakeDeadline(stopping);
        public CancellationToken Token => _deadline.Token;
        public bool Background { get; } = background;
        /// <summary>The wanted recording's length, when known; see <see cref="RecordingMatches"/>.</summary>
        public int? Duration { get; } = duration;
        public int Requests { get; private set; }
        public bool Incomplete { get; set; }
        public bool Hydrated { get; set; }
        public bool Take()
        {
            Token.ThrowIfCancellationRequested();
            if (Requests >= 7) { Incomplete = true; return false; }
            Requests++;
            return true;
        }
        private static CancellationTokenSource MakeDeadline(CancellationToken stopping)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            source.CancelAfter(TimeSpan.FromSeconds(5));
            return source;
        }
        public void Dispose() => _deadline.Dispose();
    }

    private readonly SemaphoreSlim _searchTokenGate = new(1, 1);
    private string? _searchToken;
    private DateTimeOffset _searchTokenExpires;

    private async Task<(DeezerResponse? Response, JsonElement? Hit)> FindTrackAsync(string? artist, string? title,
        CancellationToken ct, bool background = false, IReadOnlySet<string>? excluded = null, bool readableOnly = false,
        int? duration = null)
    {
        ct.ThrowIfCancellationRequested();
        _stopping.Token.ThrowIfCancellationRequested();
        // A selection with exclusions is a different question. Its underlying HTTP pages
        // are still shared below; no caller owns another caller's JsonDocument or token.
        var key = "lookup|" + JsonSerializer.Serialize(new
        {
            artist = artist?.Trim().ToLowerInvariant(), title = title?.Trim().ToLowerInvariant(),
            background, readableOnly, excluded = excluded?.Order(StringComparer.Ordinal).ToArray(), duration
        });
        var answer = await SharedAsync(key, async () =>
        {
            using var budget = new TrackSearchBudget(_stopping.Token, background, duration);
            try
            {
                var (response, hit) = await FindTrackCoreAsync(artist, title, budget, excluded, readableOnly);
                using (response)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    var json = hit?.GetRawText();
                    var transient = response?.Transient == true || json is null && budget.Incomplete;
                    if (json is not null)
                    {
                        _logger.LogDebug("deezer discovered '{Artist} - {Title}' as {Id} in {Requests} request(s)",
                            artist, title, hit is JsonElement row && row.TryGetProperty("id", out var id) ? id.ToString() : null,
                            budget.Requests);
                    }
                    return (Json: json, Transient: transient);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("deezer discovery '{Artist} - {Title}' stopped after {Requests} request(s): {Type}",
                    artist, title, budget.Requests, ex.GetType().Name);
                return (Json: (string?)null, Transient: true);
            }
        }, ct);
        var doc = answer.Json is null ? null : JsonDocument.Parse(answer.Json);
        return (new DeezerResponse { Doc = doc, Transient = answer.Transient }, doc?.RootElement);
    }

    private async Task<(DeezerResponse? Response, JsonElement? Hit)> FindTrackCoreAsync(string? artist, string? title,
        TrackSearchBudget budget, IReadOnlySet<string>? excluded, bool readableOnly)
    {
        var queries = SongIdentity.QueryVariants(title, artist).Take(RestSearchRequests).Select(v => v.Text).ToArray();
        var pages = new Queue<string>(queries.Select(q => $"{Base}/search?q={Uri.EscapeDataString(q)}&limit={TrackPageSize}"));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var count = 0; pages.Count > 0 && count < RestSearchRequests; count++)
        {
            var url = pages.Dequeue();
            if (!visited.Add(url)) continue;
            var page = await DiscoveryRequestAsync(url, budget);
            if (page.Transient) return (page, null);
            if (BestMatch(page.Doc, artist, title, excluded, readableOnly, recording: true, budget.Duration) is JsonElement hit)
                return (page, hit);
            using (page)
            {
                var hydrated = await HydrateIncompleteAsync(page.Doc, artist, title, budget, excluded, readableOnly);
                if (hydrated.Response is not null) return hydrated;
                if (page.Doc is not null && Str(page.Doc.RootElement, "next") is { Length: > 0 } next)
                {
                    if (SafeSearchPage(url, next) is { } safe && !visited.Contains(safe)) pages.Enqueue(safe);
                    else budget.Incomplete = true;
                }
                else if (page.Doc is not null)
                {
                    QueryHelpers.ParseQuery(new Uri(url).Query).TryGetValue("index", out var rawOffset);
                    int.TryParse(rawOffset, out var offset);
                    budget.Incomplete |= PageWithoutNextIsIncomplete(page.Doc.RootElement, TrackPageSize, offset);
                }
            }
        }
        budget.Incomplete |= pages.Count > 0;

        var web = await FindWebTrackAsync(PlainQuery(artist, title), artist, title, budget, excluded, readableOnly);
        if (web.Response is not null) return web;

        // Retain the established single/EP recovery, without artist popularity or a crawl.
        using var albums = await DiscoveryRequestAsync(
            $"{Base}/search/album?q={Uri.EscapeDataString(PlainQuery(artist, title))}&limit={TrackPageSize}", budget);
        if (albums.Transient) return (new DeezerResponse { Transient = true }, null);
        var album = BestMatch(albums.Doc, artist, title, recording: true);
        if (album is null || !album.Value.TryGetProperty("id", out var albumId))
        {
            budget.Incomplete |= albums.Doc is not null && (Str(albums.Doc.RootElement, "next") is { Length: > 0 }
                || PageWithoutNextIsIncomplete(albums.Doc.RootElement, TrackPageSize));
            return (null, null);
        }
        using var tracks = await DiscoveryRequestAsync($"{Base}/album/{Uri.EscapeDataString(albumId.ToString())}/tracks?limit=300", budget);
        if (tracks.Transient) return (new DeezerResponse { Transient = true }, null);
        var track = BestMatch(tracks.Doc, artist, title, excluded, readableOnly, recording: true, budget.Duration);
        if (track is null || !track.Value.TryGetProperty("id", out var trackId))
        {
            budget.Incomplete |= tracks.Doc is not null && (Str(tracks.Doc.RootElement, "next") is { Length: > 0 }
                || PageWithoutNextIsIncomplete(tracks.Doc.RootElement, 300));
            return (null, null);
        }
        return await ReadCandidateAsync(trackId.ToString(), artist, title, budget, readableOnly);
    }

    private static bool PageWithoutNextIsIncomplete(JsonElement root, int pageSize, int offset = 0)
    {
        var count = root.GetProperty("data").GetArrayLength();
        return Int(root, "total") is int total ? total > offset + count : count >= pageSize;
    }

    private static string? SafeSearchPage(string current, string next)
    {
        if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "api.deezer.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/search") return null;
        var previous = QueryHelpers.ParseQuery(new Uri(current).Query);
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue("q", out var text) || !previous.TryGetValue("q", out var oldText) || text != oldText
            || !query.TryGetValue("index", out var rawIndex) || !int.TryParse(rawIndex, out var index) || index <= 0
            || previous.TryGetValue("index", out var old) && int.TryParse(old, out var oldIndex) && index <= oldIndex) return null;
        return $"{Base}/search?q={Uri.EscapeDataString(text.ToString())}&limit={TrackPageSize}&index={index}";
    }

    private async Task<(DeezerResponse? Response, JsonElement? Hit)> HydrateIncompleteAsync(JsonDocument? page,
        string? artist, string? title, TrackSearchBudget budget, IReadOnlySet<string>? excluded, bool readableOnly)
    {
        if (page is null) return (null, null);
        foreach (var row in page.RootElement.GetProperty("data").EnumerateArray())
        {
            var gotTitle = TrackTitle(row);
            var gotArtist = row.TryGetProperty("artist", out var credit) ? Str(credit, "name") : null;
            var t = CompareTitles(title, gotTitle);
            var a = CompareArtists(artist, gotArtist);
            if (t == FieldVerdict.Mismatch || a == FieldVerdict.Mismatch) continue;
            if (RecordingMatches(artist, title, gotArtist, gotTitle, budget.Duration, Int(row, "duration"))) continue;
            if (row.TryGetProperty("id", out var id) && excluded?.Contains(id.ToString()) == true) continue;
            budget.Incomplete = true;
            if (budget.Hydrated || t != FieldVerdict.Match && a != FieldVerdict.Match
                || !row.TryGetProperty("id", out id)) continue;
            budget.Hydrated = true;
            var detail = await ReadCandidateAsync(id.ToString(), artist, title, budget, readableOnly);
            if (detail.Response is not null) return detail;
        }
        return (null, null);
    }

    private async Task<(DeezerResponse? Response, JsonElement? Hit)> ReadCandidateAsync(string id,
        string? artist, string? title, TrackSearchBudget budget, bool readableOnly, JsonElement? expected = null)
    {
        if (!long.TryParse(id, out var numeric) || numeric <= 0) { budget.Incomplete = true; return (null, null); }
        var detail = await DiscoveryRequestAsync($"{Base}/track/{numeric}", budget);
        if (detail.Transient) return (detail, null);
        if (detail.Doc is not null)
        {
            var track = detail.Doc.RootElement;
            var credit = track.TryGetProperty("artist", out var a) ? Str(a, "name") : null;
            if (track.TryGetProperty("id", out var returned) && returned.ToString() == id
                && RecordingMatches(artist, title, credit, TrackTitle(track), budget.Duration, Int(track, "duration"))
                && (!readableOnly || track.TryGetProperty("readable", out var readable) && readable.ValueKind == JsonValueKind.True)
                && (expected is null || ConsistentRecording(expected.Value, track))) return (detail, track);
            budget.Incomplete = true;
        }
        detail.Dispose();
        return (null, null);
    }

    private static bool ConsistentRecording(JsonElement expected, JsonElement actual) =>
        (Str(expected, "isrc") is not { Length: > 0 } isrc || Str(actual, "isrc") is not { Length: > 0 } got || isrc == got)
        && (Int(expected, "duration") is not > 0 || Int(actual, "duration") is not > 0
            || Math.Abs(Int(expected, "duration")!.Value - Int(actual, "duration")!.Value) <= SongIdentity.LengthToleranceSeconds);

    private async Task<DeezerResponse> DiscoveryRequestAsync(string url, TrackSearchBudget budget,
        string? body = null, string? token = null)
    {
        if (!budget.Take()) return new DeezerResponse { Transient = true };
        // Share immutable response text, then give each selection its own document.
        // Lane stays in the key: a foreground request never inherits a background queue.
        var key = $"discovery-http|{budget.Background}|{url}|{body}|{token}";
        var result = await SharedAsync(key, async () =>
        {
            if (body is null)
            {
                using var response = await GetJsonAsync(url, budget.Token, budget.Background);
                return (Json: response.Doc?.RootElement.GetRawText(), response.Transient);
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (budget.Background) request.Options.Set(DeezerRateLimitHandler.BackgroundLane, true);
                using var response = await Client().SendAsync(request, budget.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return (Json: "{\"errors\":[{\"type\":\"JwtTokenExpiredError\"}]}", Transient: false);
                if (!response.IsSuccessStatusCode) return (Json: (string?)null, Transient: true);
                var json = await response.Content.ReadAsStringAsync(budget.Token);
                using var parsed = JsonDocument.Parse(json);
                return (Json: (string?)json, Transient: parsed.RootElement.ValueKind != JsonValueKind.Object);
            }
            catch (Exception) { return (Json: (string?)null, Transient: true); }
        }, budget.Token);
        return new DeezerResponse { Doc = result.Json is null ? null : JsonDocument.Parse(result.Json), Transient = result.Transient };
    }

    private async Task<string?> SearchTokenAsync(TrackSearchBudget budget, string? rejected = null)
    {
        await _searchTokenGate.WaitAsync(budget.Token);
        try
        {
            if (_searchToken is not null && _searchToken != rejected && DateTimeOffset.UtcNow < _searchTokenExpires)
                return _searchToken;
            _searchToken = null;
            using var response = await DiscoveryRequestAsync("https://auth.deezer.com/login/anonymous?jo=p&rto=c", budget);
            if (response.Transient || response.Doc is null || Str(response.Doc.RootElement, "jwt") is not { Length: > 0 } jwt)
                return null;
            // Refresh conservatively, also respecting an earlier expiry encoded by Deezer.
            _searchTokenExpires = DateTimeOffset.UtcNow.AddMinutes(4);
            try
            {
                var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
                using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
                if (claims.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                    _searchTokenExpires = DateTimeOffset.FromUnixTimeSeconds(seconds).AddSeconds(-10) < _searchTokenExpires
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds).AddSeconds(-10) : _searchTokenExpires;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
            return _searchToken = jwt;
        }
        finally { _searchTokenGate.Release(); }
    }

    private const string WebTrackQuery = """
        query OctoTrackSearch($query:String!){instantSearch(query:$query){results{tracks(first:100){
          edges{node{id title ISRC duration contributorNames(roles:[MAIN])}}
          pageInfo{hasNextPage endCursor}}}}}
        """;

    private async Task<(DeezerResponse? Response, JsonElement? Hit)> FindWebTrackAsync(string query,
        string? artist, string? title, TrackSearchBudget budget, IReadOnlySet<string>? excluded, bool readableOnly)
    {
        var token = await SearchTokenAsync(budget);
        if (token is null) return (new DeezerResponse { Transient = true }, null);
        var body = JsonSerializer.Serialize(new { query = WebTrackQuery, variables = new { query } });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await DiscoveryRequestAsync("https://pipe.deezer.com/api", budget, body, token);
            if (response.Transient || response.Doc is null) return (new DeezerResponse { Transient = true }, null);
            var root = response.Doc.RootElement;
            if (attempt == 0 && TokenExpired(root) && (token = await SearchTokenAsync(budget, token)) is not null) continue;
            if (WebCandidates(root, "instantSearch") is not { } found) return (new DeezerResponse { Transient = true }, null);
            budget.Incomplete |= found.Incomplete;
            var candidates = found.Candidates;
            using var normalized = JsonSerializer.SerializeToDocument(new { data = candidates });
            if (BestMatch(normalized, artist, title, excluded, recording: true, duration: budget.Duration) is JsonElement match)
                return await ReadCandidateAsync(match.GetProperty("id").ToString(), artist, title, budget, readableOnly, match);
            return await HydrateIncompleteAsync(normalized, artist, title, budget, excluded, readableOnly);
        }
        return (new DeezerResponse { Transient = true }, null);
    }

    /// <summary>Songs one batched pipe request looks up; see <see cref="FindTracksBatchAsync"/>.
    /// About 170 of pipe's query cost each (3340 for 20, measured live), far under its cap of
    /// 25000, which Deezer has lowered before.</summary>
    internal const int BatchSearchSize = 20;

    /// <summary>Batched pipe requests in flight at once, across every radio being built.</summary>
    private static readonly SemaphoreSlim BatchGate = new(4, 4);

    private const string BatchTrackFields = """
        results{tracks(first:5){edges{node{id title duration album{displayTitle}
          contributorNames(roles:[MAIN])}} pageInfo{hasNextPage}}}
        """;

    /// <summary>
    /// Many songs looked up at once, for radio: one pipe request per <see cref="BatchSearchSize"/>
    /// songs, each song an aliased instantSearch for its artist and title, matched the way the
    /// single search matches. Seventy songs one search ladder at a time took half a minute.
    /// Each song gets its track, or null when this quick look found none: that is not a miss,
    /// so nothing is cached as absent and the caller can run <see cref="EnrichTrackAsync"/> for it.
    /// A batch over pipe's cost cap is asked again in halves; a malformed query is logged, not retried.
    /// </summary>
    public async Task<IReadOnlyList<TrackMeta?>> FindTracksBatchAsync(
        IReadOnlyList<(string Artist, string Title, int? Duration)> songs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var results = new TrackMeta?[songs.Count];
        await Task.WhenAll(Enumerable.Range(0, songs.Count).Chunk(BatchSearchSize).Select(LookUpAsync));
        return results;

        async Task LookUpAsync(int[] chunk)
        {
            string? retry;
            await BatchGate.WaitAsync(ct);
            try { retry = await AskAsync(chunk); }
            finally { BatchGate.Release(); }
            if (retry != "QueryComplexityExceededError") return;
            if (chunk.Length == 1) return;
            _logger.LogWarning("deezer batch of {Count} is over pipe's cost cap; asking in halves", chunk.Length);
            var half = chunk.Length / 2;
            await Task.WhenAll(LookUpAsync(chunk[..half]), LookUpAsync(chunk[half..]));
        }

        // One request for the chunk; returns the error type that refused the whole query, if any.
        async Task<string?> AskAsync(int[] chunk)
        {
            using var budget = new TrackSearchBudget(_stopping.Token, background: false);
            try
            {
                var token = await SearchTokenAsync(budget);
                if (token is null) return null;
                var query = "query OctoTrackBatch(" + string.Join(",", chunk.Select(i => $"$q{i}:String!")) + "){"
                    + string.Join(" ", chunk.Select(i => $"q{i}:instantSearch(query:$q{i}){{{BatchTrackFields}}}")) + "}";
                var body = JsonSerializer.Serialize(new
                {
                    operationName = "OctoTrackBatch",
                    query,
                    variables = chunk.ToDictionary(i => "q" + i, i => PlainQuery(songs[i].Artist, songs[i].Title)),
                });
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var response = await DiscoveryRequestAsync("https://pipe.deezer.com/api", budget, body, token);
                    if (response.Transient || response.Doc is null) return null;
                    var root = response.Doc.RootElement;
                    if (attempt == 0 && TokenExpired(root) && (token = await SearchTokenAsync(budget, token)) is not null)
                        continue;
                    if (root.TryGetProperty("extensions", out var extensions))
                        _logger.LogDebug("deezer batch of {Count} cost {Cost}", chunk.Length, Int(extensions, "queryCost"));
                    if (QueryRefusal(root) is { } refusal)
                    {
                        // Pipe answers a query it cannot parse with a bare "Error": a bug in this
                        // query or a schema change, which asking again will not fix.
                        if (refusal != "QueryComplexityExceededError")
                            _logger.LogWarning("deezer pipe refused the batch query: {Type}", refusal);
                        return refusal;
                    }
                    foreach (var i in chunk)
                    {
                        if (WebCandidates(root, "q" + i) is not { } found) continue;
                        using var normalized = JsonSerializer.SerializeToDocument(new { data = found.Candidates });
                        if (BestMatch(normalized, songs[i].Artist, songs[i].Title, recording: true,
                                duration: songs[i].Duration) is not JsonElement match) continue;
                        results[i] = new TrackMeta(
                            match.TryGetProperty("album", out var album) ? Str(album, "title") : null, null, null,
                            Int(match, "duration"),
                            match.TryGetProperty("artist", out var credit) ? Str(credit, "name") : null, null,
                            match.GetProperty("id").GetString());
                    }
                    return null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Unknown, not missing: the caller's full search decides these songs.
                _logger.LogDebug("deezer batch of {Count} stopped after {Requests} request(s): {Type}",
                    chunk.Length, budget.Requests, ex.GetType().Name);
            }
            return null;
        }
    }

    /// <summary>The error type that refused a whole pipe query (no data, an error without a path),
    /// or null when the query ran.</summary>
    private static string? QueryRefusal(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) return null;
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return null;
        return errors.EnumerateArray().Where(error => !error.TryGetProperty("path", out _))
            .Select(error => Str(error, "type") ?? (error.TryGetProperty("extensions", out var extension)
                ? Str(extension, "type") : null) ?? "error")
            .FirstOrDefault();
    }

    private static bool TokenExpired(JsonElement root) =>
        root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
        && errors.EnumerateArray().Any(error => Str(error, "type") == "JwtTokenExpiredError"
            || error.TryGetProperty("extensions", out var extension) && Str(extension, "type") == "JwtTokenExpiredError");

    /// <summary>
    /// The tracks one instantSearch field (<paramref name="field"/>: "instantSearch", or an alias
    /// in a batch) answered, in the REST search shape BestMatch reads. Null when the answer cannot
    /// be trusted. The one error accepted is node-local "media not found" under that field: its
    /// track is left out and the answer counts as incomplete, as is one with a next page.
    /// </summary>
    private static (List<object> Candidates, bool Incomplete)? WebCandidates(JsonElement root, string field)
    {
        var unavailableNodes = new HashSet<int>();
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null)
        {
            if (errors.ValueKind != JsonValueKind.Array) return null;
            foreach (var error in errors.EnumerateArray())
            {
                var type = Str(error, "type") ?? (error.TryGetProperty("extensions", out var extension)
                    ? Str(extension, "type") : null);
                if (!error.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array
                    || path.GetArrayLength() == 0 || path[0].GetString() is not { } owner)
                    return null;
                // Another field's error in a batch is that field's business.
                if (owner != field) continue;
                if (type != "TrackMediaNotFoundException"
                    || path.GetArrayLength() != 6
                    || path[1].GetString() != "results" || path[2].GetString() != "tracks"
                    || path[3].GetString() != "edges" || path[4].ValueKind != JsonValueKind.Number
                    || !path[4].TryGetInt32(out var index) || index < 0 || path[5].GetString() != "node")
                    return null;
                unavailableNodes.Add(index);
            }
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty(field, out var search) || search.ValueKind != JsonValueKind.Object) return null;
        var tracks = search.GetProperty("results").GetProperty("tracks");
        var edges = tracks.GetProperty("edges");
        if (edges.ValueKind != JsonValueKind.Array) return null;
        if (unavailableNodes.Any(index => index >= edges.GetArrayLength()
            || edges[index].GetProperty("node").ValueKind != JsonValueKind.Null))
            return null;
        var incomplete = unavailableNodes.Count > 0 || tracks.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean();
        var candidates = new List<object>();
        for (var index = 0; index < edges.GetArrayLength(); index++)
        {
            if (unavailableNodes.Contains(index)) continue;
            var node = edges[index].GetProperty("node");
            if (node.ValueKind != JsonValueKind.Object) throw new JsonException("Missing search track.");
            // Every main artist, however many: contributorNames is not paged, where contributors is.
            var names = node.TryGetProperty("contributorNames", out var credited) && credited.ValueKind == JsonValueKind.Array
                ? credited.EnumerateArray().Select(name => name.ValueKind == JsonValueKind.String ? name.GetString() : null)
                    .OfType<string>().Where(name => name.Length > 0).ToList()
                : [];
            candidates.Add(new
            {
                id = node.GetProperty("id").ToString(), title = Str(node, "title"), isrc = Str(node, "ISRC"),
                duration = Int(node, "duration"), artist = new { name = names.Count == 0 ? null : string.Join(", ", names) },
                album = new { title = node.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object
                    ? Str(album, "displayTitle") : null },
            });
        }
        return (candidates, incomplete);
    }
}
