using Octo.Services.Common;

namespace Octo.Services.LastFm;

/// <summary>Canonicalizes artist/title seeds for every Last.fm radio path, by
/// <see cref="SongIdentity"/>'s reading of a credit and a title.</summary>
public static class LastFmRadioSeedNormalizer
{
    /// <summary>The primary artist, as written: "Beyoncé" for "Beyoncé feat. Jay-Z", and
    /// "Tyler, The Creator" or "Simon &amp; Garfunkel" whole.</summary>
    public static string? Artist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return artist;
        return SongIdentity.PrimaryArtist(artist).Trim();
    }

    /// <summary>The title without its guest credits.</summary>
    public static string? Title(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        return SongIdentity.StripFeatures(title);
    }

    /// <summary>One song in one version, however its artist and title are written.</summary>
    public static string TrackKey(string? artist, string? title) => SongIdentity.MatchKey(artist, title);
}
