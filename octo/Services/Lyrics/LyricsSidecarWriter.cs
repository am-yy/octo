using System.Text;
using System.Threading.Channels;

namespace Octo.Services.Lyrics;

public sealed record LyricsJob(string AudioPath, string Artist, string Title, string? Album, int? DurationSeconds,
    int Attempt = 1);

public enum LyricsWriteOutcome { Written, Instrumental, NotFound, AlreadyThere, Gone, Retrying, GaveUp }

/// <summary>
/// Writes a lyrics file beside a download (#52), off the download path. Downloads run one at a
/// time under a lock, and LRCLIB can take seconds or shed load with a 503, so fetching lyrics
/// inline would stall every download queued behind this one. A sidecar Navidrome reads at request
/// time needs no rescan, so arriving a little later costs nothing.
///
/// Synced lyrics go in a .lrc, plain ones in a .txt: both are in Navidrome's default
/// LyricsPriority, and a .txt says plainly that there is no timing. An instrumental gets nothing.
/// A sidecar that already exists, or lyrics already embedded in the file, are never replaced.
/// </summary>
public sealed class LyricsSidecarWriter : BackgroundService
{
    internal static TimeSpan RetryDelay = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 3;

    private static readonly string[] SidecarExtensions = [".lrc", ".txt", ".ttml", ".elrc", ".srt", ".yaml", ".yml"];
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly Channel<LyricsJob> _queue =
        Channel.CreateBounded<LyricsJob>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly LyricsService _lyrics;
    private readonly ILogger<LyricsSidecarWriter> _logger;

    public LyricsSidecarWriter(LyricsService lyrics, ILogger<LyricsSidecarWriter> logger)
    {
        _lyrics = lyrics;
        _logger = logger;
    }

    public bool TryEnqueue(LyricsJob job) => _queue.Writer.TryWrite(job);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Per-item catch is mandatory: BackgroundServiceExceptionBehavior defaults to
            // StopHost, so one unhandled exception here would take Octo down.
            try
            {
                var outcome = await WriteAsync(job, stoppingToken);
                if (outcome == LyricsWriteOutcome.Retrying) _ = RetryLaterAsync(job with { Attempt = job.Attempt + 1 }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not write lyrics for {Path}: {M}", job.AudioPath, ex.Message);
            }
        }
    }

    internal async Task<LyricsWriteOutcome> WriteAsync(LyricsJob job, CancellationToken ct)
    {
        if (!File.Exists(job.AudioPath)) return LyricsWriteOutcome.Gone;

        var directory = Path.GetDirectoryName(job.AudioPath)!;
        var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(job.AudioPath));
        if (SidecarExtensions.Any(extension => File.Exists(stem + extension)) || HasEmbeddedLyrics(job.AudioPath))
            return LyricsWriteOutcome.AlreadyThere;

        var lookup = await _lyrics.FindAsync(
            new LyricsQuery(job.Artist, job.Title, job.Album, job.DurationSeconds ?? ReadDuration(job.AudioPath)), ct);
        if (lookup.Transient)
        {
            if (job.Attempt < MaxAttempts) return LyricsWriteOutcome.Retrying;
            _logger.LogInformation("No lyrics service answered for '{Artist} - {Title}' after {N} tries",
                job.Artist, job.Title, job.Attempt);
            return LyricsWriteOutcome.GaveUp;
        }

        switch (lookup.Result)
        {
            case { Instrumental: true } instrumental:
                _logger.LogInformation("{Source} says '{Artist} - {Title}' is instrumental; no lyrics file",
                    instrumental.Source, job.Artist, job.Title);
                return LyricsWriteOutcome.Instrumental;
            case { HasSynced: true } synced:
                await File.WriteAllTextAsync(stem + ".lrc", synced.Synced!.Replace("\r\n", "\n").Trim() + "\n", Utf8NoBom, ct);
                _logger.LogInformation("Lyrics for '{Artist} - {Title}' from {Source} (synced)", job.Artist, job.Title, synced.Source);
                return LyricsWriteOutcome.Written;
            case { HasPlain: true } plain:
                await File.WriteAllTextAsync(stem + ".txt", plain.Plain!.Replace("\r\n", "\n").Trim() + "\n", Utf8NoBom, ct);
                _logger.LogInformation("Lyrics for '{Artist} - {Title}' from {Source} (plain)", job.Artist, job.Title, plain.Source);
                return LyricsWriteOutcome.Written;
            default:
                _logger.LogInformation("No lyrics found for '{Artist} - {Title}'", job.Artist, job.Title);
                return LyricsWriteOutcome.NotFound;
        }
    }

    private async Task RetryLaterAsync(LyricsJob job, CancellationToken ct)
    {
        try
        {
            await Task.Delay(RetryDelay, ct);
            TryEnqueue(job);
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private static bool HasEmbeddedLyrics(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return !string.IsNullOrWhiteSpace(file.Tag.Lyrics);
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadDuration(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var seconds = (int)Math.Round(file.Properties.Duration.TotalSeconds);
            return seconds > 0 ? seconds : null;
        }
        catch
        {
            return null;
        }
    }
}
