using Microsoft.Extensions.Caching.Memory;
using Octo.Models.Domain;

namespace Octo.Services.Subsonic;

/// <summary>
/// What page one of a search showed, kept so the pages after it can carry on from it.
/// See <see cref="SearchSongPagePlanner"/> for how a later page is placed.
/// </summary>
/// <param name="Built">
/// The outside songs page one sliced from. The same frozen list ExternalSearchService
/// handed out, never a copy and never mutated.
/// </param>
/// <param name="PageOneCount">The songCount page one was asked for.</param>
/// <param name="PrefixSize">How many library rows page one asked Navidrome for.</param>
/// <param name="PrefixCount">How many it got.</param>
/// <param name="PageOneExternals">How many rows of <paramref name="Built"/> page one took.</param>
/// <param name="LaterExternals">
/// The rest of <paramref name="Built"/>, less any song page one already listed from the
/// library. Removed here rather than on each page so a later page has no gaps in it.
/// </param>
/// <param name="PrefixKeys">Dedup keys of the library rows page one showed.</param>
internal sealed record SearchSongOrder(
    IReadOnlyList<Song> Built,
    int PageOneCount,
    int PrefixSize,
    int PrefixCount,
    int PageOneExternals,
    IReadOnlyList<Song> LaterExternals,
    IReadOnlySet<string> PrefixKeys)
{
    /// <summary>
    /// The library only has more to give when page one got every row it asked for.
    /// </summary>
    public bool LibraryContinues => PrefixCount >= PrefixSize && PrefixSize > 0;

    /// <summary>
    /// How many outside rows page one shows: its share of the budget, plus whatever the
    /// library left unused, never more than were built. The search path and a rebuilt
    /// order both use this, so a rebuild places page one exactly where it fell.
    /// </summary>
    public static int PageOneExternalCount(int builtCount, int localTarget, int externalTarget, int localReturned) =>
        Math.Min(builtCount, externalTarget + Math.Max(0, localTarget - localReturned));

    /// <summary>
    /// The order for a page one built from <paramref name="built"/> and answered with
    /// <paramref name="localSongs"/> (rows as <see cref="SubsonicModelMapper.ParseSearchResponse"/>
    /// returns them).
    /// </summary>
    public static SearchSongOrder From(IReadOnlyList<Song> built, int requestedSongs,
        int localTarget, int externalTarget, IReadOnlyList<object> localSongs)
    {
        var shown = PageOneExternalCount(built.Count, localTarget, externalTarget, localSongs.Count);
        var owned = SubsonicModelMapper.LocalSongKeys(localSongs);
        var later = built.Skip(shown).Where(song => !SubsonicModelMapper.IsListed(song, owned)).ToList();
        return new SearchSongOrder(built, Math.Max(0, requestedSongs), localTarget, localSongs.Count,
            shown, later, owned);
    }
}

/// <summary>
/// The last page one of each search, per user, for a short while. Without it a later page
/// would have to build its outside songs again, and a second build is free to come back
/// different (Last.fm reorders, a lookup times out), which would repeat or drop rows the
/// user already scrolled past.
///
/// The entry is for one user, one endpoint and one music folder, because the library side
/// of the order is: two users can own different songs, and a folder narrows what
/// Navidrome returns. When an entry is gone (expired, evicted, or Octo restarted) a later
/// page builds the order again, which is the best that can be done without it.
/// </summary>
public sealed class SearchSongOrderCache
{
    /// <summary>
    /// How long an order is kept after it was last read. Long enough to scroll a result
    /// list at reading pace; short enough that a search run again later is built fresh.
    /// Reading an order renews it, so a list still being scrolled is kept.
    /// </summary>
    internal static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    /// <summary>A hard ceiling so an order still being scrolled is also rebuilt eventually.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Orders kept at once. One holds at most a build's worth of songs (60) that are shared
    /// with the build, so this is a few hundred small lists at worst.
    /// </summary>
    internal const int Capacity = 256;

    private readonly MemoryCache _orders = new(new MemoryCacheOptions { SizeLimit = Capacity });

    internal static string Key(string user, string endpoint, string? musicFolderId, string query) =>
        // Lower-cased and trimmed the same way ExternalSearchService keys its builds, so an
        // order and the build it came from agree on which queries are the same query.
        $"{user}\n{endpoint}\n{musicFolderId}\n{query.Trim().ToLowerInvariant()}";

    internal SearchSongOrder? Get(string key) =>
        _orders.TryGetValue(key, out SearchSongOrder? order) ? order : null;

    internal void Set(string key, SearchSongOrder order) =>
        _orders.Set(key, order, new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = Idle,
            AbsoluteExpirationRelativeToNow = MaxAge,
        });
}
