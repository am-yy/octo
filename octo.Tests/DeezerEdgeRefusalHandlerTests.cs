using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Octo.Services.Metadata;

namespace Octo.Tests;

// The warning throttle is process-wide, so each test asks its own host.
public class DeezerEdgeRefusalHandlerTests
{
    [Fact]
    public async Task AkamaiDenialWarnsOnceWithItsReferenceAndPassesTheBodyOn()
    {
        var log = new ListLogger();
        const string page = "<HTML><HEAD><TITLE>Access Denied</TITLE></HEAD><BODY>You don't have permission."
            + "<P>Reference&#32;#18.2f3c1702.1760000000.1a2b3c</BODY></HTML>";
        using var client = Client(log, () => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(page, Encoding.UTF8, "text/html"),
        });

        var first = await client.GetAsync("https://denial.edge-test.deezer.com/api");
        Assert.Contains("Access Denied", await first.Content.ReadAsStringAsync());
        await client.GetAsync("https://denial.edge-test.deezer.com/api");

        var warning = Assert.Single(log.Warnings);
        Assert.Contains("denial.edge-test.deezer.com answered 403 with an HTML page", warning);
        Assert.Contains("18.2f3c1702.1760000000.1a2b3c", warning);
    }

    [Fact]
    public async Task OrdinaryAnswersAndOtherHostsStayQuiet()
    {
        var log = new ListLogger();
        using var json = Client(log, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        await json.GetAsync("https://quiet.edge-test.deezer.com/search?q=x");
        using var cdn = Client(log, () => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await cdn.GetAsync("https://cdn-images.dzcdn.net/images/cover/x.jpg");

        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task RateLimitWarnsSeparatelyFromADenial()
    {
        var log = new ListLogger();
        using var limited = Client(log, () => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        await limited.GetAsync("https://limit.edge-test.deezer.com/track/1");
        using var denied = Client(log, () => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await denied.GetAsync("https://limit.edge-test.deezer.com/track/1");

        Assert.Equal(2, log.Warnings.Count);
        Assert.Contains("answered 429", log.Warnings[0]);
    }

    private static HttpClient Client(ListLogger log, Func<HttpResponseMessage> answer) =>
        new(new DeezerEdgeRefusalHandler(log) { InnerHandler = new Answering(answer) });

    private sealed class Answering(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer());
    }

    private sealed class ListLogger : ILogger<DeezerEdgeRefusalHandler>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
