using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Octo.Services.Subsonic;

/// <summary>
/// Who a Subsonic request is from, for the things Octo keeps per listener: what radio learns
/// from a scrobble, whose Last.fm a play goes to, and whose search a later page continues.
///
/// A token or password sign-in names the user in <c>u</c>. An OpenSubsonic API key sign-in
/// does not: Navidrome refuses <c>u</c> next to <c>apiKey</c> (error 43), so the key is all
/// there is. For those, Navidrome is asked through <c>tokenInfo</c> (the apiKeyAuthentication
/// extension) with the same key, and the answer is kept for a few minutes.
///
/// Callers ask only once the request's credentials have already been accepted upstream, so an
/// unknown key never costs more than the one call that refuses it. When tokenInfo fails or
/// will not say, that too is kept, briefly, so a Navidrome without it is not asked on every
/// request; and requests with one key arriving together make one call between them. The key
/// itself is never stored or logged: the kept answer is filed under a SHA-256 of it.
/// </summary>
public sealed class RequestIdentity
{
    /// <summary>How long a key's username is kept. A key can be revoked, so not for long.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long a key tokenInfo could not name is left unasked. Short: the next
    /// answer may well be different, and until then the request is simply nobody's.</summary>
    internal TimeSpan UnnamedLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Keys remembered at once. A household has a handful; this only bounds a flood.</summary>
    internal const int Capacity = 512;

    private readonly ILogger<RequestIdentity> _logger;
    private readonly MemoryCache _names = new(new MemoryCacheOptions { SizeLimit = Capacity });

    /// <summary>tokenInfo calls still out, by key hash, for the requests that arrive meanwhile.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _asking = new(StringComparer.Ordinal);

    /// <summary>What was learned of one key: its owner, or null when Navidrome would not say.</summary>
    private sealed record Answer(string? Username);

    public RequestIdentity(ILogger<RequestIdentity> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The request's username: <c>u</c> when it has one, else the owner of its API key as
    /// Navidrome reports it, else null. Null also when Navidrome could not say, so a caller
    /// skips what it keeps per user rather than filing it under nobody. The relay is the
    /// request's own (it is scoped to the request, where this is kept across requests).
    /// </summary>
    public async Task<string?> UsernameAsync(IReadOnlyDictionary<string, string> parameters,
        SubsonicProxyService relay, CancellationToken cancellationToken = default)
    {
        if (parameters.GetValueOrDefault("u") is { } named && named.Trim() is { Length: > 0 } username)
            return username;
        if (parameters.GetValueOrDefault("apiKey") is not { Length: > 0 } apiKey) return null;

        var slot = Fingerprint(apiKey);
        if (_names.TryGetValue(slot, out Answer? known)) return known!.Username;

        var ask = new Dictionary<string, string> { ["apiKey"] = apiKey, ["f"] = "json" };
        foreach (var name in new[] { "v", "c" })
            if (parameters.GetValueOrDefault(name) is { Length: > 0 } value) ask[name] = value;
        // One call per key however many requests wait on it. It is not tied to any one of
        // them, so a request that gives up does not fail the others.
        var asking = _asking.GetOrAdd(slot, _ => new Lazy<Task<string?>>(() => AskAsync(slot, ask, relay)));
        try
        {
            return await asking.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<string?> AskAsync(string slot, Dictionary<string, string> ask, SubsonicProxyService relay)
    {
        try
        {
            // Answered while this call was being set up.
            if (_names.TryGetValue(slot, out Answer? known)) return known!.Username;
            string? owner;
            try
            {
                var (body, _) = await relay.RelayAsync("rest/tokenInfo", ask);
                owner = TokenInfoUsername(body);
                if (owner is null) _logger.LogDebug("Navidrome did not say whose API key this request used");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                           or OctoNotConfiguredException)
            {
                _logger.LogDebug("Could not ask Navidrome whose API key this request used: {Reason}", ex.GetType().Name);
                owner = null;
            }
            _names.Set(slot, new Answer(owner), new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = owner is null ? UnnamedLifetime : Lifetime,
            });
            return owner;
        }
        finally
        {
            _asking.TryRemove(slot, out _);
        }
    }

    /// <summary><c>tokenInfo.username</c> from an ok answer, or null.</summary>
    internal static string? TokenInfoUsername(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("subsonic-response", out var response)
                || !response.TryGetProperty("status", out var status) || status.GetString() != "ok"
                || !response.TryGetProperty("tokenInfo", out var info)
                || !info.TryGetProperty("username", out var username)
                || username.ValueKind != JsonValueKind.String)
                return null;
            return username.GetString()?.Trim() is { Length: > 0 } name ? name : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Fingerprint(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
