using Octo.Services.Common;

namespace Octo.Tests;

/// <summary>
/// BuildTrackPath is what the Organized folder layout is built on, so an album lands as
/// one folder on disk instead of a folder per track.
/// </summary>
public class PathHelperTests
{
    // Relative so the test reads the same on Windows and on the Linux CI runner.
    private const string Root = "musicroot";

    [Fact]
    public void BuildTrackPath_GroupsByAlbumAndPrefixesTrackNumber()
    {
        // Act
        var path = PathHelper.BuildTrackPath(Root, "Boards Of Canada",
            "In A Beautiful Place Out In The Country", "Kid For Today", 1, ".mp3");

        // Assert
        var expected = Path.Combine(Root, "Boards Of Canada",
            "In A Beautiful Place Out In The Country", "01 - Kid For Today.mp3");
        Assert.Equal(expected, path);
    }

    [Fact]
    public void BuildTrackPath_PadsTrackNumberToTwoDigits()
    {
        var path = PathHelper.BuildTrackPath(Root, "A", "B", "C", 7, ".flac");
        Assert.EndsWith("07 - C.flac", path);
    }

    [Fact]
    public void BuildTrackPath_DoubleDigitTrackIsNotPaddedFurther()
    {
        var path = PathHelper.BuildTrackPath(Root, "A", "B", "C", 12, ".flac");
        Assert.EndsWith("12 - C.flac", path);
    }

    [Fact]
    public void BuildTrackPath_NoTrackNumber_OmitsPrefix()
    {
        // A standalone single has no position; it must not get a "00 - " prefix.
        var path = PathHelper.BuildTrackPath(Root, "A", "B", "Song", null, ".mp3");
        Assert.EndsWith("Song.mp3", path);
        Assert.DoesNotContain(" - Song.mp3", path);
    }

    [Fact]
    public void BuildTrackPath_EmptyExtension_ProducesNoTrailingDot()
    {
        // The yt-dlp shim appends .mp3 itself, so it is handed a path with no extension.
        var path = PathHelper.BuildTrackPath(Root, "A", "B", "Song", 3, "");
        Assert.EndsWith("03 - Song", path);
    }

    [Theory]
    [InlineData("AC/DC")]
    [InlineData("Sigur Rós: Ágætis")]
    [InlineData("What?<>|")]
    public void BuildTrackPath_SanitizesPathHostileNames(string nasty)
    {
        var path = PathHelper.BuildTrackPath(Root, nasty, "Album", "Title", 1, ".mp3");

        // Assert the invariant, not a literal: which characters are illegal differs by
        // platform (Windows rejects : ? < > |, Linux only / and NUL), so hardcoding the
        // sanitized spelling passes locally and fails on a Linux runner.
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // An artist containing a slash must not silently become an extra directory level.
        Assert.Equal(4, segments.Length);
        Assert.Equal(Root, segments[0]);
        Assert.Equal("Album", segments[2]);
        Assert.Equal("01 - Title.mp3", segments[3]);

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            Assert.False(segments[1].Contains(c), $"artist segment kept illegal char {(int)c}");
        }
    }

    [Fact]
    public void BuildTrackPath_AccentedCharactersSurvive()
    {
        // Sanitizing must not mangle legitimate non-ASCII names.
        var path = PathHelper.BuildTrackPath(Root, "Sigur Rós", "Ágætis Byrjun", "Svefn-g-englar", 2, ".mp3");

        Assert.Contains("Sigur Rós", path);
        Assert.Contains("Ágætis Byrjun", path);
        Assert.EndsWith("02 - Svefn-g-englar.mp3", path);
    }

    [Fact]
    public void BuildTrackPath_BlankAlbumOrArtist_FallsBackToUnknown()
    {
        var path = PathHelper.BuildTrackPath(Root, "", "   ", "Song", null, ".mp3");
        Assert.Contains("Unknown", path);
    }

    [Fact]
    public void SanitizeFolderName_TrimsTrailingDots()
    {
        // Windows silently drops trailing dots on folder names.
        Assert.Equal("Album", PathHelper.SanitizeFolderName("Album..."));
    }

    /// <summary>
    /// Every one of these names a different recording. Naming used to strip all brackets, so a
    /// live take and the studio cut shared one path and the second download deleted the first.
    /// </summary>
    [Theory]
    [InlineData("Song (Live)", "Song (Live)")]
    [InlineData("Song [Remix]", "Song [Remix]")]
    [InlineData("Song (feat. Guest)", "Song (feat. Guest)")]
    [InlineData("Song (Acoustic Version)", "Song (Acoustic Version)")]
    public void FileTitle_KeepsWhatNamesADifferentRecording(string title, string expected)
        => Assert.Equal(expected, PathHelper.FileTitle(title, "Artist"));

    [Theory]
    [InlineData("Song (Official Video)", "Song")]
    [InlineData("Song (Official Music Video) [HD]", "Song")]
    [InlineData("Song [Official Audio]", "Song")]
    [InlineData("Song (Lyric Video)", "Song")]
    [InlineData("Song (Live) (Official Video)", "Song (Live)")]
    public void FileTitle_DropsOnlyUploadNoise(string title, string expected)
        => Assert.Equal(expected, PathHelper.FileTitle(title, "Artist"));

    [Fact]
    public void FileTitle_DropsARedundantArtistPrefix()
        => Assert.Equal("Teardrop", PathHelper.FileTitle("Massive Attack - Teardrop", "Massive Attack"));

    /// <summary>Mezzanine's "(Exchange)" is the whole title, not an annotation.</summary>
    [Theory]
    [InlineData("(Exchange)")]
    [InlineData("(Official Video)")]
    public void FileTitle_TitleThatIsOnlyABracket_IsKept(string title)
        => Assert.Equal(title, PathHelper.FileTitle(title, "Massive Attack"));
}
