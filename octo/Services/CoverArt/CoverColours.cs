using System.Text;

namespace Octo.Services.CoverArt;

// The colour rules of the cover design, the same maths as the Octo apps' covers
// (app.winters.octo.covers) so a list looks the same wherever it is drawn. Colours
// are 0xAARRGGBB ints; rounding is half up, as the apps round.

/// <summary>A colour as OKLCH: lightness 0 to 1, chroma (0 grey, about 0.3 at the most vivid), hue in degrees.</summary>
public readonly record struct Lch(double L, double C, double H);

/// <summary>One colour of a picture and how much of it that colour covers, 0 to 1.</summary>
public readonly record struct Swatch(int Argb, float Share);

public static class CoverColours
{
    public const int White = unchecked((int)0xFFFFFFFF);
    public const int Black = unchecked((int)0xFF000000);

    internal static int Round(double v) => (int)Math.Floor(v + 0.5);
    internal static int Round(float v) => (int)MathF.Floor(v + 0.5f);

    public static int Hex(string hex) => unchecked((int)0xFF000000) | Convert.ToInt32(hex.TrimStart('#'), 16);

    public static int R(int argb) => (argb >> 16) & 0xFF;
    public static int G(int argb) => (argb >> 8) & 0xFF;
    public static int B(int argb) => argb & 0xFF;
    public static int A(int argb) => (argb >>> 24) & 0xFF;

    private static double Linear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Encoded(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

    public static Lch ToLch(int argb)
    {
        var r = Linear(R(argb));
        var g = Linear(G(argb));
        var b = Linear(B(argb));
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        var lightness = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        var hue = Math.Atan2(bb, a) * 180 / Math.PI;
        if (hue < 0) hue += 360;
        return new Lch(lightness, Math.Sqrt(a * a + bb * bb), hue);
    }

    private static (double R, double G, double B) LinearRgb(double l, double c, double h)
    {
        var rad = h * Math.PI / 180;
        var a = c * Math.Cos(rad);
        var b = c * Math.Sin(rad);
        var l1 = Math.Pow(l + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m1 = Math.Pow(l - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s1 = Math.Pow(l - 0.0894841775 * a - 1.2914855480 * b, 3);
        return (4.0767416621 * l1 - 3.3077115913 * m1 + 0.2309699292 * s1,
            -1.2684380046 * l1 + 2.6097574011 * m1 - 0.3413193965 * s1,
            -0.0041960863 * l1 - 0.7034186147 * m1 + 1.7076147010 * s1);
    }

    private static bool Shown((double R, double G, double B) rgb) =>
        rgb.R is >= -1e-4 and <= 1.0001 && rgb.G is >= -1e-4 and <= 1.0001 && rgb.B is >= -1e-4 and <= 1.0001;

    /// <summary>The colour as ARGB; a chroma the screen cannot show is lowered, keeping lightness and hue.</summary>
    public static int FromLch(double l, double c, double h)
    {
        var lightness = Math.Clamp(l, 0.0, 1.0);
        var rgb = LinearRgb(lightness, c, h);
        if (!Shown(rgb))
        {
            double low = 0, high = c;
            for (var i = 0; i < 24; i++)
            {
                var mid = (low + high) / 2;
                if (Shown(LinearRgb(lightness, mid, h))) low = mid; else high = mid;
            }
            rgb = LinearRgb(lightness, low, h);
        }
        static int Byte(double v) => Math.Clamp(Round(Encoded(Math.Clamp(v, 0.0, 1.0)) * 255), 0, 255);
        return unchecked((int)0xFF000000) | (Byte(rgb.R) << 16) | (Byte(rgb.G) << 8) | Byte(rgb.B);
    }

    /// <summary>How far apart two hues are around the circle, 0 to 180 degrees.</summary>
    public static double HueDistance(double a, double b)
    {
        var d = ((a - b) % 360 + 360) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>How far apart two colours look (0 the same).</summary>
    public static double Distance(Lch x, Lch y)
    {
        var ra = x.H * Math.PI / 180;
        var rb = y.H * Math.PI / 180;
        var da = x.C * Math.Cos(ra) - y.C * Math.Cos(rb);
        var db = x.C * Math.Sin(ra) - y.C * Math.Sin(rb);
        return Math.Sqrt(Math.Pow(x.L - y.L, 2) + da * da + db * db);
    }

    /// <summary>
    /// Yellow and orange turn to olive and brown in the dark, so a deep colour of those hues
    /// moves to red, or to green for music that is green or blue.
    /// </summary>
    public static double NotOlive(double hue, double lightness, bool towardRed)
    {
        var h = (hue % 360 + 360) % 360;
        if (lightness < 0.45 && towardRed && h is >= 40.0 and <= 170.0) return 20.0;
        if (lightness < 0.45 && h is >= 40.0 and <= 125.0) return 145.0;
        if (lightness < 0.62 && towardRed && h is >= 70.0 and <= 160.0) return 55.0;
        if (lightness < 0.62 && h is >= 70.0 and <= 125.0) return 140.0;
        return h;
    }

    /// <summary>The colour with this opacity.</summary>
    public static int Alpha(int argb, float a) => (Round(Math.Clamp(a, 0f, 1f) * 255) << 24) | (argb & 0xFFFFFF);

    /// <summary>One colour laid over another at the first one's opacity.</summary>
    public static int Over(int top, int under)
    {
        var a = A(top) / 255f;
        if (a >= 1f) return top | unchecked((int)0xFF000000);
        int Channel(int shift)
        {
            var t = (top >> shift) & 0xFF;
            var u = (under >> shift) & 0xFF;
            return Math.Clamp(Round(u + (t - u) * a), 0, 255);
        }
        return unchecked((int)0xFF000000) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    /// <summary>WCAG relative luminance, with the apps' 0.03928 knee.</summary>
    public static double RelativeLuminance(int argb)
    {
        static double Lin(int channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(R(argb)) + 0.7152 * Lin(G(argb)) + 0.0722 * Lin(B(argb));
    }

    /// <summary>How far apart two colours are for reading, from 1 (the same) to 21.</summary>
    public static double ContrastRatio(int a, int b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>A number from some text, the same on every device (FNV-1a 64 over its UTF-8), never negative.</summary>
    public static long CoverHash(string text)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 0x100000001b3UL;
        }
        return (long)(hash >> 1);
    }

    private sealed class Bucket
    {
        public int Key;
        public int Count;
        public long R, G, B;
        public int Mean => unchecked((int)0xFF000000) | ((int)(R / Count) << 16) | ((int)(G / Count) << 8) | (int)(B / Count);
    }

    /// <summary>
    /// A picture's main colours, most of the picture first, up to <paramref name="most"/>: pixels
    /// counted in coarse buckets (4 bits a channel), every <paramref name="step"/>th one, each
    /// bucket joining the first colour it looks like or starting one of its own.
    /// </summary>
    public static List<Swatch> Swatches(int[] pixels, int step = 3, int most = 6)
    {
        if (pixels.Length == 0) return [];
        var counts = new Dictionary<int, Bucket>();
        for (var i = 0; i < pixels.Length; i += step)
        {
            var p = pixels[i];
            var key = (((p >> 20) & 0xF) << 8) | (((p >> 12) & 0xF) << 4) | ((p >> 4) & 0xF);
            if (!counts.TryGetValue(key, out var bucket)) counts[key] = bucket = new Bucket { Key = key };
            bucket.Count++;
            bucket.R += R(p);
            bucket.G += G(p);
            bucket.B += B(p);
        }
        var found = counts.Values.OrderByDescending(b => b.Count).ThenBy(b => b.Key).ToList();
        var total = (float)found.Sum(b => b.Count);
        var groups = new List<(Lch Seen, Bucket Bucket)>();
        foreach (var bucket in found)
        {
            // Specks of a colour (under 0.2% of the picture) are left out.
            if (bucket.Count < total * 0.002f) break;
            var seen = ToLch(bucket.Mean);
            var near = groups.FirstOrDefault(g => Distance(g.Seen, seen) < 0.09).Bucket;
            if (near is not null)
            {
                near.Count += bucket.Count;
                near.R += bucket.R;
                near.G += bucket.G;
                near.B += bucket.B;
            }
            else if (groups.Count < most * 3)
            {
                groups.Add((seen, new Bucket { Key = bucket.Key, Count = bucket.Count, R = bucket.R, G = bucket.G, B = bucket.B }));
            }
        }
        return groups.Select(g => g.Bucket).OrderByDescending(b => b.Count).Take(most)
            .Select(b => new Swatch(b.Mean, b.Count / total)).ToList();
    }
}

/// <summary>
/// The colours a cover is designed from: two hues and how vivid each is, from its music, or
/// from its name when it has none.
/// </summary>
public sealed record CoverPalette(int Hue, double Chroma, int Hue2, double Chroma2, bool FromMusic)
{
    private const double FirstLow = 0.07, FirstHigh = 0.15, SecondLow = 0.09, SecondHigh = 0.19;

    /// <summary>Colours under this chroma read as grey and give no hue to work from.</summary>
    private const double Colourless = 0.035;

    /// <summary>Hues at least this far apart make a pair worth showing.</summary>
    private const double PairApart = 28.0;

    public string Key => $"{Hue}.{CoverColours.Round(Chroma * 1000)}.{Hue2}.{CoverColours.Round(Chroma2 * 1000)}{(FromMusic ? "m" : "s")}";

    public static CoverPalette Of(double hue, double chroma, double hue2, double chroma2, bool fromMusic) => new(
        Wrap(hue), Round3(Math.Clamp(chroma, FirstLow, FirstHigh)), Wrap(hue2), Round3(Math.Clamp(chroma2, SecondLow, SecondHigh)), fromMusic);

    private static int Wrap(double h) => (CoverColours.Round(h) % 360 + 360) % 360;
    private static double Round3(double v) => CoverColours.Round(v * 1000) / 1000.0;

    /// <summary>
    /// A palette from the main colours of some covers (a list of swatches for each), weighed by
    /// how much of its cover each colour fills and how vivid it is. The strongest gives the first
    /// hue; the strongest of a clearly different hue the second, or a neighbour of the first.
    /// With no covers, or only grey ones, a palette of its own from <paramref name="seed"/>.
    /// </summary>
    public static CoverPalette FromCovers(IReadOnlyList<IReadOnlyList<Swatch>> covers, string seed)
    {
        var withColour = covers.Where(c => c.Count > 0).ToList();
        if (withColour.Count == 0) return Seeded(seed);
        var seen = withColour.SelectMany(swatches => swatches.Take(4)
            .Select(s => (Lch: CoverColours.ToLch(s.Argb), Weight: (double)s.Share / withColour.Count))).ToList();
        var colourful = seen.Where(s => s.Lch.C >= Colourless && s.Lch.L is >= 0.15 and <= 0.97).ToList();
        if (colourful.Count == 0) return Seeded(seed);
        static double Score((Lch Lch, double Weight) s) => Math.Sqrt(s.Weight) * (0.3 + s.Lch.C * 5);
        var first = colourful.MaxBy(Score);
        var others = colourful.Where(s => CoverColours.HueDistance(s.Lch.H, first.Lch.H) >= PairApart).ToList();
        (Lch Lch, double Weight)? second = others.Count > 0 ? others.MaxBy(Score) : null;
        var turn = (CoverColours.CoverHash(seed) & 1L) == 0L ? 38.0 : -38.0;
        return Of(first.Lch.H, first.Lch.C * 1.15,
            second?.Lch.H ?? first.Lch.H + turn,
            (second?.Lch.C ?? first.Lch.C) * 1.2, fromMusic: true);
    }

    /// <summary>A palette from the name alone: a hue from its hash and a second a little way round.</summary>
    public static CoverPalette Seeded(string seed)
    {
        var hash = CoverColours.CoverHash(seed);
        var hue = (double)(hash % 360);
        var step = 30 + (hash >>> 12) % 50;
        var turn = ((hash >>> 20) & 1L) == 0L ? step : -step;
        return Of(hue, 0.12, hue + turn, 0.16, fromMusic: false);
    }
}
