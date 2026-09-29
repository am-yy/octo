using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Services.CoverArt;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Xunit.Abstractions;

namespace Octo.Tests;

/// <summary>
/// The colour rules the covers share with the Octo apps: the same maths gives the same numbers,
/// a palette comes from the music's colours, and grey music leaves the design its own colours.
/// </summary>
public class CoverColourTests
{
    private static CoverBook Book => CoverBook.Default;

    /// <summary>FNV-1a 64 over UTF-8, shifted right once, as the apps hash a list.</summary>
    [Theory]
    [InlineData("", 0xcbf29ce484222325UL >> 1)]
    [InlineData("a", 0xaf63dc4c8601ec8cUL >> 1)]
    public void CoverHash_IsFnv1aShiftedRight(string text, ulong expected) =>
        Assert.Equal((long)expected, CoverColours.CoverHash(text));

    [Fact]
    public void GradientOf_IsTheSameForANameAndSpreadsNames()
    {
        Assert.Equal(CoverLayout.GradientOf("Rock Mix", Book), CoverLayout.GradientOf("Rock Mix", Book));
        var picked = Enumerable.Range(0, 400).Select(i => CoverLayout.GradientOf($"List {i}", Book)).Distinct().Count();
        Assert.Equal(Book.Gradients.Count, picked);
    }

    [Theory]
    [InlineData("#35197f")]
    [InlineData("#ef8a2c")]
    [InlineData("#2fc39a")]
    public void Lch_RoundTripsAnSrgbColour(string hex)
    {
        var argb = CoverColours.Hex(hex);
        var lch = CoverColours.ToLch(argb);
        Assert.Equal(argb, CoverColours.FromLch(lch.L, lch.C, lch.H));
    }

    [Fact]
    public void Swatches_FindAPicturesColoursByShare()
    {
        var pixels = Enumerable.Range(0, 64 * 64).Select(i => i % 4 == 0 ? CoverColours.Hex("#1d3f8c") : CoverColours.Hex("#d9552b")).ToArray();

        var swatches = CoverColours.Swatches(pixels, step: 1);

        Assert.Equal(2, swatches.Count);
        Assert.Equal(CoverColours.Hex("#d9552b"), swatches[0].Argb);
        Assert.InRange(swatches[0].Share, 0.74f, 0.76f);
    }

    [Fact]
    public void Palette_FromColourfulMusic_TakesItsHues()
    {
        var warm = new List<IReadOnlyList<Swatch>>
        {
            new[] { new Swatch(CoverColours.Hex("#D9552B"), 0.6f), new Swatch(CoverColours.Hex("#2B1A12"), 0.3f) },
            new[] { new Swatch(CoverColours.Hex("#1D3F8C"), 0.5f) },
        };

        var palette = CoverPalette.FromCovers(warm, "x");

        Assert.True(palette.FromMusic);
        Assert.InRange(CoverColours.HueDistance(palette.Hue, CoverColours.ToLch(CoverColours.Hex("#D9552B")).H), 0, 1);
        Assert.True(CoverColours.HueDistance(palette.Hue, palette.Hue2) >= 28);
    }

    /// <summary>A neon cover gives a rich palette, never a glaring one.</summary>
    [Fact]
    public void Palette_FromNeonMusic_IsHeldToTheDesignsChroma()
    {
        var neon = new List<IReadOnlyList<Swatch>> { new[] { new Swatch(CoverColours.Hex("#00FF40"), 0.8f), new Swatch(CoverColours.Hex("#FFE000"), 0.7f) } };

        var palette = CoverPalette.FromCovers(neon, "x");

        Assert.True(palette.Chroma <= 0.15 && palette.Chroma2 <= 0.19);
        var colours = CoverLayout.GradientColours(Book.Gradients[0], palette, Book);
        Assert.All(colours, c => Assert.True(CoverColours.ToLch(c).C <= Book.Music.MaxChroma + 0.01));
    }

    [Fact]
    public void Palette_FromGreyMusic_LeavesTheDesignItsOwnColours()
    {
        var grey = new List<IReadOnlyList<Swatch>> { new[] { new Swatch(CoverColours.Hex("#808080"), 0.9f) } };

        var palette = CoverPalette.FromCovers(grey, "x");

        Assert.False(palette.FromMusic);
        var gradient = Book.Gradients[3];
        Assert.Equal(gradient.Colours.Select(CoverColours.Hex), CoverLayout.GradientColours(gradient, palette, Book).Take(3));
    }

    /// <summary>With music, the fold colour takes the music's hue and every colour keeps its lightness.</summary>
    [Fact]
    public void GradientColours_TurnTheFoldToTheMusicsHue()
    {
        var gradient = Book.Gradients[4];
        var palette = CoverPalette.Of(200, 0.12, 240, 0.16, fromMusic: true);

        var colours = CoverLayout.GradientColours(gradient, palette, Book);

        var fold = CoverColours.ToLch(colours[gradient.Folds![0].Colour]);
        Assert.InRange(CoverColours.HueDistance(fold.H, 200), 0, 2);
        for (var i = 0; i < gradient.Colours.Length; i++)
            Assert.InRange(CoverColours.ToLch(colours[i]).L - CoverColours.ToLch(CoverColours.Hex(gradient.Colours[i])).L, -0.01, 0.01);
    }

    /// <summary>
    /// Key pixels of preset 0 at 600 px worked out by hand from the design's rules (the base
    /// runs corner to corner, the fold is centred off the bottom right, the light top right), so
    /// the layers compose as the apps compose them.
    /// </summary>
    [Fact]
    public void Layers_OfPresetZero_ComposeAsTheDesignSays()
    {
        var gradient = Book.Gradients[0];
        var layers = CoverLayout.GradientLayers(gradient, CoverLayout.GradientColours(gradient, CoverPalette.Seeded("x"), Book), 600);

        // Top left: the base's first colour, beyond the fold's and the light's reach.
        Assert.Equal(CoverColours.Hex("#35197f"), CoverLayout.ColourAt(layers, 0, 0));
        // The fold's centre (672, 648) is off the cover; at (599, 599), 87.9 px away, it is solid.
        Assert.Equal(CoverColours.Hex("#d24fd0"), CoverLayout.ColourAt(layers, 599, 599));
        // The light's centre (540, 72) over the base's middle (#58249f at t=0.51): 32% of #b8a8ff.
        var base0 = CoverLayout.ColourAlong([new Stop(0, CoverColours.Hex("#35197f")), new Stop(1, CoverColours.Hex("#7b2fd6"))], (540f + 72f) / 1200f);
        var lit = CoverColours.Over(CoverColours.Alpha(CoverColours.Hex("#b8a8ff"), 0.32f), base0);
        Assert.Equal(lit, CoverLayout.ColourAt(layers, 540, 72));
    }
}

/// <summary>
/// List covers as the server serves them: the covers folder wins, a station is a plain
/// playlist cover with no badge, the words always read, long and foreign names fit, the same
/// list always gets the same bytes, and a cover is quick to draw.
/// </summary>
public class ListCoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-covers-" + Guid.NewGuid());
    private readonly ITestOutputHelper _output;

    public ListCoverTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(CoversDirectory);
    }

    private string CoversDirectory => Path.Combine(_root, "config", "covers");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private CoverArtService Service() => new(NullLogger<CoverArtService>.Instance, CoversDirectory);

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

    private static byte[] Picture(string hex, string? second = null)
    {
        using var image = new Image<Rgb24>(120, 120, Color.ParseHex(hex).ToPixel<Rgb24>());
        if (second is not null)
            image.Mutate(ctx => ctx.Fill(Color.ParseHex(second), new SixLabors.ImageSharp.Drawing.RectangularPolygon(0, 80, 120, 40)));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static Func<CancellationToken, Task<IReadOnlyList<CoverSeed>>> Seeds(params byte[][] pictures) =>
        _ => Task.FromResult<IReadOnlyList<CoverSeed>>(pictures
            .Select((bytes, i) => new CoverSeed($"seed{i}-{bytes.Length}-{bytes[^5]}", _ => Task.FromResult<byte[]?>(bytes)))
            .ToList());

    // ------------------------------------------------------------ covers folder

    [Fact]
    public void Override_IsUsedAsItIs_AndAReplacementShowsWithoutARestart()
    {
        var service = Service();
        var path = Path.Combine(CoversDirectory, "Rock Mix.png");
        using (var green = new Image<Rgb24>(120, 60, new Rgb24(0, 255, 0))) green.SaveAsPng(path);

        var first = service.GetNamedCover("Rock Mix", "Rock");
        using (var picture = Image.Load<Rgb24>(first)) Assert.Equal(600, picture.Width);
        Assert.True(Near(Pixel(first, 300, 300), "#00FF00"));

        using (var blue = new Image<Rgb24>(60, 60, new Rgb24(0, 0, 255))) blue.SaveAsPng(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.True(Near(Pixel(service.GetNamedCover("Rock Mix", "Rock"), 300, 300), "#0000FF"));
    }

    /// <summary>A picture in the covers folder beats colours from the music too.</summary>
    [Fact]
    public async Task Override_BeatsSeedColours_AndIsSizedAsAsked()
    {
        using (var green = new Image<Rgb24>(60, 60, new Rgb24(0, 255, 0)))
            green.SaveAsPng(Path.Combine(CoversDirectory, "Daft Punk Radio.png"));

        var bytes = await Service().GetListCoverAsync(
            new ListCover("Daft Punk Radio", null, ListKinds.Radio, Seeds(Picture("#C08020"))), 900);

        using var image = Image.Load<Rgb24>(bytes);
        Assert.Equal(900, image.Width);
        Assert.True(Near(image[450, 450], "#00FF00"));
    }

    [Fact]
    public void Override_ByGenre_CoversEveryListOfIt()
    {
        using (var green = new Image<Rgb24>(60, 60, new Rgb24(0, 255, 0)))
            green.SaveAsPng(Path.Combine(CoversDirectory, "Rock.png"));

        Assert.True(Near(Pixel(Service().GetNamedCover("Rock Mix", "Rock"), 300, 300), "#00FF00"));
    }

    /// <summary>Whatever a playlist is called, a cover is only ever read from the covers folder.</summary>
    [Fact]
    public void Override_NeverReachesOutsideTheCoversFolder()
    {
        var outside = Path.Combine(Path.GetDirectoryName(CoversDirectory)!, "escape.png");
        using (var green = new Image<Rgb24>(60, 60, new Rgb24(0, 255, 0))) green.SaveAsPng(outside);

        Assert.False(Near(Pixel(Service().GetNamedCover("../escape"), 300, 300), "#00FF00"));
    }

    // ------------------------------------------------------------ no badge

    /// <summary>
    /// A station is a playlist like a mix: its cover is exactly the design, with nothing added.
    /// The old badge sat top left at 28% of the cover; drawn on, it would show there.
    /// </summary>
    [Fact]
    public void StationCover_IsTheDesignAlone_WithNoBadge()
    {
        var service = Service();
        var station = service.GetRadioStationCover("Rock Radio");
        var design = service.Render(CoverArtService.Spec("Rock Radio", ListKinds.Radio, null,
            service.FallbackPalette("Rock Radio", "Rock Radio")), 600);

        Assert.Equal(design, station);
        var badged = service.AddOctoBadge(station);
        Assert.True(Changed(station, badged, 18, 18, 186, 186) > 1_000, "the badge should be visible when applied");
    }

    [Fact]
    public void RadioStationCovers_StayPlainAcrossConcurrentFirstRequests()
    {
        var service = Service();
        var covers = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(8)
            .Select(index => (Name: $"Station {index} Radio", Bytes: service.GetRadioStationCover($"Station {index} Radio")))
            .ToList();

        var fresh = Service();
        Assert.All(covers, cover => Assert.Equal(fresh.GetRadioStationCover(cover.Name), cover.Bytes));
    }

    /// <summary>A picture someone chose for a station is theirs, and is not stamped.</summary>
    [Fact]
    public void StationOverride_IsNotBadged()
    {
        var service = Service();
        using (var green = new Image<Rgb24>(60, 60, new Rgb24(0, 255, 0)))
            green.SaveAsPng(Path.Combine(CoversDirectory, "Rock Radio.png"));

        Assert.Equal(service.GetNamedCover("Rock Radio", null, null, ListKinds.Radio), service.GetRadioStationCover("Rock Radio"));
        Assert.True(Near(Pixel(service.GetRadioStationCover("Rock Radio"), 40, 40), "#00FF00", 8));
    }

    // ------------------------------------------------------------ palette sources

    [Fact]
    public async Task SeedCovers_ColourTheCover_AndANewSeedRedrawsIt()
    {
        var service = Service();
        var plain = await service.GetListCoverAsync(new ListCover("Daft Punk Radio", null, ListKinds.Radio));
        var gold = await service.GetListCoverAsync(new ListCover("Daft Punk Radio", null, ListKinds.Radio, Seeds(Picture("#C08020", "#101010"))));
        var goldAgain = await service.GetListCoverAsync(new ListCover("Daft Punk Radio", null, ListKinds.Radio, Seeds(Picture("#C08020", "#101010"))));
        var teal = await service.GetListCoverAsync(new ListCover("Daft Punk Radio", null, ListKinds.Radio, Seeds(Picture("#1C8C8C", "#101010"))));

        Assert.Equal(gold, goldAgain);
        Assert.True(Changed(plain, gold, 0, 0, 600, 600) > 10_000, "seed colours should change the cover");
        Assert.True(Changed(gold, teal, 0, 0, 600, 600) > 10_000, "a new seed cover should redraw it");
    }

    /// <summary>Grey seeds, or seeds that cannot be fetched, leave the genre's colour or the design's own.</summary>
    [Fact]
    public async Task GreyOrMissingSeeds_FallBack()
    {
        var service = Service();
        var bare = await service.GetListCoverAsync(new ListCover("Rock Mix", "Rock"));
        var grey = await service.GetListCoverAsync(new ListCover("Rock Mix", "Rock", ListKinds.Mix, Seeds(Picture("#808080"))));
        var failing = await service.GetListCoverAsync(new ListCover("Rock Mix", "Rock", ListKinds.Mix,
            _ => Task.FromResult<IReadOnlyList<CoverSeed>>([new CoverSeed("gone", _ => throw new HttpRequestException("down"))])));

        Assert.Equal(bare, grey);
        Assert.Equal(bare, failing);
    }

    [Fact]
    public void GenreAndDecade_GiveTheirHue_WhenTheSongsGiveNone()
    {
        var service = Service();
        var rock = service.FallbackPalette("Rock Mix", "Rock");
        var decade = service.FallbackPalette("1990s Mix", "1990s");
        var polka = service.FallbackPalette("Polka Mix", "Polka");

        Assert.True(rock.FromMusic);
        Assert.Equal(26, rock.Hue);
        Assert.Equal(134, decade.Hue);
        Assert.False(polka.FromMusic);
        Assert.Equal(CoverBook.Default.ListHue("Soul Radio"), CoverBook.Default.ListHue("R&B & Soul"));
    }

    // ------------------------------------------------------------ words

    [Theory]
    [InlineData("Daft Punk Radio", ListKinds.Radio, "Daft Punk", "Station")]
    [InlineData("Rock Radio", ListKinds.Radio, "Rock", "Station")]
    [InlineData("Late Night Jazz", ListKinds.Radio, "Late Night Jazz", "Station")]
    [InlineData("Rock Mix", ListKinds.Mix, "Rock", "Mix")]
    [InlineData("1990s Mix", ListKinds.Mix, "1990s", "Mix")]
    [InlineData("Late Night Jazz", ListKinds.Mix, "Late Night Jazz", "Mix")]
    public void Spec_NamesTheListAndSaysWhatItIs(string name, string kind, string title, string line)
    {
        var spec = CoverArtService.Spec(name, kind, 50, CoverPalette.Seeded(name));

        Assert.Equal(title, spec.Name);
        Assert.Equal(line, spec.Line);
        Assert.Equal("50 songs", spec.Footer);
        Assert.Equal(name, spec.Id);
    }

    /// <summary>A name that already ends in what it is gets no second line saying it again.</summary>
    [Theory]
    [InlineData("Your Mix", ListKinds.Radio)]
    [InlineData("Discovery Mix", ListKinds.Radio)]
    [InlineData("Your Mix", ListKinds.Mix)]
    [InlineData("Late Night Radio Station", ListKinds.Radio)]
    [InlineData("Road Trip Playlist", ListKinds.Mix)]
    [InlineData("Summer mixes", ListKinds.Mix)]
    [InlineData("Pirate Radios", ListKinds.Radio)]
    [InlineData("Other Stations", ListKinds.Radio)]
    [InlineData("Old Playlists", ListKinds.Mix)]
    public void Spec_NameThatSaysWhatItIs_HasNoSecondLine(string name, string kind)
    {
        var spec = CoverArtService.Spec(name, kind, 50, CoverPalette.Seeded(name));

        Assert.Equal(name, spec.Name);
        Assert.Null(spec.Line);
        var art = new CoverArtService(NullLogger<CoverArtService>.Instance).Compose(spec, 600);
        Assert.Equal(2, art.Words.Count);
        Assert.DoesNotContain(art.Words, w => w.Text is "Station" or "Mix");
    }

    /// <summary>Only the last word counts, and only a whole word.</summary>
    [Theory]
    [InlineData("Mixtape Classics", false)]
    [InlineData("Radiohead", false)]
    [InlineData("Mix Masters", false)]
    [InlineData("Your Mix", true)]
    [InlineData("Discovery MIX", true)]
    public void SaysWhatItIs_ReadsTheLastWholeWord(string name, bool expected) =>
        Assert.Equal(expected, CoverArtService.SaysWhatItIs(name));

    public static TheoryData<string> Names => new()
    {
        "Rock",
        "Red Hot Chili Peppers",
        "The Most Unreasonably Long Playlist Name Anyone Ever Typed Into A Music Server",
        "Supercalifragilisticexpialidociousness",
        "宇多田ヒカル",
        "블랙핑크 BLACKPINK",
        "فيروز",
        "שירים ישנים",
        "Late Night 🌙 Chill",
        "Ünïcödé Café",
    };

    /// <summary>Every word fits inside the margins, below the top, above the foot line.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Words_FitInsideTheMargins(string name)
    {
        var service = Service();
        foreach (var side in new[] { 600, 1200 })
        {
            var art = service.Compose(CoverArtService.Spec(name + " Radio", ListKinds.Radio, 120, CoverPalette.Seeded(name)), side);
            var margin = MathF.Round(side * CoverBook.Default.Layout.Margin);
            Assert.Equal(3, art.Words.Count);
            foreach (var words in art.Words)
            {
                var box = words.Inked;
                Assert.True(box[0] >= margin - 0.5f && box[2] <= side - margin + 0.5f, $"{name} at {side}: {words.Text} runs {box[0]}..{box[2]}");
                Assert.True(box[1] >= 0 && box[3] <= side, $"{name} at {side}: {words.Text} rows {box[1]}..{box[3]}");
                Assert.True(words.Measured.Lines <= CoverBook.Default.Layout.Title.MaxLines);
            }
            // The foot line comes first in the list; the name, then the line under it, then the foot line, top to bottom.
            Assert.True(art.Words[2].Top >= art.Words[1].Inked[3] - 0.5f && art.Words[0].Top >= art.Words[2].Inked[3]);
        }
    }

    [Fact]
    public void LongNames_WrapOntoThreeLinesAtMost_AndTheLongestIsCut()
    {
        var service = Service();
        var wraps = service.Compose(CoverArtService.Spec("Red Hot Chili Peppers And Friends Radio", ListKinds.Radio, null, CoverPalette.Seeded("x")), 600).Words[0];
        var cut = service.Compose(CoverArtService.Spec(string.Join(" ", Enumerable.Repeat("Unreasonably", 12)), ListKinds.Mix, null, CoverPalette.Seeded("x")), 600).Words[0];

        Assert.InRange(wraps.Measured.Lines, 2, 3);
        Assert.False(wraps.Measured.Cut);
        Assert.Equal(3, cut.Measured.Lines);
        Assert.True(cut.Measured.Cut);
    }

    [Fact]
    public void RightToLeftNames_AreSetFromTheRight()
    {
        var words = Service().Compose(CoverArtService.Spec("فيروز Radio", ListKinds.Radio, null, CoverPalette.Seeded("x")), 600).Words;

        Assert.All(words, w => Assert.Equal(CoverAlign.Right, w.Align));
        Assert.True(words[0].Inked[2] > 500);
    }

    /// <summary>
    /// Chinese, Japanese, Korean, Arabic, Hebrew and emoji names draw real letters, from a font
    /// that has them: the name's box holds plenty of white, and each letter differs from the
    /// empty box a missing glyph would leave.
    /// </summary>
    [Theory]
    [InlineData("宇多田ヒカル")]
    [InlineData("블랙핑크")]
    [InlineData("فيروز")]
    [InlineData("שירים")]
    [InlineData("🌙🎧")]
    public void UnicodeNames_DrawTheirLetters(string name)
    {
        var fonts = CoverFonts.Fallbacks.Count;
        if (fonts == 0) return;
        var (font, _) = CoverFonts.For(name, 600, 96);
        var hasLetters = name.EnumerateRunes().Any(System.Text.Rune.IsLetter);
        if (hasLetters) Assert.False(font.Family.Equals(CoverFonts.ForWeight(600)), $"{name} is set in Inter");
        foreach (var rune in name.EnumerateRunes())
        {
            var cp = new SixLabors.Fonts.Unicode.CodePoint(rune.Value);
            var found = CoverFonts.Has(font, cp) || CoverFonts.Fallbacks.Any(f => CoverFonts.Has(f.CreateFont(96), cp));
            Assert.True(found, $"no installed font draws U+{rune.Value:X4}");
        }

        var service = Service();
        var spec = CoverArtService.Spec(name, ListKinds.Mix, null, CoverPalette.Seeded(name));
        using var image = service.Paint(spec, 600);
        var box = service.Compose(spec, 600).Words[0].Inked;
        var white = 0;
        for (var y = (int)box[1]; y < (int)box[3]; y++)
        for (var x = (int)box[0]; x < (int)box[2]; x++)
            if (image[x, y] is { R: > 235, G: > 235, B: > 235 }) white++;
        Assert.True(white > 1500, $"{name}: only {white} white pixels in the name");
    }

    // ------------------------------------------------------------ contrast

    public static TheoryData<string, string?> ContrastCases()
    {
        var data = new TheoryData<string, string?>();
        // Two names per design, from each kind of music, and none.
        var pictures = new string?[] { null, "#FFFFFF", "#F4EEDC", "#00FF40", "#FFE000", "#D9552B", "#1D3F8C", "#808080" };
        var book = CoverBook.Default;
        var seen = new Dictionary<int, int>();
        for (var i = 0; seen.Values.Sum() < book.Gradients.Count * 2 && i < 5000; i++)
        {
            var name = $"List {i} Radio";
            var g = CoverLayout.GradientOf(name, book);
            if (seen.GetValueOrDefault(g) >= 2) continue;
            seen[g] = seen.GetValueOrDefault(g) + 1;
            data.Add(name, pictures[i % pictures.Length]);
        }
        return data;
    }

    /// <summary>
    /// Every pixel behind every word, as painted, reaches 4.5:1 against the word's own white at
    /// its own opacity: the design's grid decides the darkening, and no pixel between its points
    /// falls short.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContrastCases))]
    public void Words_ReachTheirContrast_OnEveryPixelBehindThem(string name, string? picture)
    {
        var service = Service();
        var palette = picture is null
            ? CoverPalette.Seeded(name)
            : CoverPalette.FromCovers([CoverArtService.SwatchesOf(Picture(picture))!], name);
        var spec = CoverArtService.Spec(name, ListKinds.Radio, 1234, palette);
        foreach (var side in new[] { 600, 1200 })
        {
            var art = service.Compose(spec, side);
            using var backdrop = CoverPainter.Paint(art, new CoverTypesetter(), drawWords: false);
            foreach (var words in art.Words)
            {
                var box = words.Inked;
                var worst = double.MaxValue;
                for (var y = Math.Max(0, (int)box[1]); y < Math.Min(side, (int)MathF.Ceiling(box[3])); y++)
                for (var x = Math.Max(0, (int)box[0]); x < Math.Min(side, (int)MathF.Ceiling(box[2])); x++)
                {
                    var p = backdrop[x, y];
                    var under = unchecked((int)0xFF000000) | (p.R << 16) | (p.G << 8) | p.B;
                    worst = Math.Min(worst, CoverColours.ContrastRatio(CoverColours.Over(words.Ink, under), under));
                }
                Assert.True(worst >= 4.5, $"{name} ({picture}) at {side}: '{words.Text}' reaches only {worst:F2}");
            }
        }
    }

    /// <summary>After JPEG, which moves a level or two, the words still clear 4.4:1 everywhere behind them.</summary>
    [Fact]
    public void Words_StillReadAfterJpeg()
    {
        var service = Service();
        foreach (var (name, picture) in new[] { ("List 3 Radio", "#FFFFFF"), ("Daft Punk Radio", "#FFE000"), ("Rock Mix", "#00FF40") })
        {
            var spec = CoverArtService.Spec(name, ListKinds.Radio, 99, CoverPalette.FromCovers([CoverArtService.SwatchesOf(Picture(picture))!], name));
            var art = service.Compose(spec, 600);
            using var decoded = Image.Load<Rgb24>(service.Render(spec, 600, drawWords: false));
            foreach (var words in art.Words)
            {
                var box = words.Inked;
                for (var y = (int)box[1]; y < (int)box[3]; y++)
                for (var x = (int)box[0]; x < (int)box[2]; x++)
                {
                    var p = decoded[x, y];
                    var under = unchecked((int)0xFF000000) | (p.R << 16) | (p.G << 8) | p.B;
                    Assert.True(CoverColours.ContrastRatio(CoverColours.Over(words.Ink, under), under) >= 4.4, $"{name} at {x},{y}");
                }
            }
        }
    }

    // ------------------------------------------------------------ determinism and speed

    [Fact]
    public async Task SameList_SameBytes_AcrossInstancesAndSizes()
    {
        var seeds = Seeds(Picture("#1D3F8C", "#D9552B"));
        var a = await Service().GetListCoverAsync(new ListCover("Tame Impala Radio", null, ListKinds.Radio, seeds, 50), 800);
        var b = await Service().GetListCoverAsync(new ListCover("Tame Impala Radio", null, ListKinds.Radio, seeds, 50), 800);

        Assert.Equal(a, b);
        using var image = Image.Load<Rgb24>(a);
        Assert.Equal(800, image.Width);
        Assert.Equal(600, Image.Identify(Service().GetNamedCover("x", null, 64)).Width);
        Assert.Equal(1200, Image.Identify(Service().GetNamedCover("x", null, 5000)).Width);
    }

    // ------------------------------------------------------------ contact sheet

    /// <summary>
    /// A sheet of covers for looking at the design, only when asked:
    /// OCTO_SHOTS_DIR=&lt;folder&gt; (and OCTO_SHOTS_SEEDS=&lt;folder of album covers&gt;) dotnet test --filter ContactSheet
    /// </summary>
    [Fact]
    public async Task ContactSheet()
    {
        var output = Environment.GetEnvironmentVariable("OCTO_SHOTS_DIR");
        if (string.IsNullOrEmpty(output)) return;
        var seedDir = Environment.GetEnvironmentVariable("OCTO_SHOTS_SEEDS") ?? "";
        byte[]? Seed(string stem)
        {
            var path = Path.Combine(seedDir, stem + ".jpg");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        var lists = new (string Name, string? Label, string Kind, string[] Seeds, int? Songs)[]
        {
            ("Daft Punk Radio", null, ListKinds.Radio, ["Daft_Punk_Random_Access_Memories"], 50),
            ("Billie Eilish Radio", null, ListKinds.Radio, ["Billie_Eilish_Happier_Than_Ever"], 50),
            ("Tame Impala Radio", null, ListKinds.Radio, ["Tame_Impala_Currents"], 50),
            ("Radiohead Radio", null, ListKinds.Radio, ["Radiohead_In_Rainbows"], 50),
            ("Kendrick Lamar Radio", null, ListKinds.Radio, ["Kendrick_Lamar_DAMN"], 50),
            ("Your Mix", null, ListKinds.Radio, ["Taylor_Swift_1989", "Frank_Ocean_Blonde"], 50),
            ("Discovery Mix", null, ListKinds.Radio, ["Arctic_Monkeys_AM", "Bad_Bunny_Un_Verano_Sin_Ti"], 50),
            ("Bad Bunny Radio", null, ListKinds.Radio, ["Bad_Bunny_Un_Verano_Sin_Ti"], 50),
            ("Jazz & Blues Mix", "Jazz & Blues", ListKinds.Mix, ["Miles_Davis_Kind_of_Blue"], 100),
            ("Metal Mix", "Metal", ListKinds.Mix, ["Metallica_Master_of_Puppets"], 100),
            ("1970s Mix", "1970s", ListKinds.Mix, ["Fleetwood_Mac_Rumours"], 100),
            ("Rock Mix", "Rock", ListKinds.Mix, [], null),
            ("Hip-Hop Mix", "Hip-Hop", ListKinds.Mix, [], 100),
            ("1990s Mix", "1990s", ListKinds.Mix, [], 100),
            ("2020s Mix", "2020s", ListKinds.Mix, [], 100),
            ("Electronic Radio", "electronic", ListKinds.Radio, [], 50),
            ("Polka Mix", "Polka", ListKinds.Mix, [], 37),
            ("Red Hot Chili Peppers Radio", null, ListKinds.Radio, [], 50),
            ("The Most Unreasonably Long Playlist Name Anyone Ever Typed Into A Music Server Radio", null, ListKinds.Radio, [], 50),
            ("宇多田ヒカル Radio", null, ListKinds.Radio, ["Hikaru_Utada_Fantome"], 50),
            ("블랙핑크 BLACKPINK Radio", null, ListKinds.Radio, ["BLACKPINK_The_Album"], 50),
            ("فيروز Radio", null, ListKinds.Radio, ["Fairuz"], 50),
            ("Late Night 🌙 Chill Mix", "Lo-fi & Chill", ListKinds.Mix, [], 100),
            ("Ünïcödé Café Mix", null, ListKinds.Mix, [], 1),
        };
        var service = Service();
        const int side = 600, gap = 40, columns = 6;
        var rows = (lists.Length + columns - 1) / columns;
        using var sheet = new Image<Rgba32>(columns * (side + gap) + gap, rows * (side + gap) + gap, new Rgba32(12, 12, 13));
        for (var i = 0; i < lists.Length; i++)
        {
            var list = lists[i];
            var pictures = list.Seeds.Select(Seed).Where(b => b is not null).Cast<byte[]>().ToArray();
            var watch = Stopwatch.StartNew();
            var bytes = await service.GetListCoverAsync(new ListCover(list.Name, list.Label, list.Kind,
                pictures.Length > 0 ? Seeds(pictures) : null, list.Songs), side);
            _output.WriteLine($"{list.Name}: {watch.Elapsed.TotalMilliseconds:F0} ms");
            using var cover = Image.Load<Rgba32>(bytes);
            var (x, y) = (gap + i % columns * (side + gap), gap + i / columns * (side + gap));
            sheet.Mutate(ctx => ctx.DrawImage(cover, new Point(x, y), 1f));
        }
        Directory.CreateDirectory(output);
        await sheet.SaveAsPngAsync(Path.Combine(output, "list-covers.png"));
    }
}

/// <summary>Timing runs alone, after the other tests, so it measures the cover and not the suite.</summary>
[CollectionDefinition(nameof(CoverTiming), DisableParallelization = true)]
public class CoverTiming;

[Collection(nameof(CoverTiming))]
public class CoverTimingTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// Drawing a cover, words, contrast and JPEG included. The fastest of several runs is the
    /// cover's own cost; the middle one also carries whatever else the machine is doing.
    /// </summary>
    [Fact]
    public void ACover_DrawsWellUnder100ms()
    {
        var service = new CoverArtService(NullLogger<CoverArtService>.Instance);
        var spec = CoverArtService.Spec("Red Hot Chili Peppers Radio", ListKinds.Radio, 100, CoverPalette.Seeded("t"));
        service.Render(spec, 600);
        service.Render(spec, 1200);

        (double Fastest, double Middle) Time(int side)
        {
            var times = new List<double>();
            for (var i = 0; i < 9; i++)
            {
                var watch = Stopwatch.StartNew();
                service.Render(spec with { Palette = CoverPalette.Seeded($"t{i}") }, side);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            return (times[0], times[times.Count / 2]);
        }

        var small = Time(600);
        var large = Time(1200);
        _output.WriteLine($"600 px: fastest {small.Fastest:F1} ms, middle {small.Middle:F1} ms; 1200 px: fastest {large.Fastest:F1} ms, middle {large.Middle:F1} ms");
        Assert.True(small.Fastest < 100, $"600 px took {small.Fastest:F1} ms");
        Assert.True(large.Fastest < 250, $"1200 px took {large.Fastest:F1} ms");
    }

}
