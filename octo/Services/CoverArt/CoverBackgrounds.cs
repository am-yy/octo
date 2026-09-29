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
    /// <summary>Backgrounds this close to the best match count as equally good; the list's name picks among them.</summary>
    private const double Tie = 0.04;

    /// <summary>At most this many near-equal matches take turns, so lists of one colour still vary.</summary>
    private const int Choices = 4;

    /// <summary>
    /// The background for a list. With its music's colours: the one whose colours are nearest
    /// (OKLab, lightness counted half), its second colour weighing in a little; among near-equal
    /// matches the list's name decides, so a list keeps its background while its music does.
    /// Without: one picked by the name's hash alone.
    /// </summary>
    public static int Choose(CoverBook book, CoverMusic? music, string id)
    {
        var hash = CoverColours.CoverHash(id) >>> 7;
        if (music is null) return (int)(hash % book.Backgrounds.Count);

        var ranked = book.Backgrounds
            .Select((background, index) => (Index: index, Distance: Distance(music, background)))
            .OrderBy(pair => pair.Distance).ThenBy(pair => pair.Index)
            .ToList();
        var near = ranked.TakeWhile(pair => pair.Distance <= ranked[0].Distance + Tie).Take(Choices).ToList();
        return near[(int)(hash % near.Count)].Index;
    }

    private static double Distance(CoverMusic music, CoverBook.Background background)
    {
        var first = Nearest(music.First, background.Hues, out var at);
        // A background's own strongest colour is what reads first, so matching it counts most.
        var distance = first * (at == 0 ? 1.0 : 1.2);
        if (music.Second is { } second) distance += 0.35 * Nearest(second, background.Hues, out _);
        return distance;
    }

    private static double Nearest(Lch colour, CoverBook.Hue[] hues, out int index)
    {
        index = 0;
        var best = double.MaxValue;
        for (var i = 0; i < hues.Length; i++)
        {
            var d = Lab(colour, new Lch(hues[i].L, hues[i].C, hues[i].H));
            if (d < best) (best, index) = (d, i);
        }
        return best;
    }

    private static double Lab(Lch x, Lch y)
    {
        var (ax, bx) = (x.C * Math.Cos(x.H * Math.PI / 180), x.C * Math.Sin(x.H * Math.PI / 180));
        var (ay, by) = (y.C * Math.Cos(y.H * Math.PI / 180), y.C * Math.Sin(y.H * Math.PI / 180));
        var dl = (x.L - y.L) * 0.5;
        return Math.Sqrt(dl * dl + (ax - ay) * (ax - ay) + (bx - by) * (bx - by));
    }

    private static readonly ConcurrentDictionary<(string File, int Side), Image<Rgb24>> Cache = new();

    /// <summary>
    /// A background at <paramref name="side"/> pixels, a copy the caller owns. At the stored size
    /// it is the file itself; at half, each 2 by 2 block averaged, (a + b + c + d + 2) / 4, as the
    /// reference defines; any other size is resampled from the stored file.
    /// </summary>
    public static Image<Rgb24> Load(CoverBook book, int index, int side)
    {
        var file = book.Backgrounds[index].File;
        if (Cache.Count > 12) Cache.Clear();
        var image = Cache.GetOrAdd((file, side), key => Decode(book, key.File, key.Side));
        lock (image) return image.Clone();
    }

    private static Image<Rgb24> Decode(CoverBook book, string file, int side)
    {
        using var stream = CoverBook.Resource("Backgrounds." + file);
        var stored = Image.Load<Rgb24>(stream);
        if (stored.Width == side && stored.Height == side) return stored;
        using (stored)
        {
            if (stored.Width == side * 2 && stored.Height == side * 2) return Halve(stored);
            return stored.Clone(ctx => ctx.Resize(side, side, KnownResamplers.Lanczos3));
        }
    }

    private static Image<Rgb24> Halve(Image<Rgb24> source)
    {
        var side = source.Width / 2;
        var output = new Rgb24[side * side];
        source.ProcessPixelRows(access =>
        {
            for (var y = 0; y < side; y++)
            {
                var top = access.GetRowSpan(2 * y);
                var bottom = access.GetRowSpan(2 * y + 1);
                for (var x = 0; x < side; x++)
                {
                    var (a, b, c, d) = (top[2 * x], top[2 * x + 1], bottom[2 * x], bottom[2 * x + 1]);
                    output[y * side + x] = new Rgb24(
                        (byte)((a.R + b.R + c.R + d.R + 2) / 4),
                        (byte)((a.G + b.G + c.G + d.G + 2) / 4),
                        (byte)((a.B + b.B + c.B + d.B + 2) / 4));
                }
            }
        });
        return Image.LoadPixelData<Rgb24>(output, side, side);
    }
}
