using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Octo.Services.CoverArt;

/// <summary>What makes a cover usable (#51): decodable, big enough, and square.</summary>
internal static class CoverImage
{
    /// <summary>JPEG rounding and one-pixel crops leave real covers a few pixels off square.</summary>
    private const double SquareTolerance = 0.03;
    private const int MinSide = 150;

    public static (int Width, int Height)? Measure(byte[] bytes)
    {
        try
        {
            var info = Image.Identify(bytes);
            return (info.Width, info.Height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodable and big enough, and square when <paramref name="requireSquare"/>. A cover that is
    /// not square is a video thumbnail: a 16:9 frame with the artist off-centre, which in a grid
    /// of square covers is the one that looks broken.
    /// </summary>
    public static bool IsUsable(byte[]? bytes, bool requireSquare)
    {
        if (bytes is not { Length: > 0 } || Measure(bytes) is not { } size) return false;
        if (Math.Min(size.Width, size.Height) < MinSide) return false;
        return !requireSquare
            || Math.Abs(size.Width - size.Height) <= Math.Max(size.Width, size.Height) * SquareTolerance;
    }

    /// <summary>
    /// The centre square. A YouTube "Topic" upload letterboxes the real cover inside a 16:9 frame,
    /// so for those this IS the cover; for any other video it is the middle of the picture, which
    /// still beats a stretched thumbnail.
    /// </summary>
    public static byte[]? CropToSquare(byte[] bytes)
    {
        try
        {
            using var image = Image.Load(bytes);
            var side = Math.Min(image.Width, image.Height);
            var x = (image.Width - side) / 2;
            var y = (image.Height - side) / 2;
            image.Mutate(ctx => ctx.Crop(new Rectangle(x, y, side, side)));
            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = 90 });
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>JPEG as-is, anything else re-encoded, for a cover.jpg that is what its name says.</summary>
    public static byte[] ToJpeg(byte[] bytes)
    {
        try
        {
            if (Image.DetectFormat(bytes).Name.Equals("JPEG", StringComparison.OrdinalIgnoreCase)) return bytes;
            using var image = Image.Load(bytes);
            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = 90 });
            return output.ToArray();
        }
        catch
        {
            return bytes;
        }
    }

    public static string MimeType(byte[] bytes)
    {
        try
        {
            return Image.DetectFormat(bytes).DefaultMimeType;
        }
        catch
        {
            return "image/jpeg";
        }
    }
}
