using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// A song Octo found outside the library is marked as such and carries no file facts. It used
/// to borrow a library song's shape (isExternal false, a path, a size, "now" as the date added),
/// so a library album with outside songs merged in read as if every song were on the server.
/// </summary>
public sealed class HonestOutsideSongsTests
{
    private static readonly XNamespace Ns = XNamespace.Get("http://subsonic.org/restapi");

    private static SubsonicResponseBuilder Builder(bool waitForLossless = false, string quality = "FLAC") =>
        new(new ExternalIdRegistry(), Options.Create(new SubsonicSettings { WaitForLosslessOnPlay = waitForLossless }),
            Options.Create(new DeezerSettings { CacheQuality = quality }));

    private static Song Outside() => new()
    {
        Id = "mXFKjv7oqx1HTOzoJP1nk3", Title = "One Woman (Album Version)", Artist = "Randy Rogers Band",
        Album = "Randy Rogers Band", Track = 4, Duration = 245, Year = 2008, Isrc = "USUM70813712",
        IsLocal = false,
    };

    private static readonly string[] FileFacts = ["path", "size", "created", "bitDepth", "samplingRate", "channelCount"];

    [Fact]
    public void OutsideSong_IsMarkedAndCarriesNoFileFacts()
    {
        var row = Builder().ConvertSongToJson(Outside());

        Assert.Equal(true, row["isExternal"]);
        Assert.All(FileFacts, key => Assert.False(row.ContainsKey(key), key));
        // Default playback source is selected-quality FLAC.
        Assert.Equal("flac", row["suffix"]);
        Assert.Equal("audio/flac", row["contentType"]);
        Assert.False(row.ContainsKey("bitRate"));
        Assert.Equal(245, row["duration"]);
        Assert.Equal(new[] { "USUM70813712" }, row["isrc"]);
    }

    [Fact]
    public void OutsideSong_IsMarkedTheSameWayInXml()
    {
        var xml = Builder().ConvertSongToXml(Outside(), Ns);

        Assert.Equal("true", xml.Attribute("isExternal")?.Value);
        Assert.All(FileFacts, key => Assert.Null(xml.Attribute(key)));
        Assert.Equal("flac", xml.Attribute("suffix")?.Value);
    }

    [Fact]
    public void OutsideSong_WaitingForLossless_ClaimsNoRate()
    {
        var row = Builder(waitForLossless: true).ConvertSongToJson(Outside());

        Assert.Equal(true, row["isExternal"]);
        Assert.Equal("flac", row["suffix"]);
        Assert.False(row.ContainsKey("bitRate"));
    }

    [Fact]
    public void OutsideSong_DeclaresSelectedMp3Source()
    {
        var row = Builder(quality: "MP3_320").ConvertSongToJson(Outside());

        Assert.Equal("mp3", row["suffix"]);
        Assert.Equal("audio/mpeg", row["contentType"]);
        Assert.Equal(320, row["bitRate"]);
    }

    [Fact]
    public void LibrarySong_KeepsItsFileFacts()
    {
        var song = Outside();
        song.IsLocal = true;
        song.Suffix = "flac";
        song.BitRate = 867;

        var row = Builder().ConvertSongToJson(song);

        Assert.Equal(false, row["isExternal"]);
        Assert.All(FileFacts, key => Assert.True(row.ContainsKey(key), key));
        Assert.Equal(867, row["bitRate"]);
    }

    [Fact]
    public void SyncedOutsideSong_StillCarriesItsCatalogDate()
    {
        var added = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var song = Outside();
        var catalog = new SyncCatalog([song], [], [], new Dictionary<string, DateTime> { [song.Id] = added }, "fp", added);
        var body = System.Text.Encoding.UTF8.GetBytes(
            """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1"><searchResult3/></subsonic-response>""");

        var output = SyncCatalogResponse.Append(body, "application/xml", "searchResult3", Builder(), catalog, [], [], [song]);

        var row = XDocument.Parse(System.Text.Encoding.UTF8.GetString(output)).Descendants(Ns + "song").Single();
        Assert.Equal("2026-09-01T12:00:00.000Z", row.Attribute("created")?.Value);
        Assert.Equal("true", row.Attribute("isExternal")?.Value);
    }
}
