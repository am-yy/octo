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
    private sealed class TrackSearchBudget(CancellationToken stopping, bool background) : IDisposable
    {
        private readonly CancellationTokenSource _deadline = MakeDeadline(stopping);
        public CancellationToken Token => _deadline.Token;
        public bool Background { get; } = background;
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
        CancellationToken ct, bool background = false, IReadOnlySet<string>? excluded = null, bool readableOnly = false)
    {
        ct.ThrowIfCancellationRequested();
        _stopping.Token.ThrowIfCancellationRequested();
        // A selection with exclusions is a different question. Its underlying HTTP pages
        // are still shared below; no caller owns another caller's JsonDocument or token.
        var key = "lookup|" + JsonSerializer.Serialize(new
        {
            artist = artist?.Trim().ToLowerInvariant(), title = title?.Trim().ToLowerInvariant(),
            background, readableOnly, excluded = excluded?.Order(StringComparer.Ordinal).ToArray()
        });
        var answer = await SharedAsync(key, async () =>
        {
            using var budget = new TrackSearchBudget(_stopping.Token, background);
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
            if (BestMatch(page.Doc, artist, title, excluded, readableOnly, recording: true) is JsonElement hit)
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
        var track = BestMatch(tracks.Doc, artist, title, excluded, readableOnly, recording: true);
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
            if (RecordingMatches(artist, title, gotArtist, gotTitle)) continue;
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
                && RecordingMatches(artist, title, credit, TrackTitle(track))
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
          edges{node{id title ISRC duration contributors{edges{roles node{... on Artist{id name}}}}}}
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
            var unavailableNodes = new HashSet<int>();
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null)
            {
                if (errors.ValueKind != JsonValueKind.Array) return (new DeezerResponse { Transient = true }, null);
                if (errors.GetArrayLength() > 0)
                {
                    var expired = errors.EnumerateArray().Any(error => Str(error, "type") == "JwtTokenExpiredError"
                        || error.TryGetProperty("extensions", out var extension) && Str(extension, "type") == "JwtTokenExpiredError");
                    if (expired && attempt == 0 && (token = await SearchTokenAsync(budget, token)) is not null) continue;
                    // An unavailable result can coexist with usable recordings. Accept only
                    // this known node-local error; all broader failures remain retryable.
                    foreach (var error in errors.EnumerateArray())
                    {
                        var type = Str(error, "type") ?? (error.TryGetProperty("extensions", out var extension)
                            ? Str(extension, "type") : null);
                        if (type != "TrackMediaNotFoundException"
                            || !error.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array
                            || path.GetArrayLength() != 6 || path[0].GetString() != "instantSearch"
                            || path[1].GetString() != "results" || path[2].GetString() != "tracks"
                            || path[3].GetString() != "edges" || path[4].ValueKind != JsonValueKind.Number
                            || !path[4].TryGetInt32(out var index) || index < 0 || path[5].GetString() != "node")
                            return (new DeezerResponse { Transient = true }, null);
                        unavailableNodes.Add(index);
                    }
                    budget.Incomplete = true;
                }
            }
            var tracks = root.GetProperty("data").GetProperty("instantSearch").GetProperty("results").GetProperty("tracks");
            var edges = tracks.GetProperty("edges");
            if (edges.ValueKind != JsonValueKind.Array) return (new DeezerResponse { Transient = true }, null);
            if (unavailableNodes.Any(index => index >= edges.GetArrayLength()
                || edges[index].GetProperty("node").ValueKind != JsonValueKind.Null))
                return (new DeezerResponse { Transient = true }, null);
            budget.Incomplete |= tracks.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean();
            var candidates = new List<object>();
            for (var index = 0; index < edges.GetArrayLength(); index++)
            {
                if (unavailableNodes.Contains(index)) continue;
                var node = edges[index].GetProperty("node");
                if (node.ValueKind != JsonValueKind.Object) throw new JsonException("Missing search track.");
                var names = new List<string>();
                if (node.TryGetProperty("contributors", out var contributors) && contributors.ValueKind == JsonValueKind.Object
                    && contributors.TryGetProperty("edges", out var people) && people.ValueKind == JsonValueKind.Array)
                    foreach (var person in people.EnumerateArray())
                        if (person.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
                            && roles.EnumerateArray().Any(role => role.ValueKind == JsonValueKind.String && role.GetString() == "MAIN")
                            && person.TryGetProperty("node", out var credit) && Str(credit, "name") is { Length: > 0 } name) names.Add(name);
                candidates.Add(new
                {
                    id = node.GetProperty("id").ToString(), title = Str(node, "title"), isrc = Str(node, "ISRC"),
                    duration = Int(node, "duration"), artist = new { name = names.Count == 0 ? null : string.Join(", ", names) }
                });
            }
            using var normalized = JsonSerializer.SerializeToDocument(new { data = candidates });
            if (BestMatch(normalized, artist, title, excluded, recording: true) is JsonElement match)
                return await ReadCandidateAsync(match.GetProperty("id").ToString(), artist, title, budget, readableOnly, match);
            return await HydrateIncompleteAsync(normalized, artist, title, budget, excluded, readableOnly);
        }
        return (new DeezerResponse { Transient = true }, null);
    }
}
