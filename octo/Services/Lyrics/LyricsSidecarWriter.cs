using System.Text;
using System.Threading.Channels;

namespace Octo.Services.Lyrics;

public sealed record LyricsJob(string AudioPath, string Artist, string Title, string? Album, int? DurationSeconds,
    int Attempt = 1);

public enum LyricsWriteOutcome { Written, Instrumental, NotFound, AlreadyThere, Gone, Retrying, GaveUp, Upgraded }

/// <summary>What a write did, and the lyrics it wrote, for the library job's review list.</summary>
public sealed record LyricsWrite(LyricsWriteOutcome Outcome, LyricsResult? Result);

/// <summary>
/// Writes a lyrics file beside a download (#52), off the download path. Downloads run one at a
/// time under a lock, and LRCLIB can take seconds or shed load with a 503, so fetching lyrics
/// inline would stall every download queued behind this one. A sidecar Navidrome reads at request
/// time needs no rescan, so arriving a little later costs nothing.
///
/// Synced lyrics go in a .lrc, plain ones in a .txt: both are in Navidrome's default
/// LyricsPriority, and a .txt says plainly that there is no timing. Word-timed lyrics are
/// enhanced LRC: every line keeps its standard [mm:ss.xx] tag, so any player that reads .lrc
/// shows them line by line, and one that knows &lt;mm:ss.xx&gt; word tags (Navidrome among them,
/// which turns them into OpenSubsonic word cues) gets the words too.
///
/// A .lrc Octo writes opens with [re:Octo], LRC's own "made by" tag, which every reader skips.
/// It is how Octo knows a file is its own: an instrumental gets nothing, and a sidecar that
/// already exists, or lyrics already embedded in the file, are never replaced, except that one
/// of Octo's own line-timed .lrc files may be upgraded to word timing when asked.
/// </summary>
public sealed class LyricsSidecarWriter : BackgroundService
{
    internal static TimeSpan RetryDelay = TimeSpan.FromMinutes(10);
    internal const int MaxAttempts = 3;

    /// <summary>The first line of every .lrc Octo writes.</summary>
    public const string OctoMark = "[re:Octo]";

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

    internal async Task<LyricsWriteOutcome> WriteAsync(LyricsJob job, CancellationToken ct) =>
        (await WriteAsync(job, upgrade: false, ct)).Outcome;

    /// <summary>
    /// Look the song up and write what was found. With <paramref name="upgrade"/>, a line-timed
    /// .lrc that Octo wrote is looked up again and replaced when word timing turns up; any other
    /// existing lyrics stay as they are.
    /// </summary>
    internal async Task<LyricsWrite> WriteAsync(LyricsJob job, bool upgrade, CancellationToken ct)
    {
        if (!File.Exists(job.AudioPath)) return new(LyricsWriteOutcome.Gone, null);

        var stem = Stem(job.AudioPath);
        var upgrading = upgrade && IsOctosLineTimedLrc(stem + ".lrc");
        if (!upgrading && (SidecarExtensions.Any(extension => File.Exists(stem + extension)) || HasEmbeddedLyrics(job.AudioPath)))
            return new(LyricsWriteOutcome.AlreadyThere, null);

        var lookup = await _lyrics.FindAsync(
            new LyricsQuery(job.Artist, job.Title, job.Album, job.DurationSeconds ?? ReadDuration(job.AudioPath)), ct);
        if (lookup.Transient)
        {
            if (job.Attempt < MaxAttempts) return new(LyricsWriteOutcome.Retrying, null);
            _logger.LogInformation("No lyrics service answered for '{Artist} - {Title}' after {N} tries",
                job.Artist, job.Title, job.Attempt);
            return new(LyricsWriteOutcome.GaveUp, null);
        }

        if (upgrading)
        {
            if (lookup.Result is not { Timing: LyricsTiming.Word } better) return new(LyricsWriteOutcome.AlreadyThere, null);
            await WriteFileAsync(stem, better, ct);
            _logger.LogInformation("Lyrics for '{Artist} - {Title}' upgraded to word timing from {Source}",
                job.Artist, job.Title, better.Source);
            return new(LyricsWriteOutcome.Upgraded, better);
        }

        switch (lookup.Result)
        {
            case { Instrumental: true } instrumental:
                _logger.LogInformation("{Source} says '{Artist} - {Title}' is instrumental; no lyrics file",
                    instrumental.Source, job.Artist, job.Title);
                return new(LyricsWriteOutcome.Instrumental, instrumental);
            case { } found when found.HasSynced || found.HasPlain:
                await WriteFileAsync(stem, found, ct);
                _logger.LogInformation("Lyrics for '{Artist} - {Title}' from {Source} ({Timing})",
                    job.Artist, job.Title, found.Source, found.Timing.ToString().ToLowerInvariant());
                return new(LyricsWriteOutcome.Written, found);
            default:
                _logger.LogInformation("No lyrics found for '{Artist} - {Title}'", job.Artist, job.Title);
                return new(LyricsWriteOutcome.NotFound, null);
        }
    }

    /// <summary>
    /// Replace a song's lyrics file with lyrics someone chose. Only where Octo may write: no
    /// lyrics file yet, or one Octo wrote; a file the owner put there is never touched.
    /// </summary>
    internal async Task<bool> ReplaceAsync(string audioPath, LyricsResult chosen, CancellationToken ct)
    {
        if (!File.Exists(audioPath) || (!chosen.HasSynced && !chosen.HasPlain)) return false;
        var stem = Stem(audioPath);
        var octos = IsOctos(stem + ".lrc");
        var others = SidecarExtensions.Where(extension => !(octos && extension == ".lrc"))
            .Any(extension => File.Exists(stem + extension));
        if (others || HasEmbeddedLyrics(audioPath)) return false;
        if (octos) File.Delete(stem + ".lrc");
        await WriteFileAsync(stem, chosen, ct);
        return true;
    }

    private static async Task WriteFileAsync(string stem, LyricsResult found, CancellationToken ct)
    {
        if (found.HasSynced)
            await File.WriteAllTextAsync(stem + ".lrc",
                OctoMark + "\n" + found.Synced!.Replace("\r\n", "\n").Trim() + "\n", Utf8NoBom, ct);
        else
            await File.WriteAllTextAsync(stem + ".txt", found.Plain!.Replace("\r\n", "\n").Trim() + "\n", Utf8NoBom, ct);
    }

    private static string Stem(string audioPath) =>
        Path.Combine(Path.GetDirectoryName(audioPath)!, Path.GetFileNameWithoutExtension(audioPath));

    /// <summary>Whether a .lrc opens with Octo's own mark.</summary>
    internal static bool IsOctos(string lrcPath)
    {
        try
        {
            if (!File.Exists(lrcPath)) return false;
            using var reader = new StreamReader(lrcPath, Encoding.UTF8);
            return string.Equals(reader.ReadLine()?.Trim().TrimStart('﻿'), OctoMark, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsOctosLineTimedLrc(string lrcPath)
    {
        if (!IsOctos(lrcPath)) return false;
        try
        {
            return !LyricsText.HasWordTags(File.ReadAllText(lrcPath));
        }
        catch
        {
            return false;
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

    internal static bool HasEmbeddedLyrics(string path)
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
