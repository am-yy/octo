using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Octo.Services.Common;

namespace Octo.Services.Trackers;

public sealed record ReleaseName(string Artist, string Album);

/// <summary>Only complete, well-formed tracker searches and group inventories establish a finding.</summary>
public sealed class TrackerCatalogClient(TrackerDirectQueue queue, TimeProvider? time = null)
{
    private const int MaxPages = 20;
    private const int MaxGroupDetails = 10;
    private static readonly HashSet<string> KnownMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        "WEB", "CD", "Vinyl", "SACD", "DVD", "Blu-ray", "Cassette", "DAT", "Soundboard", "Radio",
        "FM Radio", "LaserDisc", "VHS", "Video CD", "CD-R", "CDR", "Acetate", "MiniDisc", "Other",
        "Digital", "Tape", "USB", "DVD-Audio", "DVD-Video", "DVD-R", "BD", "Blu-ray Audio",
        "Reel-to-Reel", "Cartridge", "7 inch Vinyl", "10 inch Vinyl", "12 inch Vinyl",
    };
    private static readonly HashSet<string> KnownFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "FLAC", "MP3", "AAC", "Ogg Vorbis", "MP4", "WavPack", "WAV", "ALAC", "APE", "DTS", "AC3",
        "Opus", "WMA", "MPC", "M4A", "Other",
    };
    private static readonly Dictionary<int, string> KnownCategories = new()
    {
        [1] = "Music", [2] = "Applications", [3] = "E-Books", [4] = "Audiobooks",
        [5] = "E-Learning Videos", [6] = "Comedy", [7] = "Comics", [8] = "Pictures", [9] = "Misc",
    };
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Compatibility for existing non-classifying callers. Unknown medium deliberately cannot
    // produce an upload candidate.
    public Task<TrackerFinding?> SearchAsync(string tracker, IEnumerable<ReleaseName> names,
        CancellationToken ct, Func<CancellationToken, Task<bool>>? eligible = null) =>
        SearchAsync(tracker, names, "unknown", 0, "album", ct, eligible);

    public async Task<TrackerFinding?> SearchAsync(string tracker, IEnumerable<ReleaseName> names,
        string sourceMedium, long identityVersion, string releaseType, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? eligible = null)
    {
        var finding = new TrackerFinding
        {
            SourceMedium = NormalizeMedium(sourceMedium),
            IdentityVersion = identityVersion,
            CheckedUtc = _time.GetUtcNow().UtcDateTime,
        };
        if (!queue.Configured(tracker))
        {
            finding.Error = "API key missing";
            finding.AddDiagnostic("Tracker API key missing.");
            return finding;
        }

        var wanted = names.Where(n => !string.IsNullOrWhiteSpace(n.Artist) && !string.IsNullOrWhiteSpace(n.Album))
            .DistinctBy(n => SongIdentity.Key(n.Artist) + ":" + AlbumKey(n.Album)).ToList();
        if (wanted.Count is 0 or > 8)
        {
            finding.Error = "Release identity needs review";
            finding.AddDiagnostic("Release identity count outside supported bounds.");
            return finding;
        }
        var queries = wanted.SelectMany(n => new[] { n.Artist.Trim() + " " + n.Album.Trim(), n.Album.Trim() })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var groupRows = new Dictionary<int, BrowseGroup>();
        var uncertain = false;
        try
        {
            foreach (var query in queries)
            {
                var queryResults = 0;
                var pages = 0;
                var complete = false;
                int? expectedPages = null;
                try
                {
                for (var page = 1; page <= (expectedPages ?? 1); page++)
                {
                    var reply = await queue.SendAsync(tracker, "browse", HttpMethod.Get,
                        new Dictionary<string, string> { ["searchstr"] = query, ["page"] = page.ToString() },
                        null, ct, eligible);
                    var body = TrackerDirectQueue.Parse(reply);
                    if (body["results"] is not JsonArray rows) throw new InvalidDataException();
                    pages++;
                    queryResults += rows.Count;
                    foreach (var raw in rows)
                    {
                        if (raw is not JsonObject item)
                        {
                            uncertain = true;
                            finding.AddDiagnostic("Browse page contains malformed result.");
                            continue;
                        }
                        if (!ReadBrowseGroup(item, wanted, out var group, out var couldMatch))
                        {
                            if (couldMatch)
                            {
                                uncertain = true;
                                finding.AddDiagnostic("Exact-title browse result lacks verified group identity.");
                            }
                            continue;
                        }
                        if (group is null) continue;
                        if (groupRows.TryGetValue(group.GroupId, out var prior) && prior != group)
                        {
                            uncertain = true;
                            finding.AddDiagnostic("Browse results conflict for one group.");
                        }
                        else groupRows[group.GroupId] = group;
                    }

                    // Gazelle omits pagination fields only for an empty first-page search.
                    if (page == 1 && rows.Count == 0 && !body.ContainsKey("pages") && !body.ContainsKey("currentPage"))
                    {
                        complete = true;
                        break;
                    }
                    if (!int.TryParse(body["pages"]?.ToString(), out var total) || total < 0 || total > MaxPages
                        || !int.TryParse(body["currentPage"]?.ToString(), out var current)
                        || (current != page && !(total == 0 && current == 0 && page == 1))
                        || (total == 0 && (page != 1 || rows.Count != 0))
                        || (total > 0 && rows.Count == 0)
                        || (total > 0 && total < page)
                        || (expectedPages is int priorPages && priorPages != total))
                        throw new InvalidDataException();
                    expectedPages = total;
                    if (total == 0) { complete = true; break; }
                    if (page == total) complete = true;
                }
                }
                catch
                {
                    finding.QueryCoverage.Add(new TrackerQueryCoverage(Bound(query, 160), pages, queryResults, false));
                    throw;
                }
                finding.QueryCoverage.Add(new TrackerQueryCoverage(Bound(query, 160), pages, queryResults, complete));
                if (!complete)
                {
                    uncertain = true;
                    finding.AddDiagnostic("Browse query did not complete all pages.");
                }
            }

            if (groupRows.Count > MaxGroupDetails)
            {
                uncertain = true;
                finding.AddDiagnostic("More than ten matching groups require detail review.");
            }
            foreach (var row in groupRows.Values.OrderBy(g => g.GroupId).Take(MaxGroupDetails))
            {
                var detailReply = await queue.SendAsync(tracker, "torrentgroup", HttpMethod.Get,
                    new Dictionary<string, string> { ["id"] = row.GroupId.ToString() }, null, ct, eligible);
                var detail = TrackerDirectQueue.Parse(detailReply);
                if (!ReadGroupDetail(detail, tracker, row, wanted, out var match, out var detailUncertain, out var nonMusic))
                {
                    if (!nonMusic)
                    {
                        uncertain = true;
                        finding.AddDiagnostic(detailUncertain ?? "Matched group detail could not be verified.");
                    }
                    continue;
                }
                if (match is not null) finding.Matches.Add(match);
            }

            finding.Matches = finding.Matches.DistinctBy(m => m.GroupId).OrderBy(m => m.GroupId).ToList();
            finding.SearchComplete = !uncertain;
            if (uncertain) finding.Error = "Search returned uncertain or incomplete matches";
            finding.Apply(TrackerFindingClassifier.Classify(finding, sourceMedium, releaseType));
        }
        catch (TrackerRequestSkippedException) { return null; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            finding.Error = ex is HttpRequestException ? "Tracker request failed or rate limited" : "Tracker response incomplete";
            finding.AddDiagnostic(finding.Error);
            foreach (var query in queries.Skip(finding.QueryCoverage.Count))
                finding.QueryCoverage.Add(new TrackerQueryCoverage(Bound(query, 160), 0, 0, false));
            finding.SearchComplete = false;
            finding.Apply(TrackerFindingClassifier.Classify(finding, sourceMedium, releaseType));
        }
        return finding;
    }

    /// <summary>Looks up one exact source hash on its named tracker. Never probes another tracker.</summary>
    public async Task<TrackerSourceLookup?> LookupSourceByHashAsync(string tracker, string hash,
        ReleaseName expected, CancellationToken ct, Func<CancellationToken, Task<bool>>? eligible = null,
        string? releaseType = null)
    {
        var result = new TrackerSourceLookup { Tracker = tracker, Hash = (hash ?? "").ToLowerInvariant() };
        if (!queue.Configured(tracker)) { result.Error = "Tracker API key missing"; return result; }
        if (!Regex.IsMatch(hash ?? "", "^[a-fA-F0-9]{40}$") || string.IsNullOrWhiteSpace(expected.Artist)
            || string.IsNullOrWhiteSpace(expected.Album))
        { result.Error = "Source hash or release identity needs review"; return result; }
        var normalizedHash = (hash ?? "").ToUpperInvariant();
        try
        {
            var reply = await queue.SendAsync(tracker, "torrent", HttpMethod.Get,
                new Dictionary<string, string> { ["hash"] = normalizedHash }, null, ct, eligible);
            var body = TrackerDirectQueue.Parse(reply);
            var group = body["group"] as JsonObject;
            var torrent = body["torrent"] as JsonObject;
            if (group is null || torrent is null) throw new InvalidDataException();

            var returnedHash = FirstString(torrent, "hash", "infoHash", "infohash", "torrentHash")
                ?? FirstString(body, "hash", "infoHash", "infohash", "torrentHash");
            var groupId = Positive(group["id"]);
            var torrentId = Positive(torrent["id"]);
            var album = Text(group["name"]);
            var artists = ReadMainArtists(group["musicInfo"]?["artists"]);
            var artist = artists is null ? null : string.Join(" & ", artists);
            var category = ReadCategory(group, out var categoryKnown);
            var actualType = NormalizeReleaseType(group["releaseType"]?.ToString());
            var encoding = Text(torrent["encoding"]) ?? "";
            if (!TryNormalizeTorrentMetadata(Text(torrent["media"]), Text(torrent["format"]), out var medium, out var format))
                throw new InvalidDataException();

            result.GroupId = groupId > 0 ? groupId : null;
            result.TorrentId = torrentId > 0 ? torrentId : null;
            result.Artist = artist;
            result.Album = album;
            result.ReleaseType = actualType;
            result.SourceMedium = medium;
            result.Format = format;
            result.Url = groupId > 0 ? TrackerUrl(tracker, groupId) : null;

            var expectedType = string.IsNullOrWhiteSpace(releaseType) ? null : NormalizeReleaseType(releaseType);
            if (!string.Equals(returnedHash, hash, StringComparison.OrdinalIgnoreCase)
                || groupId <= 0 || torrentId <= 0 || !categoryKnown || !category
                || string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(artist)
                || AlbumKey(album) != AlbumKey(expected.Album)
                || SongIdentity.CompareArtists(expected.Artist, artist) != ArtistAgreement.Agree
                || actualType is not ("album" or "ep")
                || (expectedType is not null && expectedType != actualType)
                || medium is not ("WEB" or "CD") || format != "FLAC"
                || encoding.Contains("MQA", StringComparison.OrdinalIgnoreCase)
                || encoding.Contains("hybrid", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();

            result.Status = "verified";
        }
        catch (TrackerRequestSkippedException) { return null; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            result.Error = ex is HttpRequestException ? "Source tracker lookup failed or rate limited" : "Source tracker response needs review";
        }
        return result;
    }

    internal static string NormalizeMedium(string? medium)
    {
        var value = WebUtility.HtmlDecode(medium ?? "").Trim();
        return value.Equals("web", StringComparison.OrdinalIgnoreCase) ? "WEB"
            : value.Equals("cd", StringComparison.OrdinalIgnoreCase) ? "CD"
            : value.Length == 0 ? "unknown" : value.ToUpperInvariant();
    }

    internal static string NormalizeFormat(string? format) =>
        WebUtility.HtmlDecode(format ?? "").Trim().ToUpperInvariant();

    internal static string NormalizeReleaseType(string? type)
    {
        var value = WebUtility.HtmlDecode(type ?? "").Trim();
        if (int.TryParse(value, out var numeric)) return numeric switch
        {
            1 => "album", 3 => "soundtrack", 5 => "ep", 6 => "anthology", 7 => "compilation",
            8 => "sampler", 9 => "single", 10 => "demo", 11 => "live-album", 12 => "split",
            13 => "remix-album", 14 => "bootleg", 15 => "interview", 16 => "mixtape",
            17 => "dj-mix", 18 => "concert-recording", 21 => "unknown", _ => "unknown",
        };
        return value.ToLowerInvariant() switch
        {
            "album" => "album", "ep" => "ep", "single" => "single", "compilation" => "compilation",
            "anthology" => "anthology", "sampler" => "sampler", "split" => "split",
            "soundtrack" => "soundtrack", "live album" => "live-album", "remix album" => "remix-album",
            "bootleg" => "bootleg", "interview" => "interview", "mixtape" => "mixtape", "demo" => "demo",
            "concert" or "concert recording" => "concert-recording", "dj mix" or "djmix" => "dj-mix",
            "unknown" => "unknown", _ => "",
        };
    }

    private static bool ReadBrowseGroup(JsonObject item, IReadOnlyList<ReleaseName> wanted,
        out BrowseGroup? group, out bool couldMatch)
    {
        group = null;
        couldMatch = false;
        var isMusic = ReadCategory(item, out var categoryKnown, out var categoryInvalid);
        if (categoryKnown && !isMusic) return false;
        var rawName = Text(item["groupName"]);
        if (string.IsNullOrWhiteSpace(rawName))
        {
            couldMatch = categoryInvalid || !categoryKnown || isMusic;
            return false;
        }
        var name = WebUtility.HtmlDecode(rawName).Trim();
        var titleMatches = wanted.Where(n => AlbumKey(n.Album) == AlbumKey(name)).ToList();
        if (titleMatches.Count == 0)
        {
            // Recognized edition suffixes can identify a plausible variant, but dropping
            // that subtitle cannot prove either presence or absence of the canonical group.
            couldMatch = wanted.Any(n => IsEditionVariant(n.Album, name));
            return false;
        }
        couldMatch = true;
        if (categoryInvalid) return false;
        var rawArtist = Text(item["artist"]);
        if (string.IsNullOrWhiteSpace(rawArtist)) return false;
        var artist = WebUtility.HtmlDecode(rawArtist).Trim();
        var artistMatches = titleMatches.Where(n => SongIdentity.CompareArtists(n.Artist, artist) == ArtistAgreement.Agree).ToList();
        if (artistMatches.Count == 0) return false;
        var type = NormalizeReleaseType(item["releaseType"]?.ToString());
        var id = Positive(item["groupId"]);
        if (type.Length == 0 || type == "unknown" || id <= 0) return false;
        // Exact-title music results of another release type remain uncertain until detail confirms type.
        if (type is not ("album" or "ep")) return false;
        group = new BrowseGroup(id, artist, name, type);
        return true;
    }

    private static bool ReadGroupDetail(JsonObject body, string tracker, BrowseGroup browse, IReadOnlyList<ReleaseName> wanted,
        out TrackerMatch? match, out string? error, out bool nonMusic)
    {
        match = null;
        error = null;
        nonMusic = false;
        if (body["group"] is not JsonObject group)
        { error = "Matched group detail is missing."; return false; }
        var category = ReadCategory(group, out var categoryKnown);
        if (categoryKnown && !category) { nonMusic = true; return false; }
        if (!categoryKnown) { error = "Matched group category is missing or inconsistent."; return false; }
        if (body["torrents"] is not JsonArray torrents)
        { error = "Matched group has no complete torrent inventory."; return false; }
        var id = Positive(group["id"]);
        var name = WebUtility.HtmlDecode(Text(group["name"]) ?? "").Trim();
        var actualType = NormalizeReleaseType(group["releaseType"]?.ToString());
        var artists = ReadMainArtists(group["musicInfo"]?["artists"]);
        if (id != browse.GroupId || string.IsNullOrWhiteSpace(name) || artists is null || artists.Count == 0
            || actualType.Length == 0 || actualType == "unknown")
        { error = "Matched group identity or release type is malformed."; return false; }

        var artist = string.Join(" & ", artists);
        if (!wanted.Any(n => AlbumKey(n.Album) == AlbumKey(name)
                && SongIdentity.CompareArtists(n.Artist, artist) == ArtistAgreement.Agree))
        {
            // Browse and detail disagreement means we cannot rely on either identity.
            error = "Browse and group detail identities conflict.";
            return false;
        }
        if (actualType != browse.ReleaseType)
        { error = "Browse and group detail release types conflict."; return false; }
        if (actualType is not ("album" or "ep"))
        { error = "Matched music group has an incompatible release type."; return false; }

        var parsed = new List<TrackerTorrent>(torrents.Count);
        foreach (var raw in torrents)
        {
            if (raw is not JsonObject torrent || Positive(torrent["id"]) <= 0
                || !TryNormalizeTorrentMetadata(Text(torrent["media"]), Text(torrent["format"]), out var media, out var format))
            { error = "Matched group contains malformed medium or format data."; return false; }
            var encoding = Text(torrent["encoding"]) ?? "";
            var scene = Bool(torrent["scene"]);
            var seeders = NonNegative(torrent["seeders"]);
            parsed.Add(new TrackerTorrent(Positive(torrent["id"]), media, format, encoding, seeders, scene));
        }
        if (parsed.Count == 0) { error = "Matched group torrent inventory is empty."; return false; }
        var seedTotal = parsed.Sum(t => t.Seeders);
        var exception = parsed.Any(t => t.Encoding.Contains("MQA", StringComparison.OrdinalIgnoreCase)
            || t.Encoding.Contains("hybrid", StringComparison.OrdinalIgnoreCase));
        match = new TrackerMatch(id, artist, name, seedTotal, TrackerUrl(tracker, id),
            actualType, parsed, true, exception);
        return true;
    }

    private static bool ReadCategory(JsonObject group, out bool known) => ReadCategory(group, out known, out _);

    private static bool ReadCategory(JsonObject group, out bool known, out bool invalid)
    {
        var name = (Text(group["categoryName"]) ?? Text(group["groupCategoryName"]))?.Trim();
        var idNode = group["categoryId"] ?? group["groupCategoryId"];
        var id = Positive(idNode);
        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasId = id > 0;
        if (!hasName && idNode is null) { known = false; invalid = false; return false; }
        var nameCategory = hasName
            ? KnownCategories.Values.FirstOrDefault(category => category.Equals(name, StringComparison.OrdinalIgnoreCase))
            : null;
        var idCategory = hasId ? KnownCategories.GetValueOrDefault(id) : null;
        invalid = idNode is not null && !hasId || hasName && nameCategory is null
            || hasName && hasId && !string.Equals(nameCategory, idCategory, StringComparison.OrdinalIgnoreCase);
        known = !invalid && (nameCategory is not null || idCategory is not null);
        return known && string.Equals(nameCategory ?? idCategory, "Music", StringComparison.Ordinal);
    }

    private static string AlbumKey(string? title)
    {
        var folded = SongIdentity.Fold(WebUtility.HtmlDecode(title ?? "")).Normalize(System.Text.NormalizationForm.FormD)
            .ToLowerInvariant();
        return Regex.Replace(folded, @"[^\p{L}\p{N}\p{M}]", "");
    }

    private static bool IsEditionVariant(string expected, string actual) =>
        AlbumKey(expected) != AlbumKey(actual) && EditionBaseKey(expected) == EditionBaseKey(actual);

    private static string EditionBaseKey(string? title)
    {
        var folded = SongIdentity.Fold(WebUtility.HtmlDecode(title ?? "")).ToLowerInvariant();
        var baseTitle = Regex.Replace(folded,
            @"(?:\s*[(\[{\-:]?\s*)(?:(?:\d+(?:st|nd|rd|th)?\s+)?anniversary(?:\s+(?:edition|version))?|(?:deluxe|expanded|special|bonus)\s+(?:edition|version)|(?:\d{4}\s+)?re-?master(?:ed)?(?:\s+\d{4})?)(?:\s*[)\]}])?\s*$",
            "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return AlbumKey(baseTitle);
    }

    private static bool TryNormalizeTorrentMetadata(string? mediaText, string? formatText, out string media, out string format)
    {
        media = NormalizeMedium(mediaText);
        format = NormalizeFormat(formatText);
        return KnownMedia.Contains(media) && KnownFormats.Contains(format);
    }

    private static List<string>? ReadMainArtists(JsonNode? node)
    {
        if (node is not JsonArray artists) return null;
        var names = new List<string>();
        foreach (var raw in artists)
        {
            if (raw is not JsonObject artist || string.IsNullOrWhiteSpace(Text(artist["name"]))) return null;
            names.Add(WebUtility.HtmlDecode(Text(artist["name"])!.Trim()));
        }
        return names;
    }

    private static string TrackerUrl(string tracker, int groupId) =>
        (tracker.Equals("ops", StringComparison.OrdinalIgnoreCase) ? "https://orpheus.network" : "https://redacted.sh")
        + "/torrents.php?id=" + groupId;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string? FirstString(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys) if (Text(obj[key]) is { Length: > 0 } text) return text;
        return null;
    }
    private static int Positive(JsonNode? node) => int.TryParse(node?.ToString(), out var value) ? value : 0;
    private static int NonNegative(JsonNode? node) => int.TryParse(node?.ToString(), out var value) && value >= 0 ? value : 0;
    private static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static string Bound(string value, int limit) => value.Length <= limit ? value : value[..limit];

    private sealed record BrowseGroup(int GroupId, string Artist, string Name, string ReleaseType);
}
