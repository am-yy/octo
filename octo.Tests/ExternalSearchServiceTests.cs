using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.LastFm;

namespace Octo.Tests;

public class ExternalSearchServiceTests
{
    [Theory]
    [InlineData(50, 0)]
    [InlineData(5, 1)]
    public async Task PadsWithTopTracksOnlyWhenTrackSearchIsThin(int searchHits, int expectedTopTrackCalls)
    {
        var calls = new List<string>();
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var url = request.RequestUri!.ToString();
                calls.Add(url);
                var tracks = string.Join(",", Enumerable.Range(1, searchHits)
                    .Select(i => $$"""{"name":"Song {{i}}","artist":"Artist"}"""));
                var body = url.Contains("method=track.search")
                    ? """{"results":{"trackmatches":{"track":[""" + tracks + "]}}}"
                    : """{"toptracks":{"track":[]}}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            });
        var lastFm = new LastFmService(new HttpClient(handler.Object),
            TestOptions.Monitor(new LastFmSettings { ApiKey = "key" }),
            Options.Create(new MetadataSettings()),
            NullLogger<LastFmService>.Instance);
        var search = new ExternalSearchService(new Mock<IMusicMetadataService>().Object,
            NullLogger<ExternalSearchService>.Instance, lastFm);

        await search.GetAsync("artist");

        Assert.Equal(expectedTopTrackCalls, calls.Count(url => url.Contains("method=artist.gettoptracks")));
    }
}
