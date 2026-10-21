using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Metadata;

namespace Octo.Tests;

/// <summary>Deezer's track mix, read from the web player's gateway with an anonymous session.</summary>
public sealed class DeezerRadioTests
{
    private sealed class Gateway : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        private int _sessions;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            lock (Calls) Calls.Add(query["method"] ?? request.RequestUri.AbsolutePath);
            var body = query["method"] switch
            {
                "deezer.getUserData" => $"{{\"error\":[],\"results\":{{\"checkForm\":\"token-{++_sessions}\"}}}}",
                // The first session has gone stale by the time the mix is asked.
                "song.getSearchTrackMix" when query["api_token"] == "token-1" =>
                    """{"error":{"VALID_TOKEN_REQUIRED":"Invalid CSRF token"},"results":{}}""",
                "song.getSearchTrackMix" => """
                    {"error":[],"results":{"data":[
                      {"SNG_ID":"1","SNG_TITLE":"Aerodynamic","ART_NAME":"Daft Punk","ART_ID":"27","DURATION":"212","EXPLICIT_LYRICS":"0","ALB_TITLE":"Discovery","ALB_ID":"302127"},
                      {"SNG_ID":"2","SNG_TITLE":"D.A.N.C.E","VERSION":"(Radio Edit)","ART_NAME":"Justice","DURATION":"180","EXPLICIT_LYRICS":"1"},
                      {"SNG_ID":"3","SNG_TITLE":"","ART_NAME":"Nobody"}
                    ]}}
                    """,
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task TheTrackMix_IsReadFromTheGateway_WithOneFreshSessionWhenTheFirstIsStale()
    {
        var gateway = new Gateway();
        var deezer = new DeezerMetadataService(new ReviewFixtures.OneClientFactory(gateway),
            TestOptions.Monitor(new MetadataSettings()), NullLogger<DeezerMetadataService>.Instance);

        var mix = await deezer.GetTrackMixAsync("3135556");

        Assert.NotNull(mix);
        Assert.Equal(["Aerodynamic", "D.A.N.C.E (Radio Edit)"], mix.Select(track => track.Title));
        Assert.Equal("27", mix[0].ArtistId);
        Assert.Equal(212, mix[0].Duration);
        Assert.True(mix[1].Explicit);
        Assert.Equal(2, gateway.Calls.Count(call => call == "deezer.getUserData"));

        // A day's answer: asked again, nothing goes out.
        var before = gateway.Calls.Count;
        await deezer.GetTrackMixAsync("3135556");
        Assert.Equal(before, gateway.Calls.Count);
    }
}
