using System.Text.Json;

namespace Octo.Services.Sonic;

/// <summary>One analyzed song.</summary>
public sealed class SonicSong
{
    public string Stamp { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? Album { get; set; }
    public int? Duration { get; set; }
    public int Version { get; set; }
    public float[] F { get; set; } = [];
}

public sealed class SonicFailure
{
    public string Stamp { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTime AtUtc { get; set; }
}

public sealed class SonicState
{
    public bool Paused { get; set; }
    public int Pass { get; set; }
    public DateTime? PassFinishedUtc { get; set; }
    public DateTime? NextPassUtc { get; set; }
    /// <summary>By Navidrome song id.</summary>
    public Dictionary<string, SonicSong> Songs { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SonicFailure> Failed { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// What every analyzed song sounds like, on disk beside the settings, and the songs nearest a
/// seed. Written at most every few seconds, like the review sweep's store.
/// </summary>
public sealed class SonicStore
{
    // A whole library's numbers are megabytes of JSON; once a minute is plenty, and a crash
    // costs at most a minute of analysis.
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMinutes(1);
    private readonly string _path;
    private readonly ILogger<SonicStore> _logger;
    private readonly object _lock = new();
    /// <summary>Held from the snapshot to the file move, so an older snapshot never lands last.</summary>
    private readonly object _fileLock = new();
    private SonicState _state;
    private bool _dirty;
    private DateTime _lastFlush = DateTime.MinValue;

    public SonicStore(string path, ILogger<SonicStore> logger)
    {
        _path = path; _logger = logger;
        _state = Load();
    }

    public T Read<T>(Func<SonicState, T> read) { lock (_lock) return read(_state); }

    public void Write(Action<SonicState> write)
    {
        lock (_lock) { write(_state); _dirty = true; }
        if (DateTime.UtcNow - _lastFlush >= FlushEvery) Flush();
    }

    public void Flush()
    {
        lock (_fileLock)
        {
            string json;
            lock (_lock)
            {
                if (!_dirty) return;
                json = JsonSerializer.Serialize(_state);
                _dirty = false;
                _lastFlush = DateTime.UtcNow;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not save {Path}", _path); lock (_lock) _dirty = true; }
        }
    }

    /// <summary>Library songs that sound most like the seed, nearest first. Nothing for a song not analyzed yet.</summary>
    public IReadOnlyList<(string Id, SonicSong Song, double Distance)> Nearest(string seedId, int count)
    {
        lock (_lock)
        {
            if (!_state.Songs.TryGetValue(seedId, out var seed) || seed.F.Length == 0) return [];
            return _state.Songs.Where(pair => pair.Key != seedId && pair.Value.Version == seed.Version
                    && pair.Value.F.Length == seed.F.Length)
                .Select(pair => (pair.Key, pair.Value, Distance(seed.F, pair.Value.F, seed.Version)))
                .OrderBy(item => item.Item3).Take(count).ToList();
        }
    }

    /// <summary>
    /// bliss 0.13's own distance for feature version 2 (its `VERSION2_WEIGHTS`, a weighted
    /// Euclidean): tempo counts a quarter, since its beat tracker is unreliable on music with few
    /// onsets, and the 13 harmony values share the weight of 3, so harmony cannot outvote timbre
    /// and loudness. Another version's numbers are compared unweighted.
    /// </summary>
    internal static readonly float[] Version2Weights =
    [
        0.25f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f,
        3f / 13, 3f / 13, 3f / 13, 3f / 13, 3f / 13, 3f / 13, 3f / 13,
        3f / 13, 3f / 13, 3f / 13, 3f / 13, 3f / 13, 3f / 13,
    ];

    internal static double Distance(float[] a, float[] b, int version = 2)
    {
        var weights = version == 2 && a.Length == Version2Weights.Length ? Version2Weights : null;
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += (weights?[i] ?? 1f) * d * d;
        }
        return Math.Sqrt(sum);
    }

    internal const double FitClose = 1.2;
    internal const double FitFar = 0.6;

    /// <summary>
    /// How well each candidate's sound fits the seed, as a score multiplier: <see cref="FitClose"/>
    /// at or under the 25th percentile of the seed's distances to the whole library,
    /// <see cref="FitFar"/> at or over the 75th, in a straight line between. Measured against the
    /// seed's own spread, because how far "close" is differs from library to library and song to
    /// song. Empty when the seed has not been analyzed; a candidate without a sound is left out.
    /// </summary>
    public IReadOnlyDictionary<string, double> FitFactors(string seedId, IEnumerable<string> candidateIds)
    {
        lock (_lock)
        {
            if (!_state.Songs.TryGetValue(seedId, out var seed) || seed.F.Length == 0) return new Dictionary<string, double>();
            var all = _state.Songs.Where(pair => pair.Key != seedId && pair.Value.Version == seed.Version
                    && pair.Value.F.Length == seed.F.Length)
                .Select(pair => Distance(seed.F, pair.Value.F, seed.Version)).OrderBy(d => d).ToList();
            if (all.Count < 8) return new Dictionary<string, double>();
            var near = all[all.Count / 4];
            var far = all[all.Count * 3 / 4];
            var fit = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var id in candidateIds)
            {
                if (!_state.Songs.TryGetValue(id, out var song) || song.Version != seed.Version || song.F.Length != seed.F.Length) continue;
                var d = Distance(seed.F, song.F, seed.Version);
                fit[id] = d <= near ? FitClose : d >= far || far <= near ? FitFar
                    : FitClose + (FitFar - FitClose) * (d - near) / (far - near);
            }
            return fit;
        }
    }

    /// <summary>Analyzed loved songs a listener needs before their taste is read from them.</summary>
    internal const int TasteMinimum = 10;
    /// <summary>Share of a pool's analyzed songs a taste keeps: the ones that sound most like it.</summary>
    internal const double TasteShare = 0.6;

    /// <summary>
    /// What a listener's loved songs sound like on average, or null with fewer than
    /// <see cref="TasteMinimum"/> of them analyzed (version 2, whose numbers average sensibly).
    /// </summary>
    public float[]? Taste(IEnumerable<string> lovedIds)
    {
        lock (_lock)
        {
            var sounds = lovedIds.Distinct(StringComparer.Ordinal)
                .Select(id => _state.Songs.GetValueOrDefault(id))
                .Where(song => song is { Version: 2 } && song.F.Length == Version2Weights.Length)
                .Select(song => song!.F).ToList();
            if (sounds.Count < TasteMinimum) return null;
            var mean = new float[Version2Weights.Length];
            foreach (var f in sounds)
                for (var i = 0; i < mean.Length; i++) mean[i] += f[i] / sounds.Count;
            return mean;
        }
    }

    /// <summary>
    /// The pool with its analyzed songs that sound furthest from the taste left out: the closest
    /// <see cref="TasteShare"/> stay, and never fewer than <paramref name="keepAtLeast"/> songs in
    /// all. Songs not analyzed yet always stay, and the pool keeps its order. No taste, the pool as it is.
    /// </summary>
    public IReadOnlyList<T> NearTaste<T>(IReadOnlyList<T> pool, Func<T, string?> id, float[]? taste, int keepAtLeast)
    {
        if (taste is null) return pool;
        HashSet<int> far;
        lock (_lock)
        {
            var analyzed = pool.Select((item, index) => (Index: index, Song: id(item) is { } key ? _state.Songs.GetValueOrDefault(key) : null))
                .Where(item => item.Song is { Version: 2 } && item.Song.F.Length == taste.Length)
                .Select(item => (item.Index, Distance: Distance(taste, item.Song!.F)))
                .OrderBy(item => item.Distance).ThenBy(item => item.Index).ToList();
            var keep = Math.Max((int)Math.Ceiling(analyzed.Count * TasteShare),
                Math.Min(analyzed.Count, keepAtLeast - (pool.Count - analyzed.Count)));
            far = analyzed.Skip(keep).Select(item => item.Index).ToHashSet();
        }
        return pool.Where((_, index) => !far.Contains(index)).ToList();
    }

    /// <summary>
    /// The list with its analyzed library songs put in an order that flows: from the first, each
    /// next one is, of the following <paramref name="window"/> analyzed songs, the one that sounds
    /// most like the one before, never by the same artist as it. Other songs keep their places.
    /// The same list gives the same order.
    /// </summary>
    public IReadOnlyList<T> Flow<T>(IReadOnlyList<T> items, Func<T, string?> libraryId, Func<T, string> artist, int window)
    {
        var result = items.ToList();
        lock (_lock)
        {
            float[]? Sound(T item) => libraryId(item) is { } key && _state.Songs.GetValueOrDefault(key) is { Version: 2 } song
                && song.F.Length == Version2Weights.Length ? song.F : null;
            var slots = Enumerable.Range(0, result.Count).Where(index => Sound(result[index]) is not null).ToList();
            for (var k = 1; k < slots.Count; k++)
            {
                var previous = result[slots[k - 1]];
                var before = Sound(previous)!;
                var best = -1;
                var bestDistance = double.MaxValue;
                for (var j = k; j < Math.Min(slots.Count, k + window); j++)
                {
                    var candidate = result[slots[j]];
                    if (string.Equals(artist(candidate), artist(previous), StringComparison.OrdinalIgnoreCase)) continue;
                    var distance = Distance(before, Sound(candidate)!);
                    if (distance < bestDistance) { best = j; bestDistance = distance; }
                }
                if (best > k) (result[slots[k]], result[slots[best]]) = (result[slots[best]], result[slots[k]]);
            }
        }
        return result;
    }

    private SonicState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var state = JsonSerializer.Deserialize<SonicState>(File.ReadAllText(_path)) ?? new SonicState();
                // A hand-edited or half-written file can hold nulls; they are dropped, not crashed on.
                state.Songs = (state.Songs ?? []).Where(pair => pair.Value is { F: not null, Stamp: not null })
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                state.Failed = (state.Failed ?? []).Where(pair => pair.Value is { Stamp: not null })
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                return state;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "{Path} could not be read; starting over", _path); }
        return new SonicState();
    }
}
