using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace Octo.Services.Metadata;

/// <summary>
/// Warns when Deezer's edge (Akamai) appears to refuse Octo: a 403 or 429, or an HTML page where
/// Deezer answers in JSON. Octo already treats these as transient, so nothing is cached from them,
/// but without this they read as "Deezer has nothing" and a block goes unnoticed. One warning per
/// host and status every <see cref="Quiet"/>, so a block does not flood the log. The response is
/// passed on unchanged.
/// </summary>
public sealed partial class DeezerEdgeRefusalHandler(ILogger<DeezerEdgeRefusalHandler> logger) : DelegatingHandler
{
    internal static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, DateTime> LastWarned = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        var host = request.RequestUri?.Host.ToLowerInvariant() ?? "";
        // Images and audio come from the CDN, which is not what this watches.
        if (!host.EndsWith("deezer.com", StringComparison.Ordinal)) return response;
        var html = response.Content.Headers.ContentType?.MediaType == "text/html";
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) && !html)
            return response;

        var key = $"{host}|{(int)response.StatusCode}|{html}";
        var now = DateTime.UtcNow;
        if (LastWarned.TryGetValue(key, out var last) && now - last < Quiet) return response;
        LastWarned[key] = now;

        string? reference = null;
        if (html)
        {
            // Buffered, so the caller still reads the same body.
            await response.Content.LoadIntoBufferAsync(ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            reference = AkamaiReference().Match(body) is { Success: true } match ? match.Groups[1].Value : null;
        }
        var edge = response.Headers.Server.ToString();
        logger.LogWarning("Deezer may be refusing Octo: {Host} answered {Status}{Html} (server {Server}, reference {Reference}). "
            + "Lookups fail until it stops; further warnings for this are held for {Quiet} minutes.",
            host, (int)response.StatusCode, html ? " with an HTML page" : "", edge.Length == 0 ? "unknown" : edge,
            reference ?? "none", (int)Quiet.TotalMinutes);
        return response;
    }

    // Akamai's "Access Denied" page: "Reference&#32;#18.2f3c1702.1760000000.1a2b3c".
    [GeneratedRegex(@"Reference(?:&#32;|\s)+#([0-9a-fA-F.]+)")]
    private static partial Regex AkamaiReference();
}
