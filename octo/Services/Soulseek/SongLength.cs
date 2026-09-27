namespace Octo.Services.Soulseek;

/// <summary>
/// Where the length shown for an outside song came from, weakest first. A shown length is
/// only ever replaced by one from the same or a stronger source.
/// </summary>
public enum LengthSource
{
    /// <summary>Nothing is known. The song goes out with the 180s placeholder, which the
    /// Octo app reads as "no length".</summary>
    None = 0,

    /// <summary>The length of a YouTube video found for the song. Weakest because a video
    /// can carry an intro, an outro or a whole music-video skit.</summary>
    Video = 1,

    /// <summary>Last.fm's length: the one handed in with a radio track, or track.getInfo.
    /// Often missing, rarely wrong.</summary>
    LastFm = 2,

    /// <summary>Deezer's catalog length for the matched track.</summary>
    Deezer = 3,
}

/// <summary>
/// The rules for the length a client is shown for a song Octo found outside the library.
///
/// This is display only. It lives on <see cref="SoulseekRouting.ShownDuration"/>, never on
/// <see cref="SoulseekRouting.Duration"/>, because Duration is what a download ranks peer
/// files by and checks the finished file against, to within 8 seconds. A length filled in
/// so a row can show one must not start rejecting files a download used to accept.
/// </summary>
public static class SongLength
{
    /// <summary>Shorter than this, a video is a clip or a teaser, not the song.</summary>
    public const int MinVideoSeconds = 30;

    /// <summary>Longer than this, a video is a live set, a mix or a whole album.</summary>
    public const int MaxVideoSeconds = 20 * 60;

    /// <summary>The video's length when it is plausibly one song, or null.</summary>
    public static int? SaneVideoLength(int? seconds) =>
        seconds is int s && s >= MinVideoSeconds && s <= MaxVideoSeconds ? s : null;

    /// <summary>
    /// Store a length on the routing unless a stronger source already gave one. Returns
    /// whether it was stored. A missing or zero length is never stored: no length beats a
    /// made-up one.
    /// </summary>
    public static bool Remember(SoulseekRouting routing, int? seconds, LengthSource source)
    {
        if (seconds is not int s || s <= 0 || source == LengthSource.None) return false;
        if (source == LengthSource.Video && SaneVideoLength(s) is null) return false;

        // Locked because a background lookup and a request can both reach one routing, and
        // the pair must never be read half-written: a Deezer source with a video's length.
        lock (routing)
        {
            if (routing.ShownDurationSource > source) return false;
            routing.ShownDuration = s;
            routing.ShownDurationSource = source;
            return true;
        }
    }

    /// <summary>The shown length and where it came from, read together.</summary>
    public static (int? Seconds, LengthSource Source) Shown(SoulseekRouting routing)
    {
        lock (routing) return (routing.ShownDuration, routing.ShownDurationSource);
    }

    /// <summary>A length from a metadata source is known. A video length is only a
    /// stand-in, and still worth asking Deezer and Last.fm about.</summary>
    public static bool HasMetadataLength(SoulseekRouting routing) =>
        Shown(routing).Source >= LengthSource.LastFm;
}
