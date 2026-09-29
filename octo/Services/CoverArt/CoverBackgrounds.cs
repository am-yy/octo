using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Octo.Services.CoverArt;

/// <summary>
/// The library of painted backgrounds: which one a list gets, and its pixels at a size.
/// </summary>
internal static class CoverBackgrounds
{
    /// <summary>
    /// The background for a list, by cover-design.json "background": with its music's colour,
    /// the nearest few (ties in file order) and among them the one the list's id picks; without,
    /// any one, picked by the id alone. The same list with the same music always gets the same.
    /// </summary>
    public static int Choose(CoverBook book, CoverMusic? music, string id)
    {
        var pick = CoverColours.CoverHash(id) >>> 7;
        var all = book.Backgrounds;
        if (music is null) return (int)(pick % all.Count);
        var near = Enumerable.Range(0, all.Count)
            .OrderBy(index => Distance(all[index], music, book.BackgroundChoice))
            .Take(book.BackgroundChoice.Nearest)
            .ToList();
        return near[(int)(pick % near.Count)];
    }

    /// <summary>
    /// How a list's background is turned, so lists that share a background still look apart:
    /// (coverHash(id) >>> 11) mod 8, a quarter turn clockwise for each of its lowest two bits'
    /// worth (v mod 4), then mirrored left to right when v is 4 or more.
    /// </summary>
    public static int Orientation(string id) => (int)((CoverColours.CoverHash(id) >>> 11) % 8);

    /// <summary>Turns a background in place: (v mod 4) quarter turns clockwise, then a mirror left to right when v >= 4.</summary>
    public static void Turn(Image<Rgb24> image, int orientation)
    {
        var turns = orientation % 4;
        var mirror = orientation >= 4;
        if (turns == 0 && !mirror) return;
        image.Mutate(ctx =>
        {
            if (turns != 0) ctx.Rotate(turns switch { 1 => RotateMode.Rotate90, 2 => RotateMode.Rotate180, _ => RotateMode.Rotate270 });
            if (mirror) ctx.Flip(FlipMode.Horizontal);
        });
    }

    /// <summary>
    /// How far a background is from the music's colour: the hue distance to its nearest strong
    /// hue (a later, weaker hue counts a little less, a near-grey one hardly at all) and how
    /// differently vivid that hue is, plus the difference in lightness.
    /// </summary>
    internal static double Distance(CoverBook.Background background, CoverMusic music, CoverBook.BackgroundRule rule)
    {
        var hue = background.Hues.Length == 0 ? 1.0 : background.Hues.Select((h, i) =>
            CoverColours.HueDistance(h.H, music.Hue) / 180.0 + i * rule.HueStep
            + (h.C < rule.GreyBelow ? rule.GreyPenalty : 0.0)
            + Math.Abs(h.C - music.Chroma) * rule.ChromaWeight).Min();
        return hue + Math.Abs(background.MeanLightness - music.Lightness) * rule.LightnessWeight;
    }

    private static readonly ConcurrentDictionary<(string File, int Side), Image<Rgb24>> Cache = new();

    /// <summary>
    /// A background at <paramref name="side"/> pixels, a copy the caller owns: the stored file
    /// halved, each pixel (a + b + c + d + 2) / 4, while that still leaves at least the size
    /// wanted (so 600 is exactly the reference's), then each pixel the mean of the area it
    /// covers, as the apps sample it.
    /// </summary>
    public static Image<Rgb24> Load(CoverBook book, int index, int side)
    {
        var file = book.Backgrounds[index].File;
        if (Cache.Count > 12) Cache.Clear();
        var image = Cache.GetOrAdd((file, side), key => Decode(key.File, key.Side));
        lock (image) return image.Clone();
    }

    private static Image<Rgb24> Decode(string file, int side)
    {
        using var stream = CoverBook.Resource("Backgrounds." + file);
        using var stored = Image.Load<Rgb24>(stream);
        var size = stored.Width;
        var pixels = new Rgb24[size * size];
        stored.CopyPixelDataTo(pixels);
        while (side * 2 <= size && size % 2 == 0)
        {
            pixels = Halve(pixels, size);
            size /= 2;
        }
        if (size != side) pixels = AreaAverage(pixels, size, side);
        return Image.LoadPixelData<Rgb24>(pixels, side, side);
    }

    private static Rgb24[] Halve(Rgb24[] source, int size)
    {
        var half = size / 2;
        var output = new Rgb24[half * half];
        for (var y = 0; y < half; y++)
        for (var x = 0; x < half; x++)
        {
            var (a, b) = (source[2 * y * size + 2 * x], source[2 * y * size + 2 * x + 1]);
            var (c, d) = (source[(2 * y + 1) * size + 2 * x], source[(2 * y + 1) * size + 2 * x + 1]);
            output[y * half + x] = new Rgb24(
                (byte)((a.R + b.R + c.R + d.R + 2) / 4),
                (byte)((a.G + b.G + c.G + d.G + 2) / 4),
                (byte)((a.B + b.B + c.B + d.B + 2) / 4));
        }
        return output;
    }

    /// <summary>Each output pixel the mean of the source area it covers, part pixels in proportion, across then down.</summary>
    private static Rgb24[] AreaAverage(Rgb24[] source, int from, int side)
    {
        var scale = (double)from / side;
        var spans = new (int Start, double[] Weights)[side];
        for (var o = 0; o < side; o++)
        {
            var a = o * scale;
            var b = (o + 1) * scale;
            var start = Math.Clamp((int)Math.Floor(a), 0, from - 1);
            var end = Math.Max(Math.Min(from, (int)Math.Ceiling(b)), start + 1);
            var weights = new double[end - start];
            for (var k = 0; k < weights.Length; k++)
                weights[k] = Math.Max(0.0, Math.Min(b, start + k + 1.0) - Math.Max(a, start + k)) / (b - a);
            spans[o] = (start, weights);
        }
        var across = new double[3, side * from];
        for (var y = 0; y < from; y++)
        for (var x = 0; x < side; x++)
        {
            var (start, weights) = spans[x];
            double r = 0, g = 0, bl = 0;
            for (var k = 0; k < weights.Length; k++)
            {
                var p = source[y * from + start + k];
                r += p.R * weights[k];
                g += p.G * weights[k];
                bl += p.B * weights[k];
            }
            (across[0, y * side + x], across[1, y * side + x], across[2, y * side + x]) = (r, g, bl);
        }
        var output = new Rgb24[side * side];
        for (var i = 0; i < output.Length; i++)
        {
            var x = i % side;
            var (start, weights) = spans[i / side];
            byte Channel(int c)
            {
                var v = 0.0;
                for (var k = 0; k < weights.Length; k++) v += across[c, (start + k) * side + x] * weights[k];
                return (byte)Math.Clamp(CoverColours.Round(v), 0, 255);
            }
            output[i] = new Rgb24(Channel(0), Channel(1), Channel(2));
        }
        return output;
    }
}
