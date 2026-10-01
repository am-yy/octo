using System.Collections;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// The XML and JSON serializers must describe the same thing. They were written out by
/// hand separately and drifted: a song carried nine attributes in XML against twenty-seven
/// fields in JSON, so an XML-only client (DSub is one, and it never requests JSON) received
/// external tracks with no suffix, contentType or bitRate — precisely the fields a client
/// reads to choose a decoder, and the ones this codebase already warns must match the bytes
/// that will actually arrive.
/// </summary>
public class SerializerSymmetryTests
{
    private static readonly XNamespace Ns = XNamespace.Get("http://subsonic.org/restapi");

    private static SubsonicResponseBuilder Builder(bool waitForLossless = false) =>
        new(new ExternalIdRegistry(),
            Options.Create(new SubsonicSettings { WaitForLosslessOnPlay = waitForLossless }));

    private static Song ExternalSong() => new()
    {
        Id = "abc123",
        Title = "Karma Police",
        Artist = "Radiohead",
        Album = "OK Computer",
        Duration = 261,
        Year = 1997,
        IsLocal = false,
    };

    /// <summary>Keys the XML form is expected to carry: everything except collections.</summary>
    private static IEnumerable<string> ScalarKeys(IDictionary<string, object> fields) =>
        fields.Where(kv => kv.Value is not null
                        && (kv.Value is string || kv.Value is not IEnumerable))
              .Select(kv => kv.Key)
              .OrderBy(k => k);

    [Fact]
    public void SongCarriesTheSameFieldsInBothFormats()
    {
        var b = Builder();
        var song = ExternalSong();

        var json = b.ConvertSongToJson(song);
        var xml = b.ConvertSongToXml(song, Ns);

        Assert.Equal(ScalarKeys(json), xml.Attributes().Select(a => a.Name.LocalName).OrderBy(n => n));
    }

    [Fact]
    public void AlbumAndArtistCarryTheSameFieldsInBothFormats()
    {
        var b = Builder();
        var album = new Album { Id = "al1", Title = "In Rainbows", Artist = "Radiohead", Year = 2007, SongCount = 10 };
        var artist = new Artist { Id = "ar1", Name = "Radiohead", AlbumCount = 2 };

        var albumJson = (IDictionary<string, object>)b.ConvertAlbumToJson(album);
        var artistJson = (IDictionary<string, object>)b.ConvertArtistToJson(artist);

        Assert.Equal(ScalarKeys(albumJson),
            b.ConvertAlbumToXml(album, Ns).Attributes().Select(a => a.Name.LocalName).OrderBy(n => n));
        Assert.Equal(ScalarKeys(artistJson),
            b.ConvertArtistToXml(artist, Ns).Attributes().Select(a => a.Name.LocalName).OrderBy(n => n));
    }

    [Fact]
    public void XmlSongDeclaresHowToPlayIt()
    {
        // The specific regression. Without these a client cannot choose a decoder, and the
        // entry either fails to play or is dropped from the queue outright.
        var xml = Builder().ConvertSongToXml(ExternalSong(), Ns);

        Assert.Equal("mp3", xml.Attribute("suffix")?.Value);
        Assert.Equal("audio/mpeg", xml.Attribute("contentType")?.Value);
        Assert.Equal("320", xml.Attribute("bitRate")?.Value);
    }

    [Fact]
    public void XmlSongFollowsTheLosslessSettingJustAsJsonDoes()
    {
        // The declaration has to track what /rest/stream will actually serve, in both
        // formats. Getting this wrong in one of them is the bug this file exists to stop.
        var xml = Builder(waitForLossless: true).ConvertSongToXml(ExternalSong(), Ns);

        Assert.Equal("flac", xml.Attribute("suffix")?.Value);
        Assert.Equal("audio/flac", xml.Attribute("contentType")?.Value);
    }

    [Fact]
    public void XmlAlbumLooksLikeADirectoryToAFolderBrowsingClient()
    {
        // search2 clients browse by folder and read these three. Injected albums used to
        // carry none of them, so they looked unlike anything the upstream server returns.
        var xml = Builder().ConvertAlbumToXml(
            new Album { Id = "al1", Title = "In Rainbows", Artist = "Radiohead" }, Ns);

        Assert.Equal("true", xml.Attribute("isDir")?.Value);
        Assert.Equal("In Rainbows", xml.Attribute("title")?.Value);
        Assert.False(string.IsNullOrEmpty(xml.Attribute("parent")?.Value));
    }

    [Fact]
    public void AnUnknownYearIsOmittedRatherThanInvented()
    {
        // It used to fall back to the current year, which is not a plausible default but a
        // wrong one: a 1995 track was published to the client as this year's release, and
        // that is something the user sees and sorts by.
        var b = Builder();
        var song = ExternalSong();
        song.Year = null;

        Assert.DoesNotContain("year", b.ConvertSongToJson(song).Keys);
        Assert.Null(b.ConvertSongToXml(song, Ns).Attribute("year"));

        // Deezer's album search payload carries no release date at all, so this is the
        // normal case for an injected album row rather than an edge case.
        var album = new Album { Id = "al1", Title = "In Rainbows", Artist = "Radiohead", Year = null };
        Assert.DoesNotContain("year", ((IDictionary<string, object>)b.ConvertAlbumToJson(album)).Keys);
        Assert.Null(b.ConvertAlbumToXml(album, Ns).Attribute("year"));
    }

    [Fact]
    public void AKnownYearIsStillReported()
    {
        var b = Builder();

        Assert.Equal(1997, b.ConvertSongToJson(ExternalSong())["year"]);
        Assert.Equal("2007", b.ConvertAlbumToXml(
            new Album { Id = "al1", Title = "In Rainbows", Artist = "Radiohead", Year = 2007 }, Ns)
            .Attribute("year")?.Value);
    }

    [Fact]
    public void ValuesRenderInvariantlyRegardlessOfLocale()
    {
        // A comma decimal separator under a European locale would produce numbers no
        // Subsonic client can parse. CI runs on Linux where the ambient culture differs
        // from the workstation's, so assert the rendering rather than trust the default.
        var xml = Builder().ConvertSongToXml(ExternalSong(), Ns);

        Assert.Equal("261", xml.Attribute("duration")?.Value);
        Assert.Equal("1997", xml.Attribute("year")?.Value);

        // Only a library song has a size.
        var library = ExternalSong();
        library.IsLocal = true;
        var size = Builder().ConvertSongToXml(library, Ns).Attribute("size")?.Value;
        Assert.NotNull(size);
        Assert.DoesNotContain(",", size);
    }
}
