using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Octo.Services.CoverArt;

/// <summary>
/// Draws the small SVG subset the cover kit's templates use (#54), so covers need no browser, no
/// native library and no new package: a gradient background, icon strokes and fills under
/// transform lists, potrace-style filled paths, and a line of text. Anything outside that subset
/// is skipped rather than guessed at, and a template that cannot be drawn at all gives null so the
/// caller can fall back.
/// </summary>
internal static class SvgTemplateRenderer
{
    private sealed record Paint(string Fill, string Stroke, float StrokeWidth, string LineCap, string LineJoin,
        float Opacity, float FillOpacity, float StrokeOpacity, string FillRule, float FontSize, string FontWeight,
        string TextAnchor, string Color);

    private static readonly Paint Initial =
        new("black", "none", 1f, "butt", "miter", 1f, 1f, 1f, "nonzero", 16f, "normal", "start", "black");

    /// <summary>Narrower than this on a 600-pixel cover is invisible, and costly on a long path.</summary>
    private const float ThinnestStroke = 0.35f;

    private static readonly Regex Number = new(@"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.Compiled);
    private static readonly Regex TransformPart = new(@"([A-Za-z]+)\s*\(([^)]*)\)", RegexOptions.Compiled);

    /// <summary>A JPEG of the template at <paramref name="size"/> pixels square, or null.</summary>
    public static byte[]? Render(string svg, int size = 600, ILogger? logger = null)
    {
        try
        {
            var root = XDocument.Parse(svg).Root;
            if (root is null || root.Name.LocalName != "svg") return null;

            var (minX, minY, width, height) = ViewBox(root);
            if (width <= 0 || height <= 0) return null;
            var scale = Math.Min(size / width, size / height);
            var matrix = Matrix3x2.CreateTranslation(-minX, -minY) * Matrix3x2.CreateScale(scale);
            var gradients = root.Descendants()
                .Where(element => element.Name.LocalName == "linearGradient" && element.Attribute("id") is not null)
                .GroupBy(element => element.Attribute("id")!.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            using var image = new Image<Rgba32>(size, size, new Rgba32(0, 0, 0, 255));
            image.Mutate(ctx => Walk(ctx, root, matrix, Initial, gradients, logger));
            using var output = new MemoryStream();
            image.SaveAsJpeg(output, new JpegEncoder { Quality = 90 });
            return output.ToArray();
        }
        catch (Exception ex)
        {
            logger?.LogDebug("A cover template could not be drawn: {M}", ex.Message);
            return null;
        }
    }

    private static void Walk(IImageProcessingContext ctx, XElement element, Matrix3x2 parent, Paint inherited,
        IReadOnlyDictionary<string, XElement> gradients, ILogger? logger)
    {
        var matrix = Transform(element.Attribute("transform")?.Value, logger) * parent;
        var paint = Inherit(inherited, element);
        switch (element.Name.LocalName)
        {
            case "svg" or "g" or "a":
                foreach (var child in element.Elements()) Walk(ctx, child, matrix, paint, gradients, logger);
                break;
            case "rect":
                if (Rect(element) is { } rect) Draw(ctx, rect, closed: true, matrix, paint, gradients);
                break;
            case "circle":
                if (Attr(element, "r") is > 0 and var radius)
                    Draw(ctx, new EllipsePolygon(Attr(element, "cx") ?? 0, Attr(element, "cy") ?? 0, radius), true, matrix, paint, gradients);
                break;
            case "ellipse":
                if (Attr(element, "rx") is > 0 and var rx && Attr(element, "ry") is > 0 and var ry)
                    Draw(ctx, new EllipsePolygon(new PointF(Attr(element, "cx") ?? 0, Attr(element, "cy") ?? 0), new SizeF(rx * 2, ry * 2)),
                        true, matrix, paint, gradients);
                break;
            case "line":
                Draw(ctx, new SixLabors.ImageSharp.Drawing.Path(new LinearLineSegment(
                        new PointF(Attr(element, "x1") ?? 0, Attr(element, "y1") ?? 0),
                        new PointF(Attr(element, "x2") ?? 0, Attr(element, "y2") ?? 0))),
                    false, matrix, paint with { Fill = "none" }, gradients);
                break;
            case "polyline" or "polygon":
                var points = Points(element.Attribute("points")?.Value);
                if (points.Length < 2) break;
                var segment = new LinearLineSegment(points);
                if (element.Name.LocalName == "polygon") Draw(ctx, new Polygon(segment), true, matrix, paint, gradients);
                else Draw(ctx, new SixLabors.ImageSharp.Drawing.Path(segment), false, matrix, paint, gradients);
                break;
            case "path":
                var data = NormalizePathData(element.Attribute("d")?.Value ?? "");
                if (data.Length == 0) break;
                if (SixLabors.ImageSharp.Drawing.Path.TryParseSvgPath(data, out var path))
                    Draw(ctx, path, true, matrix, paint, gradients);
                else
                    logger?.LogWarning("A cover template path could not be read and was skipped");
                break;
            case "text":
                DrawText(ctx, element, matrix, paint, gradients);
                break;
        }
    }

    private static void Draw(IImageProcessingContext ctx, IPath shape, bool closed, Matrix3x2 matrix, Paint paint,
        IReadOnlyDictionary<string, XElement> gradients)
    {
        var device = shape.Transform(matrix);
        var bounds = shape.Bounds;

        if (closed && Brush(paint.Fill, paint.Opacity * paint.FillOpacity, bounds, matrix, gradients, paint.Color) is { } fill)
        {
            var options = new DrawingOptions
            {
                ShapeOptions = new ShapeOptions
                {
                    IntersectionRule = paint.FillRule == "evenodd" ? IntersectionRule.EvenOdd : IntersectionRule.NonZero,
                },
            };
            ctx.Fill(options, fill, device);
        }

        var width = paint.StrokeWidth * ScaleOf(matrix);
        if (width >= ThinnestStroke
            && Brush(paint.Stroke, paint.Opacity * paint.StrokeOpacity, bounds, matrix, gradients, paint.Color) is { } stroke)
        {
            var pen = new SolidPen(new PenOptions(stroke, width, [])
            {
                EndCapStyle = paint.LineCap switch { "round" => EndCapStyle.Round, "square" => EndCapStyle.Square, _ => EndCapStyle.Butt },
                JointStyle = paint.LineJoin switch { "round" => JointStyle.Round, "bevel" => JointStyle.Square, _ => JointStyle.Miter },
            });
            ctx.Draw(pen, device);
        }
    }

    /// <summary>
    /// One line of text, anchored the SVG way: x by text-anchor, y on the baseline. The kit's own
    /// font is not shipped, so DejaVu Sans, which the image carries, stands in for it.
    /// </summary>
    private static void DrawText(IImageProcessingContext ctx, XElement element, Matrix3x2 matrix, Paint paint,
        IReadOnlyDictionary<string, XElement> gradients)
    {
        var text = string.Concat(element.DescendantNodes().OfType<XText>().Select(node => node.Value)).Trim();
        if (text.Length == 0 || CoverFonts.Family() is not { } family) return;

        var size = paint.FontSize * ScaleOf(matrix);
        if (size <= 0) return;
        var bold = paint.FontWeight is "bold" or "bolder"
            || (int.TryParse(paint.FontWeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight) && weight >= 600);
        var font = family.CreateFont(size, bold ? FontStyle.Bold : FontStyle.Regular);

        var anchor = Vector2.Transform(new Vector2(FirstNumber(element.Attribute("x")?.Value), FirstNumber(element.Attribute("y")?.Value)), matrix);
        var ascent = font.FontMetrics.HorizontalMetrics.Ascender * size / font.FontMetrics.UnitsPerEm;
        var bounds = new RectangleF(0, 0, 1, 1);
        if (Brush(paint.Fill, paint.Opacity * paint.FillOpacity, bounds, matrix, gradients, paint.Color) is not { } brush) return;

        ctx.DrawText(new RichTextOptions(font)
        {
            Origin = new PointF(anchor.X, anchor.Y - ascent),
            HorizontalAlignment = paint.TextAnchor switch
            {
                "middle" => HorizontalAlignment.Center,
                "end" => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            },
            VerticalAlignment = VerticalAlignment.Top,
        }, text, brush);
    }

    private static Brush? Brush(string spec, float alpha, RectangleF bounds, Matrix3x2 matrix,
        IReadOnlyDictionary<string, XElement> gradients, string currentColor)
    {
        spec = spec.Trim();
        if (spec.Length == 0 || spec is "none" or "transparent" || alpha <= 0) return null;
        if (spec.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var id = spec[4..].TrimEnd(')').Trim().Trim('\'', '"').TrimStart('#');
            return gradients.TryGetValue(id, out var gradient) ? Gradient(gradient, alpha, bounds, matrix) : null;
        }
        if (spec.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) spec = currentColor;
        return ParseColor(spec) is { } color ? new SolidBrush(WithAlpha(color, alpha)) : null;
    }

    private static Brush? Gradient(XElement gradient, float alpha, RectangleF bounds, Matrix3x2 matrix)
    {
        var stops = gradient.Elements()
            .Where(element => element.Name.LocalName == "stop")
            .Select(stop =>
            {
                var style = Style(stop);
                var offset = Math.Clamp(Fraction(stop.Attribute("offset")?.Value, 0f), 0f, 1f);
                var color = ParseColor(style.GetValueOrDefault("stop-color") ?? stop.Attribute("stop-color")?.Value ?? "black")
                    ?? Color.Black;
                var opacity = Fraction(style.GetValueOrDefault("stop-opacity") ?? stop.Attribute("stop-opacity")?.Value, 1f);
                return new ColorStop(offset, WithAlpha(color, opacity * alpha));
            })
            .ToArray();
        if (stops.Length == 0) return null;
        if (stops.Length == 1) return new SolidBrush(stops[0].Color);

        var x1 = Fraction(gradient.Attribute("x1")?.Value, 0f);
        var y1 = Fraction(gradient.Attribute("y1")?.Value, 0f);
        var x2 = Fraction(gradient.Attribute("x2")?.Value, 1f);
        var y2 = Fraction(gradient.Attribute("y2")?.Value, 0f);
        Vector2 from, to;
        if (gradient.Attribute("gradientUnits")?.Value == "userSpaceOnUse")
        {
            from = new Vector2(x1, y1);
            to = new Vector2(x2, y2);
        }
        else
        {
            from = new Vector2(bounds.X + x1 * bounds.Width, bounds.Y + y1 * bounds.Height);
            to = new Vector2(bounds.X + x2 * bounds.Width, bounds.Y + y2 * bounds.Height);
        }
        from = Vector2.Transform(from, matrix);
        to = Vector2.Transform(to, matrix);
        return new LinearGradientBrush(new PointF(from.X, from.Y), new PointF(to.X, to.Y), GradientRepetitionMode.None, stops);
    }

    private static Paint Inherit(Paint paint, XElement element)
    {
        var style = Style(element);
        string? Get(string name)
        {
            var value = style.GetValueOrDefault(name) ?? element.Attribute(name)?.Value;
            return string.IsNullOrWhiteSpace(value) || value.Trim() == "inherit" ? null : value.Trim();
        }
        float? Measure(string name) => Get(name) is { } text && TryNumber(text, out var number) ? number : null;

        return paint with
        {
            Fill = Get("fill") ?? paint.Fill,
            Stroke = Get("stroke") ?? paint.Stroke,
            StrokeWidth = Measure("stroke-width") ?? paint.StrokeWidth,
            LineCap = Get("stroke-linecap") ?? paint.LineCap,
            LineJoin = Get("stroke-linejoin") ?? paint.LineJoin,
            Opacity = paint.Opacity * Math.Clamp(Measure("opacity") ?? 1f, 0f, 1f),
            FillOpacity = Math.Clamp(Measure("fill-opacity") ?? paint.FillOpacity, 0f, 1f),
            StrokeOpacity = Math.Clamp(Measure("stroke-opacity") ?? paint.StrokeOpacity, 0f, 1f),
            FillRule = Get("fill-rule") ?? paint.FillRule,
            FontSize = Measure("font-size") ?? paint.FontSize,
            FontWeight = Get("font-weight") ?? paint.FontWeight,
            TextAnchor = Get("text-anchor") ?? paint.TextAnchor,
            Color = Get("color") ?? paint.Color,
        };
    }

    private static Dictionary<string, string> Style(XElement element)
    {
        var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in (element.Attribute("style")?.Value ?? "").Split(';'))
        {
            var parts = declaration.Split(':', 2);
            if (parts.Length == 2 && parts[0].Trim().Length > 0) declarations[parts[0].Trim()] = parts[1].Trim();
        }
        return declarations;
    }

    /// <summary>
    /// An SVG transform list, composed as SVG composes it: "A B" applies B first, then A. With
    /// System.Numerics' row vectors that is B times A, so each later transform multiplies on the
    /// left of what came before.
    /// </summary>
    internal static Matrix3x2 Transform(string? value, ILogger? logger = null)
    {
        var result = Matrix3x2.Identity;
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (Match part in TransformPart.Matches(value))
        {
            var args = Number.Matches(part.Groups[2].Value)
                .Select(match => float.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            float Arg(int index, float fallback) => index < args.Length ? args[index] : fallback;
            var local = part.Groups[1].Value switch
            {
                "translate" => Matrix3x2.CreateTranslation(Arg(0, 0), Arg(1, 0)),
                "scale" => Matrix3x2.CreateScale(Arg(0, 1), Arg(1, Arg(0, 1))),
                "matrix" when args.Length == 6 => new Matrix3x2(args[0], args[1], args[2], args[3], args[4], args[5]),
                "rotate" => Matrix3x2.CreateRotation(Arg(0, 0) * MathF.PI / 180f, new Vector2(Arg(1, 0), Arg(2, 0))),
                "skewX" => Matrix3x2.CreateSkew(Arg(0, 0) * MathF.PI / 180f, 0),
                "skewY" => Matrix3x2.CreateSkew(0, Arg(0, 0) * MathF.PI / 180f),
                var other => Unknown(other, logger),
            };
            result = local * result;
        }
        return result;
    }

    private static Matrix3x2 Unknown(string name, ILogger? logger)
    {
        logger?.LogDebug("A cover template transform '{Name}' is not supported and was ignored", name);
        return Matrix3x2.Identity;
    }

    private static readonly Dictionary<char, int> Arity = new()
    {
        ['M'] = 2, ['L'] = 2, ['T'] = 2, ['H'] = 1, ['V'] = 1, ['C'] = 6, ['S'] = 4, ['Q'] = 4, ['A'] = 7, ['Z'] = 0,
    };

    /// <summary>
    /// Path data rewritten as one command letter per segment and one space between every token.
    /// Real path data packs numbers ("12-2", "1.5.5"), repeats a command's arguments without
    /// repeating the letter (every potrace path does), and packs an arc's two flags ("a1 1 0 011
    /// 1"), which only a reader that knows it is inside an arc can split.
    /// </summary>
    internal static string NormalizePathData(string data)
    {
        var output = new StringBuilder(data.Length + 16);
        var command = '\0';
        var group = new List<string>(7);
        var i = 0;
        while (i < data.Length)
        {
            var c = data[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }

            if (char.IsLetter(c))
            {
                var upper = char.ToUpperInvariant(c);
                if (!Arity.ContainsKey(upper)) return "";
                command = c;
                group.Clear();
                i++;
                if (upper == 'Z') output.Append(c).Append(' ');
                continue;
            }

            if (command == '\0' || char.ToUpperInvariant(command) == 'Z') return output.ToString().Trim();
            var arity = Arity[char.ToUpperInvariant(command)];
            string token;
            if (char.ToUpperInvariant(command) == 'A' && group.Count is 3 or 4)
            {
                if (c is not ('0' or '1')) return output.ToString().Trim();
                token = c.ToString();
                i++;
            }
            else
            {
                var match = Number.Match(data, i);
                if (!match.Success || match.Index != i) return output.ToString().Trim();
                token = match.Value;
                i += match.Length;
            }

            group.Add(token);
            if (group.Count < arity) continue;
            output.Append(command).Append(' ').Append(string.Join(' ', group)).Append(' ');
            group.Clear();
            // After a moveto, further pairs are lineto: M 1 2 3 4 is a move and a line.
            if (command == 'M') command = 'L';
            else if (command == 'm') command = 'l';
        }
        return output.ToString().Trim();
    }

    private static IPath? Rect(XElement element)
    {
        var x = Attr(element, "x") ?? 0;
        var y = Attr(element, "y") ?? 0;
        var width = Attr(element, "width") ?? 0;
        var height = Attr(element, "height") ?? 0;
        if (width <= 0 || height <= 0) return null;

        var rx = Attr(element, "rx");
        var ry = Attr(element, "ry");
        var radiusX = Math.Min(rx ?? ry ?? 0, width / 2);
        var radiusY = Math.Min(ry ?? rx ?? 0, height / 2);
        if (radiusX <= 0 || radiusY <= 0) return new RectangularPolygon(x, y, width, height);

        var d = string.Create(CultureInfo.InvariantCulture,
            $"M {x + radiusX} {y} H {x + width - radiusX} A {radiusX} {radiusY} 0 0 1 {x + width} {y + radiusY} "
            + $"V {y + height - radiusY} A {radiusX} {radiusY} 0 0 1 {x + width - radiusX} {y + height} "
            + $"H {x + radiusX} A {radiusX} {radiusY} 0 0 1 {x} {y + height - radiusY} "
            + $"V {y + radiusY} A {radiusX} {radiusY} 0 0 1 {x + radiusX} {y} Z");
        return SixLabors.ImageSharp.Drawing.Path.TryParseSvgPath(d, out var path) ? path : new RectangularPolygon(x, y, width, height);
    }

    private static PointF[] Points(string? value)
    {
        var numbers = Number.Matches(value ?? "")
            .Select(match => float.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        return Enumerable.Range(0, numbers.Length / 2).Select(i => new PointF(numbers[i * 2], numbers[i * 2 + 1])).ToArray();
    }

    private static (float MinX, float MinY, float Width, float Height) ViewBox(XElement root)
    {
        var box = Number.Matches(root.Attribute("viewBox")?.Value ?? "")
            .Select(match => float.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        if (box.Length == 4) return (box[0], box[1], box[2], box[3]);
        return (0, 0, Attr(root, "width") ?? 600, Attr(root, "height") ?? 600);
    }

    private static float? Attr(XElement element, string name) =>
        element.Attribute(name)?.Value is { } text && TryNumber(text, out var value) ? value : null;

    private static float FirstNumber(string? value) =>
        value is not null && Number.Match(value) is { Success: true } match
            ? float.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0;

    private static bool TryNumber(string text, out float value)
    {
        var match = Number.Match(text);
        value = match.Success ? float.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0;
        return match.Success;
    }

    /// <summary>A fraction, or a percentage of one.</summary>
    private static float Fraction(string? value, float fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || !TryNumber(value, out var number)) return fallback;
        return value.TrimEnd().EndsWith('%') ? number / 100f : number;
    }

    private static float ScaleOf(Matrix3x2 matrix) =>
        MathF.Sqrt(MathF.Abs(matrix.M11 * matrix.M22 - matrix.M12 * matrix.M21));

    internal static Color? ParseColor(string spec)
    {
        spec = spec.Trim();
        var rgb = Regex.Match(spec, @"^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$", RegexOptions.IgnoreCase);
        if (rgb.Success)
        {
            byte Channel(int index) => (byte)Math.Clamp(float.Parse(rgb.Groups[index].Value, CultureInfo.InvariantCulture), 0, 255);
            var alpha = rgb.Groups[4].Success ? Math.Clamp(float.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture), 0, 1) : 1f;
            return Color.FromRgba(Channel(1), Channel(2), Channel(3), (byte)(alpha * 255));
        }
        return Color.TryParse(spec, out var color) ? color : null;
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        var pixel = color.ToPixel<Rgba32>();
        pixel.A = (byte)Math.Clamp(pixel.A * alpha, 0, 255);
        return Color.FromPixel(pixel);
    }
}

/// <summary>The one font covers are lettered in: DejaVu Sans, which the image ships, else any.</summary>
internal static class CoverFonts
{
    public static FontFamily? Family()
    {
        var families = SystemFonts.Families.ToList();
        if (families.Count == 0) return null;
        var dejavu = families.FirstOrDefault(family => family.Name.Equals("DejaVu Sans", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(dejavu.Name) ? families[0] : dejavu;
    }
}
