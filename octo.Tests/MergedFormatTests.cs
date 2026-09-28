using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// getAlbum and getArtist merge the outside songs and albums into Navidrome's answer. The merge
/// reads JSON, and an XML client used to get Navidrome's own XML back, without the outside part:
/// half an album. Both formats must now carry the same songs.
/// </summary>
public sealed class MergedFormatTests
{
    private static readonly XNamespace Ns = "http://subsonic.org/restapi";

    /// <summary>Navidrome holding two of an album's four tracks, and Deezer knowing all four.</summary>
    private sealed class FakeServers : HttpMessageHandler
    {
        public readonly List<string> NavidromeFormats = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

            if (uri.Host == "api.deezer.com")
            {
                if (path.StartsWith("/search/album", StringComparison.Ordinal))
                    return Json("""{"data":[{"id":1,"title":"Test Album","record_type":"album","nb_tracks":4,"artist":{"name":"Test Artist"}}]}""");
                if (path == "/album/1/tracks")
                    return Json("""
                        {"total":4,"data":[
                          {"title":"One","duration":100,"track_position":1,"disk_number":1,"isrc":"GBAAA0000001","artist":{"name":"Test Artist"}},
                          {"title":"Two","duration":200,"track_position":2,"disk_number":1,"isrc":"GBAAA0000002","artist":{"name":"Test Artist"}},
                          {"title":"Three","duration":300,"track_position":3,"disk_number":1,"isrc":"GBAAA0000003","artist":{"name":"Test Artist"}},
                          {"title":"Four","duration":400,"track_position":4,"disk_number":1,"isrc":"GBAAA0000004","artist":{"name":"Test Artist"}}
                        ]}
                        """);
                if (path == "/album/1")
                    return Json("""{"id":1,"title":"Test Album","release_date":"2001-01-01","artist":{"name":"Test Artist"}}""");
                if (path.StartsWith("/search/artist", StringComparison.Ordinal))
                    return Json("""{"data":[{"id":7,"name":"Test Artist"}]}""");
                if (path.StartsWith("/artist/7/albums", StringComparison.Ordinal))
                    return Json("""{"data":[{"id":1,"title":"Test Album","record_type":"album","nb_tracks":4},{"id":2,"title":"Other Album","record_type":"album","nb_tracks":9}]}""");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var json = query["f"] == "json";
            if (path.EndsWith("/rest/getAlbum", StringComparison.Ordinal) || path.EndsWith("/rest/getArtist", StringComparison.Ordinal))
                lock (NavidromeFormats) NavidromeFormats.Add(query["f"] ?? "xml");

            if (path.EndsWith("/rest/getAlbum", StringComparison.Ordinal))
            {
                return json
                    ? Json("""
                        {"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome","album":{
                          "id":"al-1","name":"Test Album","artist":"Test Artist","songCount":2,"duration":400,
                          "genres":[{"name":"Rock"}],
                          "song":[
                            {"id":"s-1","title":"One","album":"Test Album","artist":"Test Artist","track":1,"duration":100,"isrc":["GBAAA0000001"],"replayGain":{"trackGain":-6.5}},
                            {"id":"s-3","title":"Three","album":"Test Album","artist":"Test Artist","track":3,"duration":300,"isrc":[]}
                          ]}}}
                        """)
                    : Xml("""<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1"><album id="al-1" name="Test Album" artist="Test Artist" songCount="2"><song id="s-1" title="One" track="1"/><song id="s-3" title="Three" track="3"/></album></subsonic-response>""");
            }
            if (path.EndsWith("/rest/getArtist", StringComparison.Ordinal))
            {
                return json
                    ? Json("""{"subsonic-response":{"status":"ok","version":"1.16.1","artist":{"id":"ar-1","name":"Test Artist","albumCount":1,"album":[{"id":"al-1","name":"Test Album","artist":"Test Artist","songCount":2}]}}}""")
                    : Xml("""<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1"><artist id="ar-1" name="Test Artist" albumCount="1"><album id="al-1" name="Test Album"/></artist></subsonic-response>""");
            }
            if (path.EndsWith("/rest/ping", StringComparison.Ordinal))
                return Json("""{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        private static Task<HttpResponseMessage> Xml(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") });
    }

    private sealed class WebFactory : WebApplicationFactory<Program>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "octo-merge-web-" + Guid.NewGuid());
        public FakeServers Servers { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_directory);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Subsonic:Url"] = "http://navidrome.test",
                    ["Subsonic:AutoDetectDownloadPath"] = "false",
                    ["Soulseek:BaseUrl"] = "http://127.0.0.1:1",
                    ["YouTube:ShimUrl"] = "http://127.0.0.1:1",
                    ["Library:DownloadPath"] = _directory,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new ReviewFixtures.OneClientFactory(Servers));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string Auth = "u=alice&t=good&s=salt&v=1.16.1&c=test";

    [Fact]
    public async Task GetAlbum_XmlCarriesTheSameMergedSongsAsJson()
    {
        await using var factory = new WebFactory();
        using var client = factory.CreateClient();

        using var json = JsonDocument.Parse(await client.GetStringAsync($"/rest/getAlbum.view?{Auth}&f=json&id=al-1"));
        var jsonSongs = json.RootElement.GetProperty("subsonic-response").GetProperty("album").GetProperty("song")
            .EnumerateArray().Select(s => s.GetProperty("title").GetString()).ToList();

        var response = await client.GetAsync($"/rest/getAlbum.view?{Auth}&id=al-1");
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var album = xml.Root!.Element(Ns + "album")!;
        var xmlSongs = album.Elements(Ns + "song").Select(s => (string?)s.Attribute("title")).ToList();

        Assert.Equal(["One", "Two", "Three", "Four"], jsonSongs);
        Assert.Equal(jsonSongs, xmlSongs);
        Assert.Equal("4", (string?)album.Attribute("songCount"));
        Assert.Equal("ok", (string?)xml.Root.Attribute("status"));
        // Navidrome was asked for JSON both times: the merge reads JSON.
        Assert.All(factory.Servers.NavidromeFormats, f => Assert.Equal("json", f));
    }

    [Fact]
    public async Task GetAlbum_XmlWritesListsAndObjectsTheOpenSubsonicWay()
    {
        await using var factory = new WebFactory();
        using var client = factory.CreateClient();

        var xml = XDocument.Parse(await client.GetStringAsync($"/rest/getAlbum.view?{Auth}&id=al-1"));
        var album = xml.Root!.Element(Ns + "album")!;
        var one = album.Elements(Ns + "song").First(s => (string?)s.Attribute("title") == "One");
        var two = album.Elements(Ns + "song").First(s => (string?)s.Attribute("title") == "Two");

        // A list of plain values: one text element each. An object: one child element.
        Assert.Equal(["GBAAA0000001"], one.Elements(Ns + "isrc").Select(e => e.Value));
        Assert.Equal("-6.5", (string?)one.Element(Ns + "replayGain")!.Attribute("trackGain"));
        // An outside song carries Deezer's code the same way.
        Assert.Equal(["GBAAA0000002"], two.Elements(Ns + "isrc").Select(e => e.Value));
        // A list of objects: one element each, named for the list.
        Assert.Equal("Rock", (string?)album.Element(Ns + "genres")!.Attribute("name"));
        // Plain values are attributes, never child elements.
        Assert.Equal("1", (string?)one.Attribute("track"));
        Assert.Null(one.Element(Ns + "track"));
    }

    [Fact]
    public async Task GetArtist_XmlCarriesTheSameAlbumsAsJson()
    {
        await using var factory = new WebFactory();
        using var client = factory.CreateClient();

        using var json = JsonDocument.Parse(await client.GetStringAsync($"/rest/getArtist.view?{Auth}&f=json&id=ar-1"));
        var jsonAlbums = json.RootElement.GetProperty("subsonic-response").GetProperty("artist").GetProperty("album")
            .EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();

        var response = await client.GetAsync($"/rest/getArtist.view?{Auth}&id=ar-1");
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var artist = xml.Root!.Element(Ns + "artist")!;
        var xmlAlbums = artist.Elements(Ns + "album").Select(a => (string?)a.Attribute("name")).ToList();

        Assert.NotEmpty(jsonAlbums);
        Assert.Equal(jsonAlbums, xmlAlbums);
        Assert.Equal("Test Artist", (string?)artist.Attribute("name"));
    }

    [Fact]
    public void JsonShapeToXml_LeavesOutMissingValuesAndKeepsNumbersInvariant()
    {
        var element = SubsonicResponseBuilder.JsonShapeToXml(Ns, "song", new Dictionary<string, object>
        {
            ["title"] = "A",
            ["comment"] = null!,
            ["bitRate"] = 320,
            ["averageRating"] = 4.5,
            ["starred"] = true,
            ["moods"] = new List<object>(),
        });

        Assert.Equal("A", (string?)element.Attribute("title"));
        Assert.Null(element.Attribute("comment"));
        Assert.Equal("4.5", (string?)element.Attribute("averageRating"));
        Assert.Equal("true", (string?)element.Attribute("starred"));
        Assert.Empty(element.Elements());
    }
}
