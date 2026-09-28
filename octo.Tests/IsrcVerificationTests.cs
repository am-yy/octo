using System.Diagnostics;
using System.Text.Json;
using Octo.Services.Fingerprint;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// An ISRC as evidence about a download: the one the song was asked for with, against the one in
/// the file's tags and the ones MusicBrainz lists for the recording a fingerprint named. A match
/// confirms; a difference alone never rejects, because a re-release is often given a new code.
/// </summary>
public class IsrcVerificationTests
{
    private const double Threshold = 0.85;
    private const string Isrc = "JPU901901234";

    private static AcoustIdLookup Ok(params AcoustIdResult[] results) => new(true, null, results);

    private static AcoustIdRecording Recording(string id, string title, string artist) => new(id, title, [artist], null, null);

    /// <summary>The fingerprint confidently names the song in its own script, and the request
    /// was made in its romanised name: by the text alone, a mismatch.</summary>
    private static readonly AcoustIdLookup NativeScript = Ok(new AcoustIdResult(0.97,
        [Recording("mbid-gurenge", "紅蓮華", "LiSA"), Recording("mbid-other", "紅蓮華 (TV Size)", "LiSA")]));

    private static VerificationResult TextVerdict() =>
        DownloadVerificationService.Decide(NativeScript, "LiSA", "Gurenge", Threshold, tagsAuthoritative: false) with
        {
            Fingerprint = "AQAD", DurationSeconds = 239,
        };

    [Fact]
    public void TheTextAloneCallsTheNativeTitleAMismatch() =>
        Assert.Equal(VerificationVerdict.Mismatch, TextVerdict().Verdict);

    // ---- MusicBrainz's ISRCs for the recording the fingerprint named -------------------------

    [Fact]
    public void ARecordingListingTheRequestedIsrc_Confirms()
    {
        var isrcs = new Dictionary<string, IReadOnlyList<string>> { ["mbid-gurenge"] = [Isrc], ["mbid-other"] = [] };

        var verdict = DownloadVerificationService.SettleByIsrc(TextVerdict(), NativeScript, Threshold,
            tagsAuthoritative: true, Isrc, isrcs, taggedMatch: false);

        Assert.Equal(VerificationVerdict.Confirmed, verdict.Verdict);
        Assert.Equal("mbid-gurenge", verdict.RecordingId);
        Assert.Equal("mbid-gurenge", verdict.Match?.RecordingId);
        Assert.True(verdict.TagsAuthoritative);
        Assert.Equal("AQAD", verdict.Fingerprint);
        Assert.Contains("MusicBrainz lists the requested ISRC", verdict.Evidence);
        Assert.Equal("", verdict.DenyReason);
    }

    /// <summary>Different codes are not a rejection by themselves, and not a rescue either: the
    /// verdict the text gave stands.</summary>
    [Fact]
    public void RecordingsListingOtherIsrcs_ChangeNothing()
    {
        var isrcs = new Dictionary<string, IReadOnlyList<string>> { ["mbid-gurenge"] = ["USRC17607839"] };
        var before = TextVerdict();

        var verdict = DownloadVerificationService.SettleByIsrc(before, NativeScript, Threshold,
            tagsAuthoritative: false, Isrc, isrcs, taggedMatch: true);

        Assert.Same(before, verdict);
    }

    /// <summary>The tags carry the code, MusicBrainz lists none to contradict them: two sources
    /// disagree about names only, and the file is kept and asked about instead of deleted.</summary>
    [Fact]
    public void TaggedIsrcAndARecordingWithNone_KeepsTheFileForReview()
    {
        var isrcs = new Dictionary<string, IReadOnlyList<string>> { ["mbid-gurenge"] = [], ["mbid-other"] = [] };

        var verdict = DownloadVerificationService.SettleByIsrc(TextVerdict(), NativeScript, Threshold,
            tagsAuthoritative: false, Isrc, isrcs, taggedMatch: true);

        Assert.Equal(VerificationVerdict.Inconclusive, verdict.Verdict);
        Assert.Equal(InconclusiveReason.SourceDisagreed, verdict.Reason);
        Assert.True(verdict.NeedsReview);
        Assert.Equal("", verdict.DenyReason);
        // A tag does not then promote it to Confirmed: the fingerprint did name something else.
        Assert.Equal(VerificationVerdict.Inconclusive,
            DownloadVerificationService.WithTaggedIsrc(verdict, Isrc, taggedMatch: true).Verdict);
    }

    [Fact]
    public void NoTaggedIsrcAndARecordingWithNone_StaysAMismatch()
    {
        var isrcs = new Dictionary<string, IReadOnlyList<string>> { ["mbid-gurenge"] = [] };

        var verdict = DownloadVerificationService.SettleByIsrc(TextVerdict(), NativeScript, Threshold,
            tagsAuthoritative: false, Isrc, isrcs, taggedMatch: false);

        Assert.Equal(VerificationVerdict.Mismatch, verdict.Verdict);
    }

    /// <summary>MusicBrainz could not be asked at all: nothing is known, so nothing changes.</summary>
    [Fact]
    public void MusicBrainzUnreachable_ChangesNothing()
    {
        var before = TextVerdict();
        var verdict = DownloadVerificationService.SettleByIsrc(before, NativeScript, Threshold,
            tagsAuthoritative: false, Isrc, new Dictionary<string, IReadOnlyList<string>>(), taggedMatch: true);

        Assert.Same(before, verdict);
    }

    // ---- the file's own tags ------------------------------------------------------------------

    [Theory]
    [InlineData(InconclusiveReason.Disabled)]
    [InlineData(InconclusiveReason.NotFingerprinted)]
    [InlineData(InconclusiveReason.LookupFailed)]
    [InlineData(InconclusiveReason.NoEntry)]
    [InlineData(InconclusiveReason.BelowThreshold)]
    public void ATaggedIsrc_ConfirmsWhenAcoustIdCouldNotSay(InconclusiveReason reason)
    {
        var verdict = DownloadVerificationService.WithTaggedIsrc(
            new VerificationResult { Reason = reason, Fingerprint = "AQAD" }, Isrc, taggedMatch: true);

        Assert.Equal(VerificationVerdict.Confirmed, verdict.Verdict);
        Assert.Equal(InconclusiveReason.None, verdict.Reason);
        Assert.False(verdict.NeedsReview);
        Assert.Equal("AQAD", verdict.Fingerprint);
        Assert.Equal($"the file's own tags carry the requested ISRC {Isrc}", verdict.Evidence);
    }

    [Fact]
    public void ATaggedIsrc_NeverOverrulesAConfidentFingerprint()
    {
        var mismatch = TextVerdict();
        Assert.Same(mismatch, DownloadVerificationService.WithTaggedIsrc(mismatch, Isrc, taggedMatch: true));
    }

    [Fact]
    public void NoTaggedMatch_ChangesNothing()
    {
        var inconclusive = new VerificationResult { Reason = InconclusiveReason.NoEntry };
        Assert.Same(inconclusive, DownloadVerificationService.WithTaggedIsrc(inconclusive, Isrc, taggedMatch: false));
    }

    [Fact]
    public void ReadIsrcs_AFileThatIsNotThere_IsEmpty() =>
        Assert.Empty(DownloadVerificationService.ReadIsrcs(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.flac")));

    /// <summary>
    /// The tags as another program wrote them, read through TagLib's one ISRC property: a FLAC's
    /// Vorbis ISRC and an MP3's ID3 TSRC and user text ISRC written by ffmpeg, and an MP4's iTunes ISRC atom
    /// (which ffmpeg does not write) written by TagLib into a file ffmpeg made.
    /// </summary>
    [FfmpegFact]
    public void ReadIsrcs_ReadsEveryContainersIsrcTag()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"octo-isrc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            void Ffmpeg(string arguments)
            {
                using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-y -nostdin -hide_banner -v error " + arguments)
                {
                    RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
                })!;
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, error);
            }
            const string tone = "-f lavfi -i sine=frequency=440:duration=1";
            Ffmpeg($"{tone} -metadata ISRC=JP-U90-19-01234 -c:a flac tagged.flac");
            // TSRC is ID3's own ISRC frame; ffmpeg writes a key named ISRC as a user text frame.
            Ffmpeg($"{tone} -metadata TSRC=JPU901901234 -c:a libmp3lame -b:a 128k -id3v2_version 4 tagged.mp3");
            Ffmpeg($"{tone} -metadata ISRC=JPU901901234 -c:a libmp3lame -b:a 128k -id3v2_version 4 user_frame.mp3");
            Ffmpeg($"{tone} -c:a aac -b:a 128k tagged.m4a");
            using (var m4a = TagLib.File.Create(Path.Combine(dir, "tagged.m4a")))
            {
                m4a.Tag.ISRC = "JPU901901234";
                m4a.Save();
            }
            Ffmpeg($"{tone} -c:a flac untagged.flac");

            Assert.Equal([Isrc], DownloadVerificationService.ReadIsrcs(Path.Combine(dir, "tagged.flac")));
            Assert.Equal([Isrc], DownloadVerificationService.ReadIsrcs(Path.Combine(dir, "tagged.mp3")));
            Assert.Equal([Isrc], DownloadVerificationService.ReadIsrcs(Path.Combine(dir, "user_frame.mp3")));
            Assert.Equal([Isrc], DownloadVerificationService.ReadIsrcs(Path.Combine(dir, "tagged.m4a")));
            Assert.Empty(DownloadVerificationService.ReadIsrcs(Path.Combine(dir, "untagged.flac")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* a temp folder */ }
        }
    }

    // ---- MusicBrainz's answer and the routing -------------------------------------------------

    [Fact]
    public void ParseIsrcs_ReadsARecordingLookup()
    {
        using var doc = JsonDocument.Parse("""{"id":"mbid","title":"紅蓮華","isrcs":["JPU901901234","jpu901901234","bogus"]}""");
        Assert.Equal([Isrc], MusicBrainzClient.ParseIsrcs(doc.RootElement));

        using var none = JsonDocument.Parse("""{"id":"mbid","title":"紅蓮華"}""");
        Assert.Empty(MusicBrainzClient.ParseIsrcs(none.RootElement));
    }

    /// <summary>A search row for a song an album listing already described names no ISRC, and
    /// registering it must not make the routing forget the one the album gave.</summary>
    [Fact]
    public void TheRegistryKeepsAnIsrcWhenTheSameSongIsMintedAgainWithout()
    {
        using var registry = new ExternalIdRegistry();
        var id = registry.Register(new SoulseekRouting { Artist = "LiSA", Title = "Gurenge", Duration = 239, Isrc = Isrc });
        var again = registry.Register(new SoulseekRouting { Artist = "LiSA", Title = "Gurenge", Duration = 239 });

        Assert.Equal(id, again);
        Assert.Equal(Isrc, registry.Lookup(id)?.Isrc);
    }
}
