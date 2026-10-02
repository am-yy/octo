using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Octo.Models.Domain;
using Octo.Services.Deezer;

namespace Octo.Controllers;

public partial class SubsonicController
{
    private static readonly byte[] TranscodeProfileKey = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] TranscodeProfileAad = Encoding.UTF8.GetBytes("octo-transcode-profile-v1");
    private static readonly TimeSpan TranscodeProfileLifetime = TimeSpan.FromHours(6);

    private sealed record TranscodeProfile(string MediaId, string Codec, int BitrateKbps, long ExpiresUnix);
    private sealed record NegotiatedProfile(string Codec, string Container, int BitrateKbps);

    [HttpGet, HttpPost, HttpHead]
    [Route("rest/getTranscodeDecision")]
    [Route("rest/getTranscodeDecision.view")]
    public async Task<IActionResult> GetTranscodeDecision()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var mediaId = parameters.GetValueOrDefault("mediaId", "");
        if (string.IsNullOrWhiteSpace(mediaId)) return _responseBuilder.CreateError(format, 10, "Missing mediaId parameter");

        var mediaType = parameters.GetValueOrDefault("mediaType", "song");
        var saved = _externalSaves?.GetSong(mediaId);
        if (saved is { IsLocal: true })
        {
            parameters["mediaId"] = saved.Id;
            return await RelayTranscodeDecisionAsync(parameters);
        }
        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(mediaId);
        if (!isExternal || !string.Equals(mediaType, "song", StringComparison.OrdinalIgnoreCase))
            return await RelayTranscodeDecisionAsync(parameters);
        if (!await HasAcceptedSubsonicCredentialsAsync(parameters))
            return _responseBuilder.CreateError(format, 40, "Wrong username or password");

        var song = saved ?? await _metadataService.GetSongAsync(provider!, externalId!);
        if (song is null) return _responseBuilder.CreateError(format, 70, "Song not found");
        var (sourceContainer, sourceCodec, sourceBitrate) = SelectedSourceDescription();
        var capabilities = ReadTranscodeCapabilities(parameters, out var capabilityError);
        if (capabilityError is not null) return BadRequest(new { error = capabilityError });
        if (capabilities is { } caps && !ValidateCapabilityNumbers(caps, out capabilityError))
            return BadRequest(new { error = capabilityError });

        var direct = capabilities is null
            || SupportsDirect(capabilities.Value, sourceContainer, sourceCodec, sourceBitrate);
        var target = direct || capabilities is null ? null : ChooseTranscodeProfile(capabilities.Value);
        var canTranscode = target is not null;
        string? transcodeParams = canTranscode
            ? ProtectTranscodeProfile(new(mediaId, target!.Codec, target.BitrateKbps,
                DateTimeOffset.UtcNow.Add(TranscodeProfileLifetime).ToUnixTimeSeconds()))
            : null;

        var decision = new Dictionary<string, object?>
        {
            ["canDirectPlay"] = direct,
            ["canTranscode"] = canTranscode,
            ["transcodeReason"] = direct ? Array.Empty<string>() : new[] { "AudioCodecNotSupported" },
            ["errorReason"] = canTranscode || direct ? "" : "No compatible direct-play or transcode profile",
            ["transcodeParams"] = transcodeParams,
            ["sourceStream"] = new Dictionary<string, object>
            {
                ["protocol"] = "http", ["container"] = sourceContainer, ["codec"] = sourceCodec,
                ["audioChannels"] = 2, ["audioBitrate"] = sourceBitrate,
            },
            ["transcodeStream"] = canTranscode ? new Dictionary<string, object>
            {
                ["protocol"] = "http", ["container"] = target!.Container, ["codec"] = target.Codec,
                ["audioChannels"] = 2, ["audioBitrate"] = target.BitrateKbps * 1000,
            } : null,
        };
        return TranscodeDecisionResponse(format, decision);
    }

    [HttpGet, HttpPost, HttpHead]
    [Route("rest/getTranscodeStream")]
    [Route("rest/getTranscodeStream.view")]
    public async Task<IActionResult> GetTranscodeStream()
    {
        var parameters = await ExtractAllParameters();
        var mediaId = parameters.GetValueOrDefault("mediaId", "");
        if (string.IsNullOrWhiteSpace(mediaId)) return BadRequest(new { error = "Missing mediaId parameter" });

        var saved = _externalSaves?.GetSong(mediaId);
        if (saved is { IsLocal: true })
        {
            parameters["mediaId"] = saved.Id;
            return await _proxyService.RelayStreamAsync(parameters, HttpContext.RequestAborted,
                "rest/getTranscodeStream");
        }
        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(mediaId);
        if (!isExternal || !string.Equals(parameters.GetValueOrDefault("mediaType", "song"), "song", StringComparison.OrdinalIgnoreCase))
            return await _proxyService.RelayStreamAsync(parameters, HttpContext.RequestAborted,
                "rest/getTranscodeStream");
        if (!await HasAcceptedSubsonicCredentialsAsync(parameters))
            return Unauthorized(new { error = "Wrong username or password" });

        var descriptor = parameters.GetValueOrDefault("transcodeParams",
            parameters.GetValueOrDefault("parameters", parameters.GetValueOrDefault("profile", "")));
        if (!TryUnprotectTranscodeProfile(descriptor, mediaId, out var profile))
            return BadRequest(new { error = "Invalid or expired transcode profile" });
        var offsetRaw = parameters.GetValueOrDefault("offset", parameters.GetValueOrDefault("timeOffset", "0"));
        if (!double.TryParse(offsetRaw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var offset)
            || !double.IsFinite(offset) || offset < 0 || offset > 86400)
            return BadRequest(new { error = "offset must be between 0 and 86400 seconds" });

        var song = saved ?? await _metadataService.GetSongAsync(provider!, externalId!);
        if (song is null) return NotFound(new { error = "Song not found" });
        if (_deezerDelivery is null) return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { error = "Transcoding is unavailable" });
        var deliveryParameters = new Dictionary<string, string>
        {
            ["format"] = profile!.Codec,
            ["maxBitRate"] = profile.BitrateKbps.ToString(CultureInfo.InvariantCulture),
            ["timeOffset"] = offset.ToString("0.########", CultureInfo.InvariantCulture),
        };
        var localPath = await FindAuthorizedImportedSourcePathAsync(song, parameters);
        return await _deezerDelivery.ServeAsync(HttpContext, song, localPath: localPath,
            parameters: deliveryParameters);
    }

    private async Task<IActionResult> RelayTranscodeDecisionAsync(Dictionary<string, string> parameters)
    {
        try
        {
            var result = await _proxyService.RelayRawAsync("rest/getTranscodeDecision", parameters,
                methodOverride: HttpMethods.IsPost(Request.Method) ? "POST" : "GET",
                bodyOverride: HttpContext.Items["Octo.RawBody"] as byte[]);
            Response.StatusCode = result.Status;
            foreach (var header in result.ResponseHeaders) Response.Headers[header.Key] = header.Value;
            return File(result.Body, result.ContentType ?? "application/json");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug("Navidrome transcode decision relay failed ({Msg})", ex.Message);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Navidrome transcode decision is unavailable" });
        }
    }

    private JsonElement? ReadTranscodeCapabilities(Dictionary<string, string> parameters, out string? error)
    {
        error = null;
        byte[]? bytes = HttpContext.Items["Octo.RawBody"] as byte[];
        if (bytes is { Length: > 262144 }) { error = "Transcode capability request is too large"; return null; }
        try
        {
            JsonDocument? document = null;
            if (bytes is { Length: > 0 } && Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
                document = JsonDocument.Parse(bytes);
            else if (parameters.TryGetValue("clientInfo", out var clientInfo) && clientInfo.StartsWith('{'))
                document = JsonDocument.Parse(clientInfo);
            if (document is null) return null;
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "Transcode capabilities must be a JSON object"; return null; }
                if (root.TryGetProperty("clientInfo", out var nested) && nested.ValueKind == JsonValueKind.Object)
                    root = nested;
                return root.Clone();
            }
        }
        catch (JsonException)
        {
            error = "Invalid transcode capability JSON";
            return null;
        }
    }

    private static bool SupportsDirect(JsonElement capabilities, string container, string codec, int sourceBitrate)
    {
        if (!capabilities.TryGetProperty("directPlayProfiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Array) return false;
        var maxBitrate = ReadPositiveInt(capabilities, "maxAudioBitrate");
        if (maxBitrate > 0 && sourceBitrate > maxBitrate) return false;
        return profiles.EnumerateArray().Any(profile => ProfileSupports(profile, container, codec, requireProtocol: true))
            && CodecLimitationsAllowDirect(capabilities, codec, sourceBitrate);
    }

    private static bool CodecLimitationsAllowDirect(JsonElement capabilities, string codec, int sourceBitrate)
    {
        if (!capabilities.TryGetProperty("codecProfiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Array) return true;
        foreach (var profile in profiles.EnumerateArray())
        {
            if (!string.Equals(StringProperty(profile, "type"), "AudioCodec", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(StringProperty(profile, "name"), codec, StringComparison.OrdinalIgnoreCase)
                || !profile.TryGetProperty("limitations", out var limitations)
                || limitations.ValueKind != JsonValueKind.Array) continue;
            foreach (var limitation in limitations.EnumerateArray())
            {
                if (limitation.ValueKind != JsonValueKind.Object
                    || !limitation.TryGetProperty("required", out var required)
                    || required.ValueKind != JsonValueKind.True) continue;
                var name = StringProperty(limitation, "name");
                var actual = name switch
                {
                    "audioBitrate" => sourceBitrate,
                    "audioChannels" => 2,
                    // Source sample rate and bit depth vary by recording. A required
                    // constraint we cannot prove is safe must force a transcode.
                    _ => (int?)null,
                };
                if (actual is null || !LimitationAllows(limitation, actual.Value)) return false;
            }
        }
        return true;
    }

    private static bool LimitationAllows(JsonElement limitation, int actual)
    {
        if (!limitation.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            return false;
        var allowed = values.EnumerateArray().Select(value => value.ValueKind switch
        {
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed) => (int?)parsed,
            JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
            _ => null,
        }).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        if (allowed.Length == 0) return false;
        return StringProperty(limitation, "comparison") switch
        {
            "Equals" => allowed.Contains(actual),
            "NotEquals" => allowed.All(value => value != actual),
            "LessThanEqual" => actual <= allowed[0],
            "GreaterThanEqual" => actual >= allowed[0],
            _ => false,
        };
    }

    private static NegotiatedProfile? ChooseTranscodeProfile(JsonElement capabilities)
    {
        if (!capabilities.TryGetProperty("transcodingProfiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Array) return null;
        var cap = MinPositive(ReadPositiveInt(capabilities, "maxAudioBitrate"),
            ReadPositiveInt(capabilities, "maxTranscodingAudioBitrate"));
        var bitrate = cap > 0 ? Math.Min(320, cap / 1000) : 128;
        foreach (var profile in profiles.EnumerateArray())
        {
            if (profile.ValueKind != JsonValueKind.Object) continue;
            var codec = StringProperty(profile, "audioCodec")?.ToLowerInvariant();
            var container = StringProperty(profile, "container")?.ToLowerInvariant();
            var protocol = StringProperty(profile, "protocol")?.ToLowerInvariant();
            if (protocol != "http" || ReadIntProperty(profile, "maxAudioChannels") is < 2) continue;
            if (codec == "mp3" && container == "mp3")
            {
                var target = Math.Min(bitrate, PositiveKbps(profile, "audioBitrate", "maxAudioBitrate"));
                if (target >= 32)
                {
                    var normalized = DeezerDeliveryRequest.Parse(new Dictionary<string, string>
                    {
                        ["format"] = "mp3", ["maxBitRate"] = target.ToString(CultureInfo.InvariantCulture),
                    }, "FLAC");
                    if (CodecLimitationsAllowTranscodedOutput(capabilities, "mp3", normalized.BitrateKbps))
                        return new("mp3", "mp3", normalized.BitrateKbps);
                }
            }
            if (codec == "opus" && container is "ogg" or "oga")
            {
                var target = Math.Min(bitrate, PositiveKbps(profile, "audioBitrate", "maxAudioBitrate"));
                if (target >= 6)
                {
                    var normalized = DeezerDeliveryRequest.Parse(new Dictionary<string, string>
                    {
                        ["format"] = "opus", ["maxBitRate"] = target.ToString(CultureInfo.InvariantCulture),
                    }, "FLAC");
                    if (CodecLimitationsAllowTranscodedOutput(capabilities, "opus", normalized.BitrateKbps))
                        return new("opus", "ogg", normalized.BitrateKbps);
                }
            }
        }
        return null;
    }

    private static bool CodecLimitationsAllowTranscodedOutput(JsonElement capabilities, string codec, int bitrateKbps)
    {
        if (!capabilities.TryGetProperty("codecProfiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Array) return true;
        foreach (var profile in profiles.EnumerateArray())
        {
            if (!string.Equals(StringProperty(profile, "type"), "AudioCodec", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(StringProperty(profile, "name"), codec, StringComparison.OrdinalIgnoreCase)
                || !profile.TryGetProperty("limitations", out var limitations)
                || limitations.ValueKind != JsonValueKind.Array) continue;
            foreach (var limitation in limitations.EnumerateArray())
            {
                if (limitation.ValueKind != JsonValueKind.Object
                    || !limitation.TryGetProperty("required", out var required)
                    || required.ValueKind != JsonValueKind.True) continue;
                var name = StringProperty(limitation, "name");
                var actual = name?.ToLowerInvariant() switch
                {
                    "audiobitrate" => bitrateKbps * 1000,
                    "audiochannels" => 2,
                    // MP3 output sample rate is fixed by delivery bitrate. Opus output
                    // currently follows ffmpeg defaults, and compressed bit depth/profile
                    // are not exposed; decline profiles requiring facts we cannot promise.
                    "audiosamplerate" when codec == "mp3" => bitrateKbps < 64 ? 22050 : 44100,
                    _ => (int?)null,
                };
                if (actual is null || !LimitationAllows(limitation, actual.Value)) return false;
            }
        }
        return true;
    }

    private static bool ProfileSupports(JsonElement profile, string container, string codec, bool requireProtocol)
    {
        if (profile.ValueKind != JsonValueKind.Object) return false;
        var containers = StringArray(profile, "containers");
        var codecs = StringArray(profile, "audioCodecs");
        var protocols = StringArray(profile, "protocols");
        if ((containers.Count > 0 && !containers.Contains(container, StringComparer.OrdinalIgnoreCase))
            || (codecs.Count > 0 && !codecs.Contains(codec, StringComparer.OrdinalIgnoreCase))) return false;
        if (requireProtocol && protocols.Count > 0 && !protocols.Contains("http", StringComparer.OrdinalIgnoreCase)) return false;
        return ReadIntProperty(profile, "maxAudioChannels") is not < 2;
    }

    private (string Container, string Codec, int Bitrate) SelectedSourceDescription()
    {
        var quality = _deezerCache?.SourceQuality ?? _deezerSettingsOptions?.CurrentValue.CacheQuality ?? "FLAC";
        return string.Equals(quality, "MP3_320", StringComparison.OrdinalIgnoreCase)
            ? ("mp3", "mp3", 320_000)
            : ("flac", "flac", 950_000);
    }

    private static IActionResult TranscodeDecisionResponse(string format, Dictionary<string, object?> decision)
    {
        var json = format.Equals("json", StringComparison.OrdinalIgnoreCase);
        if (json)
            return new JsonResult(new Dictionary<string, object?>
            {
                ["subsonic-response"] = new Dictionary<string, object?>
                {
                    ["status"] = "ok", ["version"] = "1.16.1", ["type"] = "octo",
                    ["openSubsonic"] = true, ["transcodeDecision"] = decision,
                },
            });
        XNamespace ns = "http://subsonic.org/restapi";
        var element = new XElement(ns + "transcodeDecision");
        foreach (var (name, value) in decision)
        {
            if (value is null) continue;
            if (value is string[] reasons)
                element.Add(reasons.Select(reason => new XElement(ns + name, reason)));
            else if (value is Dictionary<string, object> details)
                element.Add(new XElement(ns + name, details.Select(field => new XAttribute(field.Key, field.Value))));
            else element.SetAttributeValue(name, value);
        }
        return new ContentResult
        {
            Content = new XDocument(new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"), new XAttribute("version", "1.16.1"),
                new XAttribute("type", "octo"), new XAttribute("openSubsonic", "true"), element)).ToString(),
            ContentType = "application/xml",
        };
    }

    private static string ProtectTranscodeProfile(TranscodeProfile profile)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(profile);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(TranscodeProfileKey, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, TranscodeProfileAad);
        var token = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(token, 0);
        ciphertext.CopyTo(token, nonce.Length);
        tag.CopyTo(token, nonce.Length + ciphertext.Length);
        return Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryUnprotectTranscodeProfile(string token, string mediaId, out TranscodeProfile? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return false;
        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var payload = Convert.FromBase64String(base64);
            if (payload.Length < 12 + 16 + 2) return false;
            var nonce = payload.AsSpan(0, 12);
            var ciphertext = payload.AsSpan(12, payload.Length - 28);
            var tag = payload.AsSpan(payload.Length - 16);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(TranscodeProfileKey, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, TranscodeProfileAad);
            var decoded = JsonSerializer.Deserialize<TranscodeProfile>(plaintext);
            if (decoded is null || !string.Equals(decoded.MediaId, mediaId, StringComparison.Ordinal)
                || decoded.ExpiresUnix < DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                || decoded.ExpiresUnix > DateTimeOffset.UtcNow.Add(TranscodeProfileLifetime).ToUnixTimeSeconds()
                || decoded.Codec is not ("mp3" or "opus")
                || decoded.BitrateKbps is < 6 or > 320) return false;
            profile = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        { return false; }
    }

    private static int ReadPositiveInt(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : 0;

    private static bool ValidateCapabilityNumbers(JsonElement capabilities, out string? error)
    {
        error = null;
        if (!NonnegativeInteger(capabilities, "maxAudioBitrate")
            || !NonnegativeInteger(capabilities, "maxTranscodingAudioBitrate"))
        {
            error = "Audio bitrate capabilities must be non-negative integers";
            return false;
        }
        foreach (var collectionName in new[] { "directPlayProfiles", "transcodingProfiles" })
        {
            if (!capabilities.TryGetProperty(collectionName, out var profiles)) continue;
            if (profiles.ValueKind != JsonValueKind.Array) continue;
            foreach (var profile in profiles.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object) continue;
                foreach (var name in new[] { "maxAudioChannels", "audioBitrate", "maxAudioBitrate" })
                    if (!NonnegativeInteger(profile, name))
                    {
                        error = $"{name} must be a non-negative integer";
                        return false;
                    }
            }
        }
        return true;

        static bool NonnegativeInteger(JsonElement item, string name) =>
            !item.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0;
    }

    private static int? ReadIntProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static int MinPositive(int first, int second) => first > 0 && second > 0
        ? Math.Min(first, second) : Math.Max(first, second);

    private static int PositiveKbps(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (ReadPositiveInt(item, name) is var value && value > 0) return Math.Max(1, value / 1000);
        return 320;
    }

    private static string? StringProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> StringArray(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var values)
            && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString() ?? "").ToList()
                : [];
}
