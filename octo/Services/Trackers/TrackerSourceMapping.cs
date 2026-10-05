namespace Octo.Services.Trackers;

public static class TrackerSourceMapping
{
    public static string? Resolve(IConfiguration config, string? indexer)
    {
        if (string.IsNullOrWhiteSpace(indexer)) return null;
        var red = config.GetSection("Trackers:red:LidarrIndexerNames").Get<string[]>() ?? [];
        var ops = config.GetSection("Trackers:ops:LidarrIndexerNames").Get<string[]>() ?? [];
        if (red.Length == 0 || ops.Length == 0 || red.Any(string.IsNullOrWhiteSpace)
            || ops.Any(string.IsNullOrWhiteSpace) || red.Intersect(ops, StringComparer.Ordinal).Any()) return null;
        var targets = new[] { (Name: "red", Names: red), (Name: "ops", Names: ops) }
            .Where(t => t.Names.Contains(indexer, StringComparer.Ordinal)).ToList();
        return targets.Count == 1 ? targets[0].Name : null;
    }
}
