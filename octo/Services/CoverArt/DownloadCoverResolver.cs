using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Fingerprint;
using Octo.Services.Soulseek;

namespace Octo.Services.CoverArt;

/// <summary>The cover a download gets, where it came from, and whether it is simply the one the
/// file already carries (so there is nothing to rewrite).</summary>
public sealed record CoverChoice(byte[] Bytes, string Source, bool KeepsExisting);

/// <summary>
/// The download-time cover chain (#51). The download path used to embed one Deezer URL and stop,
/// so anything Deezer did not know was written with no art, while the aggregator that already
/// knew iTunes and Last.fm sat unused beside it.
///
/// In order: the Cover Art Archive when a fingerprint named the release the album tag describes,
/// the catalog's own cover, the aggregator by name, and last the file's own art. A cover that is
/// not square counts as missing, and a letterboxed video frame gives up its centre.
/// </summary>
public sealed class DownloadCoverResolver
{
    /// <summary>A slow cover source must cost seconds, never the download: the whole finalize
    /// phase runs under the download lock.</summary>
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(8);

    private readonly CoverArtArchiveLookup _archive;
    private readonly CoverArtAggregator _aggregator;
    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<MetadataSettings> _settings;
    private readonly ILogger<DownloadCoverResolver> _logger;

    public DownloadCoverResolver(CoverArtArchiveLookup archive, CoverArtAggregator aggregator,
        IHttpClientFactory http, IOptionsMonitor<MetadataSettings> settings, ILogger<DownloadCoverResolver> logger)
    {
        _archive = archive;
        _aggregator = aggregator;
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public async Task<CoverChoice?> ResolveAsync(Song song, byte[]? embedded, CancellationToken ct)
    {
        var settings = _settings.CurrentValue;
        var requireSquare = settings.ReplaceVideoCovers;

        // Only when the album tag IS the release the fingerprint matched: a download tagged with
        // a compilation's name must not get the original album's cover.
        if (settings.UseCoverArtArchive
            && (song.MusicBrainzReleaseId is { Length: > 0 } || song.MusicBrainzReleaseGroupId is { Length: > 0 })
            && VerificationResult.AlbumIsFromRelease(song))
        {
            var archived = await _archive.TryFetchAsync(song.MusicBrainzReleaseId, song.MusicBrainzReleaseGroupId, ct);
            if (CoverImage.IsUsable(archived, requireSquare)) return new(archived!, "Cover Art Archive", false);
        }

        if ((song.CoverArtUrlLarge ?? song.CoverArtUrl) is { Length: > 0 } url)
        {
            var catalog = await DownloadAsync(url, ct);
            if (CoverImage.IsUsable(catalog, requireSquare)) return new(catalog!, "the catalog", false);
        }

        try
        {
            var routing = new SoulseekRouting
            {
                Kind = string.IsNullOrWhiteSpace(song.Album) ? RoutingKind.Song : RoutingKind.Album,
                Artist = song.PrimaryArtist ?? song.Artist,
                Title = song.Title,
                Album = song.Album,
            };
            var aggregated = await _aggregator.GetCoverAsync(routing, background: true, ct);
            if (CoverImage.IsUsable(aggregated, requireSquare)) return new(aggregated!, "a cover search", false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("cover search failed for {Artist} - {Title}: {M}", song.Artist, song.Title, ex.Message);
        }

        if (embedded is { Length: > 0 })
        {
            if (CoverImage.IsUsable(embedded, requireSquare)) return new(embedded, "the file itself", true);
            if (requireSquare && CoverImage.CropToSquare(embedded) is { } cropped && CoverImage.IsUsable(cropped, true))
                return new(cropped, "the centre of a video frame", false);
        }
        return null;
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CatalogTimeout);
            using var response = await _http.CreateClient().GetAsync(url, timeout.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(timeout.Token) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("cover download {Url} failed: {M}", url, ex.Message);
            return null;
        }
    }
}
