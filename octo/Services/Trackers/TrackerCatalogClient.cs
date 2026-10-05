using System.Net;
using System.Text.Json.Nodes;
using Octo.Services.Common;

namespace Octo.Services.Trackers;

public sealed record ReleaseName(string Artist, string Album);

/// <summary>Only exhaustive, well-formed searches can establish an absent release.</summary>
public sealed class TrackerCatalogClient(TrackerDirectQueue queue, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<TrackerFinding?> SearchAsync(string tracker, IEnumerable<ReleaseName> names,
        CancellationToken ct, Func<CancellationToken, Task<bool>>? eligible = null)
    {
        var finding = new TrackerFinding { CheckedUtc = _time.GetUtcNow().UtcDateTime };
        if (!queue.Configured(tracker)) { finding.Error = "API key missing"; return finding; }
        var wanted = names.Where(n => !string.IsNullOrWhiteSpace(n.Artist) && !string.IsNullOrWhiteSpace(n.Album))
            .DistinctBy(n => SongIdentity.Key(n.Artist) + ":" + SongIdentity.Key(n.Album)).ToList();
        if (wanted.Count is 0 or > 8) { finding.Error = "Release identity needs review"; return finding; }
        var queries = wanted.SelectMany(n => new[] { n.Artist + " " + n.Album, n.Album }).Distinct().ToList();
        var uncertain = false;
        try
        {
            foreach (var query in queries)
            {
                int? expectedPages = null;
                for (var page = 1; page <= (expectedPages ?? 1); page++)
                {
                    var reply = await queue.SendAsync(tracker, "browse", HttpMethod.Get,
                        new Dictionary<string, string> { ["searchstr"] = query, ["page"] = page.ToString() },
                        null, ct, eligible);
                    var body = TrackerDirectQueue.Parse(reply);
                    if (body["results"] is not JsonArray rows) throw new InvalidDataException();
                    foreach (var raw in rows)
                    {
                        if (raw is not JsonObject item) { uncertain = true; continue; }
                        if (item["groupName"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var rawName)
                            || item["artist"] is not JsonValue artistValue || !artistValue.TryGetValue<string>(out var rawArtist))
                        { uncertain = true; continue; }
                        var name = WebUtility.HtmlDecode(rawName);
                        var artist = WebUtility.HtmlDecode(rawArtist);
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(artist))
                        { uncertain = true; continue; }
                        var exact = wanted.Any(n => SongIdentity.Key(n.Album) == SongIdentity.Key(name)
                            && SongIdentity.Key(n.Artist) == SongIdentity.Key(artist));
                        if (!exact)
                        {
                            if (wanted.Any(n => SongIdentity.LooseKey(n.Album) == SongIdentity.LooseKey(name)
                                || SongIdentity.Key(n.Album).Contains(SongIdentity.Key(name))
                                || SongIdentity.Key(name).Contains(SongIdentity.Key(n.Album)))) uncertain = true;
                            continue;
                        }
                        if (!int.TryParse(item["groupId"]?.ToString(), out var id) || id <= 0
                            || item["torrents"] is not JsonArray torrents || torrents.Count == 0
                            || torrents.Any(t => t is not JsonObject))
                        { uncertain = true; continue; }
                        var seeders = torrents.OfType<JsonObject>().Sum(t =>
                            int.TryParse(t["seeders"]?.ToString(), out var count) && count >= 0 ? count : 0);
                        finding.Matches.Add(new TrackerMatch(id, artist, name, seeders,
                            (tracker == "red" ? "https://redacted.sh" : "https://orpheus.network") + "/torrents.php?id=" + id));
                    }
                    if (finding.Matches.Count > 0)
                    {
                        finding.Status = "present";
                        finding.Matches = finding.Matches.DistinctBy(m => m.GroupId).ToList();
                        return finding;
                    }
                    // Gazelle omits pagination entirely for an empty first-page search.
                    // Missing paging on later/nonempty pages still means incomplete evidence.
                    if (page == 1 && rows.Count == 0 && !body.ContainsKey("pages") && !body.ContainsKey("currentPage"))
                        break;
                    if (!int.TryParse(body["pages"]?.ToString(), out var total) || total < 0 || total > 20
                        || !int.TryParse(body["currentPage"]?.ToString(), out var current)
                        || (current != page && !(total == 0 && current == 0 && page == 1))
                        || (total == 0 && (page != 1 || rows.Count != 0))
                        || (total > 1 && rows.Count == 0)
                        || (total > 0 && total < page)
                        || (expectedPages is int prior && prior != total)) throw new InvalidDataException();
                    expectedPages = total;
                }
            }
            finding.Status = uncertain ? "unknown" : "missing";
            if (uncertain) finding.Error = "Search returned uncertain or incomplete matches";
        }
        catch (TrackerRequestSkippedException) { return null; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            finding.Error = ex is HttpRequestException ? "Tracker request failed or rate limited" : "Tracker response incomplete";
        }
        return finding;
    }
}
