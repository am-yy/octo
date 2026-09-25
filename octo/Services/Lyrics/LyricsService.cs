using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Fingerprint;

namespace Octo.Services.Lyrics;

/// <summary>
/// Lyrics from the sources LYRICS_SOURCES names, in that order (#52). Synced beats plain, so a
/// later source is only asked while nothing earlier had timing, and a plain answer is kept in
/// case nothing better turns up.
/// </summary>
public sealed class LyricsService : IDisposable
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(30);

    private readonly IReadOnlyList<ILyricsSource> _sources;
    private readonly IOptionsMonitor<MetadataSettings> _settings;
    private readonly ILogger<LyricsService> _logger;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 512 });

    public LyricsService(IEnumerable<ILyricsSource> sources, IOptionsMonitor<MetadataSettings> settings,
        ILogger<LyricsService> logger)
    {
        _sources = sources.ToList();
        _settings = settings;
        _logger = logger;
    }

    public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Artist) || string.IsNullOrWhiteSpace(query.Title)) return LyricsLookup.Miss;

        var key = $"{TrackMatchComparer.Normalize(query.Artist)}|{TrackMatchComparer.Normalize(query.Title)}|{query.DurationSeconds}";
        if (_cache.TryGetValue(key, out LyricsLookup? cached) && cached is not null) return cached;

        LyricsResult? plain = null;
        var transient = false;
        foreach (var name in _settings.CurrentValue.EffectiveLyricsSources)
        {
            if (_sources.FirstOrDefault(source => source.Key == name) is not { } source) continue;

            LyricsLookup lookup;
            try { lookup = await source.FindAsync(query, ct); }
            catch (Exception ex)
            {
                _logger.LogDebug("lyrics source {Source} threw: {M}", name, ex.Message);
                lookup = LyricsLookup.Failed;
            }

            if (lookup.Transient) { transient = true; continue; }
            if (lookup.Result is not { } result) continue;
            if (result.HasSynced || result.Instrumental) return Remember(key, new LyricsLookup(result, false), HitTtl);
            if (result.HasPlain) plain ??= result;
        }

        // A plain answer found while a better source could not be asked is kept only briefly,
        // so the synced one gets another chance soon.
        if (plain is not null) return Remember(key, new LyricsLookup(plain, false), transient ? MissTtl : HitTtl);
        if (transient) return LyricsLookup.Failed;
        return Remember(key, LyricsLookup.Miss, MissTtl);
    }

    private LyricsLookup Remember(string key, LyricsLookup lookup, TimeSpan ttl)
    {
        _cache.Set(key, lookup, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });
        return lookup;
    }

    public void Dispose() => _cache.Dispose();
}
