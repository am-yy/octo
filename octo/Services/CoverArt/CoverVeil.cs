using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Octo.Services.CoverArt;

/// <summary>
/// The colour-keeping veil under a cover's words, exactly as cover-design.json "veil" says and
/// tools/cover-art/reference.py does: only what is too bright for white words is darkened, by
/// OKLab lightness with hue and chroma kept (yellows turned toward amber or green, never olive),
/// and only as far as each block of words asks.
/// </summary>
internal static class CoverVeil
{
    /// <summary>One block of words' region: its box in pixels, how far its reach eases out, and its rule.</summary>
    internal sealed record Region(double X0, double Y0, double X1, double Y1, double[] Falloff, double Aim, double Least, double MaxDrop);

    private static readonly double[] Luma = [0.2126, 0.7152, 0.0722];

    private static readonly double[,] M1 =
    {
        { 0.4122214708, 0.5363325363, 0.0514459929 },
        { 0.2119034982, 0.6806995451, 0.1073969566 },
        { 0.0883024619, 0.2817188376, 0.6299787005 },
    };

    private static readonly double[,] M2 =
    {
        { 0.2104542553, 0.7936177850, -0.0040720468 },
        { 1.9779984951, -2.4285922050, 0.4505937099 },
        { 0.0259040371, 0.7827717662, -0.8086757660 },
    };

    // The inverses are worked out from the forward matrices, as the reference does.
    private static readonly double[,] M1Inverse = Invert(M1);
    private static readonly double[,] M2Inverse = Invert(M2);

    /// <summary>The regions for laid-out words: the name with its light line, and the foot line.</summary>
    public static List<Region> Regions(CoverBook book, IReadOnlyList<CoverWords> words, int side)
    {
        var veil = book.Veil;
        double s = side;
        var regions = new List<Region>();
        var block = words.Where(w => w.Role is WordsRole.Title or WordsRole.Line).ToList();
        if (block.Count > 0)
        {
            var v = veil.Title;
            var pad = v.Pad * s;
            var bottom = block.Max(w => (double)w.Top + w.Measured.Height) + pad;
            var (x0, x1) = block[0].Align == CoverAlign.Right
                ? (block.Min(w => (double)w.Inked[0]) - pad, s)
                : (0.0, block.Max(w => (double)w.Inked[2]) + pad);
            regions.Add(new Region(x0, 0, x1, bottom, v.Falloff,
                LimitFor(v.AimContrast) * (1 - veil.Margin), LimitFor(v.MinContrast) * (1 - veil.Margin), v.MaxDrop));
        }
        if (words.FirstOrDefault(w => w.Role == WordsRole.Footer) is { } foot)
        {
            var v = veil.Footer;
            var pad = v.Pad * s;
            var (x0, x1) = foot.Align == CoverAlign.Right ? (foot.Inked[0] - pad, s) : (0.0, foot.Inked[2] + pad);
            var need = LimitFor(v.Contrast, book.Layout.Footer.Opacity) * (1 - veil.Margin);
            regions.Add(new Region(x0, foot.Top - pad, x1, s, v.Falloff, need, need, 1.0));
        }
        return regions;
    }

    /// <summary>
    /// The most a background's luminance may be for white words at <paramref name="opacity"/>,
    /// laid over it in sRGB as the apps draw, to reach <paramref name="contrast"/> on grey.
    /// </summary>
    internal static double LimitFor(double contrast, double opacity = 1)
    {
        if (opacity >= 1) return 1.05 / contrast - 0.05;
        double low = 0, high = 1;
        for (var i = 0; i < 40; i++)
        {
            var mid = (low + high) / 2;
            var background = ToSrgb(mid);
            var ink = ToLinear(background + (1 - background) * opacity);
            if ((ink + 0.05) / (mid + 0.05) >= contrast) low = mid; else high = mid;
        }
        return low;
    }

    /// <summary>Each 8-bit level in linear light, worked out once.</summary>
    private static readonly double[] Levels = Enumerable.Range(0, 256).Select(v => ToLinear(v / 255.0)).ToArray();

    /// <summary>Darkens the background under the regions in place.</summary>
    public static void Apply(Image<Rgb24> image, IReadOnlyList<Region> regions, CoverBook.VeilNumbers veil)
    {
        if (regions.Count == 0) return;
        var side = image.Width;
        var n = side * side;
        var pixels = System.Buffers.ArrayPool<Rgb24>.Shared.Rent(n);
        var keeps = System.Buffers.ArrayPool<float>.Shared.Rent(n);
        try
        {
            image.CopyPixelDataTo(pixels.AsSpan(0, n));
            Veil(pixels, keeps, side, regions, veil);
            image.ProcessPixelRows(access =>
            {
                for (var row = 0; row < side; row++)
                    pixels.AsSpan(row * side, side).CopyTo(access.GetRowSpan(row));
            });
        }
        finally
        {
            System.Buffers.ArrayPool<Rgb24>.Shared.Return(pixels);
            System.Buffers.ArrayPool<float>.Shared.Return(keeps);
        }
    }

    private static void Veil(Rgb24[] pixels, float[] keeps, int side, IReadOnlyList<Region> regions, CoverBook.VeilNumbers veil)
    {
        // Each region's reach at every pixel centre, across and down.
        var weights = regions.Select(r => Weights(r, side)).ToArray();
        var yellow = veil.Yellow;
        var massSum = 0.0;
        var hueSum = 0.0;
        var gate = new object();

        // First the share of its luminance each pixel keeps, and which way the yellows lean.
        Parallel.For(0, side, row =>
        {
            double rowMass = 0, rowHue = 0;
            for (var col = 0; col < side; col++)
            {
                var i = row * side + col;
                var p = pixels[i];
                var (r, g, b) = (Levels[p.R], Levels[p.G], Levels[p.B]);
                var lum = Luma[0] * r + Luma[1] * g + Luma[2] * b;
                // Each region keeps a share of the luminance; shares multiply, so the veil stays
                // smooth where two regions meet.
                double keep = 1, most = 0;
                for (var k = 0; k < regions.Count; k++)
                {
                    var across = weights[k].Across[col];
                    var down = weights[k].Down[row];
                    if (across >= 1 || down >= 1) continue;
                    var w = 1 - Smooth(0, 1, Math.Sqrt(across * across + down * down));
                    if (w <= 0) continue;
                    var region = regions[k];
                    var limit = Math.Min(Math.Max(region.Aim, lum * (1 - region.MaxDrop)), region.Least);
                    keep *= 1 - w * (1 - Math.Min(1.0, limit / Math.Max(lum, 1e-9)));
                    most = Math.Max(most, w);
                }
                keeps[i] = (float)keep;
                if (most <= 0) continue;
                var (_, A, B) = ToOklab(r, g, b);
                var (h, c, wy) = Yellowness(A, B, yellow);
                if (wy <= 0) continue;
                var mass = wy * c * most;
                rowMass += mass;
                rowHue += h * mass;
            }
            lock (gate)
            {
                massSum += rowMass;
                hueSum += rowHue;
            }
        });

        // One direction per cover: yellows under the veil that lean yellow deepen to amber,
        // those that lean lime to green.
        var lean = massSum > 0 ? hueSum / massSum : 0.0;
        var towards = lean <= yellow.Split ? yellow.Towards[0] : yellow.Towards[1];

        Parallel.For(0, side, row =>
        {
            for (var col = 0; col < side; col++)
            {
                var i = row * side + col;
                var keep = keeps[i];
                if (keep >= 1f) continue;
                var p = pixels[i];
                var (r, g, bl) = (Levels[p.R], Levels[p.G], Levels[p.B]);
                var y0 = Luma[0] * r + Luma[1] * g + Luma[2] * bl;
                var t = y0 * keep;
                if (!(t < y0 - 1e-9)) continue;
                var (L, A, B) = ToOklab(r, g, bl);
                var drop = 1 - t / y0;
                var (h, c, wy) = Yellowness(A, B, yellow);
                var share = Math.Clamp(yellow.TurnPerDrop * drop, 0, 1) * wy;
                var h2 = h + (towards - h) * share;
                var c2 = c * (1 + yellow.ChromaLift * drop * wy);
                var a2 = c2 * Math.Cos(h2 * Math.PI / 180);
                var b2 = c2 * Math.Sin(h2 * Math.PI / 180);

                // Lightness for the target luminance: exact for greys at first, then refined
                // against what the colour settles to after gamut mapping.
                var L2 = L * Math.Cbrt(t / y0);
                for (var k = 0; k < veil.Refine; k++)
                {
                    var (sr, sg, sb) = Settle(L2, a2, b2);
                    var got = Luma[0] * sr + Luma[1] * sg + Luma[2] * sb;
                    L2 *= Math.Cbrt(t / Math.Max(got, 1e-6));
                }
                var (fr, fg, fb) = Settle(L2, a2, b2);
                pixels[i] = new Rgb24(Byte(fr), Byte(fg), Byte(fb));
            }
        });
    }

    private static (double[] Across, double[] Down) Weights(Region r, int side)
    {
        var across = new double[side];
        var down = new double[side];
        for (var i = 0; i < side; i++)
        {
            var c = i + 0.5;
            across[i] = Math.Max(Math.Max(r.X0 - c, c - r.X1), 0) / (r.Falloff[0] * side);
            down[i] = Math.Max(Math.Max(r.Y0 - c, c - r.Y1), 0) / (r.Falloff[1] * side);
        }
        return (across, down);
    }

    private static (double H, double C, double Wy) Yellowness(double a, double b, CoverBook.VeilYellow yellow)
    {
        var c = Math.Sqrt(a * a + b * b);
        var h = Math.Atan2(b, a) * 180 / Math.PI;
        h = (h % 360 + 360) % 360;
        var wy = Smooth(yellow.Hues[0], yellow.Hues[1], h) * (1 - Smooth(yellow.Hues[2], yellow.Hues[3], h))
            * Math.Clamp(c / yellow.ChromaFrom, 0, 1);
        return (h, c, wy);
    }

    /// <summary>OKLab to linear sRGB inside the gamut: chroma eased toward grey where it is out, clipped where it is only just out.</summary>
    private static (double R, double G, double B) Settle(double L, double a, double b)
    {
        var raw = ToLinearRgb(L, a, b);
        var low = Math.Min(raw.R, Math.Min(raw.G, raw.B));
        var high = Math.Max(raw.R, Math.Max(raw.G, raw.B));
        if (low >= 0 && high <= 1) return raw;

        double lo = 0, hi = 1;
        for (var k = 0; k < 12; k++)
        {
            var mid = (lo + hi) / 2;
            var test = ToLinearRgb(L, a * mid, b * mid);
            var ok = Math.Min(test.R, Math.Min(test.G, test.B)) >= -1e-4 && Math.Max(test.R, Math.Max(test.G, test.B)) <= 1 + 1e-4;
            if (ok) lo = mid; else hi = mid;
        }
        var reduced = ToLinearRgb(L, a * lo, b * lo);
        var excess = Math.Max(-low, high - 1);
        var w = Smooth(0, 1, Math.Clamp(excess / 0.05, 0, 1));
        double Mix(double r, double d) => Math.Clamp(r, 0, 1) * (1 - w) + Math.Clamp(d, 0, 1) * w;
        return (Math.Clamp(Mix(raw.R, reduced.R), 0, 1), Math.Clamp(Mix(raw.G, reduced.G), 0, 1), Math.Clamp(Mix(raw.B, reduced.B), 0, 1));
    }

    private static (double L, double A, double B) ToOklab(double r, double g, double b)
    {
        var l = Math.Cbrt(M1[0, 0] * r + M1[0, 1] * g + M1[0, 2] * b);
        var m = Math.Cbrt(M1[1, 0] * r + M1[1, 1] * g + M1[1, 2] * b);
        var s = Math.Cbrt(M1[2, 0] * r + M1[2, 1] * g + M1[2, 2] * b);
        return (M2[0, 0] * l + M2[0, 1] * m + M2[0, 2] * s,
            M2[1, 0] * l + M2[1, 1] * m + M2[1, 2] * s,
            M2[2, 0] * l + M2[2, 1] * m + M2[2, 2] * s);
    }

    private static (double R, double G, double B) ToLinearRgb(double L, double a, double b)
    {
        var l = M2Inverse[0, 0] * L + M2Inverse[0, 1] * a + M2Inverse[0, 2] * b;
        var m = M2Inverse[1, 0] * L + M2Inverse[1, 1] * a + M2Inverse[1, 2] * b;
        var s = M2Inverse[2, 0] * L + M2Inverse[2, 1] * a + M2Inverse[2, 2] * b;
        (l, m, s) = (l * l * l, m * m * m, s * s * s);
        return (M1Inverse[0, 0] * l + M1Inverse[0, 1] * m + M1Inverse[0, 2] * s,
            M1Inverse[1, 0] * l + M1Inverse[1, 1] * m + M1Inverse[1, 2] * s,
            M1Inverse[2, 0] * l + M1Inverse[2, 1] * m + M1Inverse[2, 2] * s);
    }

    internal static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    internal static double ToSrgb(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    }

    /// <summary>Linear light to an 8-bit level, rounding half to even as the reference does.</summary>
    private static byte Byte(double linear) => (byte)Math.Clamp(Math.Round(ToSrgb(linear) * 255), 0, 255);

    private static double Smooth(double e0, double e1, double x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }

    private static double[,] Invert(double[,] m)
    {
        var (a, b, c) = (m[0, 0], m[0, 1], m[0, 2]);
        var (d, e, f) = (m[1, 0], m[1, 1], m[1, 2]);
        var (g, h, i) = (m[2, 0], m[2, 1], m[2, 2]);
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return new[,]
        {
            { (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det },
            { (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det },
            { (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det },
        };
    }
}
