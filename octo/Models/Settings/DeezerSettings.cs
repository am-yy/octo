namespace Octo.Models.Settings;

public sealed class DeezerSettings
{
    /// <summary>Account ARL cookie from deezer.com. Required for full-track playback.</summary>
    public string? Arl { get; set; }
    /// <summary>Optional account to try when the primary account cannot serve a track.</summary>
    public string? ArlFallback { get; set; }
    /// <summary>Permanent-copy quality, with lower-quality fallback. Playback uses MP3.</summary>
    public string Quality { get; set; } = "FLAC";
}
