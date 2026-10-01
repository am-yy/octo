namespace Octo.Models.Settings;

public sealed class DeezerSettings
{
    /// <summary>Account ARL cookie from deezer.com. Required for full-track playback.</summary>
    public string? Arl { get; set; }
    /// <summary>Optional account to try when the primary account cannot serve a track.</summary>
    public string? ArlFallback { get; set; }
    /// <summary>Permanent-copy quality, with lower-quality fallback. Continuous radio uses MP3.</summary>
    public string Quality { get; set; } = "FLAC";

    /// <summary>Store decoded FLAC playback copies outside the music library.</summary>
    public bool CacheEnabled { get; set; }

    /// <summary>Persistent path for decoded FLAC playback copies.</summary>
    public string CachePath { get; set; } = Path.Combine(Path.GetTempPath(), "octo-cache", "deezer");

    /// <summary>Maximum size for unpinned playback copies. Pinned tracks can exceed it.</summary>
    public double CacheMaxGiB { get; set; } = 20;

    /// <summary>Retention for unpinned playback copies, measured from last play or completion.</summary>
    public int CacheRetentionDays { get; set; } = 7;
}
