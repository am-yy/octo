using System.Text.Json;
using System.Text.Json.Serialization;
using Octo.Services.Common;

namespace Octo.Services.CoverArt;

/// <summary>
/// The design of generated list covers as numbers, read from Design/cover-design.json, and the
/// library of painted backgrounds from Design/Backgrounds/backgrounds.json. The Octo apps draw
/// their playlist covers from the same files, so the server's covers match theirs: sizes and
/// places are shares of the cover's side.
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

    public sealed record LayoutNumbers(float Margin, double Contrast, int TinyBelowPx,
        TitleNumbers Title, LineNumbers Line, FooterNumbers Footer);

    public sealed record VeilTitle(double Pad, double[] Falloff, double AimContrast, double MinContrast, double MaxDrop);

    public sealed record VeilFooter(double Pad, double[] Falloff, double Contrast);

    public sealed record VeilYellow(double[] Hues, double Split, double[] Towards, double TurnPerDrop, double ChromaFrom, double ChromaLift);

    /// <summary>How a list's background is picked from the library by its music's colour.</summary>
    public sealed record BackgroundRule(int Nearest, double HueStep, double GreyBelow, double GreyPenalty,
        double ChromaWeight, double LightnessWeight);

    /// <summary>How the background is darkened under the words, keeping its colour.</summary>
    public sealed record VeilNumbers(double Margin, int Refine, VeilTitle Title, VeilFooter Footer, VeilYellow Yellow);

    public sealed record Hue(double L, double C, double H);

    /// <summary>One painted background: its file and its colours, strongest first.</summary>
    public sealed record Background(string File, string Name, string Family, Hue[] Hues, double MeanLightness);

    private sealed record Document(int Version, FontFiles Fonts, LayoutNumbers Layout, BackgroundRule Background, VeilNumbers Veil);

    private sealed record Library(int Size, Background[] Backgrounds);

    private sealed record HueDocument(double Chroma, Dictionary<string, double> Hues);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public int Version { get; }
    public FontFiles Fonts { get; }
    public LayoutNumbers Layout { get; }
    public VeilNumbers Veil { get; }
    public BackgroundRule BackgroundChoice { get; }
    public IReadOnlyList<Background> Backgrounds { get; }

    /// <summary>The side the backgrounds are stored at.</summary>
    public int BackgroundSize { get; }

    private readonly double _listChroma;
    private readonly Dictionary<string, double> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _parts = new(StringComparer.Ordinal);

    private CoverBook(Document document, Library library, HueDocument? hues)
    {
        Version = document.Version;
        Fonts = document.Fonts;
        Layout = document.Layout;
        Veil = document.Veil;
        BackgroundChoice = document.Background;
        Backgrounds = library.Backgrounds.Where(b => b.Hues is { Length: > 0 }).ToList();
        BackgroundSize = library.Size;
        if (Backgrounds.Count == 0) throw new InvalidDataException("the cover library has no backgrounds");
        _listChroma = hues?.Chroma ?? 0.14;
        foreach (var (name, hue) in hues?.Hues ?? [])
        {
            _exact.TryAdd(SongIdentity.Key(name), hue);
            // "R&B & Soul" answers to "R&B" and to "Soul" as well.
            foreach (var part in name.Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _parts.TryAdd(SongIdentity.Key(part), hue);
        }
    }

    private static readonly Lazy<CoverBook> Shipped = new(() =>
        Parse(Text("cover-design.json"), Text("Backgrounds.backgrounds.json"), Text("list-hues.json")));

    /// <summary>The book shipped inside the app.</summary>
    public static CoverBook Default => Shipped.Value;

    public static CoverBook Parse(string design, string library, string? listHues = null) => new(
        JsonSerializer.Deserialize<Document>(design, Json) ?? throw new InvalidDataException("empty cover design"),
        JsonSerializer.Deserialize<Library>(library, Json) ?? throw new InvalidDataException("empty cover library"),
        listHues is null ? null : JsonSerializer.Deserialize<HueDocument>(listHues, Json));

    internal static Stream Resource(string name) =>
        typeof(CoverBook).Assembly.GetManifestResourceStream("Octo.CoverDesign." + name)
        ?? throw new FileNotFoundException("missing embedded cover design resource", name);

    private static string Text(string name)
    {
        using var reader = new StreamReader(Resource(name));
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
