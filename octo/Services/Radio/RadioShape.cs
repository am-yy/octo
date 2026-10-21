namespace Octo.Services.Radio;

/// <summary>
/// The draw stations and song radio share: Efraimidis-Spirakis, where each candidate draws
/// u^(1/weight) and the pool is sorted by that, so a candidate's chance of landing near the front
/// is proportional to its weight and every build draws differently.
/// </summary>
public static class RadioShape
{
    /// <summary>One candidate's draw; higher goes first.</summary>
    public static double Draw(Random random, double weight) => Math.Pow(random.NextDouble(), 1d / weight);

    /// <summary>The pool in draw order, the heaviest candidate first whatever it draws: a radio
    /// opens on its best match. Weights at or under zero sort last.</summary>
    public static List<T> Order<T>(IReadOnlyList<T> pool, Func<T, double> weight, Random random) where T : class
    {
        if (pool.Count == 0) return [];
        var first = pool.MaxBy(weight)!;
        return [first, .. pool.Where(item => !ReferenceEquals(item, first))
            .Select(item => (Item: item, Key: Draw(random, Math.Max(1e-6, weight(item)))))
            .OrderByDescending(pair => pair.Key).Select(pair => pair.Item)];
    }
}
