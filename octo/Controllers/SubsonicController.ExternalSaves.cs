using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Octo.Models.Domain;
using Octo.Services.Subsonic;

namespace Octo.Controllers;

public partial class SubsonicController
{
    private async Task<(string? User, IActionResult? Error)> SavedCallerAsync(
        Dictionary<string, string> parameters, bool native, string format)
    {
        if (native)
        {
            var check = await _proxyService.RelayRawAsync("api/playlist",
                new() { ["_end"] = "0" }, "GET");
            if (check.Status is < 200 or >= 300)
                return (null, StatusCode(check.Status, new { error = "Navidrome authentication failed" }));
            var user = NativeUsername(new Dictionary<string, string>());
            if (string.IsNullOrWhiteSpace(user)) return (null, Unauthorized(new { error = "Navidrome did not identify this caller" }));
            await RetrySavedMirrorsAsync(user, parameters, true);
            return (user, null);
        }
        if (!await HasAcceptedSubsonicCredentialsAsync(parameters))
            return (null, _responseBuilder.CreateError(format, 40, "Wrong username or password"));
        var username = await _requestIdentity.UsernameAsync(parameters, _proxyService);
        if (string.IsNullOrWhiteSpace(username)) return (null, _responseBuilder.CreateError(format, 40, "Navidrome did not identify this caller"));
        await RetrySavedMirrorsAsync(username, parameters, false);
        return (username, null);
    }

    // Cached per sign-in, so ranged reads and prewarms do not ping Navidrome each time. No
    // request token: prewarm can run after the response, and the check bounds its own wait.
    private async Task<bool> HasAcceptedSubsonicCredentialsAsync(Dictionary<string, string> parameters) =>
        await _credentialCheck.CheckAsync(SubsonicCredential.From(parameters), _proxyService)
            == CredentialVerdict.Accepted;

    private async Task<Song?> SavedSongAsync(string id, Dictionary<string, string> parameters, bool native)
    {
        if (_externalSaves?.GetSong(id) is { IsLocal: false } kept) return kept;
        id = _externalSaves?.CanonicalSongId(id) ?? id;
        var (external, provider, externalId) = _localLibraryService.ParseSongId(id);
        if (external)
        {
            var song = await _metadataService.GetSongAsync(provider!, externalId!);
            if (song is not null)
            {
                song.Id = id;
                song.DeezerId ??= _idRegistry.Lookup(id)?.DeezerId;
            }
            return song;
        }
        if (native)
        {
            var raw = await _proxyService.RelayRawAsync("api/song/" + Uri.EscapeDataString(id), new(), "GET");
            return raw.Status == 200 && JsonNode.Parse(raw.Body) is JsonObject row ? SavedLibrarySong(row) : null;
        }
        var p = new Dictionary<string, string>(parameters) { ["id"] = id, ["f"] = "json" };
        var answer = await _proxyService.RelaySafeAsync("rest/getSong", p);
        return answer.Success && answer.Body is not null
            && JsonNode.Parse(answer.Body)?["subsonic-response"]?["song"] is JsonObject s
            ? SavedLibrarySong(s) : null;
    }

    internal static Song SavedLibrarySong(JsonObject row) => new()
    {
        Id = row["mediaFileId"]?.ToString() ?? row["id"]?.ToString() ?? "",
        Title = row["title"]?.ToString() ?? "", Artist = row["artist"]?.ToString() ?? "",
        Album = row["album"]?.ToString() ?? "", Duration = (int?)Number(row["duration"]),
        Suffix = row["suffix"]?.ToString(), BitRate = (int?)Number(row["bitRate"]),
        Track = (int?)Number(row["trackNumber"] ?? row["track"]),
        DiscNumber = (int?)Number(row["discNumber"]), IsLocal = true,
        AlbumId = row["albumId"]?.ToString(), ArtistId = row["artistId"]?.ToString(),
        LocalPath = row["path"]?.ToString(),
    };

    private static bool IsMissingSubsonicResource(byte[] body, string format)
    {
        try
        {
            return format == "json"
                ? JsonNode.Parse(body)?["subsonic-response"]?["error"]?["code"]?.ToString() == "70"
                : XDocument.Parse(Encoding.UTF8.GetString(body)).Descendants()
                    .Any(node => node.Name.LocalName == "error" && node.Attribute("code")?.Value == "70");
        }
        catch { return false; }
    }

    private static double? Number(JsonNode? node) => double.TryParse(node?.ToString(),
        System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    private async Task<ExternalSavedPlaylist?> LoadSavedPlaylistAsync(string user, string id,
        Dictionary<string, string> parameters, bool native, bool write, bool allowMissing = false)
    {
        JsonObject? row;
        JsonArray? tracks;
        if (native)
        {
            var raw = await _proxyService.RelayRawAsync("api/playlist/" + Uri.EscapeDataString(id), new(), "GET");
            if (allowMissing && raw.Status == 404) return _externalSaves!.GetPlaylist(user, id);
            if (raw.Status != 200) return null;
            row = JsonNode.Parse(raw.Body) as JsonObject;
            if (row is null || write && (row["ownerName"]?.ToString() != user
                || row["readonly"]?.ToString() == "true" || row["sync"]?.ToString() == "true"
                || row["rules"] is not null)) return null;
            var existing = _externalSaves!.GetPlaylist(row["ownerName"]?.ToString() ?? user, id);
            if (existing is not null) return existing;
            var full = await _proxyService.RelayRawAsync("api/playlist/" + Uri.EscapeDataString(id) + "/tracks",
                new() { ["_end"] = "0" }, "GET");
            if (full.Status != 200) return null;
            tracks = JsonNode.Parse(full.Body) as JsonArray;
        }
        else
        {
            var p = new Dictionary<string, string>(parameters) { ["id"] = id, ["f"] = "json" };
            var raw = await _proxyService.RelaySafeAsync("rest/getPlaylist", p);
            if (allowMissing && raw.Success && raw.Body is not null && IsMissingSubsonicResource(raw.Body, "json"))
                return _externalSaves!.GetPlaylist(user, id);
            if (!raw.Success || raw.Body is null || !IsSuccessfulSubsonicResponse(raw.Body, "json")) return null;
            row = JsonNode.Parse(raw.Body)?["subsonic-response"]?["playlist"] as JsonObject;
            if (row is null || write && row["owner"]?.ToString() != user) return null;
            var existing = _externalSaves!.GetPlaylist(row["owner"]?.ToString() ?? user, id);
            if (existing is not null) return existing;
            tracks = row["entry"] as JsonArray;
        }
        return new ExternalSavedPlaylist
        {
            Id = id, UserId = user, Owner = user, Name = row["name"]?.ToString() ?? "", Comment = row["comment"]?.ToString() ?? "",
            Public = row["public"]?.ToString() == "true",
            Tracks = (tracks ?? []).OfType<JsonObject>().Select(t =>
            {
                var song = SavedLibrarySong(t);
                return new ExternalPlaylistTrack { SongId = song.Id, Song = song };
            }).ToList(),
        };
    }

    // ponytail: serialize playlist requests for household use; use per-playlist locks if write traffic grows.
    private static readonly SemaphoreSlim SavedPlaylistGate = new(1, 1);

    private async Task<IActionResult> MutateSavedPlaylistAsync(string endpoint,
        Dictionary<string, string> parameters, string format)
    {
        await SavedPlaylistGate.WaitAsync(HttpContext.RequestAborted);
        try { return await MutateSavedPlaylistCoreAsync(endpoint, parameters, format); }
        finally { SavedPlaylistGate.Release(); }
    }

    private async Task<IActionResult> MutateSavedPlaylistCoreAsync(string endpoint,
        Dictionary<string, string> parameters, string format)
    {
        var (user, error) = await SavedCallerAsync(parameters, false, format);
        if (error is not null) return error;
        var id = parameters.GetValueOrDefault("playlistId", parameters.GetValueOrDefault("id", ""));
        var adding = await _requestParser.ExtractParameterValuesAsync(Request,
            endpoint == "createPlaylist" ? "songId" : "songIdToAdd");
        ExternalSavedPlaylist? playlist = null;
        if (id.Length > 0)
        {
            playlist = await LoadSavedPlaylistAsync(user!, id, parameters, false, true, allowMissing: endpoint == "deletePlaylist");
            if (playlist is null) return _responseBuilder.CreateError(format, 50, "Playlist is not editable by this caller");
        }
        try
        {
            if (endpoint == "deletePlaylist")
            {
                var deleted = await _proxyService.RelaySafeAsync("rest/deletePlaylist", parameters);
                if (!deleted.Success || deleted.Body is null || !(IsSuccessfulSubsonicResponse(deleted.Body, format)
                    || IsMissingSubsonicResource(deleted.Body, format)))
                    return _responseBuilder.CreateError(format, 50, "Unable to delete playlist");
                await _externalSaves!.RemovePlaylistAsync(user!, id);
                _saveWorker?.Wake();
                return _responseBuilder.CreateResponse(format, "deletePlaylist", new { });
            }
            var songs = new List<Song>();
            foreach (var trackId in adding)
            {
                var song = await SavedSongAsync(trackId, parameters, false);
                if (song is null) return _responseBuilder.CreateError(format, 70, "Song not found: " + trackId);
                songs.Add(song);
            }
            if (playlist is null)
            {
                var create = new Dictionary<string, string>(parameters) { ["f"] = "json" };
                create.Remove("songId");
                var created = await _proxyService.RelaySafeAsync("rest/createPlaylist", create);
                if (!created.Success || created.Body is null || !IsSuccessfulSubsonicResponse(created.Body, "json"))
                    return _responseBuilder.CreateError(format, 50, "Unable to create playlist");
                var row = JsonNode.Parse(created.Body)?["subsonic-response"]?["playlist"];
                id = row?["id"]?.ToString() ?? "";
                if (id.Length == 0) return _responseBuilder.CreateError(format, 0, "Navidrome returned no playlist ID");
                playlist = new() { Id = id, UserId = user!, Owner = user!, Name = parameters.GetValueOrDefault("name", "") };
            }
            if (endpoint == "createPlaylist") playlist.Tracks.Clear();
            else
            {
                var removals = await _requestParser.ExtractParameterValuesAsync(Request, "songIndexToRemove");
                var indices = removals.Select(s => int.TryParse(s, out var n) ? n : -1).ToHashSet();
                if (indices.Any(n => n < 0 || n >= playlist.Tracks.Count))
                    return _responseBuilder.CreateError(format, 10, "Invalid playlist position");
                playlist.Tracks = playlist.Tracks.Where((_, n) => !indices.Contains(n)).ToList();
            }
            playlist.Tracks.AddRange(songs.Select(song => new ExternalPlaylistTrack { SongId = song.Id, Song = song }));
            playlist.Name = parameters.GetValueOrDefault("name", playlist.Name);
            if (parameters.TryGetValue("comment", out var comment)) playlist.Comment = comment;
            if (parameters.TryGetValue("public", out var publicity)) playlist.Public = publicity == "true";
            playlist.PendingMirror = true;
            playlist = await _externalSaves!.UpsertPlaylistAsync(playlist);
            await QueueSavedSongsAsync(songs, user!);
            await MirrorSavedPlaylistAsync(playlist, parameters, false);
            return SavedSubsonicPlaylist(playlist, format, user!);
        }
        catch (IOException) { return _responseBuilder.CreateError(format, 0, "Unable to persist playlist"); }
        catch (UnauthorizedAccessException) { return _responseBuilder.CreateError(format, 0, "Unable to persist playlist"); }
    }

    private Task QueueSavedSongsAsync(IEnumerable<Song> songs, string user)
    {
        foreach (var song in songs.Where(song => !song.IsLocal))
        {
            var provider = song.ExternalProvider ?? "soulseek";
            var externalId = song.ExternalId ?? song.Id;
            _acquisitionTracker?.Begin(provider, externalId,
                song.Id, _subsonicSettings.RecordRequestedBy ? user : null, song.Artist, song.Title, song.Album);
            // Import may have completed while this request still held external metadata.
            if (_externalSaves?.GetSong(song.Id) is { IsLocal: true } imported)
                _acquisitionTracker?.Complete(provider, externalId, imported.Id);
        }
        _saveWorker?.Wake();
        return Task.CompletedTask;
    }

    private async Task MirrorSavedPlaylistAsync(ExternalSavedPlaylist playlist,
        Dictionary<string, string> parameters, bool native)
    {
        try
        {
            var ids = playlist.Tracks.Where(t => t.Song?.IsLocal == true || t.CanonicalId is { Length: > 0 })
                .Select(t => t.CanonicalId ?? t.SongId).ToList();
            bool success;
            if (native)
            {
                var body = new JsonObject { ["id"] = playlist.Id, ["name"] = playlist.Name,
                    ["comment"] = playlist.Comment, ["public"] = playlist.Public,
                    ["tracks"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["mediaFileId"] = id }).ToArray()) };
                var result = await _proxyService.RelayRawAsync("api/playlist/" + Uri.EscapeDataString(playlist.Id), new(), "PUT",
                    Encoding.UTF8.GetBytes(body.ToJsonString()));
                success = result.Status is >= 200 and < 300;
            }
            else
            {
                var p = parameters.Where(kv => kv.Key is "u" or "t" or "s" or "p" or "apiKey" or "c" or "v").ToList();
                p.AddRange(new Dictionary<string, string> { ["playlistId"] = playlist.Id, ["name"] = playlist.Name, ["f"] = "json" });
                p.AddRange(ids.Select(id => new KeyValuePair<string, string>("songId", id)));
                var result = await _proxyService.RelayAsync("rest/createPlaylist", p);
                success = IsSuccessfulSubsonicResponse(result.Body, "json");
                if (success)
                {
                    var metadata = parameters.Where(kv => kv.Key is "u" or "t" or "s" or "p" or "apiKey" or "c" or "v")
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                    metadata["playlistId"] = playlist.Id;
                    metadata["comment"] = playlist.Comment ?? "";
                    metadata["public"] = playlist.Public.ToString().ToLowerInvariant();
                    metadata["f"] = "json";
                    var updated = await _proxyService.RelayAsync("rest/updatePlaylist", metadata);
                    success = IsSuccessfulSubsonicResponse(updated.Body, "json");
                }
            }
            if (success) await _externalSaves!.MarkPlaylistMirroredAsync(playlist.UserId, playlist.Id, playlist.UpdatedUtc);
        }
        catch (Exception ex) { _logger.LogWarning("Playlist {Id} mirror deferred: {Reason}", playlist.Id, ex.GetType().Name); }
    }

    private JsonObject SavedPlaylistFields(ExternalSavedPlaylist playlist, bool native) => new()
    {
        ["id"] = playlist.Id, ["name"] = playlist.Name, ["comment"] = playlist.Comment,
        [native ? "ownerName" : "owner"] = playlist.Owner, ["public"] = playlist.Public,
        ["songCount"] = playlist.Tracks.Count,
        ["duration"] = playlist.Tracks.Sum(track => track.Song is { } song ? _idRegistry.GetDisplayMetadata(song).Duration ?? 0 : 0),
        [native ? "updatedAt" : "changed"] = playlist.UpdatedUtc,
    };

    private IActionResult SavedSubsonicPlaylist(ExternalSavedPlaylist playlist, string format, string user)
    {
        var fields = SavedPlaylistFields(playlist, false);
        var entries = playlist.Tracks.Where(t => t.Song is not null).Select(t =>
        {
            var song = JsonSerializer.Deserialize<Song>(JsonSerializer.Serialize(t.Song))!;
            song.Id = t.CanonicalId ?? t.SongId;
            return song;
        }).ToList();
        if (format == "json")
        {
            fields["entry"] = new JsonArray(entries.Select(s =>
            {
                var row = JsonSerializer.SerializeToNode(_responseBuilder.ConvertSongToJson(s))!;
                if (_externalSaves!.IsHearted(user, s.Id)) row["starred"] = DateTime.UtcNow;
                return row;
            }).ToArray());
            return new JsonResult(new JsonObject { ["subsonic-response"] = new JsonObject
                { ["status"] = "ok", ["version"] = "1.16.1", ["playlist"] = fields } });
        }
        XNamespace ns = "http://subsonic.org/restapi";
        var row = new XElement(ns + "playlist", fields.Select(kv => new XAttribute(kv.Key, kv.Value?.ToString() ?? "")),
            entries.Select(s => { var entry = _responseBuilder.ConvertSongToXml(s, ns); entry.Name = ns + "entry";
                if (_externalSaves!.IsHearted(user, s.Id)) entry.SetAttributeValue("starred", DateTime.UtcNow.ToString("O"));
                return entry; }));
        return Content(new XDocument(new XElement(ns + "subsonic-response", new XAttribute("status", "ok"),
            new XAttribute("version", "1.16.1"), row)).ToString(), "application/xml");
    }

    private async Task<IActionResult?> TryServeSavedEndpointAsync(string endpoint,
        Dictionary<string, string> parameters, string format)
    {
        var name = endpoint.Replace(".view", "");
        if (name.Equals("rest/unstar", StringComparison.OrdinalIgnoreCase))
            return await MutateSavedHeartsAsync(parameters, format, false);
        if (name.Equals("rest/getStarred2", StringComparison.OrdinalIgnoreCase)
            || name.Equals("rest/getStarred", StringComparison.OrdinalIgnoreCase))
            return await SavedFavoritesAsync(name, parameters, format, false);
        if (name.Equals("api/playlist", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(Request.Method))
            return await SavedNativeCreateAsync(parameters);
        if (name.StartsWith("api/playlist/", StringComparison.OrdinalIgnoreCase))
            return await SavedNativePlaylistAsync(name, parameters);
        if (name.Equals("api/song", StringComparison.OrdinalIgnoreCase) && parameters.GetValueOrDefault("starred") == "true")
            return await SavedFavoritesAsync(name, parameters, "json", true);
        if (name.StartsWith("api/song/", StringComparison.OrdinalIgnoreCase))
        {
            var songId = name[9..].Trim('/');
            if (songId.Contains('/') || _externalSaves!.GetSong(songId) is not { } song) return null;
            var (user, error) = await SavedCallerAsync(parameters, true, "json");
            if (error is not null) return error;
            if (HttpMethods.IsGet(Request.Method))
            {
                if (song.IsLocal && await SavedSongAsync(song.Id, parameters, true) is null) return NotFound();
                var row = BuildNativeSongObject(song);
                row["starred"] = _externalSaves.IsHearted(user!, songId);
                return new JsonResult(row);
            }
            if (HttpContext.Items["Octo.RawBody"] is byte[] bytes
                && JsonNode.Parse(bytes)?["starred"] is { } heart)
                return await MutateSavedHeartsAsync(new(parameters) { ["id"] = songId }, "json", heart.ToString() == "true", true);
        }
        return null;
    }

    private async Task<IActionResult?> MutateSavedHeartsAsync(Dictionary<string, string> parameters,
        string format, bool heart, bool native = false)
    {
        var ids = await _requestParser.ExtractParameterValuesAsync(Request, "id");
        if (ids.Count == 0 && parameters.GetValueOrDefault("id") is { Length: > 0 } initialId) ids = [initialId];
        if (!ids.Any(IsSavedSongReference)) return null;
        var (user, error) = await SavedCallerAsync(parameters, native, format);
        if (error is not null) return error;
        try
        {
            var songs = new List<Song>();
            foreach (var id in ids)
            {
                var song = await SavedSongAsync(id, parameters, native);
                if (song is null) return _responseBuilder.CreateError(format, 70, "Song not found");
                songs.Add(song);
            }
            foreach (var song in songs) await _externalSaves!.SetHeartAsync(user!, song, heart);
            if (heart) await QueueSavedSongsAsync(songs, user!);
            _saveWorker?.Wake();
            await RetrySavedMirrorsAsync(user!, parameters, native);
            return native ? Ok(new { }) : _responseBuilder.CreateResponse(format, heart ? "starred" : "unstarred", new { });
        }
        catch (IOException) { return native ? StatusCode(500, new { error = "Unable to persist heart" })
                : _responseBuilder.CreateError(format, 0, "Unable to persist heart"); }
        catch (UnauthorizedAccessException) { return native ? StatusCode(500, new { error = "Unable to persist heart" })
                : _responseBuilder.CreateError(format, 0, "Unable to persist heart"); }
    }

    private async Task<IActionResult> SavedFavoritesAsync(string endpoint,
        Dictionary<string, string> parameters, string format, bool native)
    {
        var (user, error) = await SavedCallerAsync(parameters, native, format);
        if (error is not null) return error;
        if (native)
        {
            var p = new Dictionary<string, string>(parameters) { ["_start"] = "0", ["_end"] = "0" };
            var raw = await _proxyService.RelayRawAsync(endpoint, p, "GET");
            if (raw.Status != 200) return StatusCode(raw.Status);
            var rows = JsonNode.Parse(raw.Body) as JsonArray ?? [];
            RemoveUnhearted(rows, user!);
            foreach (var heart in _externalSaves!.GetHearts(user!))
            { var song = heart.Song;
                if (!rows.Any(r => r?["id"]?.ToString() == song.Id))
                { var row = BuildNativeSongObject(song); row["starred"] = true; rows.Add(row); } }
            return SavedNativePage(rows, parameters);
        }
        var p2 = new Dictionary<string, string>(parameters) { ["f"] = "json" };
        var relay = await _proxyService.RelaySafeAsync(endpoint, p2);
        if (!relay.Success || relay.Body is null || !IsSuccessfulSubsonicResponse(relay.Body, "json"))
            return _responseBuilder.CreateError(format, 0, "Unable to load favorites");
        var root = JsonNode.Parse(relay.Body)!;
        var key = endpoint.EndsWith("2") ? "starred2" : "starred";
        var starred = root["subsonic-response"]![key] as JsonObject ?? new();
        root["subsonic-response"]![key] = starred;
        var entries = starred["song"] as JsonArray ?? new();
        starred["song"] = entries;
        RemoveUnhearted(entries, user!);
        foreach (var heart in _externalSaves!.GetHearts(user!))
            { var song = heart.Song;
            if (!entries.Any(r => r?["id"]?.ToString() == song.Id))
            { var row = JsonSerializer.SerializeToNode(_responseBuilder.ConvertSongToJson(song))!; row["starred"] = DateTime.UtcNow; entries.Add(row); } }
        if (format == "json") return new JsonResult(root);
        XNamespace ns = "http://subsonic.org/restapi";
        XElement Row(string kind, JsonNode row) => new(ns + kind,
            row.AsObject().Where(kv => kv.Value is JsonValue).Select(kv => new XAttribute(kv.Key, kv.Value!.ToString())));
        return Content(new XElement(ns + "subsonic-response", new XAttribute("status", "ok"), new XAttribute("version", "1.16.1"),
            new XElement(ns + key, starred.SelectMany(kv => (kv.Value as JsonArray ?? []).Select(row => Row(kv.Key, row!))))).ToString(), "application/xml");
    }

    private IActionResult SavedNativePage(JsonArray rows, Dictionary<string, string> parameters)
    {
        var start = int.TryParse(parameters.GetValueOrDefault("_start"), out var a) ? Math.Max(0, a) : 0;
        var end = int.TryParse(parameters.GetValueOrDefault("_end"), out var b) && b > 0 ? b : rows.Count;
        Response.Headers["X-Total-Count"] = rows.Count.ToString();
        return new JsonResult(new JsonArray(rows.Skip(start).Take(Math.Max(0, end - start)).Select(row => row?.DeepClone()).ToArray()));
    }

    private void RemoveUnhearted(JsonArray rows, string user)
    {
        var removed = _externalSaves!.GetHeartMutations(user).Where(h => !h.Hearted)
            .Select(h => _externalSaves.CanonicalSongId(h.SongId)).ToHashSet(StringComparer.Ordinal);
        for (var index = rows.Count - 1; index >= 0; index--)
            if (removed.Contains(rows[index]?["id"]?.ToString() ?? "")) rows.RemoveAt(index);
    }

    private async Task<IActionResult?> SavedNativePlaylistAsync(string endpoint, Dictionary<string, string> parameters)
    {
        await SavedPlaylistGate.WaitAsync(HttpContext.RequestAborted);
        try { return await SavedNativePlaylistCoreAsync(endpoint, parameters); }
        finally { SavedPlaylistGate.Release(); }
    }

    private async Task<IActionResult?> SavedNativePlaylistCoreAsync(string endpoint, Dictionary<string, string> parameters)
    {
        var parts = endpoint.Split('/');
        if (parts.Length < 3 || IsOctoPlaylistId(parts[2])) return null;
        var id = parts[2];
        var tracksEndpoint = parts.Length >= 4 && parts[3] == "tracks";
        var known = _externalSaves!.Snapshot().Playlists.Any(p => p.Id == id);
        var input = HttpContext.Items["Octo.RawBody"] is byte[] bytes && bytes.Length > 0 ? JsonNode.Parse(bytes) as JsonObject : null;
        var addIds = (input?["ids"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").ToList();
        var replacement = input?["tracks"] as JsonArray;
        if (!known && HttpMethods.IsGet(Request.Method)) return null;
        if (!known && !addIds.Any(trackId => _localLibraryService.ParseSongId(trackId).isExternal)
            && replacement is null) return null;
        var (user, error) = await SavedCallerAsync(parameters, true, "json");
        if (error is not null) return error;
        if (input is not null && new[] { "albumIds", "artistIds", "discs" }.Any(key => input[key] is JsonArray { Count: > 0 }))
            return BadRequest(new { error = "Use song IDs when editing a mixed playlist" });
        if (replacement is not null && replacement.Any(track => track is not JsonObject))
            return BadRequest(new { error = "Invalid playlist tracks" });
        var playlist = await LoadSavedPlaylistAsync(user!, id, parameters, true, !HttpMethods.IsGet(Request.Method),
            allowMissing: HttpMethods.IsDelete(Request.Method) && !tracksEndpoint);
        if (playlist is null) return StatusCode(403, new { error = "Playlist is unavailable or not editable" });
        try
        {
            if (HttpMethods.IsGet(Request.Method))
            {
                if (!tracksEndpoint) return new JsonResult(SavedPlaylistFields(playlist, true));
                var rows = new JsonArray(playlist.Tracks.Select((track, index) =>
                {
                    var row = track.Song is not null ? BuildNativeSongObject(track.Song) : new JsonObject();
                    row["starred"] = _externalSaves.IsHearted(user!, track.CanonicalId ?? track.SongId);
                    row["id"] = (index + 1).ToString(); row["mediaFileId"] = track.CanonicalId ?? track.SongId;
                    row["playlistId"] = playlist.Id; return (JsonNode)row;
                }).ToArray());
                if (parts.Length == 5 && int.TryParse(parts[4], out var position))
                    return position > 0 && position <= rows.Count ? new JsonResult(rows[position - 1]!.DeepClone()) : NotFound();
                return SavedNativePage(rows, parameters);
            }
            if (HttpMethods.IsDelete(Request.Method) && !tracksEndpoint)
            {
                var raw = await _proxyService.RelayRawAsync(endpoint, parameters);
                if (raw.Status != 404 && (raw.Status is < 200 or >= 300)) return StatusCode(raw.Status);
                await _externalSaves.RemovePlaylistAsync(user!, id);
                _saveWorker?.Wake();
                return Ok(new { id });
            }
            var songs = new List<Song>();
            if (HttpMethods.IsPost(Request.Method) && tracksEndpoint || replacement is not null)
            {
                if (replacement is not null)
                    addIds = replacement.Select(n => n?["mediaFileId"]?.ToString() ?? n?["id"]?.ToString() ?? "").ToList();
                foreach (var songId in addIds)
                {
                    var song = await SavedSongAsync(songId, parameters, true);
                    if (song is null) return BadRequest(new { error = "Song not found: " + songId });
                    songs.Add(song);
                }
                if (replacement is not null) playlist.Tracks.Clear();
                playlist.Tracks.AddRange(songs.Select(s => new ExternalPlaylistTrack { SongId = s.Id, Song = s }));
            }
            else if (HttpMethods.IsDelete(Request.Method) && tracksEndpoint)
            {
                var remove = await _requestParser.ExtractParameterValuesAsync(Request, "id");
                if (parts.Length == 5) remove = [parts[4]];
                var indices = remove.Select(s => int.TryParse(s, out var n) ? n - 1 : -1).ToHashSet();
                if (indices.Any(n => n < 0 || n >= playlist.Tracks.Count)) return BadRequest(new { error = "Invalid playlist position" });
                playlist.Tracks = playlist.Tracks.Where((_, n) => !indices.Contains(n)).ToList();
            }
            else if (HttpMethods.IsPut(Request.Method) && tracksEndpoint && parts.Length == 5)
            {
                if (!int.TryParse(parts[4], out var from) || !int.TryParse(input?["insert_before"]?.ToString(), out var to)
                    || from <= 0 || from > playlist.Tracks.Count || to <= 0 || to > playlist.Tracks.Count + 1)
                    return BadRequest(new { error = "Invalid playlist position" });
                var moved = playlist.Tracks[from - 1];
                playlist.Tracks.RemoveAt(from - 1);
                playlist.Tracks.Insert(Math.Min(to > from ? to - 2 : to - 1, playlist.Tracks.Count), moved);
            }
            else if (!HttpMethods.IsPut(Request.Method) || tracksEndpoint) return BadRequest(new { error = "Unsupported playlist mutation" });
            playlist.Name = input?["name"]?.ToString() ?? playlist.Name;
            playlist.Comment = input?["comment"]?.ToString() ?? playlist.Comment;
            if (input?["public"] is { } pub) playlist.Public = pub.ToString() == "true";
            playlist.PendingMirror = true;
            playlist = await _externalSaves.UpsertPlaylistAsync(playlist);
            await QueueSavedSongsAsync(songs, user!);
            await MirrorSavedPlaylistAsync(playlist, parameters, true);
            return new JsonResult(tracksEndpoint ? new JsonObject { ["id"] = parts.Length == 5 ? parts[4] : id, ["added"] = songs.Count }
                : SavedPlaylistFields(playlist, true));
        }
        catch (IOException) { return StatusCode(500, new { error = "Unable to persist playlist" }); }
        catch (UnauthorizedAccessException) { return StatusCode(500, new { error = "Unable to persist playlist" }); }
    }
}

public partial class SubsonicController
{
    private static bool IsPlayableFlac(string? path)
    {
        if (path is null || !System.IO.File.Exists(path) || !Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase)) return false;
        try { using var audio = TagLib.File.Create(path); return audio is TagLib.Flac.File && audio.Properties.Duration > TimeSpan.Zero; }
        catch { return false; }
    }

    private void ApplySavedPlaylistRows(JsonArray rows, string user, bool native)
    {
        if (_externalSaves is null) return;
        foreach (var row in rows.OfType<JsonObject>())
            if (_externalSaves.GetPlaylist(row[native ? "ownerName" : "owner"]?.ToString() ?? user, row["id"]?.ToString() ?? "") is { } saved)
                foreach (var field in SavedPlaylistFields(saved, native)) row[field.Key] = field.Value?.DeepClone();
    }

    private void ApplySavedPlaylistXml(XElement playlists, string user)
    {
        if (_externalSaves is null) return;
        foreach (var row in playlists.Elements())
            if (_externalSaves.GetPlaylist(row.Attribute("owner")?.Value ?? user, row.Attribute("id")?.Value ?? "") is { } saved)
                foreach (var field in SavedPlaylistFields(saved, false)) row.SetAttributeValue(field.Key, field.Value?.ToString());
    }

    private IActionResult SavedSongResponse(Song song, string id, string user, string format)
    {
        var hearted = _externalSaves!.IsHearted(user, id);
        if (format == "json")
        {
            var row = JsonSerializer.SerializeToNode(_responseBuilder.ConvertSongToJson(song))!;
            if (hearted) row["starred"] = DateTime.UtcNow;
            return new JsonResult(new JsonObject { ["subsonic-response"] = new JsonObject
                { ["status"] = "ok", ["version"] = "1.16.1", ["song"] = row } });
        }
        XNamespace ns = "http://subsonic.org/restapi";
        var entry = _responseBuilder.ConvertSongToXml(song, ns);
        if (hearted) entry.SetAttributeValue("starred", DateTime.UtcNow.ToString("O"));
        return Content(new XElement(ns + "subsonic-response", new XAttribute("status", "ok"),
            new XAttribute("version", "1.16.1"), entry).ToString(), "application/xml");
    }
}

public partial class SubsonicController
{
    private async Task RetrySavedMirrorsAsync(string user, Dictionary<string, string> parameters, bool native)
    {
        if (_externalSaves is null) return;
        foreach (var playlist in _externalSaves.GetPlaylists(user).Where(p => p.PendingMirror))
            await MirrorSavedPlaylistAsync(playlist, parameters, native);
        var auth = parameters.Where(kv => kv.Key is "u" or "t" or "s" or "p" or "apiKey" or "c" or "v")
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var mutation in _externalSaves.GetHeartMutations(user).Where(h => h.PendingMirror))
        {
            var canonical = _externalSaves.CanonicalSongId(mutation.SongId);
            if (canonical == mutation.SongId && !mutation.Song.IsLocal) continue;
            try
            {
                bool mirrored;
                if (native)
                {
                    var body = Encoding.UTF8.GetBytes(new JsonObject { ["starred"] = mutation.Hearted }.ToJsonString());
                    var answer = await _proxyService.RelayRawAsync("api/song/" + Uri.EscapeDataString(canonical), new(), "PUT", body);
                    mirrored = answer.Status is >= 200 and < 300;
                }
                else
                {
                    var p = new Dictionary<string, string>(auth) { ["id"] = canonical, ["f"] = "json" };
                    var answer = await _proxyService.RelayAsync(mutation.Hearted ? "rest/star" : "rest/unstar", p);
                    mirrored = IsSuccessfulSubsonicResponse(answer.Body, "json");
                }
                if (mirrored) await _externalSaves.MarkHeartMirroredAsync(user, mutation.SongId, mutation.UpdatedUtc);
            }
            catch (Exception ex) { _logger.LogWarning("Heart mirror deferred: {Reason}", ex.GetType().Name); }
        }
    }
}

public partial class SubsonicController
{
    private async Task PrewarmPlaybackAsync(IEnumerable<Song> songs, int topN)
    {
        var visible = songs.Take(topN).ToList();
        if (_deezerCache?.Enabled == true)
        {
            var accepted = Request.Path.StartsWithSegments("/api")
                ? (await _proxyService.RelayRawAsync("api/playlist", new() { ["_end"] = "0" }, "GET")).Status is >= 200 and < 300
                : await HasAcceptedSubsonicCredentialsAsync(await ExtractAllParameters());
            if (!accepted) return;
        }
        await _metadataService.PrewarmDeezerIdsAsync(visible, topN);
        if (_deezerCache?.Enabled == true) await _deezerCache.PrewarmAsync(visible, topN);
    }

    private async Task PrewarmPlaybackIdsAsync(IEnumerable<string> songIds, int topN)
    {
        var ids = songIds.Take(topN).ToList();
        await _metadataService.PrewarmDeezerIdsForSongIdsAsync(ids, topN);
        if (_deezerCache?.Enabled != true) return;
        var songs = new List<Song>();
        foreach (var id in ids)
        {
            var (external, provider, externalId) = _localLibraryService.ParseSongId(id);
            var song = _externalSaves?.GetSong(id);
            if (song is null && external) song = await _metadataService.GetSongAsync(provider!, externalId!);
            if (song is { IsLocal: false }) songs.Add(song);
        }
        await _deezerCache.PrewarmAsync(songs, topN);
    }
}

public partial class SubsonicController
{
    private bool IsSavedSongReference(string id)
    {
        if (_externalSaves!.GetSong(id) is not null) return true;
        if (Octo.Services.Common.PlaylistIdHelper.IsExternalPlaylist(id)) return false;
        if (_idRegistry.Lookup(id) is { } routing) return routing.Kind == Octo.Services.Soulseek.RoutingKind.Song;
        var parsed = _localLibraryService.ParseExternalId(id);
        return parsed.isExternal ? parsed.type == "song" : _localLibraryService.ParseSongId(id).isExternal;
    }
}

public partial class SubsonicController
{
    private async Task<IActionResult?> SavedNativeCreateAsync(Dictionary<string, string> parameters)
    {
        if (HttpContext.Items["Octo.RawBody"] is not byte[] bytes || JsonNode.Parse(bytes) is not JsonObject body
            || body["tracks"] is not JsonArray tracks) return null;
        if (tracks.Any(track => track is not JsonObject)) return BadRequest(new { error = "Invalid playlist tracks" });
        var trackIds = tracks.Select(t => t?["mediaFileId"]?.ToString() ?? t?["id"]?.ToString() ?? "").ToList();
        if (!trackIds.Any(IsSavedSongReference)) return null;
        var (user, error) = await SavedCallerAsync(parameters, true, "json");
        if (error is not null) return error;
        var songs = new List<Song>();
        foreach (var id in trackIds)
        {
            var song = await SavedSongAsync(id, parameters, true);
            if (song is null) return BadRequest(new { error = "Song not found: " + id });
            songs.Add(song);
        }
        body["tracks"] = new JsonArray(songs.Where(s => s.IsLocal)
            .Select(s => (JsonNode)new JsonObject { ["mediaFileId"] = s.Id }).ToArray());
        var raw = await _proxyService.RelayRawAsync("api/playlist", new(), "POST", Encoding.UTF8.GetBytes(body.ToJsonString()));
        if (raw.Status is < 200 or >= 300) return StatusCode(raw.Status);
        var newId = JsonNode.Parse(raw.Body)?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(newId)) return StatusCode(502, new { error = "Navidrome returned no playlist ID" });
        try
        {
            var playlist = await _externalSaves!.UpsertPlaylistAsync(new ExternalSavedPlaylist
            {
                Id = newId, UserId = user!, Owner = user!, Name = body["name"]?.ToString() ?? "",
                Comment = body["comment"]?.ToString(), Public = body["public"]?.ToString() == "true",
                Tracks = songs.Select(s => new ExternalPlaylistTrack { SongId = s.Id, Song = s }).ToList(),
            });
            await QueueSavedSongsAsync(songs, user!);
            Response.StatusCode = raw.Status;
            return new JsonResult(SavedPlaylistFields(playlist, true));
        }
        catch (IOException) { return StatusCode(500, new { error = "Unable to persist playlist" }); }
        catch (UnauthorizedAccessException) { return StatusCode(500, new { error = "Unable to persist playlist" }); }
    }
}
