using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Octo.Models.Domain;
using Octo.Services;
using Octo.Services.Deezer;
using Octo.Services.Local;

namespace Octo.Tests;

public sealed class SubsonicTranscodingTests
{
    private const string MediaId = "ext-deezer-123";

    [Fact]
    public async Task DecisionHonorsCapabilityBitrateAndIssuesBoundOpaqueProfile()
    {
        using var handler = new NavidromeHandler();
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/rest/getTranscodeDecision?u=alice&id={MediaId}&mediaId={MediaId}&mediaType=song&f=json")
        {
            Content = Json("""{"name":"test","platform":"test","maxAudioBitrate":192000,"maxTranscodingAudioBitrate":100000,"directPlayProfiles":[{"containers":["flac"],"audioCodecs":["flac"],"protocols":["http"]}],"transcodingProfiles":[{"container":"mp3","audioCodec":"mp3","protocol":"http","maxAudioChannels":2}]}"""),
        };

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var decision = document.RootElement.GetProperty("subsonic-response").GetProperty("transcodeDecision");

        Assert.False(decision.GetProperty("canDirectPlay").GetBoolean());
        Assert.True(decision.GetProperty("canTranscode").GetBoolean());
        Assert.Equal(96_000, decision.GetProperty("transcodeStream").GetProperty("audioBitrate").GetInt32());
        var descriptor = decision.GetProperty("transcodeParams").GetString();
        Assert.False(string.IsNullOrWhiteSpace(descriptor));

        using var denied = await client.GetAsync($"/rest/getTranscodeStream?u=alice&mediaId={MediaId}&transcodeParams=bad");
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

        using var tampered = await client.GetAsync($"/rest/getTranscodeStream?u=alice&mediaId={MediaId}&transcodeParams={Tamper(descriptor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);

        using var rebound = await client.GetAsync($"/rest/getTranscodeStream?u=alice&mediaId=ext-deezer-456&transcodeParams={Uri.EscapeDataString(descriptor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, rebound.StatusCode);

        using var unauthenticated = await client.GetAsync($"/rest/getTranscodeStream?u=mallory&mediaId={MediaId}&transcodeParams={Uri.EscapeDataString(descriptor!)}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head,
            $"/rest/getTranscodeStream.view?u=alice&mediaId={MediaId}&transcodeParams={Uri.EscapeDataString(descriptor!)}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, head.StatusCode); // valid route, no delivery injected
    }

    [Fact]
    public async Task LibraryDecisionFailureAndTranscodeStreamHeadRelayToNavidrome()
    {
        using var handler = new NavidromeHandler { DecisionStatus = HttpStatusCode.BadGateway };
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();

        using var decision = await client.PostAsync("/rest/getTranscodeDecision?u=alice&id=library-1&mediaId=library-1&mediaType=song&f=json",
            Json("""{"name":"client","platform":"test","directPlayProfiles":[]}"""));
        Assert.Equal(HttpStatusCode.BadGateway, decision.StatusCode);
        Assert.Contains("upstream decision failure", await decision.Content.ReadAsStringAsync());
        Assert.Equal(HttpMethod.Post, handler.DecisionMethod);
        Assert.Contains("directPlayProfiles", handler.DecisionBody);

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head,
            "/rest/getTranscodeStream.view?u=alice&id=library-1&mediaId=library-1"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(HttpMethod.Head, handler.TranscodeStreamMethod);
        Assert.Equal(321, head.Content.Headers.ContentLength);
        Assert.Equal("audio/ogg", head.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task EmptyDirectProfileArraysMeanWildcard()
    {
        using var handler = new NavidromeHandler();
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(
            $"/rest/getTranscodeDecision?u=alice&id={MediaId}&mediaId={MediaId}&mediaType=song&f=json",
            Json("""{"name":"test","platform":"test","directPlayProfiles":[{"containers":[],"audioCodecs":[],"protocols":[]}]}"""));

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var decision = document.RootElement.GetProperty("subsonic-response").GetProperty("transcodeDecision");
        Assert.True(decision.GetProperty("canDirectPlay").GetBoolean());
        Assert.False(decision.GetProperty("canTranscode").GetBoolean());
    }

    [Fact]
    public async Task InvalidNumericCapabilityReturnsBadRequest()
    {
        using var handler = new NavidromeHandler();
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(
            $"/rest/getTranscodeDecision?u=alice&id={MediaId}&mediaId={MediaId}&mediaType=song&f=json",
            Json("""{"name":"test","platform":"test","maxAudioBitrate":"bad","directPlayProfiles":[]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RequiredSampleRateLimitationsAreAppliedToKnownOutputAndUnknownSource()
    {
        using var handler = new NavidromeHandler();
        using var factory = CreateFactory(handler);
        using var client = factory.CreateClient();
        var endpoint = $"/rest/getTranscodeDecision?u=alice&id={MediaId}&mediaId={MediaId}&mediaType=song&f=json";

        using var supported = await client.PostAsync(endpoint, Json("""{"name":"test","platform":"test","transcodingProfiles":[{"container":"mp3","audioCodec":"mp3","protocol":"http","maxAudioChannels":2}],"codecProfiles":[{"type":"AudioCodec","name":"mp3","limitations":[{"name":"audioSamplerate","comparison":"Equals","values":[44100],"required":true}]}]}"""));
        supported.EnsureSuccessStatusCode();
        using var supportedDocument = JsonDocument.Parse(await supported.Content.ReadAsStringAsync());
        Assert.True(supportedDocument.RootElement.GetProperty("subsonic-response").GetProperty("transcodeDecision").GetProperty("canTranscode").GetBoolean());

        using var unsupported = await client.PostAsync(endpoint, Json("""{"name":"test","platform":"test","directPlayProfiles":[{"containers":[],"audioCodecs":[],"protocols":[]}],"transcodingProfiles":[{"container":"mp3","audioCodec":"mp3","protocol":"http","maxAudioChannels":2}],"codecProfiles":[{"type":"AudioCodec","name":"flac","limitations":[{"name":"audioSamplerate","comparison":"Equals","values":[44100],"required":true}]},{"type":"AudioCodec","name":"mp3","limitations":[{"name":"audioSamplerate","comparison":"Equals","values":[48000],"required":true}]}]}"""));
        unsupported.EnsureSuccessStatusCode();
        using var unsupportedDocument = JsonDocument.Parse(await unsupported.Content.ReadAsStringAsync());
        var decision = unsupportedDocument.RootElement.GetProperty("subsonic-response").GetProperty("transcodeDecision");
        Assert.False(decision.GetProperty("canDirectPlay").GetBoolean()); // Source sample rate unknown.
        Assert.False(decision.GetProperty("canTranscode").GetBoolean()); // MP3 is emitted at 44.1 kHz here.
    }

    private static WebApplicationFactory<Program> CreateFactory(NavidromeHandler handler)
    {
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(service => service.GetSongAsync("deezer", "123"))
            .ReturnsAsync(new Song { Id = MediaId, Title = "Title", Artist = "Artist", IsLocal = false });
        var library = new Mock<ILocalLibraryService>();
        library.Setup(service => service.ParseSongId(It.IsAny<string>()))
            .Returns((string id) => id.StartsWith("ext-deezer-", StringComparison.Ordinal)
                ? (true, "deezer", id[11..]) : (false, null, null));
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Subsonic:Url"] = "http://navidrome.test",
                    ["Library:DownloadPath"] = Path.GetTempPath(),
                    ["Deezer:CacheEnabled"] = "false",
                    ["Deezer:CachePath"] = Path.GetTempPath(),
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IHttpClientFactory>();
                services.RemoveAll<IMusicMetadataService>();
                services.RemoveAll<ILocalLibraryService>();
                services.RemoveAll<DeezerDeliveryService>();
                services.AddSingleton(clients.Object);
                services.AddSingleton(metadata.Object);
                services.AddSingleton(library.Object);
            });
        });
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
    private static string Tamper(string token) => Uri.EscapeDataString((token[0] == 'A' ? "B" : "A") + token[1..]);

    private sealed class NavidromeHandler : HttpMessageHandler
    {
        public HttpStatusCode DecisionStatus { get; init; } = HttpStatusCode.OK;
        public HttpMethod? DecisionMethod { get; private set; }
        public HttpMethod? TranscodeStreamMethod { get; private set; }
        public string DecisionBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/rest/ping")
            {
                var user = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query).GetValueOrDefault("u");
                var status = user == "alice" ? "ok" : "failed";
                return JsonResponse(HttpStatusCode.OK, $"{{\"subsonic-response\":{{\"status\":\"{status}\"}}}}");
            }
            if (path == "/rest/getTranscodeDecision")
            {
                DecisionMethod = request.Method;
                DecisionBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                return JsonResponse(DecisionStatus, DecisionStatus == HttpStatusCode.OK
                    ? "{\"subsonic-response\":{\"status\":\"ok\"}}"
                    : "{\"error\":\"upstream decision failure\"}");
            }
            if (path == "/rest/getTranscodeStream")
            {
                TranscodeStreamMethod = request.Method;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : [1, 2, 3]),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
                response.Content.Headers.ContentLength = 321;
                return response;
            }
            return JsonResponse(HttpStatusCode.OK, "{\"subsonic-response\":{\"status\":\"ok\"}}");
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
