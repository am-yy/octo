using Octo.Services.Common;
using System.Text.Json;

namespace Octo.Services.Fingerprint;

/// <summary>
/// One question for MusicBrainz, asked only when a person keeps a track AcoustID had never heard
/// of: which recording is this? That is the MusicBrainz id a confirmed fingerprint needs before
/// AcoustID will take it (#47).
///
/// MusicBrainz allows about one request a second and wants a User-Agent naming the application,
/// and it holds near-duplicate recordings (two "Teardrop" by Massive Attack, 27 ms apart), so an
/// answer is only returned when exactly one recording fits. Anything else is a guess, and a
/// guess is never submitted with someone's name on it.
/// </summary>
public sealed class MusicBrainzClient
{
    public const string ClientName = "musicbrainz";
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(1100);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<MusicBrainzClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;

    public MusicBrainzClient(IHttpClientFactory http, ILogger<MusicBrainzClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<string?> FindRecordingAsync(string artist, string title, int durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title) || durationSeconds <= 0) return null;

        await _gate.WaitAsync(ct);
        try
        {
            var gap = _lastCallUtc + MinimumGap - DateTime.UtcNow;
            if (gap > TimeSpan.Zero) await Task.Delay(gap, ct);
            _lastCallUtc = DateTime.UtcNow;

            var query = $"recording:\"{Escape(title)}\" AND artist:\"{Escape(artist)}\"";
            using var response = await _http.CreateClient(ClientName)
                .GetAsync($"recording/?query={Uri.EscapeDataString(query)}&fmt=json&limit=25", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            return Pick(doc.RootElement, artist, title, durationSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("musicbrainz recording search failed: {M}", ex.Message);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The one recording that is this song, this version, by this artist, at this length.</summary>
    internal static string? Pick(JsonElement root, string artist, string title, int durationSeconds)
    {
        if (!root.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
            return null;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recording in recordings.EnumerateArray())
        {
            if (recording.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number
                && score.GetInt32() < 90) continue;
            if (recording.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.True) continue;
            if (!recording.TryGetProperty("length", out var length) || length.ValueKind != JsonValueKind.Number
                || Math.Abs(length.GetDouble() / 1000 - durationSeconds) > 3) continue;

            var name = recording.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var disambiguation = recording.TryGetProperty("disambiguation", out var d) ? d.GetString() ?? "" : "";
            // A live take or a remix says so in its disambiguation, not always in its title.
            var described = disambiguation.Length > 0 ? $"{name} ({disambiguation})" : name;
            if (!SongIdentity.SameTitle(title, described, SongIdentity.StrictTitles).IsSame) continue;

            var credits = recording.TryGetProperty("artist-credit", out var credit) && credit.ValueKind == JsonValueKind.Array
                ? credit.EnumerateArray().Select(entry => entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                    .Where(entry => entry.Length > 0).ToList()
                : [];
            if (!TrackMatchComparer.ArtistMatches(artist, string.Join(" & ", credits), credits)) continue;

            if (recording.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } recordingId)
                ids.Add(recordingId);
        }
        return ids.Count == 1 ? ids.First() : null;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
