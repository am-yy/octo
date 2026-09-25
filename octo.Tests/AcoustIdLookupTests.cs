using System.Text.Json;
using Octo.Services.Fingerprint;

namespace Octo.Tests;

/// <summary>
/// AcoustID's response decides whether a finished download is kept or deleted, so the parser
/// has to tell three things apart that all look like "no match" from a distance: a refusal, a
/// track AcoustID has never heard of, and a confident identification of something else.
/// </summary>
public class AcoustIdLookupTests
{
    private static AcoustIdLookup Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return AcoustIdClient.ParseLookup(doc.RootElement);
    }

    /// <summary>
    /// The separator is load-bearing. FormUrlEncodedContent encodes a literal '+' as %2B, so a
    /// '+'-joined meta reaches AcoustID as ONE unknown token; it answers 200 with a real score
    /// and no metadata, every result has zero recordings, and the verdict is permanently
    /// Inconclusive. The feature then accepts every file forever while looking healthy, which
    /// is exactly the silent no-op this whole design is meant to avoid.
    /// </summary>
    [Fact]
    public void MetaFields_AreSpaceSeparated()
    {
        Assert.DoesNotContain('+', AcoustIdClient.MetaFields);
        Assert.Equal(["recordings", "releasegroups", "releases", "tracks", "compress"],
            AcoustIdClient.MetaFields.Split(' '));
    }

    [Fact]
    public void ParseLookup_RealResponse_ReadsScoreTitleArtistAlbumAndYear()
    {
        var lookup = Parse("""
        {
          "status": "ok",
          "results": [{
            "id": "9ff43b6a-4f16-427c-93c2-92307ca505e0",
            "score": 0.97,
            "recordings": [{
              "id": "0a1b2c3d-0000-0000-0000-000000000001",
              "title": "Teardrop",
              "artists": [{ "name": "Massive Attack" }, { "name": "Elizabeth Fraser" }],
              "releasegroups": [{
                "id": "rg-1", "title": "Mezzanine", "type": "Album",
                "releases": [{ "date": { "year": 2011 } }, { "date": { "year": 1998 } }]
              }]
            }]
          }]
        }
        """);

        Assert.True(lookup.IsOk);
        var result = Assert.Single(lookup.Results);
        Assert.Equal(0.97, result.Score, 3);

        var recording = Assert.Single(result.Recordings);
        Assert.Equal("Teardrop", recording.Title);
        // Not "Massive Attack, Elizabeth Fraser": Navidrome never splits an artist on a comma, so
        // that credit became one artist, and one folder, that neither of them owns (#49).
        Assert.Equal("Massive Attack & Elizabeth Fraser", recording.ArtistCredit);
        Assert.Equal("Mezzanine", recording.AlbumTitle);
        // The earliest release, because a 2011 reissue is not the track's year.
        Assert.Equal(1998, recording.Year);
    }

    /// <summary>
    /// The Deezer trap, reproduced here: refusal arrives as HTTP 200 with an error envelope.
    /// Reading IsOk from the status code would turn every over-budget call into "no match",
    /// which this feature reads as "accept the file".
    /// </summary>
    [Fact]
    public void ParseLookup_ErrorEnvelopeInA200_IsNotOk()
    {
        var lookup = Parse("""
        {"status": "error", "error": {"code": 6, "message": "invalid API key"}}
        """);

        Assert.False(lookup.IsOk);
        Assert.Equal("invalid API key", lookup.Error);
        Assert.Empty(lookup.Results);
    }

    /// <summary>
    /// A compilation or a live album must not supply the album name and year for a studio
    /// track, so a release group carrying secondarytypes loses to a plain one.
    /// </summary>
    [Fact]
    public void ParseLookup_CompilationReleaseGroup_LosesToThePlainAlbum()
    {
        var lookup = Parse("""
        {
          "status": "ok",
          "results": [{
            "score": 0.99,
            "recordings": [{
              "id": "r1", "title": "Song", "artists": [{ "name": "Artist" }],
              "releasegroups": [
                { "title": "Now That's What I Call Music! 42", "type": "Album",
                  "secondarytypes": ["Compilation"], "releases": [{ "date": { "year": 1999 } }] },
                { "title": "The Real Album", "type": "Album",
                  "releases": [{ "date": { "year": 1997 } }] }
              ]
            }]
          }]
        }
        """);

        var recording = lookup.Results[0].Recordings[0];
        Assert.Equal("The Real Album", recording.AlbumTitle);
        Assert.Equal(1997, recording.Year);
    }

    /// <summary>
    /// AcoustID knows the audio but has no MusicBrainz link for it. Nothing can be decided,
    /// so nothing is.
    /// </summary>
    [Fact]
    public void ParseLookup_ResultWithNoRecordings_YieldsNoMetadata()
    {
        var lookup = Parse("""{"status": "ok", "results": [{"id": "x", "score": 0.99}]}""");

        Assert.True(lookup.IsOk);
        Assert.Empty(Assert.Single(lookup.Results).Recordings);
    }

    /// <summary>
    /// The most important case in the whole feature. A legitimately obscure track, which is
    /// the music Soulseek is best at and the reason Octo uses it, has no AcoustID entry at
    /// all. Rejecting on absence would make verification worst exactly where the library is
    /// rarest.
    /// </summary>
    [Fact]
    public void ParseLookup_NoResultsAtAll_IsAnOkLookupWithNothingToSay()
    {
        var lookup = Parse("""{"status": "ok", "results": []}""");

        Assert.True(lookup.IsOk);
        Assert.Empty(lookup.Results);
    }

    [Fact]
    public void ParseLookup_MalformedRecordingFields_AreSkippedNotFatal()
    {
        var lookup = Parse("""
        {"status": "ok", "results": [{"score": 0.9, "recordings": [
          {"id": "r1", "title": "Song", "artists": [{"nope": "x"}], "releasegroups": []}
        ]}]}
        """);

        var recording = lookup.Results[0].Recordings[0];
        Assert.Empty(recording.Artists);
        Assert.Null(recording.AlbumTitle);
        Assert.Null(recording.Year);
    }

    /// <summary>
    /// Trimmed from a live answer to meta=recordings releasegroups releases tracks compress
    /// (2026-09-25, the docs' example track). The first group is a Various Artists soundtrack
    /// compilation and must lose; within the real album the EARLIEST release supplies the ids and
    /// the track position, and compress has dropped the release title because it equals the
    /// group's.
    /// </summary>
    [Fact]
    public void ParseLookup_TracksMeta_ReadsReleaseIdsAndTrackPosition()
    {
        var lookup = Parse("""
        {"status": "ok", "results": [{"id": "9ff43b6a-4f16-427c-93c2-92307ca505e0", "score": 1.0,
          "recordings": [{
            "id": "cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff",
            "title": "Lower Your Eyelids to Die With the Sun",
            "duration": 637.333,
            "artists": [{"id": "6d7b7cd4-254b-4c25-83f6-dd20f98ceacd", "name": "M83"}],
            "releasegroups": [
              {"id": "9e585041-f2c1-3f0d-be40-40c845a3323f", "title": "Donkey Punch", "type": "Album",
               "secondarytypes": ["Compilation", "Soundtrack"],
               "artists": [{"id": "89ad4ac3-39f7-470e-963a-56509c546377", "name": "Various Artists"}],
               "releases": [{"id": "11de51d7-32c0-4f8b-8df8-1dce7e65245f", "date": {"year": 2008, "month": 7, "day": 28},
                 "mediums": [{"position": 1, "track_count": 16, "tracks": [{"id": "t0", "position": 16}]}]}]},
              {"id": "ddaa2d4d-314e-3e7c-b1d0-f6d207f5aa2f", "title": "Before the Dawn Heals Us", "type": "Album",
               "artists": [{"id": "6d7b7cd4-254b-4c25-83f6-dd20f98ceacd", "name": "M83"}],
               "releases": [
                 {"id": "fad5e4b4-13ac-3f6c-9915-1f0267780df7", "date": {"year": 2005, "month": 1, "day": 25},
                  "mediums": [{"position": 1, "track_count": 15, "tracks": [{"id": "t1", "position": 15}]}]},
                 {"id": "db85c244-53e7-441c-bab0-52c9c0d27450", "date": {"year": 2005, "month": 1, "day": 24},
                  "mediums": [{"position": 1, "track_count": 15, "tracks": [{"id": "t2", "position": 15}]}]},
                 {"id": "e719659b-f591-4faf-ae77-f7f9ccc921c0", "date": {"year": 2014, "month": 8, "day": 26},
                  "mediums": [{"position": 2, "track_count": 6, "tracks": [{"id": "t3", "position": 6}]}]}
               ]}
            ]
          }]
        }]}
        """);

        var recording = lookup.Results[0].Recordings[0];
        Assert.Equal("Before the Dawn Heals Us", recording.AlbumTitle);
        Assert.Equal(2005, recording.Year);
        Assert.Equal(637, recording.DurationSeconds);
        Assert.Equal("M83", recording.PrimaryArtist);
        Assert.Equal("6d7b7cd4-254b-4c25-83f6-dd20f98ceacd", Assert.Single(recording.Credits).ArtistId);

        var release = Assert.IsType<AcoustIdRelease>(recording.Release);
        Assert.Equal("db85c244-53e7-441c-bab0-52c9c0d27450", release.ReleaseId);
        Assert.Equal("ddaa2d4d-314e-3e7c-b1d0-f6d207f5aa2f", release.ReleaseGroupId);
        Assert.Equal("Before the Dawn Heals Us", release.Title);
        Assert.Equal(15, release.TrackNumber);
        Assert.Equal(15, release.TrackCount);
        Assert.Equal(1, release.DiscNumber);
        Assert.Equal("M83", release.AlbumArtist);
        Assert.False(release.IsCompilation);
    }

    [Fact]
    public void ParseLookup_JoinPhrases_BuildTheCreditMusicBrainzPrints()
    {
        var lookup = Parse("""
        {"status": "ok", "results": [{"score": 0.99, "recordings": [{
          "id": "r1", "title": "Under Pressure",
          "artists": [{"id": "a1", "name": "Queen", "joinphrase": " & "}, {"id": "a2", "name": "David Bowie"}]
        }]}]}
        """);

        var recording = lookup.Results[0].Recordings[0];
        Assert.Equal("Queen & David Bowie", recording.ArtistCredit);
        Assert.Equal("Queen", recording.PrimaryArtist);
        Assert.Equal(["Queen", "David Bowie"], recording.Artists);
    }

    [Theory]
    [InlineData(new[] { "Bizarrap", "Rauw Alejandro" }, "Bizarrap & Rauw Alejandro")]
    [InlineData(new[] { "A", "B", "C" }, "A, B & C")]
    [InlineData(new[] { "Earth, Wind & Fire" }, "Earth, Wind & Fire")]
    public void ArtistCredit_WithoutJoinPhrases_NeverCommaJoinsTwoArtists(string[] names, string expected) =>
        Assert.Equal(expected, new AcoustIdRecording("r", "t", names, null, null).ArtistCredit);

    /// <summary>
    /// With only compilations to choose from, the one picked still names the album, and it says
    /// so, which is what keeps a single from being filed as a hundred-track various-artists album.
    /// </summary>
    [Fact]
    public void PickRelease_VariousArtistsGroup_IsACompilation()
    {
        var lookup = Parse("""
        {"status": "ok", "results": [{"score": 0.99, "recordings": [{
          "id": "r1", "title": "Song", "artists": [{"name": "Artist"}],
          "releasegroups": [{"id": "g1", "title": "Summer Hits", "type": "Album",
            "artists": [{"name": "Various Artists"}],
            "releases": [{"id": "rel1", "date": {"year": 2001}}]}]
        }]}]}
        """);

        var release = lookup.Results[0].Recordings[0].Release!;
        Assert.True(release.IsCompilation);
        Assert.Equal("Various Artists", release.AlbumArtist);
        Assert.Equal("rel1", release.ReleaseId);
    }
}
