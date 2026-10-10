using Octo.Services.CoverArt;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Octo.Tests;

/// <summary>Covers are read without TIFF, so a crafted BigTIFF cannot hold a thread.</summary>
public sealed class CoverDecodingTests
{
    /// <summary>GHSA-wmxv-xphr-5c9g's input: a little-endian BigTIFF whose first IFD, at offset 16,
    /// declares five billion entries and holds none.</summary>
    private static byte[] SpinningBigTiff()
    {
        var bytes = new byte[24];
        "II"u8.CopyTo(bytes);
        BitConverter.GetBytes((ushort)43).CopyTo(bytes, 2);
        BitConverter.GetBytes((ushort)8).CopyTo(bytes, 4);
        BitConverter.GetBytes(16UL).CopyTo(bytes, 8);
        BitConverter.GetBytes(5_000_000_000UL).CopyTo(bytes, 16);
        return bytes;
    }

    [Fact]
    public async Task ABigTiff_IsUnreadableAtOnce_AndAPngStillReads()
    {
        var tiff = SpinningBigTiff();
        var read = Task.Run(() => (CoverImage.Measure(tiff), CoverImage.LooksHash(tiff), CoverImage.IsUsable(tiff, false)));
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2))));
        Assert.Equal((((int, int)?)null, (ulong?)null, false), await read);

        using var image = new Image<Rgb24>(300, 300);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        Assert.Equal((300, 300), CoverImage.Measure(png.ToArray()));
    }
}
