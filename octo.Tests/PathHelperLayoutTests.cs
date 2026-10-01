using Octo.Models.Settings;
using Octo.Services.Common;

namespace Octo.Tests;

public class PathHelperLayoutTests
{
    private const string Root = "/music";

    [Fact]
    public void Flat_PutsEverythingInOneDirectory()
    {
        var path = PathHelper.BuildLayoutPath(FolderStructure.Flat, Root,
            "Daft Punk", "Discovery", "Digital Love", 3, ".flac");

        Assert.Equal(Path.Combine(Root, "Daft Punk - Digital Love.flac"), path);
    }

    [Fact]
    public void ByArtist_UsesAnArtistFolderAndNoAlbumFolder()
    {
        // The point of the layout: a library built a track at a time gets one folder per
        // artist instead of one folder per single, or one directory of thousands of files.
        var path = PathHelper.BuildLayoutPath(FolderStructure.ByArtist, Root,
            "Daft Punk", "Discovery", "Digital Love", 3, ".flac");

        Assert.Equal(Path.Combine(Root, "Daft Punk", "Digital Love.flac"), path);
    }

    [Fact]
    public void Organized_KeepsTheAlbumFolderAndTrackNumber()
    {
        var path = PathHelper.BuildLayoutPath(FolderStructure.Organized, Root,
            "Daft Punk", "Discovery", "Digital Love", 3, ".flac");

        Assert.Equal(Path.Combine(Root, "Daft Punk", "Discovery", "03 - Digital Love.flac"), path);
    }

    [Fact]
    public void Organized_NoAlbum_FallsBackToTheTitleAsTheFolder()
    {
        // Reproduces the pre-album shape for a genuine single, and the rule lives in the
        // resolver so every caller gets it rather than each one remembering.
        var path = PathHelper.BuildLayoutPath(FolderStructure.Organized, Root,
            "Daft Punk", "", "Digital Love", null, ".flac");

        Assert.Equal(Path.Combine(Root, "Daft Punk", "Digital Love", "Digital Love.flac"), path);
    }

    [Fact]
    public void ByArtist_NoAlbum_IsUnaffected()
    {
        var path = PathHelper.BuildLayoutPath(FolderStructure.ByArtist, Root,
            "Daft Punk", "", "Digital Love", null, ".flac");

        Assert.Equal(Path.Combine(Root, "Daft Punk", "Digital Love.flac"), path);
    }

    [Fact]
    public void EmptyExtension_IsHonoured()
    {
        // Deezer staging leaves the extension to the selected media format.
        var path = PathHelper.BuildLayoutPath(FolderStructure.ByArtist, Root,
            "Daft Punk", "Discovery", "Digital Love", 3, "");

        Assert.Equal(Path.Combine(Root, "Daft Punk", "Digital Love"), path);
    }

    [Fact]
    public void UnknownLayout_ThrowsRatherThanSilentlyPickingOne()
    {
        // Three separate switches used to decide this and two had a silent default arm, so
        // a new layout would have filed into the old one. This must never be a default.
        Assert.Throws<ArgumentOutOfRangeException>(() => PathHelper.BuildLayoutPath(
            (FolderStructure)999, Root, "A", "B", "C", 1, ".flac"));
    }
}
