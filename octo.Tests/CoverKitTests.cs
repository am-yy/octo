using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Services.CoverArt;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Octo.Tests;

/// <summary>
/// The SVG subset the cover kit's templates use (#54). Path data in the wild is packed in ways a
/// strict reader refuses, so the normaliser is tested on the real shapes: Lucide's compact paths,
/// potrace's repeated segments and packed arc flags.
/// </summary>
public class SvgTemplateRendererTests
{
    [Theory]
    [InlineData("M9 18V5l12-2v13", "M 9 18 V 5 l 12 -2 v 13")]
    [InlineData("a1 1 0 011 1", "a 1 1 0 0 1 1 1")]
    [InlineData("M0 0a1 1 0 011 1", "M 0 0 a 1 1 0 0 1 1 1")]
    [InlineData("M3 14h3a2 2 0 0 1 2 2", "M 3 14 h 3 a 2 2 0 0 1 2 2")]
    [InlineData("M100 2591 l0 -67 32 -2 c44 -4 126 -42 169 -80 19 -17 48 -57 64 -89",
        "M 100 2591 l 0 -67 l 32 -2 c 44 -4 126 -42 169 -80 c 19 -17 48 -57 64 -89")]
    [InlineData("M1 2 3 4", "M 1 2 L 3 4")]
    [InlineData("m1 2 3 4z", "m 1 2 l 3 4 z")]
    [InlineData("M.5.5l-.586 1.414", "M .5 .5 l -.586 1.414")]
    [InlineData("M1,2 L3,4 Z", "M 1 2 L 3 4 Z")]
    [InlineData("M1 2 X3 4", "")]
    public void NormalizePathData_OneLetterPerSegmentAndSpacesBetweenTokens(string data, string expected)
        => Assert.Equal(expected, SvgTemplateRenderer.NormalizePathData(data));

    /// <summary>SVG composes a transform list right to left: the kit's icons are scaled, then moved.</summary>
    [Theory]
    [InlineData("translate(156 156) scale(12) translate(-0 -0)", 1, 1, 168, 168)]
    [InlineData("translate(10,20) scale(2,-1)", 1, 1, 12, 19)]
    [InlineData("matrix(1 0 0 1 5 6)", 1, 1, 6, 7)]
    [InlineData("rotate(90 10 10)", 20, 10, 10, 20)]
    [InlineData("", 3, 4, 3, 4)]
    public void Transform_ComposesTheWaySvgDoes(string transform, float x, float y, float expectedX, float expectedY)
    {
        var point = Vector2.Transform(new Vector2(x, y), SvgTemplateRenderer.Transform(transform));

        Assert.Equal(expectedX, point.X, 3);
        Assert.Equal(expectedY, point.Y, 3);
    }

    private const string Template = """
        <svg xmlns="http://www.w3.org/2000/svg" width="600" height="600" viewBox="0 0 600 600">
          <defs><linearGradient id="f" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#FF0000"/><stop offset="1" stop-color="#0000FF"/></linearGradient></defs>
          <rect width="600" height="600" fill="url(#f)"/>
          <g transform="translate(156 156) scale(12)" fill="none" stroke="#FFFFFF" stroke-width="1.9" stroke-linecap="round"><circle cx="12" cy="12" r="10"/></g>
          <g transform="translate(0,600) scale(0.1,-0.1)"><path d="M300 300 l0 400 400 0 0 -400 z" fill="#00FF00"/></g>
        </svg>
        """;

    [Fact]
    public void Render_DrawsTheGradientTheStrokeAndAFlippedPotraceFill()
    {
        var jpeg = SvgTemplateRenderer.Render(Template)!;

        using var image = Image.Load<Rgb24>(jpeg);
        Assert.Equal(600, image.Width);
        var topLeft = image[6, 6];
        Assert.True(topLeft.R > 200 && topLeft.B < 70, $"top left {topLeft}");
        var bottomRight = image[593, 593];
        Assert.True(bottomRight.B > 200 && bottomRight.R < 70, $"bottom right {bottomRight}");
        var ring = image[420, 300];
        Assert.True(ring.R > 200 && ring.G > 200 && ring.B > 200, $"ring {ring}");
        // 30..70 in potrace units under scale(0.1,-0.1) and a 600 flip: x 30..70, y 530..570.
        var square = image[50, 550];
        Assert.True(square.G > 200 && square.R < 80 && square.B < 80, $"square {square}");
    }

    [Theory]
    [InlineData("not svg")]
    [InlineData("<html/>")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 0 0"/>""")]
    public void Render_SomethingItCannotDraw_IsNull(string svg) => Assert.Null(SvgTemplateRenderer.Render(svg));

    /// <summary>The decades are lettering: centred on x, sitting on the baseline at y.</summary>
    [Fact]
    public void Render_TextSitsOnItsBaselineCentredOnItsAnchor()
    {
        if (CoverFonts.Family() is null) return;
        var jpeg = SvgTemplateRenderer.Render("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 600"><rect width="600" height="600" fill="#000000"/>
            <text x="300" y="360" text-anchor="middle" font-weight="800" font-size="190" fill="#FFFFFF">90s</text></svg>
            """)!;

        using var image = Image.Load<Rgb24>(jpeg);
        int left = 0, right = 0, below = 0, above = 0;
        for (var y = 0; y < 600; y++)
        for (var x = 0; x < 600; x++)
        {
            if (image[x, y].R < 160) continue;
            if (x < 300) left++; else right++;
            if (y > 375) below++; else above++;
        }
        Assert.True(left > 500 && right > 500, $"left {left}, right {right}");
        Assert.InRange(left / (double)right, 0.6, 1.6);
        Assert.True(below < above / 10, $"below the baseline {below}, above {above}");
    }
}

/// <summary>Which design a name gets, from a small kit built for the test.</summary>
public class CoverKitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-kit-" + Guid.NewGuid());
    internal string KitDirectory => Path.Combine(_root, "kit");
    internal string CoversDirectory => Path.Combine(_root, "config", "covers");

    internal const string IconTemplate = """
        <svg xmlns="http://www.w3.org/2000/svg" width="600" height="600" viewBox="0 0 600 600"><defs><linearGradient id="f" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="__BG_FROM__"/><stop offset="1" stop-color="__BG_TO__"/></linearGradient></defs><rect width="600" height="600" fill="url(#f)"/><g transform="translate(156.0 156.0) scale(12.000)" fill="none" stroke="__STROKE__" stroke-width="__STROKE_WIDTH__" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/></g></svg>
        """;

    public CoverKitTests()
    {
        Directory.CreateDirectory(Path.Combine(KitDirectory, "templates"));
        Directory.CreateDirectory(CoversDirectory);
        File.WriteAllText(Path.Combine(KitDirectory, "palette.json"), """
            {"stroke_default":"#FFFFFF","stroke_width_default":1.9,"lists":{
              "Rock":{"from":"#B85851","to":"#80462D"},
              "R&B & Soul":{"from":"#9E51B8","to":"#7E2D80"},
              "Your Mix":{"from":"#515EB8","to":"#352D80"},
              "1990s":{"from":"#7CB851","to":"#3C802D"},
              "Metal":{"from":"#5170B8","to":"#2D3280"},
              "Other":{"from":"#A95F5F","to":"#754637"}}}
            """);
        foreach (var name in new[] { "Rock", "RandB_and_Soul", "Your_Mix", "1990s", "Other" })
            File.WriteAllText(Path.Combine(KitDirectory, "templates", name + ".svg"), IconTemplate);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private CoverKit Kit() => new(KitDirectory);

    [Theory]
    [InlineData("Rock", "Rock")]
    [InlineData("rock", "Rock")]
    [InlineData("Rock Mix", "Rock")]
    [InlineData("▸ Rock", "Rock")]
    [InlineData("R&B", "R&B & Soul")]
    [InlineData("Soul Radio", "R&B & Soul")]
    [InlineData("Your Mix", "Your Mix")]
    [InlineData("1990s Mix", "1990s")]
    public void TryResolve_MatchesExactlyOrOnAPartOfACompoundName(string query, string expected)
    {
        Assert.True(Kit().TryResolve(query, out var entry));
        Assert.Equal(expected, entry.Name);
    }

    /// <summary>A cover that is almost the right genre is the wrong genre.</summary>
    [Theory]
    [InlineData("Hip-Hop")]
    [InlineData("1980s Mix")]
    [InlineData("Rocksteady")]
    [InlineData("Metal")]
    [InlineData("")]
    public void TryResolve_NothingCloseCounts_NorAListWithoutATemplate(string query)
        => Assert.False(Kit().TryResolve(query, out _));

    [Fact]
    public void Generic_IsTheSameForANameAndDiffersBetweenNames()
    {
        var polka = CoverKit.Generic("Polka");

        Assert.Equal(polka, CoverKit.Generic("polka"));
        Assert.NotEqual(polka, CoverKit.Generic("Zydeco"));
        Assert.Matches("^#[0-9A-F]{6}$", polka.From);
        Assert.Equal("Other", Kit().GenericEntry("Polka")?.Name);
    }

    [Theory]
    [InlineData("R&B & Soul", "RandB_and_Soul")]
    [InlineData("▸ Review", "t_Review")]
    [InlineData("◆ Keep", "d_Keep")]
    [InlineData("* Liked Songs", "s_Liked_Songs")]
    public void Slug_IsTheKitsFileName(string name, string expected) => Assert.Equal(expected, CoverKit.Slug(name));
}

/// <summary>
/// Named covers and the provenance rule (R-M14): the Octo badge marks what came from outside the
/// library, so a radio station carries it and a mix, the listener's own music, does not.
/// </summary>
public class NamedCoverTests : IDisposable
{
    private readonly CoverKitTests _kit = new();

    public void Dispose()
    {
        _kit.Dispose();
        GC.SuppressFinalize(this);
    }

    private CoverArtService Service(bool withKit = true) => new(NullLogger<CoverArtService>.Instance, _kit.CoversDirectory,
        withKit ? _kit.KitDirectory : Path.Combine(_kit.CoversDirectory, "no-kit"));

    private static Rgb24 Pixel(byte[] jpeg, int x, int y)
    {
        using var image = Image.Load<Rgb24>(jpeg);
        return image[x, y];
    }

    private static bool Near(Rgb24 pixel, string hex, int tolerance = 24)
    {
        var expected = Color.ParseHex(hex).ToPixel<Rgb24>();
        return Math.Abs(pixel.R - expected.R) <= tolerance && Math.Abs(pixel.G - expected.G) <= tolerance
            && Math.Abs(pixel.B - expected.B) <= tolerance;
    }

    /// <summary>Pixels in a square where two covers differ clearly, not just by JPEG noise.</summary>
    private static int Changed(byte[] a, byte[] b, int x0, int y0, int x1, int y1)
    {
        using var left = Image.Load<Rgb24>(a);
        using var right = Image.Load<Rgb24>(b);
        var changed = 0;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var p = left[x, y];
            var q = right[x, y];
            if (Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B))) > 40) changed++;
        }
        return changed;
    }

    [Fact]
    public void Mix_GetsItsGenresDesign()
        => Assert.True(Near(Pixel(Service().GetNamedCover("Rock Mix", "Rock"), 4, 4), "#B85851"));

    [Fact]
    public void Unmatched_GetsTheGenericDesignInAColourOfItsOwn()
        => Assert.True(Near(Pixel(Service().GetNamedCover("Polka Mix", "Polka"), 4, 4), CoverKit.Generic("Polka").From));

    [Fact]
    public void WithoutAKit_AGradientStillMakesACover()
    {
        var jpeg = Service(withKit: false).GetNamedCover("Polka Mix", "Polka");

        Assert.True(Near(Pixel(jpeg, 4, 4), CoverKit.Generic("Polka").From));
    }

    [Fact]
    public void Override_IsUsedAsItIs_AndAReplacementShowsWithoutARestart()
    {
        var service = Service();
        var path = Path.Combine(_kit.CoversDirectory, "Rock Mix.png");
        using (var green = new Image<Rgb24>(120, 60, new Rgb24(0, 255, 0))) green.SaveAsPng(path);

        var first = service.GetNamedCover("Rock Mix", "Rock");
        using (var picture = Image.Load<Rgb24>(first)) Assert.Equal(600, picture.Width);
        Assert.True(Near(Pixel(first, 300, 300), "#00FF00"));

        using (var blue = new Image<Rgb24>(60, 60, new Rgb24(0, 0, 255))) blue.SaveAsPng(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.True(Near(Pixel(service.GetNamedCover("Rock Mix", "Rock"), 300, 300), "#0000FF"));
    }

    /// <summary>Whatever a playlist is called, a cover is only ever read from the covers folder.</summary>
    [Fact]
    public void Override_NeverReachesOutsideTheCoversFolder()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_kit.CoversDirectory)!, "escape.png");
        using (var green = new Image<Rgb24>(60, 60, new Rgb24(0, 255, 0))) green.SaveAsPng(outside);

        Assert.False(Near(Pixel(Service().GetNamedCover("../escape"), 300, 300), "#00FF00"));
    }

    [Fact]
    public void MixCover_HasNoOctoBadge()
    {
        var service = Service();

        var mix = service.GetNamedCover("Rock Radio");
        var station = service.GetRadioStationCover("Rock Radio");

        // The badge sits top left, 28% of the cover plus 3% padding; the rest is the same design.
        // The logo has transparent margins, so it moves the corner's average by a few levels while
        // everything outside the corner stays identical.
        var inside = Changed(mix, station, 18, 18, 186, 186);
        var outside = Changed(mix, station, 400, 400, 580, 580);
        Assert.True(inside > 1_000, $"the station should carry the badge: {inside} pixels changed");
        Assert.True(outside == 0, $"the rest should be the same design: {outside} pixels changed");
        Assert.True(Near(Pixel(mix, 60, 60), "#B85851", 40), "the mix corner should be the plain design");
    }

    [Fact]
    public void RadioStationCovers_CarryTheOctoBadgeAcrossConcurrentFirstRequests()
    {
        var service = Service();
        var covers = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(8)
            .Select(index => (Name: $"Station {index}", Bytes: service.GetRadioStationCover($"Station {index}")))
            .ToList();

        Assert.All(covers, cover =>
        {
            var inside = Changed(service.GetNamedCover(cover.Name), cover.Bytes, 18, 18, 186, 186);
            Assert.True(inside > 1_000, $"{cover.Name} has no badge: {inside} pixels changed");
        });
    }

    /// <summary>A picture someone chose for a station is theirs, and is not stamped.</summary>
    [Fact]
    public void StationOverride_IsNotBadged()
    {
        var service = Service();
        using (var green = new Image<Rgb24>(60, 60, new Rgb24(0, 255, 0)))
            green.SaveAsPng(Path.Combine(_kit.CoversDirectory, "Rock Radio.png"));

        Assert.Equal(service.GetNamedCover("Rock Radio"), service.GetRadioStationCover("Rock Radio"));
    }
}
