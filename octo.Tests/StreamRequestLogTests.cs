using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Octo.Middleware;
using Octo.Models.Settings;
using Xunit;

namespace Octo.Tests;

public class StreamRequestLogTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AStreamThatEndsEarlyIsAlwaysLogged(bool clientCloses, bool logEvery)
    {
        var (lines, run) = Pipeline(logEvery, async context =>
        {
            context.Response.ContentLength = 100;
            await context.Response.Body.WriteAsync(new byte[40]);
            if (clientCloses) context.Abort();
        });

        await run("/rest/stream.view");

        var line = Assert.Single(lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("ended early: 40 of 100 bytes", line.Message);
        Assert.Contains(clientCloses ? "client closed the connection" : "response ended short", line.Message);
    }

    [Fact]
    public async Task OctoCuttingAStreamSaysWhy()
    {
        var (lines, run) = Pipeline(false, async context =>
        {
            await context.Response.Body.WriteAsync(new byte[10]);
            context.Items[StreamRequestLog.EndReasonKey] = "delivery failed: IOException";
        });

        await run("/rest/stream");

        Assert.Contains("delivery failed: IOException", Assert.Single(lines).Message);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task ACompleteStreamIsLoggedOnlyWhenAskedFor(bool logEvery, int expected)
    {
        var (lines, run) = Pipeline(logEvery, async context =>
        {
            context.Response.ContentLength = 100;
            await context.Response.Body.WriteAsync(new byte[100]);
        });

        await run("/rest/download");

        Assert.Equal(expected, lines.Count);
        if (expected == 1)
        {
            Assert.Equal(LogLevel.Information, lines[0].Level);
            Assert.Contains("100 bytes", lines[0].Message);
        }
    }

    [Fact]
    public async Task OtherPathsAreNotWatched()
    {
        var (lines, run) = Pipeline(true, async context =>
        {
            context.Response.ContentLength = 100;
            await context.Response.Body.WriteAsync(new byte[1]);
        });

        await run("/rest/getAlbum.view");

        Assert.Empty(lines);
    }

    private static (List<(LogLevel Level, string Message)> Lines, Func<string, Task> Run) Pipeline(
        bool logEvery, RequestDelegate handler)
    {
        var lines = new List<(LogLevel, string)>();
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(new ListProvider(lines)))
            .Configure<ServerSettings>(settings => settings.LogStreamRequests = logEvery)
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseStreamRequestLog();
        app.Run(handler);
        var pipeline = app.Build();
        return (lines, async path =>
        {
            using var aborted = new CancellationTokenSource();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Path = path;
            context.Request.QueryString = new QueryString("?id=song-1");
            context.Response.Body = new MemoryStream();
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature>(
                new Lifetime(aborted));
            await pipeline(context);
        });
    }

    private sealed class Lifetime(CancellationTokenSource source)
        : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get => source.Token; set { } }
        public void Abort() => source.Cancel();
    }

    private sealed class ListProvider(List<(LogLevel, string)> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(lines);
        public void Dispose() { }
    }

    private sealed class ListLogger(List<(LogLevel, string)> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Add((logLevel, formatter(state, exception)));
    }
}
