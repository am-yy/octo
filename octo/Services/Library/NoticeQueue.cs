using System.Text.Json;
using System.Text.Json.Serialization;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Fingerprint;

namespace Octo.Services.Library;

public enum NoticeState { Waiting, Queued, Kept, Acted, Dismissed, Expired }

/// <summary>One question Octo is asking one person about one track.</summary>
public sealed record NoticeEntry
{
    public string Key { get; init; } = "";
    public NoticeKind Kind { get; init; }
    public string Username { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Album { get; init; }
    public string? NavidromeId { get; init; }
    public string? GroupKey { get; init; }
    public int Order { get; init; }
    public NoticeState State { get; init; }

    /// <summary>What the dashboard shows as the reason Octo is asking.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Why verification could not decide, which is what makes an answer submittable.</summary>
    public InconclusiveReason Cause { get; init; }

    public string? Fingerprint { get; init; }
    public int DurationSeconds { get; init; }
    public string? CandidateRecordingId { get; init; }
    public string? FileFormat { get; init; }
    public bool Submitted { get; init; }
    public int LookupAttempts { get; init; }
    public DateTime NextLookupUtc { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? QueuedUtc { get; init; }
    public DateTime? ResolvedUtc { get; init; }

    [JsonIgnore] public bool IsOpen => State is NoticeState.Waiting or NoticeState.Queued;
}

/// <summary>
/// What Octo has asked each person about, and what they answered (#47, #53).
///
/// Resolved entries are kept, bounded, so a rescan never asks again about something a person
/// already settled: "a track resolved once should not come back". A fingerprint is dropped the
/// moment its entry resolves any way but Keep, and once it has been sent or refused, so the file
/// stays small.
/// </summary>
public sealed class NoticeQueue : IDisposable
{
    public const int MaxEntries = 5000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A person who removes a track from Review and then drops it into Delete answered with
    /// Delete, even though the sweep saw the removal first.
    /// </summary>
    private static readonly TimeSpan ActedAfterDismissWindow = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string? _path;
    private readonly ILogger<NoticeQueue>? _logger;
    private readonly Timer? _flushTimer;
    private readonly object _lock = new();
    private readonly object _flushLock = new();
    private readonly Dictionary<string, NoticeEntry> _entries = new(StringComparer.Ordinal);
    private int _dirty;

    public NoticeQueue(string? path = null, ILogger<NoticeQueue>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _logger = logger;
        if (_path is null) return;

        Load();
        _flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public static string ReviewKey(string username, string localPath) =>
        $"review|{username.Trim().ToLowerInvariant()}|{localPath}";

    /// <summary>
    /// Ask <paramref name="username"/> about a download verification could not settle. A file
    /// already asked about is never asked about again, in any state, which is what stops a
    /// dismissal from being undone by the next download of the same file.
    /// </summary>
    public bool AddReview(string username, string localPath, Song song, VerificationResult verdict)
    {
        var key = ReviewKey(username, localPath);
        lock (_lock)
        {
            if (_entries.ContainsKey(key)) return false;
            _entries[key] = new NoticeEntry
            {
                Key = key,
                Kind = NoticeKind.Review,
                Username = username.Trim(),
                LocalPath = localPath,
                Artist = song.Artist,
                Title = song.Title,
                Album = song.Album,
                State = NoticeState.Waiting,
                Reason = verdict.Reason switch
                {
                    InconclusiveReason.NoEntry => "AcoustID has never heard this recording",
                    InconclusiveReason.BelowThreshold => "AcoustID was not sure what this is",
                    InconclusiveReason.SourceDisagreed => $"AcoustID thinks this is {verdict.Describe()}",
                    _ => "Octo could not check this download",
                },
                Cause = verdict.Reason,
                Fingerprint = verdict.Fingerprint,
                DurationSeconds = verdict.DurationSeconds,
                CandidateRecordingId = verdict.CandidateRecordingId,
                FileFormat = Path.GetExtension(localPath).TrimStart('.').ToLowerInvariant(),
                NextLookupUtc = DateTime.UtcNow,
            };
            Trim();
        }
        MarkDirty();
        return true;
    }

    public IReadOnlyList<NoticeEntry> ForUser(string username, NoticeKind kind)
    {
        lock (_lock)
            return _entries.Values
                .Where(entry => entry.Kind == kind && entry.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    /// <summary>Review entries still waiting for Navidrome to scan the file, whose next lookup is due.</summary>
    public IReadOnlyList<NoticeEntry> DueForLookup(DateTime nowUtc, int limit)
    {
        lock (_lock)
            return _entries.Values
                .Where(entry => entry.Kind == NoticeKind.Review && entry.State == NoticeState.Waiting
                    && entry.NavidromeId is null && entry.NextLookupUtc <= nowUtc)
                .OrderBy(entry => entry.CreatedUtc)
                .Take(limit)
                .ToList();
    }

    public bool IsQueued(string username, string navidromeId)
    {
        lock (_lock)
            return _entries.Values.Any(entry => entry.State == NoticeState.Queued
                && entry.NavidromeId == navidromeId
                && entry.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public void SetNavidromeId(string key, string navidromeId) =>
        Update(key, entry => entry with { NavidromeId = navidromeId });

    /// <summary>Not scanned yet: look again after 1, 2, 4, 8, then 16 minutes.</summary>
    public void DeferLookup(string key, DateTime nowUtc) =>
        Update(key, entry => entry with
        {
            LookupAttempts = entry.LookupAttempts + 1,
            NextLookupUtc = nowUtc.AddMinutes(Math.Pow(2, Math.Min(entry.LookupAttempts, 4))),
        });

    public void Resolve(string key, NoticeState state) =>
        Update(key, entry => entry with
        {
            State = state,
            ResolvedUtc = DateTime.UtcNow,
            Fingerprint = state == NoticeState.Kept ? entry.Fingerprint : null,
        });

    public void MarkQueued(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            Update(key, entry => entry with { State = NoticeState.Queued, QueuedUtc = DateTime.UtcNow });
    }

    /// <summary>
    /// A person kept this track. For Review the question was about the file, so every open Review
    /// entry for it is answered, whoever else was asked. Returns the entry Octo had open for this
    /// person, or null when Octo never asked them about this track.
    /// </summary>
    public NoticeEntry? MarkKept(string username, string navidromeId)
    {
        NoticeEntry? mine;
        lock (_lock)
        {
            mine = _entries.Values.FirstOrDefault(entry => entry.IsOpen && entry.NavidromeId == navidromeId
                && entry.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
            if (mine is null) return null;

            var now = DateTime.UtcNow;
            foreach (var entry in _entries.Values.Where(entry => entry.IsOpen && entry.Kind == mine.Kind).ToList())
            {
                var answered = mine.Kind == NoticeKind.Review
                    ? entry.NavidromeId == navidromeId || entry.LocalPath == mine.LocalPath
                    // Keeping one copy of a duplicate says these copies are on purpose: the whole
                    // group is settled for this person.
                    : entry.GroupKey == mine.GroupKey
                      && entry.Username.Equals(mine.Username, StringComparison.OrdinalIgnoreCase);
                if (!answered) continue;
                _entries[entry.Key] = entry with
                {
                    State = mine.Kind == NoticeKind.Review ? NoticeState.Kept : NoticeState.Dismissed,
                    ResolvedUtc = now,
                    // One answer is one submission. A download nobody requested is asked of every
                    // allowed user, and each of their entries carries the same fingerprint.
                    Fingerprint = entry.Key == mine.Key ? entry.Fingerprint : null,
                };
            }
        }
        MarkDirty();
        return mine;
    }

    /// <summary>
    /// An action ran on this track, so every open question about it is answered, for everyone:
    /// the file it was about has gone or changed.
    /// </summary>
    public void MarkActed(string navidromeId)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (var entry in _entries.Values.Where(entry => entry.NavidromeId == navidromeId).ToList())
            {
                var recentlyDismissed = entry.State == NoticeState.Dismissed
                    && entry.ResolvedUtc is { } at && now - at < ActedAfterDismissWindow;
                if (!entry.IsOpen && !recentlyDismissed) continue;
                _entries[entry.Key] = entry with { State = NoticeState.Acted, ResolvedUtc = now, Fingerprint = null };
            }
        }
        MarkDirty();
    }

    /// <summary>Kept entries with a fingerprint that has not been sent or refused yet.</summary>
    public IReadOnlyList<NoticeEntry> AwaitingSubmission()
    {
        lock (_lock)
            return _entries.Values
                .Where(entry => entry.State == NoticeState.Kept && !entry.Submitted && entry.Fingerprint is not null)
                .ToList();
    }

    /// <summary>Sent, or deliberately not sent: either way the fingerprint is not needed again.</summary>
    public void MarkSubmitted(IEnumerable<string> keys, bool sent)
    {
        foreach (var key in keys)
            Update(key, entry => entry with { Submitted = sent, Fingerprint = null });
    }

    public IReadOnlyList<NoticeEntry> Recent(int limit = 200)
    {
        lock (_lock)
            return _entries.Values
                .OrderByDescending(entry => entry.ResolvedUtc ?? entry.QueuedUtc ?? entry.CreatedUtc)
                .Take(limit)
                .ToList();
    }

    private void Update(string key, Func<NoticeEntry, NoticeEntry> change)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;
            _entries[key] = change(entry);
        }
        MarkDirty();
    }

    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>Oldest resolved entries go first. An open question is never forgotten.</summary>
    private void Trim()
    {
        if (_entries.Count <= MaxEntries) return;
        var resolved = _entries.Values.Where(entry => !entry.IsOpen)
            .OrderBy(entry => entry.ResolvedUtc ?? entry.CreatedUtc)
            .Take(_entries.Count - MaxEntries)
            .Select(entry => entry.Key)
            .ToList();
        foreach (var key in resolved) _entries.Remove(key);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var entries = JsonSerializer.Deserialize<List<NoticeEntry>>(File.ReadAllText(_path!), Json);
            if (entries is null) return;
            lock (_lock)
            {
                foreach (var entry in entries.Where(entry => !string.IsNullOrEmpty(entry.Key)))
                    _entries[entry.Key] = entry;
                Trim();
            }
        }
        catch (Exception ex)
        {
            // Kept aside rather than overwritten: it holds what people already answered.
            _logger?.LogWarning("notice queue could not be read ({M}); starting empty", ex.Message);
            try { File.Move(_path!, $"{_path}.corrupt-{DateTime.UtcNow.Ticks}"); } catch { /* best effort */ }
        }
    }

    public bool Flush()
    {
        if (_path is null) return true;
        lock (_flushLock)
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return true;
            try
            {
                List<NoticeEntry> entries;
                lock (_lock) entries = _entries.Values.ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Json));
                File.Move(tmp, _path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _dirty, 1);
                _logger?.LogWarning("notice queue could not be written: {M}", ex.Message);
                return false;
            }
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        Flush();
    }
}
