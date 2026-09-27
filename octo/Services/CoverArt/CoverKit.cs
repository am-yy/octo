using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Octo.Services.Common;

namespace Octo.Services.CoverArt;

/// <summary>
/// ncfer's cover kit (#54): a palette of named colours and one SVG template per genre, decade and
/// station, shipped under Assets/cover-kit. Matching is exact on purpose: a cover that is almost
/// the right genre is the wrong genre, and anything unmatched gets the kit's generic template in a
/// colour of its own instead.
/// </summary>
public sealed class CoverKit
{
    /// <summary>A template and the two background colours to fill it with.</summary>
    public sealed record Entry(string Name, string TemplatePath, string From, string To);

    private sealed record Swatch(string Name, string From, string To);

    private readonly string _templates;
    private readonly Dictionary<string, Swatch> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Swatch> _parts = new(StringComparer.Ordinal);

    public string Stroke { get; } = "#FFFFFF";
    public double StrokeWidth { get; } = 1.9;
    public bool IsLoaded => _exact.Count > 0;

    public CoverKit(string directory, ILogger? logger = null)
    {
        _templates = Path.Combine(directory, "templates");
        var palette = Path.Combine(directory, "palette.json");
        if (!File.Exists(palette)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(palette));
            var root = document.RootElement;
            if (root.TryGetProperty("stroke_default", out var stroke) && stroke.GetString() is { Length: > 0 } strokeColour)
                Stroke = strokeColour;
            if (root.TryGetProperty("stroke_width_default", out var width) && width.TryGetDouble(out var strokeWidth))
                StrokeWidth = strokeWidth;
            if (!root.TryGetProperty("lists", out var lists) || lists.ValueKind != JsonValueKind.Object) return;

            foreach (var list in lists.EnumerateObject())
            {
                if (!list.Value.TryGetProperty("from", out var from) || !list.Value.TryGetProperty("to", out var to)) continue;
                var swatch = new Swatch(list.Name, from.GetString() ?? "", to.GetString() ?? "");
                _exact.TryAdd(SongIdentity.Key(list.Name), swatch);
                // "R&B & Soul" answers to "R&B" and to "Soul" as well.
                foreach (var part in list.Name.Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    _parts.TryAdd(SongIdentity.Key(part), swatch);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("The cover kit palette could not be read ({M}); covers use the generic design", ex.Message);
            _exact.Clear();
            _parts.Clear();
        }
    }

    /// <summary>
    /// The kit's template for a genre, decade or station name. The playlist markers in front and
    /// a trailing " Mix" or " Radio" are not part of the name, so "Rock Mix", "Hip-Hop Radio" and
    /// "Your Mix" all find theirs.
    /// </summary>
    public bool TryResolve(string name, out Entry entry)
    {
        entry = null!;
        foreach (var candidate in Candidates(name))
        {
            var key = SongIdentity.Key(candidate);
            if (key.Length == 0) continue;
            if ((_exact.TryGetValue(key, out var swatch) || _parts.TryGetValue(key, out swatch))
                && Template(swatch.Name) is { } template)
            {
                entry = new Entry(swatch.Name, template, swatch.From, swatch.To);
                return true;
            }
        }
        return false;
    }

    /// <summary>The kit's "Other" design, in the colours this name hashes to.</summary>
    public Entry? GenericEntry(string name)
    {
        var template = Template("Other");
        if (template is null) return null;
        var (from, to) = Generic(name);
        return new Entry("Other", template, from, to);
    }

    /// <summary>
    /// A colour pair of the kit's own family for any name: the hue from the name's hash, the
    /// saturation and lightness the kit uses for every list. The same name always gets the same
    /// colours.
    /// </summary>
    public static (string From, string To) Generic(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(SongIdentity.Key(name)));
        var hue = ((hash[0] << 8) | hash[1]) % 360;
        return (Hsl(hue, 0.42, 0.52), Hsl((hue + 14) % 360, 0.48, 0.34));
    }

    /// <summary>The kit's file name rule for a list name.</summary>
    public static string Slug(string name) =>
        name.Replace(" ", "_").Replace("&", "and").Replace("◆", "d").Replace("▸", "t").Replace("*", "s");

    private string? Template(string name)
    {
        var path = Path.Combine(_templates, Slug(name) + ".svg");
        return File.Exists(path) ? path : null;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        var bare = name.Trim().TrimStart('▸', '◆', '*', ' ');
        if (bare.StartsWith("\U0001F6E0", StringComparison.Ordinal)) bare = bare["\U0001F6E0".Length..].TrimStart();
        yield return bare;
        foreach (var suffix in new[] { " Mix", " Radio" })
            if (bare.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && bare.Length > suffix.Length)
                yield return bare[..^suffix.Length];
    }

    private static string Hsl(int hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        var m = lightness - chroma / 2;
        var (r, g, b) = (hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return string.Create(CultureInfo.InvariantCulture, $"#{Channel(r + m):X2}{Channel(g + m):X2}{Channel(b + m):X2}");
    }
}
