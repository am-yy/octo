using Octo.Controllers;
using Xunit;

namespace Octo.Tests;

/// <summary>
/// The Octo app draws its own "not in your library" marks, so only its covers come back
/// without the badge; every other client keeps it.
/// </summary>
public class CleanCoverClientTests
{
    [Theory]
    [InlineData("Octo", true)]
    [InlineData("octo", true)]
    [InlineData(" Octo ", true)]
    [InlineData("Symfonium", false)]
    [InlineData("Feishin", false)]
    [InlineData("OctoPlayer", false)]
    [InlineData("", false)]
    public void OnlyTheOctoAppGetsPlainCovers(string client, bool plain)
    {
        var parameters = new Dictionary<string, string> { ["id"] = "abc", ["c"] = client };
        Assert.Equal(plain, SubsonicController.DrawsItsOwnMarks(parameters));
    }

    [Fact]
    public void ARequestWithoutAClientNameKeepsTheBadge()
    {
        Assert.False(SubsonicController.DrawsItsOwnMarks(new Dictionary<string, string> { ["id"] = "abc" }));
    }
}
