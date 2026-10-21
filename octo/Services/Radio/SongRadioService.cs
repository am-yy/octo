using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.LastFm;

namespace Octo.Services.Radio;

/// <summary>
/// Radio from one song, album or artist (getSimilarSongs): every source asked at once, their
/// answers blended, drawn in a weighted order so the same seed gives a different radio each
/// time, shaped as stations are (an artist cap, songs heard lately last), resolved local first,
/// and each song marked with the source it came from. Library songs take at most
/// <see cref="RadioSourceSettings.LibraryShare"/> of it.
/// </summary>
public sealed class SongRadioService(RadioSourceSet sources, LastFmRadioTrackResolver resolver,
    IMusicMetadataService metadata,
    IOptionsMonitor<LastFmSettings> lastFmSettings,
    IOptionsMonitor<SubsonicSettings> subsonicSettings,
    ILogger<SongRadioService> logger, RadioOutcomeStore? outcomes = null,
    IOptionsMonitor<RadioSourceSettings>? radioSettings = null, Octo.Services.Sonic.SonicStore? sonic = null,
    LastFmRadioStateStore? state = null, Octo.Services.Local.ILocalLibraryService? library = null,
    RadioServedMemory? served = null, Func<Random>? randomizer = null)
{
    /// <summary>Picks resolved beyond the count asked for in the first round, so the checks after
    /// it (playable, library share, explicit filter) have some to drop.</summary>
    internal const double PickHeadroom = 1.5;
    /// <summary>Rounds of resolving before a radio settles for fewer songs than asked.</summary>
    internal const int ResolveRounds = 3;
    /// <summary>A song this listener was served from this seed lately keeps this share of its
    /// weight, as a station's previous snapshot does: the next radio from it is mostly new.</summary>
    internal const double ServedLatelyWeight = LastFmRadioRecommendationService.PreviousSnapshotWeight;
    internal static readonly TimeSpan ServedLately = TimeSpan.FromDays(7);

    public bool Enabled => lastFmSettings.CurrentValue.EnableRadio && sources.Available.Count > 0;

    /// <param name="listener">Who the radio is for, named the way scrobbles name them
    /// (RequestIdentity), so what they play teaches their own weights; null for nobody.</param>
    public async Task<List<Song>> BuildAsync(RadioSeed seed, int count, string? listener,
        IReadOnlyDictionary<string, string> auth, IReadOnlySet<string> bans, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var share = radioSettings?.CurrentValue.EffectiveLibraryShare ?? 100;
        var libraryCap = (int)Math.Floor(count * share / 100d);
        // Strip collab/feature decoration so the catalogs find the canonical artist.
        RadioSeed Lookup(RadioSeed original) => original with
        {
            Artist = LastFmRadioSeedNormalizer.Artist(original.Artist) ?? original.Artist,
            Title = LastFmRadioSeedNormalizer.Title(original.Title) ?? original.Title,
        };
        // An album's next songs are asked as well, so its radio is the album's, not one song's.
        var seeds = new[] { Lookup(seed) }.Concat((seed.AlsoFrom ?? [])
            .Select(song => Lookup(new RadioSeed(song.Artist, song.Title, song.Duration, song)))).ToList();
        // Deeper than asked for: the middle of each list is where the less obvious songs are, and
        // filler, one-star songs, the artist cap and the library share all leave some out. With no
        // library share, Sounds alike (library songs only) is not asked.
        var ask = Math.Min(count * 2, 100);
        var answers = (await Task.WhenAll(seeds.Select(each => sources.AskAllAsync(each, ask, auth, ct,
            source => libraryCap > 0 || source.Provider != RadioProvider.SoundsAlike)))).SelectMany(each => each).ToList();

        // When no catalog has songs like this very song, the seed's own tags know its style better
        // than a guess from its artist's name, which for a YouTube upload is only the channel's (#78).
        var libraryLed = seed.LibrarySong is not null && !seed.IsArtist && RadioBlend.LibraryLed(answers, count);
        if (libraryLed)
        {
            if (libraryCap > 0)
                answers.Add(new RadioAnswer(RadioProvider.Library, RadioMatch.Song, [],
                    await resolver.LibraryNeighboursAsync(seed.LibrarySong!, count, auth)));
            if (RadioBlend.PrimaryGenre(seed.LibrarySong!.Genre) is { } genre)
                answers.AddRange(await sources.TagAllAsync(genre, count, ct));
        }

        var asked = clock.Elapsed;
        answers = answers.Select(answer => RadioBlend.WithoutFillerOrBans(answer, bans)).ToList();
        var weights = sources.Weights(listener);
        var seedKey = LastFmRadioSeedNormalizer.TrackKey(seed.Artist, seed.Title);
        var pool = RadioBlend.Blend(answers, weights, int.MaxValue, seedKey, seed.LibrarySong?.Id, wanted: count)
            .Where(pick => libraryCap > 0 || pick.LibrarySong is null).ToList();
        logger.LogInformation("Radio for '{Artist} - {Title}': {Answers}; weights {Weights}; {Picks} in the pool{Led}",
            seed.Artist, seed.Title,
            string.Join(", ", answers.Select(answer => $"{answer.Provider} {answer.Match} {answer.Count}")),
            string.Join(", ", weights.Select(pair => $"{pair.Key} {pair.Value:0.00}")),
            pool.Count, libraryLed ? ", led by the library" : "");

        var seedArtist = ArtistKey(seed.Artist);
        var known = share < 100 ? await KnownArtistsAsync(listener, ct) : new HashSet<string>();
        known.Remove(seedArtist);
        var servedKey = $"{listener}|{seedKey}";
        var servedLately = served?.Recent(servedKey) ?? new HashSet<string>();
        var fit = FitFactors(seed, pool);
        // The draw's weight: the blend's score squared, so a strong match still usually leads,
        // with artists the listener already knows and songs this seed served them lately held back.
        double Weight(RadioPick pick)
        {
            var (artist, title) = Names(pick);
            var weight = pick.Score * pick.Score;
            if (known.Contains(ArtistKey(artist))) weight *= LastFmRadioRecommendationService.KnownArtistWeight;
            if (servedLately.Contains(LastFmRadioSeedNormalizer.TrackKey(artist, title))) weight *= ServedLatelyWeight;
            if (pick.LibrarySong is { } song) weight *= fit.GetValueOrDefault(song.Id, 1.0);
            return weight;
        }
        var ordered = RadioShape.Order(pool, Weight, randomizer?.Invoke() ?? Random.Shared);

        var heard = HeardLately(listener);
        var songs = await TakeAsync(ordered, seed, count, libraryCap, seedArtist, heard, auth, ct);
        outcomes?.Served(listener, songs.Select(song => (song.Id, RadioProvider.FromDisplayName(song.SuggestedBy))));
        served?.Remember(servedKey, songs.Select(song => LastFmRadioSeedNormalizer.TrackKey(song.Artist, song.Title)));
        logger.LogInformation("Radio for '{Artist} - {Title}' took {Total} ms: sources {Asked} ms; {Songs} songs, {Local} from the library",
            seed.Artist, seed.Title, (int)clock.ElapsedMilliseconds, (int)asked.TotalMilliseconds, songs.Count,
            songs.Count(song => song.IsLocal));

        // A library-led radio keeps its order: its neighbours often share the seed's artist (one
        // uploader for a whole playlist), and spacing by artist would push every one of them back.
        return libraryLed ? songs : LastFmRadioSpacing.Spread(songs, song => song.Artist, seed.Artist);
    }

    /// <summary>
    /// The radio's songs, walking the draw: each round resolves the next picks (local first, then
    /// one Deezer check for every outside one), and a song joins unless it is unplayable, the seed,
    /// a library song past the share, filtered as explicit, or by an artist past the cap. A song
    /// heard in the last <see cref="LastFmRadioRecommendationService.HeardLately"/> waits, and
    /// joins only if the walk comes up short.
    /// </summary>
    private async Task<List<Song>> TakeAsync(List<RadioPick> ordered, RadioSeed seed, int count, int libraryCap,
        string seedArtist, IReadOnlySet<string> heard, IReadOnlyDictionary<string, string> auth, CancellationToken ct)
    {
        var output = new List<Song>();
        var held = new List<Song>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var perArtist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var local = 0;
        // An artist is a guest on a song radio; the seed's own artist may hold a quarter of it.
        int Cap(string artist) => artist == seedArtist ? Math.Max(3, count / 4) : Math.Max(2, count / 10);
        bool Full(string artist) => perArtist.GetValueOrDefault(artist) >= Cap(artist);
        var filter = subsonicSettings.CurrentValue.ExplicitFilter;
        var next = 0;
        for (var round = 0; round < ResolveRounds && output.Count < count && next < ordered.Count; round++)
        {
            var batch = new List<RadioPick>();
            var size = round == 0 ? (int)Math.Ceiling(count * PickHeadroom) : count;
            for (; next < ordered.Count && batch.Count < size; next++)
                if (!Full(ArtistKey(Names(ordered[next]).Artist))) batch.Add(ordered[next]);
            foreach (var song in await ResolveAsync(batch, seed, auth, ct))
            {
                if (output.Count >= count) break;
                var artist = ArtistKey(song.Artist);
                if (!ids.Add(song.Id) || Full(artist) || (song.IsLocal && local >= libraryCap)) continue;
                // The admin's explicit filter, as stations apply it: with outside songs from every
                // source, radio is where most unknown lyrics now come from.
                if (filter switch
                    {
                        ExplicitFilter.CleanOnly => song.ExplicitContentLyrics is 1,
                        ExplicitFilter.ExplicitOnly => song.ExplicitContentLyrics is 3,
                        _ => false,
                    }) continue;
                if (heard.Contains(LastFmRadioSeedNormalizer.TrackKey(song.Artist, song.Title))) { held.Add(song); continue; }
                output.Add(song);
                perArtist[artist] = perArtist.GetValueOrDefault(artist) + 1;
                if (song.IsLocal) local++;
            }
        }
        foreach (var song in held)
        {
            if (output.Count >= count) break;
            var artist = ArtistKey(song.Artist);
            if (Full(artist) || (song.IsLocal && local >= libraryCap)) continue;
            output.Add(song);
            perArtist[artist] = perArtist.GetValueOrDefault(artist) + 1;
            if (song.IsLocal) local++;
        }
        return output;
    }

    /// <summary>The picks as songs, in their order: for each, the local copy if we own it (tracks
    /// the user already has play at full quality from Navidrome and skip the YouTube round trip),
    /// else an outside placeholder Deezer can play. Unresolved and unplayable picks drop out.</summary>
    private async Task<List<Song>> ResolveAsync(List<RadioPick> picks, RadioSeed seed,
        IReadOnlyDictionary<string, string> auth, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(10);
        var resolved = await Task.WhenAll(picks.Select(async pick =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var song = pick.LibrarySong is { } library
                    ? (pick.NeedsReading ? await resolver.ReadLibrarySongAsync(library.Id, auth) : library)
                    : await resolver.ResolvePlaceholderAsync(pick.Track!.Artist, pick.Track.Title, pick.Track.Duration,
                        auth, pick.Track.YouTubeId);
                if (song is not null) song.SuggestedBy = RadioProvider.DisplayName(pick.Provider);
                return song;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "radio resolve failed for a {Provider} pick", pick.Provider);
                return null;
            }
            finally { gate.Release(); }
        }));
        // Every outside song is checked against Deezer in one batched pass, not one search each.
        var playable = (await metadata.PlayableAsync(resolved.OfType<Song>().Where(song => !song.IsLocal).ToList(), ct))
            .ToHashSet();
        return resolved.OfType<Song>()
            .Where(song => (song.IsLocal || playable.Contains(song)) && song.Id != seed.LibrarySong?.Id).ToList();
    }

    /// <summary>Sound check: a library pick that sounds far from the seed weighs less, one that
    /// sounds close more. It catches a catalog's wrong-mood pick that the other sources missed.
    /// Only songs Octo has analysed have a sound, so outside songs keep their weight.</summary>
    private IReadOnlyDictionary<string, double> FitFactors(RadioSeed seed, IEnumerable<RadioPick> pool) =>
        sonic is not null && radioSettings?.CurrentValue.SoundsAlike == true && seed.LibrarySong is { Id.Length: > 0 } seedSong
            ? sonic.FitFactors(seedSong.Id, pool.Where(pick => pick.LibrarySong is not null).Select(pick => pick.LibrarySong!.Id))
            : new Dictionary<string, double>();

    /// <summary>The artists this listener already knows: everyone they have played, and everyone
    /// in the library when Octo can read it.</summary>
    private async Task<HashSet<string>> KnownArtistsAsync(string? listener, CancellationToken ct)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (listener is not null && state is not null)
            foreach (var play in state.GetUser(listener).Plays) known.Add(ArtistKey(play.Artist));
        foreach (var name in await LibraryArtistsAsync(ct)) known.Add(ArtistKey(name));
        return known;
    }

    // ponytail: one process-wide copy of the library's artists, read again after ten minutes;
    // an artist added since plays as unknown until then. Upgrade: refresh on Navidrome's scan.
    private static (DateTime Until, IReadOnlyList<string> Names) _libraryArtists = (DateTime.MinValue, []);

    private async Task<IReadOnlyList<string>> LibraryArtistsAsync(CancellationToken ct)
    {
        if (library is null) return [];
        var cached = _libraryArtists;
        if (cached.Until > DateTime.UtcNow) return cached.Names;
        try
        {
            var names = await library.GetLibraryArtistNamesAsync(ct) ?? [];
            // An empty answer is often Navidrome not answering: asked again next radio.
            if (names.Count > 0) _libraryArtists = (DateTime.UtcNow.AddMinutes(10), names);
            return names;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "radio could not read the library's artists");
            return [];
        }
    }

    private IReadOnlySet<string> HeardLately(string? listener)
    {
        if (listener is null || state is null) return new HashSet<string>();
        var after = DateTime.UtcNow - LastFmRadioRecommendationService.HeardLately;
        return state.GetUser(listener).Plays.Where(play => play.PlayedAtUtc > after)
            .Select(play => LastFmRadioSeedNormalizer.TrackKey(play.Artist, play.Title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static (string Artist, string Title) Names(RadioPick pick) =>
        pick.Track is { } track ? (track.Artist, track.Title) : (pick.LibrarySong!.Artist, pick.LibrarySong.Title);

    /// <summary>One spelling per artist for the cap and the known set, as stations spell it.</summary>
    private static string ArtistKey(string artist) =>
        (LastFmRadioSeedNormalizer.Artist(artist) ?? artist).Trim().ToLowerInvariant();
}

/// <summary>
/// The songs each listener was served from each seed lately, so the next radio from the same
/// seed leads with others.
/// </summary>
// ponytail: in memory, so a restart forgets it, and capped at MaxSeeds seeds. Upgrade: keep it in
// RadioOutcomeStore beside what each served song taught.
public sealed class RadioServedMemory
{
    private const int MaxSeeds = 2000;
    private readonly ConcurrentDictionary<string, (DateTime At, HashSet<string> Keys)> _seeds = new();

    public IReadOnlySet<string> Recent(string key) =>
        _seeds.TryGetValue(key, out var entry) && entry.At > DateTime.UtcNow - SongRadioService.ServedLately
            ? entry.Keys : new HashSet<string>();

    public void Remember(string key, IEnumerable<string> songs)
    {
        var keys = songs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Two radios in a row from one seed both count: the third leads with neither.
        if (_seeds.TryGetValue(key, out var old) && old.At > DateTime.UtcNow - SongRadioService.ServedLately)
            keys.UnionWith(old.Keys);
        _seeds[key] = (DateTime.UtcNow, keys);
        if (_seeds.Count <= MaxSeeds) return;
        foreach (var stale in _seeds.OrderBy(pair => pair.Value.At).Take(_seeds.Count - MaxSeeds).Select(pair => pair.Key).ToList())
            _seeds.TryRemove(stale, out _);
    }
}
