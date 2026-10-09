using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Octo.Services.CoverArt;

/// <summary>
/// A cover ready to paint: its side in pixels, its background (an index into the library), how
/// that background is turned (0 to 7: quarter turns clockwise, then a mirror from 4 up), and its words.
/// </summary>
public sealed record CoverArt(int Side, int Background, int Orientation, IReadOnlyList<CoverWords> Words);

/// <summary>
/// Paints a composed cover: its painted background at the cover's size, turned as the cover
/// says, the colour-keeping veil under the words, then the words in white at their lines' baselines.
/// </summary>
internal static class CoverPainter
{
    /// <summary>Smoothed edges. ImageSharp.Drawing 3 rasterizes coverage exactly, so there is no
    /// subpixel depth to choose any more.</summary>
    private static readonly DrawingOptions Drawing = new() { GraphicsOptions = new GraphicsOptions { Antialias = true } };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, bool, float), float> Feet = new();

    /// <summary>How far below the top of its line a font's baseline sits.</summary>
    private static float Foot(RichTextOptions options)
    {
        var key = (options.Font.Name, options.Font.IsBold, options.Font.Size);
        return Feet.GetOrAdd(key, _ => TextMeasurer.MeasureBounds("H", options).Bottom);
    }

    public static Image<Rgb24> Paint(CoverBook book, CoverArt art, CoverTypesetter setter, bool drawWords = true)
    {
        var image = CoverBackgrounds.Load(book, art.Background, art.Side);
        CoverBackgrounds.Turn(image, art.Orientation);
        CoverVeil.Apply(image, CoverVeil.Regions(book, art.Words, art.Side), book.Veil);
        if (!drawWords) return image;

        var draws = new List<(RichTextOptions Options, string Text, Color Ink)>();
        foreach (var words in art.Words)
        {
            var ink = Color.White.WithAlpha(CoverColours.A(words.Ink) / 255f);
            foreach (var (text, font, fallbacks, x, baseline) in setter.Place(words))
            {
                var options = new RichTextOptions(font)
                {
                    FallbackFontFamilies = fallbacks,
                    ColorFontSupport = ColorFontSupport.None,
                    KerningMode = KerningMode.Standard,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                // The baseline sits where a capital H's foot is, measured in this very font.
                options.Origin = new PointF(x, baseline - Foot(options));
                draws.Add((options, text, ink));
            }
        }
        // Each line is set on a layer of its own, all at once, then laid over the colours in
        // order: setting type is the slow part of a cover, and lines do not depend on each other.
        var sheets = new (Image<Rgba32>? Layer, Point At)[draws.Count];
        Parallel.For(0, draws.Count, i =>
        {
            var (options, text, ink) = draws[i];
            var bounds = TextMeasurer.MeasureBounds(text, options);
            var left = (int)MathF.Floor(bounds.Left) - 2;
            var top = (int)MathF.Floor(bounds.Top) - 2;
            var width = (int)MathF.Ceiling(bounds.Right) + 2 - left;
            var height = (int)MathF.Ceiling(bounds.Bottom) + 2 - top;
            if (width <= 0 || height <= 0) return;
            var layer = new Image<Rgba32>(width, height);
            var local = new RichTextOptions(options) { Origin = new PointF(options.Origin.X - left, options.Origin.Y - top) };
            layer.Mutate(ctx => ctx.Paint(Drawing, canvas => canvas.DrawText(local, text, Brushes.Solid(ink), null)));
            sheets[i] = (layer, new Point(left, top));
        });
        image.Mutate(ctx =>
        {
            foreach (var (layer, at) in sheets)
                if (layer is not null) ctx.DrawImage(layer, at, 1f);
        });
        foreach (var (layer, _) in sheets) layer?.Dispose();
        return image;
    }
}
