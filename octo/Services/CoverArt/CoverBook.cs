using System.Text.Json;
using System.Text.Json.Serialization;
using Octo.Services.Common;

namespace Octo.Services.CoverArt;

/// <summary>
/// The design of generated list covers as numbers, read from Design/cover-design.json. The Octo
/// apps draw their playlist covers from the same file, so the server's covers match theirs:
/// sizes and places are shares of the cover's side, colours sRGB hex.
/// </summary>
public sealed class CoverBook
{
    public sealed record FontFiles(string Family, string Title, string Line, string Footer);

    public sealed record TitleNumbers(float Top, int Weight, float Size, float OneLineDownTo, float WrapSize,
        float MinSize, float MinPx, int MaxLines, float LineHeight, float Tracking,
        float OneLineMinPx = 0, float WrapMinPx = 0);

    public sealed record LineNumbers(int Weight, float ShareOfTitle, float MinPx, float LineHeight, float Tracking, int ShowFromPx);

    public sealed record FooterNumbers(int Weight, float Size, float MinPx, float Bottom, float Opacity, float LineHeight,
        float Tracking, int ShowFromPx);

    public sealed record LayoutNumbers(float Margin, double Contrast, int TinyBelowPx, float TinyMargin, float TinySize,
        TitleNumbers Title, LineNumbers Line, FooterNumbers Footer);

    public sealed record MusicRule(double MaxChroma, double MinChroma = 0);

    public sealed record Base(float[] From, float[] To, float[][] Stops);

    /// <summary>A disc of one colour, solid out to (1 - soft) of its radius, then fading to nothing.</summary>
    public sealed record Fold(float[] Centre, float Radius, float Soft, int Colour);

    /// <summary>A faint round light, its opacity at the centre and nothing at its radius.</summary>
    public sealed record Light(float[] Centre, float Radius, string Colour, float Opacity);

    public sealed record Gradient(string Name, string[] Colours, Base Base, Fold[]? Folds = null, Light? Light = null);

    private sealed record Document(int Version, FontFiles Fonts, LayoutNumbers Layout, MusicRule Music, Gradient[] Gradients);

    private sealed record HueDocument(double Chroma, Dictionary<string, double> Hues);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public int Version { get; }
    public FontFiles Fonts { get; }
    public LayoutNumbers Layout { get; }
    public MusicRule Music { get; }
    public IReadOnlyList<Gradient> Gradients { get; }

    private readonly double _listChroma;
    private readonly Dictionary<string, double> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _parts = new(StringComparer.Ordinal);

    private CoverBook(Document document, HueDocument? hues)
    {
        Version = document.Version;
        Fonts = document.Fonts;
        Layout = document.Layout;
        Music = document.Music;
        Gradients = document.Gradients;
        if (Gradients.Count == 0) throw new InvalidDataException("the cover design has no gradients");
        _listChroma = hues?.Chroma ?? 0.14;
        foreach (var (name, hue) in hues?.Hues ?? [])
        {
            _exact.TryAdd(SongIdentity.Key(name), hue);
            // "R&B & Soul" answers to "R&B" and to "Soul" as well.
            foreach (var part in name.Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _parts.TryAdd(SongIdentity.Key(part), hue);
        }
    }

    private static readonly Lazy<CoverBook> Shipped = new(() => Parse(Resource("cover-design.json"), Resource("list-hues.json")));

    /// <summary>The book shipped inside the app.</summary>
    public static CoverBook Default => Shipped.Value;

    public static CoverBook Parse(string design, string? listHues = null) => new(
        JsonSerializer.Deserialize<Document>(design, Json) ?? throw new InvalidDataException("empty cover design"),
        listHues is null ? null : JsonSerializer.Deserialize<HueDocument>(listHues, Json));

    internal static string Resource(string name)
    {
        using var stream = typeof(CoverBook).Assembly.GetManifestResourceStream("Octo.CoverDesign." + name)
            ?? throw new FileNotFoundException("missing embedded cover design resource", name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// A stand-in for the music's colour when a list's songs give none: its genre's or decade's
    /// hue. A trailing " Mix" or " Radio" is not part of the name, so "Rock Mix" finds Rock's.
    /// </summary>
    public (double Hue, double Chroma)? ListHue(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var bare = name.Trim();
        foreach (var candidate in new[] { bare, Without(bare, " Mix"), Without(bare, " Radio") })
        {
            var key = SongIdentity.Key(candidate);
            if (key.Length == 0) continue;
            if (_exact.TryGetValue(key, out var hue) || _parts.TryGetValue(key, out hue)) return (hue, _listChroma);
        }
        return null;
    }

    private static string Without(string name, string suffix) =>
        name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length ? name[..^suffix.Length] : name;
}
