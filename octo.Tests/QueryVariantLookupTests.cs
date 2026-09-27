using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Octo.Models.Domain;
using Octo.Models.Search;
using Octo.Models.Settings;
using Octo.Models.Subsonic;
using Octo.Services.LastFm;
using Octo.Services.Lyrics;
using Octo.Services.Metadata;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// A source that indexes "Suicideboys" still finds a "$uicideboy$" song: each lookup tries the
/// song as asked, then the other ways SongIdentity writes it, and holds whatever it finds to
/// the song as asked. And search merging keeps an owned song from being listed twice.
/// </summary>
public class QueryVariantLookupTests
{
    /// <summary>Answers by the first route whose needle the unescaped URL contains (or ends
    /// with, for a needle ending in $); anything else is a 404. Every URL asked for is kept,
    /// unescaped.</summary>
    private static (IHttpClientFactory Factory, List<string> Calls) Http(params (string Needle, string Body)[] routes)
    {
        var calls = new List<string>();
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
                calls.Add(url);
                foreach (var (needle, body) in routes)
                    if (needle.EndsWith('$') ? url.EndsWith(needle[..^1], StringComparison.Ordinal) : url.Contains(needle, StringComparison.Ordinal))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler.Object));
        return (factory.Object, calls);
    }

    private static readonly LyricsQuery Suicide = new("$uicideboy$", "$UICIDE", null, 170);

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task KuGou_FindsTheSongUnderItsSpelledOutName()
    {
        var (factory, calls) = Http(
            ("keyword=suicideboys - SUICIDE", """{"status":200,"candidates":[{"id":"5","accesskey":"K5","song":"Suicide","singer":"Suicideboys","duration":170000}]}"""),
            ("keyword=", """{"status":200,"candidates":[]}"""),
            ("fmt=krc", """{"status":404,"content":""}"""),
            ("fmt=lrc", $$"""{"status":200,"content":"{{Base64("[00:01.00]Line one")}}"}"""));
        var source = new KugouLyricsSource(factory, NullLogger<KugouLyricsSource>.Instance);

        var lookup = await source.FindAsync(Suicide, CancellationToken.None);

        Assert.Equal("kugou:5.K5", lookup.Result?.CandidateId);
        Assert.Contains(calls, call => call.Contains("keyword=$uicideboy$ - $UICIDE"));
        Assert.DoesNotContain(calls, call => call.Contains("mobileservice"));
    }

    [Fact]
    public async Task KuGou_AVariantThatFindsOnlyAnotherVersion_IsStillAMiss()
    {
        var (factory, calls) = Http(
            ("keyword=suicideboys - SUICIDE", """{"status":200,"candidates":[{"id":"5","accesskey":"K5","song":"Suicide (Live)","singer":"Suicideboys","duration":170000}]}"""),
            ("keyword=", """{"status":200,"candidates":[]}"""),
            ("mobileservice", """{"status":1,"data":{"info":[]}}"""));
        var source = new KugouLyricsSource(factory, NullLogger<KugouLyricsSource>.Instance);

        var lookup = await source.FindAsync(Suicide, CancellationToken.None);

        Assert.Null(lookup.Result);
        Assert.DoesNotContain(calls, call => call.Contains("/download"));
    }

    [Fact]
    public async Task Lrclib_SearchesAgainUnderTheSpelledOutName()
    {
        var (factory, calls) = Http(
            ("track_name=SUICIDE&artist_name=suicideboys",
                """[{"id":42,"trackName":"Suicide","artistName":"Suicideboys","duration":170.0,"syncedLyrics":"[00:01.00]Line one"}]"""),
            ("/api/search", "[]"));
        var source = new LrclibLyricsSource(factory, NullLogger<LrclibLyricsSource>.Instance);

        var lookup = await source.FindAsync(Suicide, CancellationToken.None);

        Assert.Equal("lrclib:42", lookup.Result?.CandidateId);
        Assert.Equal(2, calls.Count(call => call.Contains("/api/search")));
    }

    [Fact]
    public async Task Lrclib_APlainSong_CostsNoExtraSearch()
    {
        var (factory, calls) = Http(("/api/search", "[]"));
        var source = new LrclibLyricsSource(factory, NullLogger<LrclibLyricsSource>.Instance);

        var lookup = await source.FindAsync(new LyricsQuery("Drake", "Landed", null, 200), CancellationToken.None);

        Assert.Null(lookup.Result);
        Assert.Single(calls, call => call.Contains("/api/search"));
    }

    [Fact]
    public async Task NetEase_SearchesAgainUntilTheSongIsThere()
    {
        var (factory, calls) = Http(
            ("s=suicideboys SUICIDE", """{"result":{"songs":[{"id":7,"name":"Suicide","artists":[{"name":"Suicideboys"}],"duration":170000}]}}"""),
            ("/api/search/get", """{"result":{"songs":[{"id":8,"name":"Ultimate $uicide","artists":[{"name":"$uicideboy$"}],"duration":170000}]}}"""),
            ("/api/song/lyric", """{"lrc":{"lyric":"[00:01.00]Line one"}}"""));
        var source = new NeteaseLyricsSource(factory, NullLogger<NeteaseLyricsSource>.Instance);

        var lookup = await source.FindAsync(Suicide, CancellationToken.None);

        Assert.Equal("netease:7", lookup.Result?.CandidateId);
        Assert.Equal(2, calls.Count(call => call.Contains("/api/search/get")));
    }

    [Fact]
    public async Task LyricsOvh_TriesTheSpelledOutName()
    {
        var (factory, _) = Http(("/v1/suicideboys/SUICIDE", """{"lyrics":"Line one"}"""));
        var source = new LyricsOvhLyricsSource(factory, NullLogger<LyricsOvhLyricsSource>.Instance);

        var lookup = await source.FindAsync(Suicide, CancellationToken.None);

        Assert.Equal("Line one", lookup.Result?.Plain);
    }

    [Fact]
    public async Task LyricsOvh_NeverAsksForTheOriginalOfAVersion()
    {
        // lyrics.ovh names nothing back, so its answer to "Creep" could not be told from the
        // live take's: a title naming a version is asked for as it is, once.
        var (factory, calls) = Http(("/v1/Radiohead/Creep$", """{"lyrics":"studio words"}"""));
        var source = new LyricsOvhLyricsSource(factory, NullLogger<LyricsOvhLyricsSource>.Instance);

        var lookup = await source.FindAsync(new LyricsQuery("Radiohead", "Creep (Live)", null, 240), CancellationToken.None);

        Assert.Null(lookup.Result);
        Assert.Single(calls);
    }

    [Fact]
    public async Task Deezer_FindsTheTrackUnderItsSpelledOutName()
    {
        var (factory, calls) = Http(
            ("search?q=suicideboys SUICIDE", """{"data":[{"id":1,"title":"Suicide","duration":171,"artist":{"name":"Suicideboys"},"album":{"id":0,"title":"Dirtiest"}}]}"""),
            ("/search?q=", """{"data":[]}"""));
        var deezer = new DeezerMetadataService(factory, TestOptions.Monitor(new MetadataSettings()),
            new Mock<ILogger<DeezerMetadataService>>().Object);

        var meta = await deezer.EnrichTrackAsync("$uicideboy$", "$UICIDE", includeYear: false);

        Assert.Equal(171, meta?.Duration);
        Assert.Equal("Dirtiest", meta?.AlbumTitle);
        Assert.Equal(2, calls.Count(call => call.Contains("/search?q=")));
    }

    [Fact]
    public async Task Deezer_NeverTakesAnotherVersionsAlbumAndLength()
    {
        var (factory, _) = Http(
            ("/search?q=", """{"data":[{"id":1,"title":"Creep (Live)","duration":260,"artist":{"name":"Radiohead"},"album":{"id":0,"title":"Live Album"}}]}"""));
        var deezer = new DeezerMetadataService(factory, TestOptions.Monitor(new MetadataSettings()),
            new Mock<ILogger<DeezerMetadataService>>().Object);

        Assert.Null(await deezer.EnrichTrackAsync("Radiohead", "Creep", includeYear: false));
    }

    [Fact]
    public async Task LastFm_AsksForTheSongWrittenPlainlyBeforeGivingUpOnIt()
    {
        var calls = new List<string>();
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
                calls.Add(url);
                var body = url.Contains("artist=Drake&track=Too Good&")
                    ? """{"similartracks":{"track":[{"name":"Controlla","artist":{"name":"Drake"},"match":0.9}]}}"""
                    : """{"similartracks":{"track":[]}}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            });
        var service = new LastFmService(new HttpClient(handler.Object),
            TestOptions.Monitor(new LastFmSettings { ApiKey = "key" }),
            Options.Create(new MetadataSettings()),
            new Mock<ILogger<LastFmService>>().Object);

        var similar = await service.GetSimilarTracksAsync("Drake feat. Rihanna", "Too Good (feat. Rihanna)");

        Assert.Equal("Controlla", Assert.Single(similar).Title);
        Assert.DoesNotContain(calls, call => call.Contains("getsimilarartists") || call.Contains("artist.getsimilar"));
    }

    [Fact]
    public void SearchMerge_AnOwnedSongIsNotListedAgain_ButAnotherVersionIs()
    {
        var builder = new SubsonicResponseBuilder(new Octo.Services.Soulseek.ExternalIdRegistry(),
            Options.Create(new SubsonicSettings()));
        var mapper = new SubsonicModelMapper(builder, new Mock<ILogger<SubsonicModelMapper>>().Object);
        var local = new List<object>
        {
            new Dictionary<string, object> { ["id"] = "l1", ["title"] = "Too Good", ["artist"] = "Drake" },
        };
        var external = new SearchResult
        {
            Songs =
            [
                new Song { Id = "e1", Title = "Too Good (feat. Rihanna)", Artist = "Drake" },
                new Song { Id = "e2", Title = "Too Good (Live)", Artist = "Drake" },
            ],
            Albums = [],
            Artists = [new Artist { Id = "a1", Name = "Beyonce" }],
        };
        var localArtists = new List<object> { new Dictionary<string, object> { ["id"] = "la", ["name"] = "Beyoncé" } };

        var (songs, _, artists) = mapper.MergeSearchResults(local, [], localArtists, external, [], true);

        Assert.Equal(2, songs.Count);
        Assert.Single(artists);
    }
}
