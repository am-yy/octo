using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;

namespace Octo.Services.Fingerprint;

public enum VerificationVerdict
{
    /// <summary>AcoustID identified the file as the track that was asked for.</summary>
    Confirmed,

    /// <summary>
    /// Nothing could be established: verification off, no key, no fpcalc, AcoustID down or
    /// rate limited, no result above the score threshold, or no AcoustID entry at all. The
    /// file is kept and nothing is remembered.
    ///
    /// The last of those is the one that matters. A legitimately obscure track, which is the
    /// music Soulseek is best at and the reason Octo uses it, has no AcoustID entry. Treating
    /// absence as evidence would make this feature worst exactly where the library is rarest.
    /// </summary>
    Inconclusive,

    /// <summary>
    /// The file was identified with confidence and it is a different recording, or it holds
    /// no decodable audio at all. The only verdict that discards a file or writes a denial.
    /// </summary>
    Mismatch,
}

/// <summary>
/// Why a verdict was Inconclusive. Only the last three are questions a person can settle by
/// listening, so only those reach the Review playlist (#47). The others are Octo not asking.
/// </summary>
public enum InconclusiveReason
{
    None,
    Disabled,
    NotFingerprinted,
    LookupFailed,
    NoEntry,
    BelowThreshold,
    SourceDisagreed,
}

public sealed record VerificationResult
{
    public VerificationVerdict Verdict { get; init; } = VerificationVerdict.Inconclusive;
    public double Score { get; init; }
    public string? MatchedTitle { get; init; }
    public string? MatchedArtist { get; init; }
    public string? MatchedAlbum { get; init; }
    public int? MatchedYear { get; init; }
    public string? RecordingId { get; init; }
    public string DenyReason { get; init; } = "";
    public bool TagsAuthoritative { get; init; }

    public InconclusiveReason Reason { get; init; }

    /// <summary>The recording that agreed with the request. Set only when Confirmed.</summary>
    public AcoustIdRecording? Match { get; init; }

    /// <summary>Kept so a person's confirmation can be sent back to AcoustID (#47).</summary>
    public string? Fingerprint { get; init; }
    public int DurationSeconds { get; init; }

    /// <summary>
    /// The one recording AcoustID proposed below the threshold that agrees with the request on
    /// title, artist and length. The first MusicBrainz id a person's Keep may submit, and null
    /// when there were none or more than one.
    /// </summary>
    public string? CandidateRecordingId { get; init; }

    public bool NeedsReview => Verdict == VerificationVerdict.Inconclusive
        && Reason is InconclusiveReason.NoEntry or InconclusiveReason.BelowThreshold
            or InconclusiveReason.SourceDisagreed;

    public static readonly VerificationResult Inconclusive = new() { Reason = InconclusiveReason.Disabled };

    /// <summary>
    /// Whether a song's album IS the MusicBrainz release its fingerprint matched, so the ids and
    /// artwork that belong to that release describe the album the tags name. A download tagged
    /// with a compilation's name must not get the original album's cover or group id.
    /// </summary>
    public static bool AlbumIsFromRelease(Song song) =>
        !string.IsNullOrWhiteSpace(song.MusicBrainzAlbumTitle)
        && TrackMatchComparer.Normalize(song.Album) == TrackMatchComparer.Normalize(song.MusicBrainzAlbumTitle);

    public string Describe() => string.IsNullOrEmpty(MatchedArtist) && string.IsNullOrEmpty(MatchedTitle)
        ? "a different recording"
        : $"'{MatchedArtist} - {MatchedTitle}'";

    /// <summary>
    /// Record what a confirmed match proved, and overwrite the song's name from it when
    /// tagging from MusicBrainz is on.
    ///
    /// The ids are written whenever the match is confirmed. An id says what the file IS and
    /// changes nothing a person reads, and it is what lets every later pass skip identifying
    /// the same file again (#48). The release id stays in memory for the cover lookup and is
    /// never written: Navidrome groups albums by MUSICBRAINZ_ALBUMID before the album name, so
    /// one track carrying it beside another without it would split an album in two.
    ///
    /// The name overwrite is unconditional where the Deezer fill is conditional. That one fills
    /// only what is missing, because a peer's own tags beat nothing. A confirmed fingerprint
    /// match beats the peer, which is the entire point of the setting.
    /// </summary>
    public void ApplyTagsTo(Song song)
    {
        if (Verdict != VerificationVerdict.Confirmed) return;

        if (!string.IsNullOrEmpty(RecordingId)) song.MusicBrainzRecordingId = RecordingId;
        if (Match is { } match)
        {
            if (match.Credits.Count > 1) song.Artists = match.Credits.Select(credit => credit.Name).ToList();
            if (match.Credits.Count == 1 && match.Credits[0].ArtistId is { Length: > 0 } artistId)
                song.MusicBrainzArtistIds = [artistId];
            if (match.PrimaryArtist is { Length: > 0 } primary) song.PrimaryArtist = primary;
            song.MusicBrainzReleaseId = match.Release?.ReleaseId;
            song.MusicBrainzReleaseGroupId = match.Release?.ReleaseGroupId;
            song.MusicBrainzAlbumTitle = match.AlbumTitle;
        }

        if (!TagsAuthoritative) return;
        if (!string.IsNullOrEmpty(MatchedTitle)) song.Title = MatchedTitle;
        if (!string.IsNullOrEmpty(MatchedArtist)) song.Artist = MatchedArtist;
        if (!string.IsNullOrEmpty(MatchedAlbum)) song.Album = MatchedAlbum;
        if (MatchedYear is > 0) song.Year = MatchedYear;

        if (Match?.Release is { } release && !string.IsNullOrEmpty(MatchedAlbum))
        {
            if (release.TrackNumber is > 0) song.Track = release.TrackNumber;
            if (release.TrackCount is > 0) song.TotalTracks = release.TrackCount;
            if (release.DiscNumber is > 0) song.DiscNumber = release.DiscNumber;
            if (!string.IsNullOrEmpty(release.AlbumArtist)) song.AlbumArtist = release.AlbumArtist;
            song.IsCompilation = release.IsCompilation;
        }
    }
}

/// <summary>
/// Asks what a finished download actually contains, rather than what its name and advertised
/// length claim.
///
/// Every failure path accepts the file. That is the correct trade and also the dominant risk:
/// a broken key, a missing binary, a mishandled gzip and a parse error all look exactly like
/// "everything is fine", which is why the pieces below log refusals at Warning.
/// </summary>
public sealed class DownloadVerificationService
{
    private readonly AudioFingerprinter _fingerprinter;
    private readonly AcoustIdClient _client;
    private readonly IOptionsMonitor<SoulseekSettings> _options;
    private readonly ILogger<DownloadVerificationService> _logger;

    public DownloadVerificationService(AudioFingerprinter fingerprinter, AcoustIdClient client,
        IOptionsMonitor<SoulseekSettings> options, ILogger<DownloadVerificationService> logger)
    {
        _fingerprinter = fingerprinter;
        _client = client;
        _options = options;
        _logger = logger;
    }

    /// <summary>AcoustID can answer at all.</summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(_options.CurrentValue.AcoustIdApiKey);

    /// <summary>
    /// The user asked Octo to police what peers deliver. Governs the deny-list on its own,
    /// because the duration check needs no API key and its verdicts are just as durable.
    /// </summary>
    public bool RemembersRejections => _options.CurrentValue.VerifyDownloads;

    /// <summary>
    /// A lookup can actually happen. Split from the above deliberately, in the shape
    /// LastFmService uses for HasApiKey/IsRadioEnabled: collapsing them would mean a user who
    /// switches verification on without a key gets no bad-peer memory either, and the
    /// duration check quietly keeps re-downloading the same wrong file.
    /// </summary>
    public bool IsFingerprintingEnabled => RemembersRejections && HasApiKey;

    /// <summary>
    /// No CancellationToken parameter, deliberately. There must be no way for a caller who
    /// has already given up to skip verification on a file that is about to enter the library.
    /// </summary>
    public async Task<VerificationResult> VerifyAsync(string path, string? requestedArtist, string? requestedTitle)
    {
        if (!IsFingerprintingEnabled) return VerificationResult.Inconclusive;

        var settings = _options.CurrentValue;
        var fingerprint = await _fingerprinter.FingerprintAsync(path,
            settings.EffectiveFingerprintSeconds, settings.EffectiveFingerprintTimeoutSeconds);

        if (fingerprint.Outcome == FingerprintOutcome.Undecodable)
            return new VerificationResult
            {
                Verdict = VerificationVerdict.Mismatch,
                DenyReason = "delivered a file with no decodable audio",
            };

        if (fingerprint.Outcome != FingerprintOutcome.Ok || string.IsNullOrEmpty(fingerprint.Fingerprint))
            return new VerificationResult { Reason = InconclusiveReason.NotFingerprinted };

        // TagLib's duration, not fpcalc's. -length pins how much audio is fingerprinted, and
        // staking a rejection on whether that also truncates the reported duration would
        // reject every track over two minutes if it does.
        var seconds = ReadDurationSeconds(path);
        if (seconds <= 0) seconds = fingerprint.DecodedSeconds;
        if (seconds <= 0) return new VerificationResult { Reason = InconclusiveReason.NotFingerprinted };

        var lookup = await _client.LookupAsync(settings.AcoustIdApiKey, fingerprint.Fingerprint,
            seconds, settings.EffectiveAcoustIdTimeoutSeconds);
        if (lookup is null) return new VerificationResult { Reason = InconclusiveReason.LookupFailed };
        if (!lookup.IsOk)
        {
            _logger.LogWarning("acoustid refused the lookup for {Path}: {Error}", path, lookup.Error);
            return new VerificationResult { Reason = InconclusiveReason.LookupFailed };
        }
        if (lookup.Results.Count == 0)
        {
            _logger.LogInformation(
                "acoustid has no entry for '{Artist} - {Title}'; keeping the file. Obscure music is "
                + "exactly what Soulseek is for, so an absent match is never treated as a mismatch.",
                requestedArtist, requestedTitle);
            // Kept with the fingerprint: this is the one case a person listening can settle, and
            // the one where their answer is worth sending back to AcoustID.
            return new VerificationResult
            {
                Reason = InconclusiveReason.NoEntry,
                Fingerprint = fingerprint.Fingerprint,
                DurationSeconds = seconds,
            };
        }

        // NameFromMatch implies authoritative tags: a path from MusicBrainz beside tags from the
        // source is exactly the split it exists to remove (#48).
        var verdict = Decide(lookup, requestedArtist, requestedTitle,
            settings.EffectiveMinScoreFraction, settings.TagFromMusicBrainz || settings.NameFromMatch,
            seconds) with
        {
            Fingerprint = fingerprint.Fingerprint,
            DurationSeconds = seconds,
        };

        // A confirmation is logged too, not just a refusal. The dominant risk in this feature is
        // that a broken key, a missing binary or a mangled request makes it accept everything
        // while looking healthy, and silence on success is indistinguishable from never running.
        if (verdict.Verdict == VerificationVerdict.Confirmed)
            _logger.LogInformation(
                "acoustid confirmed '{Artist} - {Title}' at {Score:P0}{Album}",
                requestedArtist, requestedTitle, verdict.Score,
                string.IsNullOrEmpty(verdict.MatchedAlbum) ? "" : $" from '{verdict.MatchedAlbum}'");

        if (verdict.Verdict == VerificationVerdict.Inconclusive)
            _logger.LogInformation(
                "acoustid's best match for '{Artist} - {Title}' scored {Best:P0} against a {Threshold:P0} "
                + "threshold, so it decides nothing and the file is kept",
                requestedArtist, requestedTitle,
                lookup.Results.Max(result => result.Score), settings.EffectiveMinScoreFraction);

        return verdict;
    }

    /// <summary>
    /// The whole decision, separated from the I/O so it can be driven directly. Every branch
    /// here either keeps a file or deletes one, and the sealed fingerprinter and HTTP client
    /// above make the orchestration awkward to mock for no benefit.
    /// </summary>
    internal static VerificationResult Decide(AcoustIdLookup lookup, string? requestedArtist,
        string? requestedTitle, double threshold, bool tagsAuthoritative, int durationSeconds = 0)
    {
        var qualifying = lookup.Results
            .Where(result => result.Score >= threshold && result.Recordings.Count > 0)
            .OrderByDescending(result => result.Score)
            .ToList();

        // Below the threshold an answer is ignored, never acted on. That is why raising
        // MinMatchScore makes Octo MORE permissive rather than less.
        if (qualifying.Count == 0)
        {
            // A result above the threshold with no recordings is a fingerprint AcoustID knows and
            // MusicBrainz does not: exactly the gap a person's confirmation can fill.
            var known = lookup.Results.Any(result => result.Score >= threshold);
            return new VerificationResult
            {
                Reason = known ? InconclusiveReason.NoEntry : InconclusiveReason.BelowThreshold,
                CandidateRecordingId = AgreeingCandidate(lookup, requestedArtist, requestedTitle, durationSeconds),
            };
        }

        var best = qualifying[0];

        // Any, not first: one AcoustID id maps to several MusicBrainz recordings when the
        // same audio ships on an album and a compilation, and demanding the first would
        // reject correct files.
        var agreed = best.Recordings.FirstOrDefault(recording =>
            TrackMatchComparer.TitleMatches(requestedTitle, recording.Title)
            && TrackMatchComparer.ArtistMatches(requestedArtist, recording.ArtistCredit, recording.Artists));

        if (agreed is not null)
            return new VerificationResult
            {
                Verdict = VerificationVerdict.Confirmed,
                Score = best.Score,
                MatchedTitle = agreed.Title,
                MatchedArtist = agreed.ArtistCredit,
                MatchedAlbum = agreed.AlbumTitle,
                MatchedYear = agreed.Year,
                RecordingId = agreed.RecordingId,
                TagsAuthoritative = tagsAuthoritative,
                Match = agreed,
            };

        var actual = best.Recordings[0];
        return new VerificationResult
        {
            Verdict = VerificationVerdict.Mismatch,
            Score = best.Score,
            MatchedTitle = actual.Title,
            MatchedArtist = actual.ArtistCredit,
            MatchedAlbum = actual.AlbumTitle,
            MatchedYear = actual.Year,
            RecordingId = actual.RecordingId,
            DenyReason = $"is '{actual.ArtistCredit} - {actual.Title}'",
        };
    }

    /// <summary>
    /// The single recording, at any score, that agrees on title, artist and length (7 seconds
    /// either way). Two or more is ambiguity, and ambiguity submits nothing.
    /// </summary>
    internal static string? AgreeingCandidate(AcoustIdLookup lookup, string? requestedArtist,
        string? requestedTitle, int durationSeconds)
    {
        var ids = lookup.Results
            .SelectMany(result => result.Recordings)
            .Where(recording => !string.IsNullOrEmpty(recording.RecordingId)
                && TrackMatchComparer.TitleMatches(requestedTitle, recording.Title)
                && TrackMatchComparer.ArtistMatches(requestedArtist, recording.ArtistCredit, recording.Artists)
                && (durationSeconds <= 0 || recording.DurationSeconds is null
                    || Math.Abs(recording.DurationSeconds.Value - durationSeconds) <= 7))
            .Select(recording => recording.RecordingId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ids.Count == 1 ? ids[0] : null;
    }

    private int ReadDurationSeconds(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return (int)Math.Round(file.Properties.Duration.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("could not read a duration from {Path}: {M}", path, ex.Message);
            return 0;
        }
    }
}
