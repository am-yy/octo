using System.Globalization;
using System.Text;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;

namespace Octo.Services.CoverArt;

/// <summary>
/// The cover type: Inter Display in the three weights the design names, shipped inside the app
/// so every box sets the same letters, with the system's fonts behind it for writing Inter does
/// not cover (Chinese, Japanese, Korean, Arabic, Hebrew, emoji). The Docker image installs Noto
/// CJK and Symbola for that; DejaVu was already there.
/// </summary>
internal static class CoverFonts
{
    /// <summary>Tried in this order for letters Inter lacks; only the installed ones count.</summary>
    private static readonly string[] FallbackNames =
    [
        "Noto Sans CJK SC", "Noto Sans CJK JP", "Noto Sans CJK KR", "Noto Sans CJK TC",
        "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Microsoft JhengHei",
        "Noto Sans Arabic", "Noto Sans Hebrew", "Segoe UI", "DejaVu Sans", "Noto Sans",
        "Segoe UI Symbol", "Segoe UI Emoji", "Symbola", "Noto Emoji",
    ];

    private static readonly Lazy<Dictionary<string, FontFamily>> Shipped = new(() =>
    {
        var families = new Dictionary<string, FontFamily>(StringComparer.Ordinal);
        var book = CoverBook.Default;
        foreach (var file in new[] { book.Fonts.Title, book.Fonts.Line, book.Fonts.Footer }.Distinct())
        {
            // One collection each: the weights are separate families that share a name stem.
            var collection = new FontCollection();
            using var stream = typeof(CoverFonts).Assembly.GetManifestResourceStream("Octo.CoverDesign.Fonts." + file)
                ?? throw new FileNotFoundException("missing embedded cover font", file);
            families[file] = collection.Add(stream);
        }
        return families;
    });

    private static readonly Lazy<IReadOnlyList<FontFamily>> Installed = new(() =>
    {
        var found = new List<FontFamily>();
        try
        {
            foreach (var name in FallbackNames)
                if (SystemFonts.TryGet(name, CultureInfo.InvariantCulture, out var family) && !found.Contains(family))
                    found.Add(family);
        }
        catch (Exception)
        {
            // No system fonts at all: Inter alone.
        }
        return found;
    });

    public static IReadOnlyList<FontFamily> Fallbacks => Installed.Value;

    /// <summary>The design's file for a weight: 600 the name, 300 the light line, anything else the foot line.</summary>
    public static FontFamily ForWeight(int weight)
    {
        var fonts = CoverBook.Default.Fonts;
        var file = weight >= 500 ? fonts.Title : weight < 350 ? fonts.Line : fonts.Footer;
        return Shipped.Value[file];
    }

    /// <summary>Whether the font draws the character itself, not its empty box.</summary>
    public static bool Has(Font font, CodePoint cp) =>
        font.TryGetGlyphs(cp, ColorFontSupport.None, out var glyphs) && glyphs.Any(g => g.GlyphMetrics.GlyphId != 0);

    /// <summary>
    /// The font to set <paramref name="text"/> in: Inter, unless it holds letters Inter does not
    /// have; then the installed font that covers most of them, in bold for the name so a Japanese
    /// name is as heavy as an English one, with Inter behind it.
    /// </summary>
    public static (Font Font, IReadOnlyList<FontFamily> Fallbacks) For(string text, int weight, float size)
    {
        var (family, style, fallbacks) = Choices.GetOrAdd((text, weight), key => Choose(key.Text, key.Weight));
        return (family.CreateFont(size, style), fallbacks);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Text, int Weight),
        (FontFamily Family, FontStyle Style, IReadOnlyList<FontFamily> Fallbacks)> Choices = new();

    private static (FontFamily, FontStyle, IReadOnlyList<FontFamily>) Choose(string text, int weight)
    {
        if (Choices.Count >= 20_000) Choices.Clear();
        var interFamily = ForWeight(weight);
        var inter = interFamily.CreateFont(16, FontStyle.Regular);
        var missing = new List<CodePoint>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            var cp = new CodePoint(rune.Value);
            if (!Has(inter, cp)) missing.Add(cp);
        }
        if (missing.Count == 0) return (interFamily, FontStyle.Regular, Fallbacks);

        FontFamily? best = null;
        var bestCount = 0;
        foreach (var family in Fallbacks)
        {
            var font = family.CreateFont(16, FontStyle.Regular);
            var count = missing.Count(cp => Has(font, cp));
            if (count > bestCount) (best, bestCount) = (family, count);
        }
        if (best is not { } chosen) return (interFamily, FontStyle.Regular, Fallbacks);

        var bold = weight >= 500 && chosen.GetAvailableStyles().Contains(FontStyle.Bold);
        var rest = new List<FontFamily> { interFamily };
        rest.AddRange(Fallbacks.Where(f => !f.Equals(chosen)));
        return (chosen, bold ? FontStyle.Bold : FontStyle.Regular, rest);
    }
}

/// <summary>
/// The design's text engine on SixLabors.Fonts: lines broken between words (and between
/// Chinese, Japanese and Korean characters), each line's height the size times the line
/// height with the letters centred in it, and an ellipsis where words are cut, as the apps
/// set them.
/// </summary>
internal sealed class CoverTypesetter : ICoverTypesetter
{
    public sealed record Line(string Text, float Width);

    public Measured Measure(string text, CoverType type, float width)
    {
        var (lines, cut) = Lines(text, type, width);
        var widest = lines.Count == 0 ? 0f : lines.Max(line => line.Width);
        return new Measured(lines.Count, widest, MathF.Ceiling(lines.Count * type.SizePx * type.LineHeight), cut);
    }

    public float WidthOf(string text, CoverType type) => MathF.Ceiling(Advance(text, type));

    private static TextOptions Options(Font font, IReadOnlyList<FontFamily> fallbacks) => new(font)
    {
        FallbackFontFamilies = fallbacks,
        ColorFontSupport = ColorFontSupport.None,
        KerningMode = KerningMode.Standard,
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int, float), float> Advances = new();

    /// <summary>The width of the text on one line. Remembered, since fitting asks the same widths often.</summary>
    internal static float Advance(string text, CoverType type)
    {
        if (text.Length == 0) return 0f;
        var key = (text, type.Weight, type.SizePx);
        if (Advances.TryGetValue(key, out var known)) return known;
        var (font, fallbacks) = CoverFonts.For(text, type.Weight, type.SizePx);
        var width = TextMeasurer.MeasureAdvance(text, Options(font, fallbacks)).Width;
        if (Advances.Count >= 50_000) Advances.Clear();
        Advances[key] = width;
        return width;
    }

    /// <summary>The text broken into at most <c>type.MaxLines</c> lines of <paramref name="width"/>.</summary>
    public (List<Line> Lines, bool Cut) Lines(string text, CoverType type, float width)
    {
        var pieces = Pieces(text);
        var lines = new List<string>();
        var current = new StringBuilder();
        var index = 0;
        for (; index < pieces.Count; index++)
        {
            var (piece, spaced) = pieces[index];
            var candidate = current.Length == 0 ? piece : current + (spaced ? " " : "") + piece;
            if (current.Length == 0 || Advance(candidate, type) <= width)
            {
                current.Clear().Append(candidate);
                continue;
            }
            lines.Add(current.ToString());
            current.Clear().Append(piece);
        }
        if (current.Length > 0) lines.Add(current.ToString());

        var max = Math.Max(1, type.MaxLines);
        var cut = lines.Count > max;
        if (cut)
        {
            var rest = string.Join(" ", lines.Skip(max - 1));
            lines = [.. lines.Take(max - 1), Ellipsize(rest, type, width)];
        }
        // A single word wider than the line is cut too.
        for (var i = 0; i < lines.Count; i++)
        {
            if (Advance(lines[i], type) <= width) continue;
            lines[i] = Ellipsize(lines[i], type, width);
            cut = true;
        }
        return (lines.Select(line => new Line(line, Advance(line, type))).ToList(), cut);
    }

    private static string Ellipsize(string text, CoverType type, float width)
    {
        var graphemes = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) graphemes.Add(e.GetTextElement());
        for (var keep = graphemes.Count; keep > 0; keep--)
        {
            var candidate = string.Concat(graphemes.Take(keep)).TrimEnd() + "…";
            if (Advance(candidate, type) <= width) return candidate;
        }
        return "…";
    }

    /// <summary>The unbreakable pieces of the text, each marked with whether a space came before it.</summary>
    private static List<(string Piece, bool Spaced)> Pieces(string text)
    {
        var pieces = new List<(string, bool)>();
        var word = new StringBuilder();
        var wordSpaced = false;
        var space = false;
        void End()
        {
            if (word.Length > 0) pieces.Add((word.ToString(), wordSpaced));
            word.Clear();
        }
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                End();
                space = true;
            }
            else if (CoverLayout.IsWide(rune.Value))
            {
                End();
                pieces.Add((rune.ToString(), space));
                space = false;
            }
            else
            {
                if (word.Length == 0)
                {
                    wordSpaced = space;
                    space = false;
                }
                word.Append(rune.ToString());
            }
        }
        End();
        return pieces;
    }

    /// <summary>
    /// Where each line's letters go: its left edge and baseline. A line is the size times the
    /// line height tall, with the font's ascent and descent centred in it.
    /// </summary>
    public IEnumerable<(string Text, Font Font, IReadOnlyList<FontFamily> Fallbacks, float X, float Baseline)> Place(CoverWords words)
    {
        var type = words.Type;
        var (lines, _) = Lines(words.Text, type, words.Width);
        var lineHeight = type.SizePx * type.LineHeight;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var (font, fallbacks) = CoverFonts.For(line.Text, type.Weight, type.SizePx);
            var metrics = font.FontMetrics;
            var scale = type.SizePx / metrics.UnitsPerEm;
            var ascent = metrics.HorizontalMetrics.Ascender * scale;
            var descent = -metrics.HorizontalMetrics.Descender * scale;
            var baseline = words.Top + i * lineHeight + (lineHeight - (ascent + descent)) / 2 + ascent;
            var x = words.Align == CoverAlign.Left ? words.Left : words.Left + words.Width - line.Width;
            yield return (line.Text, font, fallbacks, x, baseline);
        }
    }
}
