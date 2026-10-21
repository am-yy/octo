using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.LastFm;
using Octo.Services.Metadata;

namespace Octo.Services.Radio;

/// <summary>
/// Deezer's track mix for a song, found by one batched catalog lookup; Deezer's radio for the
/// artist when Deezer has no such song, and for an artist's radio.
/// </summary>
public sealed class DeezerRadioSource(DeezerMetadataService deezer, IOptionsMonitor<RadioSourceSettings> settings)
    : IRadioSource
{
    public string Provider => RadioProvider.Deezer;
    public bool Available => settings.CurrentValue.Deezer;

    public async Task<RadioAnswer> SongsLikeAsync(RadioSeed seed, int count,
        IReadOnlyDictionary<string, string> auth, CancellationToken ct)
    {
        if (!seed.IsArtist)
        {
            var found = (await deezer.FindTracksBatchAsync([(seed.Artist, seed.Title, seed.Duration)], ct)).FirstOrDefault();
            if (found?.DeezerId is { Length: > 0 } trackId
                && await deezer.GetTrackMixAsync(trackId, ct) is { Count: > 0 } mix)
                return new(Provider, RadioMatch.Song, Tracks(mix.Where(track => track.DeezerId != trackId), count), []);
        }
        var artist = (await deezer.SearchArtistsAsync(seed.Artist, 1, ct)).FirstOrDefault();
        // Only the artist Deezer files under this very name: a search's best guess for an
        // uploader's channel name is anyone.
        if (artist is null || !string.Equals(LastFmRadioSeedNormalizer.Artist(artist.Name) ?? artist.Name,
                LastFmRadioSeedNormalizer.Artist(seed.Artist) ?? seed.Artist, StringComparison.OrdinalIgnoreCase))
            return RadioAnswer.Nothing(Provider);
        var radio = await deezer.GetArtistRadioAsync(artist.DeezerId, ct) ?? [];
        return new(Provider, radio.Count > 0 ? RadioMatch.ArtistsTrusted : RadioMatch.None, Tracks(radio, count), []);
    }

    private List<LastFmService.SimilarTrack> Tracks(IEnumerable<DeezerMetadataService.ChartTrack> tracks, int count) =>
        tracks.Take(count).Select(track => new LastFmService.SimilarTrack(track.Artist, track.Title, 1.0,
            track.Duration, Provider: Provider)).ToList();
}
