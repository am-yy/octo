using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Fingerprint;
using Octo.Services.Local;
using Octo.Services.Notifications;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;
using Octo.Services.YouTube;

namespace Octo.Tests;

/// <summary>
/// Two reports from 2026-10-04. "Songs with (Radio edit) only download radio edit songs": the
/// title's version tag was read as words the file name had to carry, so "Too Close (Radio Edit)"
/// took only files that spelled out "radio edit", and the same rule made "Take Care (feat.
/// Rihanna)" refuse every file not naming Rihanna. And "Take Care downloaded MP3, Find better
/// quality found FLAC": the star's search stopped at 500 files after one second, while Better
/// quality read 2,000. Every file name below is shaped like a real Soulseek share.
/// </summary>
public sealed class DownloadMatchingTests
{
    // ---- The name rule reads the title, not its tags ------------------------------------------

    [Theory]
    [InlineData(@"Music\Drake\Take Care (Deluxe) (2011)\04 - Take Care.flac")]
    [InlineData(@"Music\Drake\Take Care\04 - Drake ft. Rihanna - Take Care.flac")]
    [InlineData(@"VA - Now That's What I Call Music! 81\CD1\17. Drake Feat. Rihanna - Take Care.flac")]
    public void AGuestCreditIsNotWordsTheFileMustCarry(string file)
    {
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(file, "Take Care (feat. Rihanna)"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(file, "Take Care (feat. Rihanna)", requirePhrase: true));
    }

    [Theory]
    [InlineData("Too Close (Radio Edit)", "Too Close")]
    [InlineData("Too Close - Radio Edit", "Too Close")]
    [InlineData("Teardrop - Remastered 2011", "Teardrop")]
    [InlineData("Take Care (feat. Rihanna)", "Take Care")]
    [InlineData("Blue (Da Ba Dee)", "Blue (Da Ba Dee)")]
    [InlineData("Song (Pt. 2)", "Song (Pt. 2)")]
    [InlineData("(Exchange)", "(Exchange)")]
    public void TheNameTitleDropsOnlyGuestsAndVersionTags(string title, string expected) =>
        Assert.Equal(expected, SoulseekDownloadService.NameTitle(title));

    [Fact]
    public void ARadioEditRequestHearsAPlainlyNamedSingle() =>
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"music\Next\Too Close (CDS) (1998)\01 - Too Close.flac", "Too Close (Radio Edit)"));

    // ---- Wanting the song never takes another version of it ------------------------------------

    [Theory]
    [InlineData(@"music\Ministry Of Sound - One R&B [UK] [3CD]\CD3\09 - Next - Too Close [Radio Edit].flac")]
    [InlineData(@"music\Next\Singles\Next - Too Close - Radio Edit.flac")]
    [InlineData(@"music\Next\Singles\09 Next-Too Close-radio edit.flac")]
    [InlineData(@"music\Next\Singles\09_next_-_too_close_(radio_edit).flac")]
    [InlineData(@"music\Next\Rated Next\03 - Too Close (Clean).flac")]
    [InlineData(@"tiktok\Next - Too Close (Sped Up).flac")]
    [InlineData(@"Club\Next - Too Close (Extended Mix).flac")]
    [InlineData(@"music\Next\Singles\Too Close (Single Edit).flac")]
    [InlineData(@"music\Next\Singles\Too Close (Kenny Dope Remix).flac")]
    public void TheSongItselfNeverTakesAnEditOrARemix(string file) =>
        Assert.True(SoulseekDownloadService.AddsVersion(file, "Too Close"));

    [Theory]
    [InlineData(@"music\Next\Rated Next (1997)\03 - Too Close.flac")]
    [InlineData(@"music\Next\Rated Next (1997)\03 - Too Close (Album Version).flac")]
    [InlineData(@"music\Next\Rated Next (1997)\03 - Too Close (Explicit).flac")]
    [InlineData(@"music\Next\Rated Next (Remastered 2017)\03 - Too Close (Remastered).flac")]
    public void TheSameRecordingUnderAnotherLabelIsFine(string file)
    {
        Assert.False(SoulseekDownloadService.AddsVersion(file, "Too Close"));
        Assert.False(SoulseekDownloadService.FromVersionFolder(file, "Too Close", "Rated Next", "Next"));
    }

    [Theory]
    [InlineData(@"music\Next\Too Close (Radio Edit) - Single\01 - Too Close.flac")]
    [InlineData(@"Share\VA - Club Hits 1998 (Extended Mixes)\CD2\04 - Next - Too Close.flac")]
    [InlineData(@"Share\Next - Too Close (The Remixes)\01 - Too Close.flac")]
    [InlineData(@"Share\Next - Rated Next [Clean]\03 - Too Close.flac")]
    [InlineData(@"Share\Next - Rated Next (Instrumentals)\03 - Too Close.flac")]
    [InlineData(@"Share\Ministry of Sound - The Annual 1999 (DJ Mix)\CD1\05 - Next - Too Close.flac")]
    public void AFolderThatNamesAVersionMakesItsPlainFilesThatVersion(string file) =>
        Assert.True(SoulseekDownloadService.FromVersionFolder(file, "Too Close", "Rated Next", "Next"));

    [Fact]
    public void ARemixAlbumsFolderIsReadLikeALiveOne() =>
        // The real folder that pulled four Mezzanine tracks off a Mad Professor remix album.
        Assert.True(SoulseekDownloadService.FromVersionFolder(
            @"Music\Massive Attack\Massive Attack V Mad Professor Part II (Mezzanine Remix Tapes '98)\03 - Angel.flac",
            "Angel", "Mezzanine", "Massive Attack"));

    [Theory]
    [InlineData(@"Music\Drake\Take Care (Deluxe Edition) [2011] [FLAC 16-44]\04 - Take Care.flac")]
    [InlineData(@"Music\Drake\Take Care (Extended Edition)\04 - Take Care.flac")]
    [InlineData(@"Music\Clean Bandit\New Eyes (2014)\04 - Take Care.flac")]
    [InlineData(@"Music\Reprise Records\Take Care\04 - Take Care.flac")]
    [InlineData(@"Music\Drake\Take Care (Japanese Version)\04 - Take Care.flac")]
    public void AnEditionOrANameIsNotAVersion(string file) =>
        Assert.False(SoulseekDownloadService.FromVersionFolder(file, "Take Care", "Take Care", "Drake"));

    [Fact]
    public void ABandCalledLiveIsNotALiveTake()
    {
        const string file = @"Music\Live\Throwing Copper (1994)\05 - Lightning Crashes.flac";
        Assert.False(SoulseekDownloadService.FromVersionFolder(file, "Lightning Crashes", "Throwing Copper", "Live"));
        // Their concert record still reads as one by its other words; AcoustID's live check backs it up.
        Assert.True(SoulseekDownloadService.FromLiveFolder(@"Music\Live\Live - Paradiso (In Concert 1995)\05 - Lightning Crashes.flac",
            "Lightning Crashes", "Throwing Copper", "Live"));
    }

    [Fact]
    public void ARequestOnTheVersionsAlbumTakesItsFolder() =>
        Assert.False(SoulseekDownloadService.FromVersionFolder(@"Share\Next - Too Close (The Remixes)\01 - Too Close.flac",
            "Too Close", "Too Close (The Remixes)", "Next"));

    // ---- Wanting a version gets that version ----------------------------------------------------

    private static SoulseekFileHit Hit(string user, string file, int seconds, string ext = "flac", int queue = 0) => new()
    {
        Username = user, Filename = file, Size = 30_000_000, Length = seconds, Extension = ext, QueueLength = queue,
        UploadSpeed = 1_000_000,
    };

    private static List<SoulseekFileHit> Rank(IEnumerable<SoulseekFileHit> hits, string title, int? seconds,
        string? album = null, string? artist = null) =>
        SoulseekDownloadService.Rank(hits, new SoulseekDownloadService.CandidateWant(title, seconds, Album: album, Artist: artist),
            "flac", 0, _ => true);

    [Fact]
    public void ARadioEditRequestTakesTheNamedEditFirst_AndAPlainCopyOfItsLength()
    {
        var ranked = Rank(
        [
            // The plain one first in every other way: no queue.
            Hit("plain", @"music\Next\Too Close (CDS)\01 - Too Close.flac", 226),
            Hit("named", @"music\VA - One R&B\CD3\09 - Next - Too Close [Radio Edit].flac", 227, queue: 5),
            Hit("album", @"music\Next\Rated Next (1997)\03 - Too Close.flac", 261),
            Hit("clean", @"music\Next\Singles\Too Close (Radio Edit) (Clean).flac", 226),
        ], "Too Close (Radio Edit)", 226, artist: "Next");

        Assert.Equal(["named", "plain"], ranked.Select(h => h.Username));
    }

    [Fact]
    public void TheSongItselfNeverRanksAnEdit()
    {
        var ranked = Rank(
        [
            Hit("edit", @"music\VA - One R&B\CD3\09 - Next - Too Close [Radio Edit].flac", 262),
            Hit("single", @"music\Next\Too Close (Radio Edit) - Single\01 - Too Close.flac", 262),
            Hit("album", @"music\Next\Rated Next (1997)\03 - Too Close.flac", 261, queue: 9),
        ], "Too Close", 261, "Rated Next", "Next");

        Assert.Equal("album", Assert.Single(ranked).Username);
    }

    [Theory]
    [InlineData("Too Close (Kenny Dope Remix)", @"music\Next\Rated Next (1997)\03 - Too Close.flac")]
    [InlineData("Heat Waves (Sped Up)", @"music\Glass Animals\Dreamland\08 - Heat Waves.flac")]
    [InlineData("Creep (Acoustic)", @"music\Radiohead\Pablo Honey\02 - Creep.flac")]
    [InlineData("Love Story (Taylor's Version)", @"music\Taylor Swift\Fearless (2008)\03 - Love Story.flac")]
    [InlineData("Smile in Your Sleep (Live)", @"Music\Silverstein\Discovering the Waterfront (2005)\05 - Smile in Your Sleep.flac")]
    public void AVersionThatIsNotACutIsNeverAnsweredByThePlainSong(string title, string file) =>
        Assert.True(VersionVariant.LacksRequested(file, title));

    [Theory]
    [InlineData("Too Close (Kenny Dope Remix)", @"Share\Next - Too Close (The Remixes)\02 - Too Close (Kenny Dope Remix).flac")]
    [InlineData("Heat Waves (Sped Up)", @"tiktok\Glass Animals - Heat Waves (Sped Up).flac")]
    [InlineData("Love Story (Taylor's Version)", @"music\Taylor Swift\Fearless (Taylor's Version)\03 - Love Story (Taylor's Version).flac")]
    [InlineData("Smile in Your Sleep (Live)", @"Music\Silverstein\Decade (live at the El Mocambo) (2010)\17 - Smile in Your Sleep.flac")]
    [InlineData("Too Close (Radio Edit)", @"music\Next\Too Close (CDS)\01 - Too Close.flac")]
    public void TheVersionAskedForPassesWhereverItIsNamed(string title, string file)
    {
        Assert.False(VersionVariant.LacksRequested(file, title));
        Assert.False(SoulseekDownloadService.AddsVersion(file, title));
    }

    // ---- AcoustID says which recording arrived ---------------------------------------------------

    private static AcoustIdLookup Lookup(string title) =>
        new(true, null, [new AcoustIdResult(0.98, [new AcoustIdRecording("rec", title, ["Next"], "Rated Next", 1997)])]);

    [Fact]
    public void TheSongItselfRefusesARecordingMusicBrainzCallsTheRadioEdit() =>
        Assert.Equal(VerificationVerdict.Mismatch, DownloadVerificationService.Decide(Lookup("Too Close (radio edit)"),
            "Next", "Too Close", 0.85, tagsAuthoritative: true).Verdict);

    [Fact]
    public void ARadioEditRequestKeepsTheRecordingMusicBrainzListsPlainly() =>
        Assert.Equal(VerificationVerdict.Confirmed, DownloadVerificationService.Decide(Lookup("Too Close"),
            "Next", "Too Close (Radio Edit)", 0.85, tagsAuthoritative: true).Verdict);

    // ---- The Take Care canary ------------------------------------------------------------------

    [Fact]
    public void TheTakeCareSearchFindsThePlainFlacAmongItsMp3s()
    {
        // 'Drake Take Care' answers as the canary saw them: a wall of MP3s, the FLACs named plainly.
        var hits = Enumerable.Range(1, 40)
            .Select(i => Hit($"mp3peer{i}", $@"Music\Drake - Take Care\04 - Take Care (feat. Rihanna).mp3", 277, "mp3"))
            .Append(Hit("flacpeer", @"Music\Drake\Take Care (Deluxe) (2011)\04 - Take Care.flac", 277, queue: 3))
            .ToList();

        var ranked = Rank(hits, "Take Care (feat. Rihanna)", 277, "Take Care", "Drake");

        Assert.Equal("flacpeer", Assert.Single(ranked).Username);
    }
}
