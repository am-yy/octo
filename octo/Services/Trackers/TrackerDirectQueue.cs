using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Octo.Services.Trackers;

public sealed class TrackerRequestSkippedException : Exception { }
public sealed class TrackerRequestRejectedException(string message) : HttpRequestException(message) { }
public sealed record TrackerReply(HttpStatusCode Status, byte[] Body);

/// <summary>One durable, serialized direct-request lane per tracker. Prowlarr is separate.</summary>
public sealed class TrackerDirectQueue
{
    private sealed class Lane
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTimeOffset NextUtc;
    }

    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = new(), ["ops"] = new(),
    };
    private readonly string _path;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _time;
    private readonly object _saveGate = new();

    public TrackerDirectQueue(string path, IConfiguration config, IHttpClientFactory clients,
        TimeProvider? time = null)
    {
        _path = path;
        _config = config;
        _clients = clients;
        _time = time ?? TimeProvider.System;
        if (!File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path));
            if (saved is null) return;
            foreach (var (name, next) in saved)
                if (_lanes.TryGetValue(name, out var lane)) lane.NextUtc = next;
        }
        catch (JsonException) { /* A corrupt cooldown must not cause a burst. */
            foreach (var lane in _lanes.Values) lane.NextUtc = _time.GetUtcNow().AddMinutes(1);
        }
    }

    public bool Configured(string tracker) =>
        _lanes.ContainsKey(tracker) && !string.IsNullOrWhiteSpace(_config[$"Trackers:{tracker}:ApiKey"]);

    public async Task<JsonObject> GetAsync(string tracker, string action,
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct,
        Func<bool>? eligible = null) => Parse(await SendAsync(tracker, action, HttpMethod.Get, parameters,
            null, ct, eligible is null ? null : _ => Task.FromResult(eligible())));

    public static JsonObject Parse(TrackerReply reply)
    {
        var json = JsonNode.Parse(reply.Body) as JsonObject;
        if ((int)reply.Status < 500 && json?["status"] is JsonValue status
            && status.TryGetValue<string>(out var state) && state == "failure")
            throw new TrackerRequestRejectedException("Tracker explicitly rejected the request.");
        if ((int)reply.Status is < 200 or >= 300)
            throw new HttpRequestException($"Tracker returned HTTP {(int)reply.Status}.");
        if (json?["status"]?.GetValue<string>() != "success" || json["response"] is not JsonObject body)
            throw new InvalidDataException("Tracker returned an incomplete or unsuccessful API response.");
        return body;
    }

    public async Task<TrackerReply> SendAsync(string tracker, string action, HttpMethod method,
        IReadOnlyDictionary<string, string> parameters, HttpContent? content, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? eligible = null, Action? dispatching = null)
    {
        if (!_lanes.TryGetValue(tracker, out var lane)) throw new ArgumentException("Unknown tracker.", nameof(tracker));
        var key = _config[$"Trackers:{tracker}:ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"{tracker.ToUpperInvariant()} API key is missing.");
        var origin = tracker.Equals("red", StringComparison.OrdinalIgnoreCase)
            ? "https://redacted.sh" : "https://orpheus.network";
        var query = new Dictionary<string, string>(parameters) { ["action"] = action };
        var url = origin + "/ajax.php?" + string.Join("&", query.Select(x =>
            Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
        await lane.Gate.WaitAsync(ct);
        try
        {
            TimeSpan delay;
            lock (_saveGate) delay = lane.NextUtc - _time.GetUtcNow();
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, ct);
            if (eligible is not null && !await eligible(ct)) throw new TrackerRequestSkippedException();
            // Persist before dispatch. Crash after dispatch cannot produce a second immediate hit.
            SetCooldown(lane, _time.GetUtcNow().AddSeconds(11));
            using var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.TryAddWithoutValidation("Authorization", tracker.Equals("ops", StringComparison.OrdinalIgnoreCase) ? "token " + key : key);
            request.Headers.UserAgent.ParseAdd("Octo-Salmon/1.0");
            using var client = _clients.CreateClient("tracker-direct");
            ct.ThrowIfCancellationRequested();
            dispatching?.Invoke(); // Durable POST intent belongs at the transport boundary, after queued checks.
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - _time.GetUtcNow());
                SetCooldown(lane, _time.GetUtcNow() + (retry is { } wait
                    ? (wait > TimeSpan.Zero ? wait : TimeSpan.Zero) + TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(60)));
                throw new TrackerRequestRejectedException($"{tracker.ToUpperInvariant()} rate limited the direct request queue.");
            }
            return new TrackerReply(response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct));
        }
        finally { lane.Gate.Release(); }
    }

    private void SetCooldown(Lane lane, DateTimeOffset deadline)
    {
        lock (_saveGate)
        {
            if (deadline > lane.NextUtc) lane.NextUtc = deadline;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _lanes.ToDictionary(x => x.Key, x => x.Value.NextUtc));
                stream.Flush(true);
            }
            File.Move(temp, _path, true);
        }
    }
}
