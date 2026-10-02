using Microsoft.Extensions.Logging;
using Moq;
using Octo.Services.Metadata;
using System.Net;
using System.Threading.RateLimiting;

namespace Octo.Tests;

/// <summary>
/// The existing Deezer harness stubs CreateClient(It.IsAny&lt;string&gt;()), so it builds the
/// named client WITHOUT this handler. These drive the handler pipeline directly, because
/// the traps here are all silent: a wrongly-metered CDN, a lane that steals another lane's
/// budget, or a rejection that never surfaces.
/// </summary>
public class DeezerRateLimitHandlerTests
{
    /// <summary>Terminates the chain and records what actually reached the network.</summary>
    private sealed class CountingInnerHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient Client, CountingInnerHandler Inner, DeezerRateLimiter Limiter) Build()
    {
        var limiter = new DeezerRateLimiter();
        var inner = new CountingInnerHandler();
        var handler = new DeezerRateLimitHandler(limiter, new Mock<ILogger<DeezerRateLimitHandler>>().Object)
        {
            InnerHandler = inner,
        };
        return (new HttpClient(handler), inner, limiter);
    }

    /// <summary>
    /// The cover-art lookup fetches image bytes from the CDN through the SAME client it
    /// uses for API calls. Metering those would spend an API permit per rendered row and
    /// throttle a host that has no quota, so the budget must ignore them entirely.
    /// </summary>
    [Fact]
    public async Task CdnRequestsAreNotMetered()
    {
        var (client, inner, limiter) = Build();
        using var _ = limiter;

        // Far more than the interactive permit count, so metering these would visibly
        // throttle them.
        for (var i = 0; i < 120; i++)
        {
            var resp = await client.GetAsync($"https://cdn-images.dzcdn.net/images/cover/{i}/1000x1000.jpg");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        Assert.Equal(120, inner.Calls);

        // Asserting they merely SUCCEEDED is not enough: a metered burst still succeeds,
        // it just waits for permits. What proves they were unmetered is that the whole
        // interactive window is still intact afterwards, with every permit available
        // without waiting.
        var leases = new List<RateLimitLease>();
        try
        {
            for (var i = 0; i < 30; i++)
            {
                var pending = limiter.AcquireAsync(background: false, CancellationToken.None);
                Assert.True(pending.IsCompleted, $"interactive permit {i} had to wait, so CDN traffic spent the API budget");
                var lease = await pending;
                Assert.True(lease.IsAcquired);
                leases.Add(lease);
            }
        }
        finally
        {
            foreach (var l in leases) l.Dispose();
        }
    }

    /// <summary>Anything that is not the Deezer API is passed straight through.</summary>
    [Fact]
    public async Task UnrelatedHostsArePassedThrough()
    {
        var (client, inner, limiter) = Build();
        using var _ = limiter;

        var resp = await client.GetAsync("https://itunes.apple.com/search?term=x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    /// <summary>
    /// API calls really do spend the budget, and it is bounded. Note this is a sliding
    /// WINDOW: permits come back with time, not when a lease is disposed, so an
    /// over-budget caller waits rather than being refused outright.
    /// </summary>
    [Fact]
    public async Task RestWebAndAuthenticationShareTheInteractiveBudget()
    {
        var (client, inner, limiter) = Build();
        using var _l = limiter;

        for (var i = 0; i < 30; i++)
        {
            var host = new[] { "api.deezer.com", "pipe.deezer.com", "auth.deezer.com" }[i % 3];
            var resp = await client.GetAsync($"https://{host}/request/{i}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        Assert.Equal(30, inner.Calls);

        // The window is spent, so the next acquire must queue rather than complete now.
        var queued = limiter.AcquireAsync(background: false, CancellationToken.None).AsTask();
        Assert.False(queued.IsCompleted);

        // Drain it so the ValueTask is observed rather than abandoned.
        using var lease = await queued.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(lease.IsAcquired);
    }

    /// <summary>
    /// Background cache warming must not be able to starve a search the user is waiting
    /// on. Two limiters whose permits summed above the ceiling would defeat the point, so
    /// this pins that they are genuinely separate allowances.
    /// </summary>
    [Fact]
    public async Task BackgroundLaneDoesNotConsumeInteractivePermits()
    {
        var (_, _, limiter) = Build();
        using var _l = limiter;

        var background = new List<IDisposable>();
        for (var i = 0; i < 10; i++)
        {
            var lease = await limiter.AcquireAsync(background: true, CancellationToken.None);
            Assert.True(lease.IsAcquired);
            background.Add(lease);
        }

        // Background is spent; interactive must still be immediately available.
        using var interactive = await limiter.AcquireAsync(background: false, CancellationToken.None);
        Assert.True(interactive.IsAcquired);

        foreach (var l in background) l.Dispose();
    }
}
