namespace Octo.Models.Radio;

public sealed class LastFmRadioStateDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, LastFmRadioUserState> Users { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LastFmRadioUserState
{
    public string Username { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public int NewPlaysSinceRefresh { get; set; }
    public List<LastFmRadioPlay> Plays { get; set; } = [];
    public List<LastFmRadioStation> Stations { get; set; } = [];
    public List<LastFmRadioUnavailableTrack> UnavailableTracks { get; set; } = [];
    public List<LastFmRadioBan> RadioBans { get; set; } = [];
    /// <summary>Songs the listener hearted, as picks: the strongest thing they can say about a song.</summary>
    public List<LastFmRadioPick> Hearts { get; set; } = [];
    /// <summary>The listener's own playlists as Octo last read them. A song in one is a pick, and
    /// a playlist they added to lately seeds a "More like" station.</summary>
    public List<LastFmRadioPlaylist> Playlists { get; set; } = [];
    /// <summary>New artists Discovery Mix has put in front of the listener, so one they keep
    /// passing over steps aside for others.</summary>
    public List<LastFmRadioOffer> Offers { get; set; } = [];
    public DateTime? LastRefreshAttemptUtc { get; set; }
    public DateTime? LastRefreshSuccessUtc { get; set; }
    public string? LastRefreshError { get; set; }
    public bool Refreshing { get; set; }
}

public sealed class LastFmRadioUnavailableTrack
{
    public string Key { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime FailedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime RetryAfterUtc { get; set; } = DateTime.UtcNow.AddHours(24);
}

/// <summary>A song the listener rated one star: it never plays on their radio.</summary>
public sealed class LastFmRadioBan
{
    public string Key { get; set; } = string.Empty;
    public string SongId { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime BannedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LastFmRadioPlay
{
    public string SongId { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int? Duration { get; set; }
    public bool IsLocal { get; set; }
    public bool Hearted { get; set; }
    public bool LearnedSignal { get; set; } = true;
    public string Source { get; set; } = "scrobble";
    public DateTime PlayedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>How much a pick counts (see <see cref="LastFmRadioPick.Weight"/>). Set only on the
    /// plays a build makes from picks; never stored.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? Weight { get; set; }
}

/// <summary>
/// A song the listener chose: hearted, or put in one of their playlists. Unlike a play it says
/// they want it, not only that it was on.
/// </summary>
public sealed class LastFmRadioPick
{
    public string SongId { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int? Duration { get; set; }
    public bool IsLocal { get; set; }
    /// <summary>Where a heart came from: "star" (a library song, recorded as it happened) or
    /// "saved" (Octo's own record of hearts on outside songs, read again each refresh).</summary>
    public string Origin { get; set; } = string.Empty;
    /// <summary>1 for a song chosen on its own. Songs of one artist added together share
    /// <c>LastFmRadioStateStore.BatchArtistShare</c>, so a whole album added at once counts as a
    /// couple of picks of its artist, not a dozen.</summary>
    public double Weight { get; set; } = 1;
    public DateTime PickedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One of the listener's playlists as Octo last read it.</summary>
public sealed class LastFmRadioPlaylist
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>When the playlist last changed, as its server said. An unchanged playlist is not read again.</summary>
    public DateTime ChangedUtc { get; set; }
    public List<LastFmRadioPick> Songs { get; set; } = [];
}

/// <summary>A new artist Discovery Mix has offered, counted once per mix the listener played from
/// and got past them in.</summary>
public sealed class LastFmRadioOffer
{
    public string Key { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int Times { get; set; }
    public DateTime LastOfferedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Until then Discovery Mix leaves the artist out; after it they may be offered again.</summary>
    public DateTime? RestUntilUtc { get; set; }
}

public enum LastFmRadioStationKind
{
    Starter,
    YourMix,
    Discovery,
    Artist,
    Genre,
    Pinned,
    /// <summary>"More like" one of the listener's playlists, seeded by its songs.</summary>
    Playlist,
}

public sealed class LastFmRadioStation
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public LastFmRadioStationKind Kind { get; set; }
    public bool Personalized { get; set; }
    public int DefinitionVersion { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ChangedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ValidUntilUtc { get; set; } = DateTime.UtcNow;
    public List<string> Seeds { get; set; } = [];
    public List<LastFmRadioTrack> Tracks { get; set; } = [];
}

public sealed class LastFmRadioTrack
{
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int? Duration { get; set; }
    public int? Year { get; set; }
    public double Score { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? ResolvedId { get; set; }
    public bool IsLocal { get; set; }
    public string? ExternalProvider { get; set; }
    public string? YouTubeId { get; set; } // Legacy identity, preserved across migration.
    public string? DeezerId { get; set; }
}

public sealed class LastFmRadioUserSummary
{
    public string Username { get; set; } = string.Empty;
    public int PlayCount { get; set; }
    public int StationCount { get; set; }
    public int NewPlaysSinceRefresh { get; set; }
    public DateTime? LastRefreshSuccessUtc { get; set; }
    public string? LastRefreshError { get; set; }
    public bool Refreshing { get; set; }
}
