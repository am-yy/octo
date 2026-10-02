using System.Net;

namespace Octo.Services.Metadata;

/// <summary>
/// Spends a <see cref="DeezerRateLimiter"/> permit before each Deezer API call.
/// Transient by design: IHttpClientFactory recycles handler chains, so the limiter
/// itself is the singleton and this is just the seam that consults it.
/// </summary>
public sealed class DeezerRateLimitHandler : DelegatingHandler
{
    /// <summary>Marks a request as background work, so cache warming yields to anything
    /// a user is actually waiting on.</summary>
    public static readonly HttpRequestOptionsKey<bool> BackgroundLane = new("octo.deezer.background");

    private readonly DeezerRateLimiter _limiter;
    private readonly ILogger<DeezerRateLimitHandler> _logger;

    public DeezerRateLimitHandler(DeezerRateLimiter limiter, ILogger<DeezerRateLimitHandler> logger)
    {
        _limiter = limiter;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Catalog recovery and its authentication spend the same local allowance.
        // This is an Octo traffic bound, not a claim about the web endpoint's quota.
        // Cover images and audio CDN transfers remain outside discovery's budget.
        if (request.RequestUri?.Host.ToLowerInvariant() is not ("api.deezer.com" or "pipe.deezer.com" or "auth.deezer.com"))
            return await base.SendAsync(request, ct);

        var background = request.Options.TryGetValue(BackgroundLane, out var bg) && bg;

        using var lease = await _limiter.AcquireAsync(background, ct);
        if (!lease.IsAcquired)
        {
            // The queue is full. Answering 429 rather than throwing means callers take
            // their existing "Deezer refused this" path, which never caches the result —
            // so back-pressure can slow us down but can never poison the cache.
            _logger.LogWarning("deezer rate limiter rejected a {Lane} request to {Url}",
                background ? "background" : "interactive", request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
        }

        return await base.SendAsync(request, ct);
    }
}
