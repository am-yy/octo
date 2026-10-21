using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Octo.Services.LastFm;

/// <summary>Bounded in-process refresh queue. Duplicate user jobs collapse while queued.</summary>
/// <param name="StationKey">The one station to replace ("pinned-rock", "discovery"); null for all.</param>
/// <param name="Reason">Why it was asked for, for the log: "5 new plays", "stale", "settings".</param>
public sealed record LastFmRadioRefreshJob(string Username, string? StationKey = null, string Reason = "asked");

public sealed class LastFmRadioRefreshQueue
{
    private readonly Channel<LastFmRadioRefreshJob> _jobs = Channel.CreateBounded<LastFmRadioRefreshJob>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);

    public bool Enqueue(string username, string? stationKey = null, string reason = "asked")
    {
        username = username.Trim();
        var key = username + "|" + stationKey;
        if (username.Length == 0 || !_queued.TryAdd(key, 0)) return false;
        if (_jobs.Writer.TryWrite(new LastFmRadioRefreshJob(username, stationKey, reason))) return true;
        _queued.TryRemove(key, out _);
        return false;
    }

    public async ValueTask<LastFmRadioRefreshJob> DequeueAsync(CancellationToken cancellationToken)
    {
        var job = await _jobs.Reader.ReadAsync(cancellationToken);
        _queued.TryRemove(job.Username + "|" + job.StationKey, out _);
        return job;
    }
}
