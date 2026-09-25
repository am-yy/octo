namespace Octo.Services.Lyrics;

public sealed record LyricsQuery(string Artist, string Title, string? Album, int? DurationSeconds);

/// <summary>
/// Synced is LRC text with timestamps; Plain is untimed text. Instrumental means the source knows
/// the track has no words, which is an answer, not a miss.
/// </summary>
public sealed record LyricsResult(string Source, string? Synced, string? Plain, bool Instrumental)
{
    public bool HasSynced => !string.IsNullOrWhiteSpace(Synced);
    public bool HasPlain => !string.IsNullOrWhiteSpace(Plain);
}

/// <summary>
/// A source's answer. Transient means it could not answer right now (rate limited, overloaded,
/// timed out), which is never remembered as "this song has no lyrics": LRCLIB sheds load with
/// 503s often enough that caching those as misses would blank songs for no reason.
/// </summary>
public sealed record LyricsLookup(LyricsResult? Result, bool Transient)
{
    public static readonly LyricsLookup Miss = new(null, false);
    public static readonly LyricsLookup Failed = new(null, true);
}

public interface ILyricsSource
{
    /// <summary>The name in LYRICS_SOURCES: lrclib, netease or lyricsovh.</summary>
    string Key { get; }

    Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct);
}
