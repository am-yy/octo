using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.CoverArt;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// Covers for songs outside the library come from the catalog at full size. The lookup used
/// field-qualified searches the catalog no longer answers, so every one came back empty and a
/// smaller source served a soft 600 pixel cover instead.
/// </summary>
public sealed class CoverLookupTests
{
    private sealed class FakeCatalog : HttpMessageHandler
    {
        public readonly List<Uri> Asked = [];
        public Func<Uri, string?> Json = _ => null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            lock (Asked) Asked.Add(uri);
            if (uri.Host == "cdn.test")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes(uri.AbsolutePath)) });
            var body = Json(uri);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static DeezerCoverArtLookup Lookup(FakeCatalog catalog) =>
        new(new ReviewFixtures.OneClientFactory(catalog), Options.Create(new MetadataSettings()), NullLogger<DeezerCoverArtLookup>.Instance);

    [Fact]
    public async Task AnAlbumWithAKnownIdIsFetchedByThatId()
    {
        var catalog = new FakeCatalog
        {
            Json = uri => uri.AbsolutePath == "/album/302127"
                ? """{"id":302127,"title":"Discovery","cover_xl":"https://cdn.test/discovery-1000.jpg","cover_big":"https://cdn.test/discovery-500.jpg"}"""
                : null,
        };

        var bytes = await Lookup(catalog).TryFetchAsync(new SoulseekRouting
        {
            Kind = RoutingKind.Album, Artist = "Daft Punk", Album = "Discovery", ExternalAlbumId = "302127",
        });

        Assert.Equal("/discovery-1000.jpg", Encoding.ASCII.GetString(bytes!));
        Assert.DoesNotContain(catalog.Asked, u => u.AbsolutePath.StartsWith("/search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASearchIsPlainAndTakesTheAskedForTitle()
    {
        var catalog = new FakeCatalog
        {
            // The same artist's other record first, the asked-for one second.
            Json = uri => uri.AbsolutePath == "/search/album"
                ? """
                  {"data":[
                    {"title":"Homework","artist":{"name":"Daft Punk"},"cover_xl":"https://cdn.test/homework.jpg"},
                    {"title":"Random Access Memories (Drumless Edition)","artist":{"name":"Daft Punk"},"cover_xl":"https://cdn.test/ram-drumless.jpg"}
                  ]}
                  """
                : null,
        };

        var bytes = await Lookup(catalog).TryFetchAsync(new SoulseekRouting
        {
            Kind = RoutingKind.Album, Artist = "Daft Punk", Album = "Random Access Memories (Drumless Edition)",
        });

        Assert.Equal("/ram-drumless.jpg", Encoding.ASCII.GetString(bytes!));
        var search = Assert.Single(catalog.Asked, u => u.AbsolutePath == "/search/album");
        var q = System.Web.HttpUtility.ParseQueryString(search.Query)["q"]!;
        Assert.DoesNotContain("artist:", q);
        Assert.DoesNotContain("album:", q);
        Assert.Contains("Daft Punk", q);
    }

    [Fact]
    public async Task ASongsCoverComesFromAPlainTrackSearch()
    {
        var catalog = new FakeCatalog
        {
            Json = uri => uri.AbsolutePath == "/search"
                ? """
                  {"data":[
                    {"title":"Something Else","artist":{"name":"Air"},"album":{"title":"Talkie Walkie","cover_xl":"https://cdn.test/other.jpg"}},
                    {"title":"Sexy Boy","artist":{"name":"Air"},"album":{"title":"Moon Safari","cover_xl":"https://cdn.test/moon-safari.jpg"}}
                  ]}
                  """
                : null,
        };

        var bytes = await Lookup(catalog).TryFetchAsync(new SoulseekRouting { Kind = RoutingKind.Song, Artist = "Air", Title = "Sexy Boy" });

        Assert.Equal("/moon-safari.jpg", Encoding.ASCII.GetString(bytes!));
        var q = System.Web.HttpUtility.ParseQueryString(Assert.Single(catalog.Asked, u => u.AbsolutePath == "/search").Query)["q"]!;
        Assert.DoesNotContain("track:", q);
    }
}
