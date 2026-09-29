using System.Globalization;
using System.Text;

namespace Octo.Services.CoverArt;

// What a cover is made of, as the Octo apps compose it (app.winters.octo.covers): the
// gradient's layers, where the words go and how big, and the darkening that keeps white
// words readable. Painting is CoverPainter's.

/// <summary>A colour at a point of a gradient, 0 to 1 along it; the colour may be see-through.</summary>
public readonly record struct Stop(float At, int Argb);

/// <summary>What a cover is painted with, bottom first, in pixels.</summary>
public abstract record CoverLayer
{
    public sealed record Linear(float X0, float Y0, float X1, float Y1, IReadOnlyList<Stop> Stops) : CoverLayer;

    public sealed record Radial(float Cx, float Cy, float Radius, IReadOnlyList<Stop> Stops) : CoverLayer;
}

public enum CoverAlign { Left, Right }

/// <summary>The kinds of writing a cover sets differently.</summary>
public enum CoverScript { Latin, Wide, Tall, Emoji }

/// <summary>How words are set: size in pixels, weight, tracking and line height (shares of the size), most lines.</summary>
public sealed record CoverType(float SizePx, int Weight, float Tracking, float LineHeight, int MaxLines);

/// <summary>What the text engine found: lines, the widest line, the height, and whether words were cut.</summary>
public sealed record Measured(int Lines, float Width, float Height, bool Cut);

/// <summary>The text engine, as the cover's design needs it.</summary>
public interface ICoverTypesetter
{
    /// <summary>The text wrapped in <paramref name="width"/>, at most <c>type.MaxLines</c> lines.</summary>
    Measured Measure(string text, CoverType type, float width);

    /// <summary>The text on one line.</summary>
    float WidthOf(string text, CoverType type);
}

/// <summary>Words on a cover: what, how set, the box they are set in, how they sit in it, and their colour.</summary>
public sealed record CoverWords(string Text, CoverType Type, float Left, float Top, float Width, CoverAlign Align, int Ink, Measured Measured)
{
    /// <summary>Where the letters themselves are: left, top, right, bottom.</summary>
    public float[] Inked
    {
        get
        {
            var w = Math.Min(Measured.Width, Width);
            var x = Align == CoverAlign.Left ? Left : Left + Width - w;
            return [x, Top, x + w, Top + Measured.Height];
        }
    }
}

/// <summary>A cover ready to paint: its side in pixels, its layers and its words.</summary>
public sealed record CoverArt(int Side, IReadOnlyList<CoverLayer> Layers, IReadOnlyList<CoverWords> Words);

/// <summary>What a cover is made from: whose it is (its id), the name, the light line, the foot line, its colours.</summary>
public sealed record CoverSpec(string Id, string Name, string? Line, string? Footer, CoverPalette Palette);

public sealed record FittedText(string Text, CoverType Type, Measured Measured);

public static class CoverLayout
{
    /// <summary>Which of the book's gradients a list gets: always the same one for the same id.</summary>
    public static int GradientOf(string id, CoverBook book) =>
        (int)((CoverColours.CoverHash(id) >>> 7) % book.Gradients.Count);

    /// <summary>
    /// The gradient's colours for this palette: as written, or turned round the colour wheel so
    /// its fold colour takes the music's first hue, keeping the gradient's own lightness and
    /// spread of hues, its chroma met halfway by the music's. The light's colour comes last.
    /// </summary>
    public static IReadOnlyList<int> GradientColours(CoverBook.Gradient gradient, CoverPalette palette, CoverBook book)
    {
        var written = gradient.Colours.Select(CoverColours.Hex).ToList();
        if (gradient.Light is { } light) written.Add(CoverColours.Hex(light.Colour));
        if (!palette.FromMusic) return written;
        var anchor = gradient.Folds is { Length: > 0 } folds
            ? Math.Clamp(folds[0].Colour, 0, gradient.Colours.Length - 1)
            : gradient.Colours.Length - 1;
        var turn = palette.Hue - CoverColours.ToLch(written[anchor]).H;
        var rule = book.Music;
        var towardRed = palette.Hue is < 98 or > 300;
        return written.Select(argb =>
        {
            var own = CoverColours.ToLch(argb);
            var chroma = Math.Clamp((own.C + palette.Chroma) / 2, rule.MinChroma, rule.MaxChroma);
            return CoverColours.FromLch(own.L, chroma, CoverColours.NotOlive(own.H + turn, own.L, towardRed));
        }).ToList();
    }

    /// <summary>The background's layers for a cover <paramref name="side"/> pixels square.</summary>
    public static List<CoverLayer> GradientLayers(CoverBook.Gradient gradient, IReadOnlyList<int> colours, int side)
    {
        float s = side;
        int Colour(int i) => colours[Math.Clamp(i, 0, gradient.Colours.Length - 1)];
        var b = gradient.Base;
        var layers = new List<CoverLayer>
        {
            new CoverLayer.Linear(b.From[0] * s, b.From[1] * s, b.To[0] * s, b.To[1] * s,
                b.Stops.Select(stop => new Stop(stop[0], Colour(CoverColours.Round(stop[1])))).ToList()),
        };
        foreach (var fold in gradient.Folds ?? [])
        {
            var c = Colour(fold.Colour);
            layers.Add(new CoverLayer.Radial(fold.Centre[0] * s, fold.Centre[1] * s, fold.Radius * s,
                [new Stop(0f, c), new Stop(Math.Clamp(1f - fold.Soft, 0f, 1f), c), new Stop(1f, CoverColours.Alpha(c, 0f))]));
        }
        if (gradient.Light is { } light)
        {
            var c = colours.Count > gradient.Colours.Length ? colours[gradient.Colours.Length] : CoverColours.Hex(light.Colour);
            layers.Add(new CoverLayer.Radial(light.Centre[0] * s, light.Centre[1] * s, light.Radius * s,
                [new Stop(0f, CoverColours.Alpha(c, light.Opacity)), new Stop(1f, CoverColours.Alpha(c, 0f))]));
        }
        return layers;
    }

    /// <summary>The whole design of a cover <paramref name="side"/> pixels square.</summary>
    public static CoverArt Compose(CoverSpec spec, int side, ICoverTypesetter setter, CoverBook book)
    {
        var gradient = book.Gradients[GradientOf(spec.Id, book)];
        var layers = GradientLayers(gradient, GradientColours(gradient, spec.Palette, book), side);
        var words = Words(spec, side, setter, book);
        var (placed, painted) = ReadableWords(layers, words, side, book.Layout.Contrast);
        return new CoverArt(side, painted, placed);
    }

    /// <summary>Where the words go, all white for now.</summary>
    public static List<CoverWords> Words(CoverSpec spec, int side, ICoverTypesetter setter, CoverBook book)
    {
        var layout = book.Layout;
        float s = side;
        var align = IsRightToLeft(spec.Name) ? CoverAlign.Right : CoverAlign.Left;
        var name = spec.Name.Trim();
        var white = CoverColours.White;
        if (side < layout.TinyBelowPx)
        {
            if (name.Length == 0) return [];
            var tinyMargin = MathF.Max(2f, CoverColours.Round(s * layout.TinyMargin));
            var letter = Monogram(name);
            var tinyLook = new CoverType(0f, layout.Title.Weight, 0f, LineHeight(ScriptOf(letter), 1f), 1);
            var tiny = FitText(letter, s - 2 * tinyMargin, s - 2 * tinyMargin, 1, MathF.Max(8f, s * 0.3f), s * layout.TinySize, tinyLook, setter);
            return [new CoverWords(letter, tiny.Type, tinyMargin, tinyMargin, s - 2 * tinyMargin, align, white, tiny.Measured)];
        }
        float margin = CoverColours.Round(s * layout.Margin);
        var width = s - 2 * margin;
        var output = new List<CoverWords>();

        // The foot line first, so the name knows how far down it may go.
        var floor = s - margin;
        var footerText = spec.Footer?.Trim() is { Length: > 0 } f0 && side >= layout.Footer.ShowFromPx ? f0 : null;
        if (footerText is not null)
        {
            var f = layout.Footer;
            var look = new CoverType(0f, f.Weight, f.Tracking, LineHeight(ScriptOf(footerText), f.LineHeight), 1);
            var size = MathF.Max(f.MinPx, s * f.Size);
            var fit = FitText(footerText, width, s * 0.2f, 1, f.MinPx, size, look, setter);
            var top = s - s * f.Bottom - fit.Measured.Height;
            output.Add(new CoverWords(footerText, fit.Type, margin, top, width, align, CoverColours.Alpha(white, f.Opacity), fit.Measured));
            floor = top - s * 0.04f;
        }
        if (name.Length == 0) return output;

        var t = layout.Title;
        var titleTop = s * t.Top;
        var lineText = spec.Line?.Trim() is { Length: > 0 } l0 && side >= layout.Line.ShowFromPx ? l0 : null;
        var lineRoom = lineText is null ? 0f : MathF.Max(s * t.WrapSize, t.WrapMinPx) * layout.Line.ShareOfTitle * layout.Line.LineHeight;
        var room = floor - titleTop - lineRoom;
        var titleLook = new CoverType(0f, t.Weight, t.Tracking, LineHeight(ScriptOf(name), t.LineHeight), 1);
        var minPx = MathF.Max(t.MinPx, s * t.MinSize);
        var oneLineLeast = MathF.Max(minPx, MathF.Max(s * t.OneLineDownTo, t.OneLineMinPx));
        var wrapMost = MathF.Max(minPx, MathF.Max(s * t.WrapSize, MathF.Min(t.WrapMinPx, s * t.Size)));
        var title = (oneLineLeast <= s * t.Size ? FitOrNull(name, width, room, 1, oneLineLeast, s * t.Size, titleLook, setter) : null)
            ?? FitText(name, width, room, t.MaxLines, minPx, wrapMost, titleLook, setter);
        output.Add(new CoverWords(name, title.Type, margin, titleTop, width, align, white, title.Measured));

        if (lineText is not null)
        {
            var l = layout.Line;
            var lineTop = titleTop + title.Measured.Height;
            var most = MathF.Max(l.MinPx, title.Type.SizePx * l.ShareOfTitle);
            var lineLook = new CoverType(0f, l.Weight, l.Tracking, LineHeight(ScriptOf(lineText), l.LineHeight), 1);
            var fit = FitText(lineText, width, s, 1, MathF.Min(l.MinPx, most), most, lineLook, setter);
            if (lineTop + fit.Measured.Height <= floor)
                output.Add(new CoverWords(lineText, fit.Type, margin, lineTop, width, align, white, fit.Measured));
        }
        return output;
    }

    /// <summary>
    /// The largest whole-pixel size from <paramref name="minPx"/> to <paramref name="maxPx"/> at
    /// which the text fits the box in at most <paramref name="maxLines"/> lines with no word
    /// broken, or null when even the smallest does not.
    /// </summary>
    public static FittedText? FitOrNull(string text, float width, float height, int maxLines, float minPx, float maxPx,
        CoverType look, ICoverTypesetter setter)
    {
        var runs = UnbreakableRuns(text);
        CoverType TypeAt(float size) => look with { SizePx = size, MaxLines = maxLines };
        Measured? Fits(float size)
        {
            var type = TypeAt(size);
            if (runs.Any(run => setter.WidthOf(run, type) > width)) return null;
            var measured = setter.Measure(text, type, width);
            return !measured.Cut && measured.Lines <= maxLines && measured.Height <= height ? measured : null;
        }
        var low = MathF.Max(MathF.Floor(minPx), 1f);
        var high = MathF.Max(MathF.Floor(maxPx), low);
        if (Fits(high) is { } atHigh) return new FittedText(text, TypeAt(high), atHigh);
        if (Fits(low) is not { } best) return null;
        while (high - low > 1f)
        {
            var mid = MathF.Floor((low + high) / 2);
            if (Fits(mid) is { } measured)
            {
                low = mid;
                best = measured;
            }
            else
            {
                high = mid;
            }
        }
        return new FittedText(text, TypeAt(low), best);
    }

    /// <summary>As <see cref="FitOrNull"/>, but when nothing fits: the smallest size, as many lines as the box holds, the rest cut.</summary>
    public static FittedText FitText(string text, float width, float height, int maxLines, float minPx, float maxPx,
        CoverType look, ICoverTypesetter setter)
    {
        if (FitOrNull(text, width, height, maxLines, minPx, maxPx, look, setter) is { } fitted) return fitted;
        var size = MathF.Max(MathF.Floor(minPx), 1f);
        // A word too long for the width even at the smallest is cut on one line rather than broken across two.
        var tooLong = UnbreakableRuns(text).Any(run => setter.WidthOf(run, look with { SizePx = size }) > width);
        var lines = tooLong ? 1 : Math.Clamp((int)(height / (size * look.LineHeight)), 1, maxLines);
        var type = look with { SizePx = size, MaxLines = lines };
        return new FittedText(text, type, setter.Measure(text, type, width));
    }

    // ---------------------------------------------------------------- writing

    private static bool IsEmoji(int cp) =>
        cp is (>= 0x1F000 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or (>= 0x2B00 and <= 0x2BFF);

    internal static bool IsWide(int cp) =>
        cp is (>= 0x1100 and <= 0x11FF) or (>= 0x2E80 and <= 0x2FDF) or (>= 0x3040 and <= 0x30FF) or (>= 0x3100 and <= 0x312F)
            or (>= 0x3130 and <= 0x318F) or (>= 0x31A0 and <= 0x31BF) or (>= 0x31F0 and <= 0x31FF) or (>= 0x3400 and <= 0x4DBF)
            or (>= 0x4E00 and <= 0x9FFF) or (>= 0xA960 and <= 0xA97F) or (>= 0xAC00 and <= 0xD7FF) or (>= 0xF900 and <= 0xFAFF)
            or (>= 0xFF66 and <= 0xFF9F) or (>= 0x20000 and <= 0x3FFFF) or 0x3005 or 0x3006 or 0x3007;

    private static bool IsSpaced(int cp) =>
        cp is (>= 0x0041 and <= 0x024F) or (>= 0x1E00 and <= 0x1EFF) or (>= 0x0370 and <= 0x03FF) or (>= 0x1F00 and <= 0x1FFF)
            or (>= 0x0400 and <= 0x052F) or (>= 0x0530 and <= 0x058F) or (>= 0x10A0 and <= 0x10FF) or (>= 0x2C00 and <= 0x2C7F)
            or (>= 0xA720 and <= 0xA7FF) or (>= 0xFF21 and <= 0xFF5A);

    private static bool IsRtlLetter(int cp) =>
        cp is (>= 0x0590 and <= 0x05FF) or (>= 0x0600 and <= 0x06FF) or (>= 0x0700 and <= 0x074F) or (>= 0x0750 and <= 0x077F)
            or (>= 0x0780 and <= 0x07BF) or (>= 0x07C0 and <= 0x07FF) or (>= 0x0860 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF)
            or (>= 0xFE70 and <= 0xFEFF) or (>= 0x1EE00 and <= 0x1EEFF);

    private static IEnumerable<Rune> Runes(string text) => text.EnumerateRunes();

    /// <summary>The writing most of the text is in; digits, spaces and marks do not count.</summary>
    public static CoverScript ScriptOf(string text)
    {
        int latin = 0, wide = 0, tall = 0, emoji = 0;
        foreach (var rune in Runes(text))
        {
            var cp = rune.Value;
            if (IsEmoji(cp)) emoji++;
            else if (!Rune.IsLetter(rune)) { }
            else if (IsWide(cp)) wide++;
            else if (IsSpaced(cp)) latin++;
            else tall++;
        }
        var most = Math.Max(Math.Max(latin, wide), Math.Max(tall, emoji));
        if (most == 0 || most == latin) return CoverScript.Latin;
        if (most == wide) return CoverScript.Wide;
        return most == tall ? CoverScript.Tall : CoverScript.Emoji;
    }

    /// <summary>Whether the text reads right to left: its first letter decides.</summary>
    public static bool IsRightToLeft(string text)
    {
        foreach (var rune in Runes(text))
            if (Rune.IsLetter(rune)) return IsRtlLetter(rune.Value);
        return false;
    }

    /// <summary>The space between lines for this writing: room for marks in the scripts that have them.</summary>
    public static float LineHeight(CoverScript script, float wanted) => script switch
    {
        CoverScript.Latin => wanted,
        CoverScript.Wide or CoverScript.Emoji => MathF.Max(wanted, 1.15f),
        _ => MathF.Max(wanted, 1.4f),
    };

    /// <summary>The one character a tiny cover shows: the first letter, number or emoji, a capital where the writing has them.</summary>
    public static string Monogram(string name)
    {
        var e = StringInfo.GetTextElementEnumerator(name);
        while (e.MoveNext())
        {
            var element = e.GetTextElement();
            var first = Rune.GetRuneAt(element, 0);
            if (Rune.IsWhiteSpace(first)) continue;
            if (Rune.IsLetterOrDigit(first) || first.Value >= 0x2600)
                return element.Length == 1 ? element.ToUpperInvariant() : element;
        }
        return name.Trim().Length > 0 ? name.Trim()[..1] : "";
    }

    /// <summary>The pieces that cannot be broken across lines: words between spaces; wide characters each stand alone.</summary>
    public static List<string> UnbreakableRuns(string text)
    {
        var runs = new List<string>();
        var word = new StringBuilder();
        void End()
        {
            if (word.Length > 0) runs.Add(word.ToString());
            word.Clear();
        }
        foreach (var rune in Runes(text))
        {
            if (Rune.IsWhiteSpace(rune)) End();
            else if (IsWide(rune.Value))
            {
                End();
                runs.Add(rune.ToString());
            }
            else word.Append(rune.ToString());
        }
        End();
        return runs;
    }

    // ---------------------------------------------------------------- colour under the words

    /// <summary>A gradient's colour <paramref name="t"/> of the way along, each channel and the opacity mixed.</summary>
    public static int ColourAlong(IReadOnlyList<Stop> stops, float t)
    {
        if (t <= stops[0].At) return stops[0].Argb;
        if (t >= stops[^1].At) return stops[^1].Argb;
        var after = 0;
        while (stops[after].At < t) after++;
        var a = stops[after - 1];
        var b = stops[after];
        var f = b.At == a.At ? 1f : (t - a.At) / (b.At - a.At);
        int Channel(int shift)
        {
            var x = (a.Argb >>> shift) & 0xFF;
            var y = (b.Argb >>> shift) & 0xFF;
            return Math.Clamp(CoverColours.Round(x + (y - x) * f), 0, 255);
        }
        return (Channel(24) << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    /// <summary>The colour the layers paint at a point.</summary>
    public static int ColourAt(IReadOnlyList<CoverLayer> layers, float x, float y) => Compile(layers).At(x, y);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<CoverLayer>, Compiled> CompiledLayers = new();

    /// <summary>The layers as flat arrays, so a whole cover's pixels can be worked out quickly.</summary>
    internal static Compiled Compile(IReadOnlyList<CoverLayer> layers) => CompiledLayers.GetValue(layers, list => new Compiled(list));

    /// <summary>
    /// The layers laid out for speed, with the same arithmetic as the design: every pixel of a
    /// painted cover is the colour the contrast check saw there.
    /// </summary>
    internal sealed class Compiled
    {
        private readonly bool[] _radial;
        private readonly float[] _a, _b, _c, _d;
        private readonly float[][] _at;
        private readonly int[][] _argb;

        public Compiled(IReadOnlyList<CoverLayer> layers)
        {
            var n = layers.Count;
            (_radial, _a, _b, _c, _d, _at, _argb) = (new bool[n], new float[n], new float[n], new float[n], new float[n], new float[n][], new int[n][]);
            for (var i = 0; i < n; i++)
            {
                IReadOnlyList<Stop> stops;
                switch (layers[i])
                {
                    case CoverLayer.Linear linear:
                        (_a[i], _b[i], _c[i], _d[i]) = (linear.X0, linear.Y0, linear.X1 - linear.X0, linear.Y1 - linear.Y0);
                        stops = linear.Stops;
                        break;
                    case CoverLayer.Radial radial:
                        _radial[i] = true;
                        (_a[i], _b[i], _c[i]) = (radial.Cx, radial.Cy, radial.Radius);
                        stops = radial.Stops;
                        break;
                    default:
                        throw new ArgumentException("unknown layer");
                }
                _at[i] = stops.Select(stop => stop.At).ToArray();
                _argb[i] = stops.Select(stop => stop.Argb).ToArray();
            }
        }

        public int At(float x, float y)
        {
            var colour = CoverColours.Black;
            for (var i = 0; i < _radial.Length; i++)
            {
                float t;
                if (_radial[i])
                {
                    var dx = x - _a[i];
                    var dy = y - _b[i];
                    // Past a disc's edge it is see-through and leaves the colour as it was.
                    if ((dx >= _c[i] || dx <= -_c[i] || dy >= _c[i] || dy <= -_c[i]) && _argb[i][^1] >>> 24 == 0) continue;
                    t = (float)Math.Sqrt((double)dx * dx + (double)dy * dy) / _c[i];
                }
                else
                {
                    var dx = _c[i];
                    var dy = _d[i];
                    var length = dx * dx + dy * dy;
                    t = length == 0f ? 0f : ((x - _a[i]) * dx + (y - _b[i]) * dy) / length;
                }
                colour = CoverColours.Over(Along(_at[i], _argb[i], Math.Clamp(t, 0f, 1f)), colour);
            }
            return colour;
        }

        private static int Along(float[] at, int[] argb, float t)
        {
            if (t <= at[0]) return argb[0];
            var last = at.Length - 1;
            if (t >= at[last]) return argb[last];
            var after = 1;
            while (at[after] < t) after++;
            var (a0, a1) = (at[after - 1], at[after]);
            var f = a1 == a0 ? 1f : (t - a0) / (a1 - a0);
            var (x, y) = (argb[after - 1], argb[after]);
            return (Mix(x >>> 24, y >>> 24, f) << 24) | (Mix((x >> 16) & 0xFF, (y >> 16) & 0xFF, f) << 16)
                | (Mix((x >> 8) & 0xFF, (y >> 8) & 0xFF, f) << 8) | Mix(x & 0xFF, y & 0xFF, f);
        }

        private static int Mix(int x, int y, float f) => Math.Clamp(CoverColours.Round(x + (y - x) * f), 0, 255);
    }

    /// <summary>The colours under a box of words, on a grid over it and a little round it.</summary>
    internal static IEnumerable<int> ColoursUnder(IReadOnlyList<CoverLayer> layers, float[] box, int side, int grid = 9)
    {
        var pad = side * 0.015f;
        var left = box[0] - pad;
        var top = box[1] - pad;
        var w = box[2] - box[0] + 2 * pad;
        var h = box[3] - box[1] + 2 * pad;
        for (var i = 0; i < grid; i++)
        for (var j = 0; j < grid; j++)
        {
            var x = Math.Clamp(left + w * i / (grid - 1), 0f, side - 1f);
            var y = Math.Clamp(top + h * j / (grid - 1), 0f, side - 1f);
            yield return ColourAt(layers, x, y);
        }
    }

    /// <summary>The lowest contrast words in <paramref name="ink"/> get over any part of the colour under the box.</summary>
    public static double WorstContrast(IReadOnlyList<CoverLayer> layers, float[] box, int side, int ink, int grid = 9) =>
        ColoursUnder(layers, box, side, grid).Min(under => CoverColours.ContrastRatio(CoverColours.Over(ink, under), under));

    /// <summary>A darkening under a box of words: from the foot up for low words, from the top down for high ones.</summary>
    internal static CoverLayer ShadeUnder(float[] box, int side, float strength)
    {
        float s = side;
        var full = CoverColours.Alpha(CoverColours.Black, strength);
        var none = CoverColours.Alpha(CoverColours.Black, 0f);
        return (box[1] + box[3]) / 2 > s / 2
            ? new CoverLayer.Linear(0f, box[1] - s * 0.24f, 0f, box[1], [new Stop(0f, none), new Stop(1f, full)])
            : new CoverLayer.Linear(0f, box[3] + s * 0.24f, 0f, box[3], [new Stop(0f, none), new Stop(1f, full)]);
    }

    /// <summary>
    /// The words with the colour each needs to read, and the layers with any darkening added:
    /// each block keeps its own opacity where that reaches the contrast over every part of the
    /// colour under it; else it goes more opaque, up to solid white; else the colour under it is
    /// darkened just enough. Checked on the design's 9 by 9 grid and then on a fine grid, so no
    /// pixel between the coarse points can fall short.
    /// </summary>
    public static (List<CoverWords> Words, List<CoverLayer> Layers) ReadableWords(IReadOnlyList<CoverLayer> layers,
        IReadOnlyList<CoverWords> words, int side, double contrast)
    {
        var goal = contrast + 0.05;
        var current = layers.ToList();
        var placed = new List<CoverWords>();
        foreach (var word in words)
        {
            var box = word.Inked;
            var fine = Math.Max(9, (int)((box[2] - box[0]) / 6) + 1);
            bool Coarse(List<CoverLayer> under, int ink) => Reaches(under, box, side, ink, 9, goal);
            bool Both(List<CoverLayer> under, int ink) => Coarse(under, ink) && Reaches(under, box, side, ink, fine, goal);

            // The apps' decision on the design's 9 by 9 grid; decided again on a fine grid too
            // in the rare case a pixel between the coarse points still falls short.
            var (ink, shade) = Decide(word, box, side, current, Coarse);
            List<CoverLayer> under = shade is null ? current : [.. current, shade];
            if (!Reaches(under, box, side, ink, fine, goal)) (ink, shade) = Decide(word, box, side, current, Both);
            // A new list, never an added layer: layers are compiled once per list.
            if (shade is not null) current = [.. current, shade];
            placed.Add(word with { Ink = ink });
        }
        return (placed, current);
    }

    /// <summary>
    /// One block of words: its own opacity where that reads; else more opaque, up to solid
    /// white; else solid white over the colour darkened just enough.
    /// </summary>
    private static (int Ink, CoverLayer? Shade) Decide(CoverWords word, float[] box, int side, List<CoverLayer> current,
        Func<List<CoverLayer>, int, bool> reads)
    {
        if (reads(current, word.Ink)) return (word.Ink, null);
        if (reads(current, CoverColours.White))
        {
            float lowA = CoverColours.A(word.Ink) / 255f, highA = 1f;
            for (var i = 0; i < 12; i++)
            {
                var mid = (lowA + highA) / 2;
                if (reads(current, CoverColours.Alpha(CoverColours.White, mid))) highA = mid; else lowA = mid;
            }
            return (CoverColours.Alpha(CoverColours.White, highA), null);
        }
        float low = 0f, high = 0.95f;
        for (var i = 0; i < 16; i++)
        {
            var mid = (low + high) / 2;
            if (reads([.. current, ShadeUnder(box, side, mid)], CoverColours.White)) high = mid; else low = mid;
        }
        return (CoverColours.White, ShadeUnder(box, side, high));
    }

    /// <summary>Whether the words reach the goal over every point of the grid, stopping at the first that does not.</summary>
    private static bool Reaches(List<CoverLayer> layers, float[] box, int side, int ink, int grid, double goal)
    {
        foreach (var under in ColoursUnder(layers, box, side, grid))
            if (CoverColours.ContrastRatio(CoverColours.Over(ink, under), under) < goal) return false;
        return true;
    }
}
