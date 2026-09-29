using System.Collections.Concurrent;
using System.Globalization;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Octo.Services.CoverArt;

/// <summary>
/// Composites the Octo logo onto cover art so radio-sourced tracks are visually
/// distinguishable from local-library tracks in the Subsonic client UI. The
/// previous Tidal-era version drew a procedural diamond; this version loads a
/// real PNG asset shipped in the project's Assets/ directory.
///
/// Logo placement: bottom-right, ~15% of cover dimension, with a soft dark
/// circle behind it so it stays legible on any background. If the asset is
/// missing the badge call returns the original bytes unchanged — never fatal.
/// </summary>
public class CoverArtService
{
    private readonly ILogger<CoverArtService> _logger;
    private Image? _octoLogo;
    private readonly object _logoLock = new();
    private volatile bool _logoLoadAttempted;
    private readonly ConcurrentDictionary<string, NamedCover> _namedCovers = new(StringComparer.Ordinal);
    private readonly string? _coversDirectory;
    private readonly string _kitDirectory;
    private CoverKit? _kit;

    private const int CoverSize = 600;

    /// <summary>Where a named cover came from: someone's own picture in the covers folder, a
    /// drawn design, or the last-resort render.</summary>
    private enum CoverSource { Override, Drawn, Legacy }

    private sealed record NamedCover(byte[] Bytes, CoverSource Source);

    /// <param name="coversDirectory">Pictures that replace a generated cover, named after the
    /// playlist (/app/config/covers).</param>
    /// <param name="kitDirectory">The cover kit; Assets/cover-kit beside the app by default.</param>
    public CoverArtService(ILogger<CoverArtService> logger, string? coversDirectory = null, string? kitDirectory = null)
    {
        _logger = logger;
        _coversDirectory = string.IsNullOrWhiteSpace(coversDirectory) ? null : coversDirectory;
        _kitDirectory = kitDirectory ?? KitDirectoryIn(AppContext.BaseDirectory);
    }

    /// <summary>
    /// Where the build put the cover kit. A publish ships Assets only under wwwroot (the Content
    /// link wins over the None copy), a plain build ships both, so look in both, as the logo does.
    /// </summary>
    internal static string KitDirectoryIn(string baseDirectory)
    {
        var beside = System.IO.Path.Combine(baseDirectory, "Assets", "cover-kit");
        var served = System.IO.Path.Combine(baseDirectory, "wwwroot", "Assets", "cover-kit");
        return Directory.Exists(beside) || !Directory.Exists(served) ? beside : served;
    }

    private CoverKit Kit => LazyInitializer.EnsureInitialized(ref _kit, () => new CoverKit(_kitDirectory, _logger));

    private Image? GetOctoLogo()
    {
        if (_logoLoadAttempted) return _octoLogo;
        lock (_logoLock)
        {
            if (_logoLoadAttempted) return _octoLogo;
            // The logo can land in either Assets/ or wwwroot/Assets/ in the
            // publish output depending on how the csproj globs play out
            // (sometimes the wwwroot/<Content Link=> entry overrides the
            // Assets/<None> entry and only one copy actually ships). Try both
            // so adding the logo isn't tied to which MSBuild quirk wins this
            // build. AppContext.BaseDirectory is the publish root at runtime.
            string[] candidates = new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "octo_logo.png"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "Assets", "octo_logo.png"),
            };
            string? loadedFrom = null;
            foreach (var path in candidates)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        _octoLogo = Image.Load<Rgba32>(path);
                        loadedFrom = path;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load Octo logo from {Path}", path);
                }
            }
            if (loadedFrom != null && _octoLogo != null)
            {
                _logger.LogInformation("Octo logo loaded from {Path} ({W}x{H})",
                    loadedFrom, _octoLogo.Width, _octoLogo.Height);
            }
            else
            {
                _logger.LogWarning("Octo logo not found at any of: {Paths}; radio cover badges disabled",
                    string.Join(", ", candidates));
            }

            // Publish completion only after the image reference is ready. The
            // volatile write prevents concurrent first requests from observing
            // "attempted" while _octoLogo is still temporarily null.
            _logoLoadAttempted = true;
            return _octoLogo;
        }
    }

    private Image? CloneOctoLogo(int width, int height)
    {
        var logo = GetOctoLogo();
        if (logo is null) return null;
        // ImageSharp does not promise concurrent processing operations on the
        // same Image instance. Keep only the clone/resize inside this lock;
        // each caller renders its independent clone concurrently afterward.
        lock (_logoLock)
            return logo.Clone(ctx => ctx.Resize(width, height));
    }

    /// <summary>
    /// Composites the Octo logo onto the bottom-right of an existing cover art image.
    /// Returns the modified bytes as JPEG, or the original bytes unchanged if the
    /// logo is missing or the source image fails to decode.
    /// </summary>
    public byte[] AddOctoBadge(byte[] originalArt)
    {
        try
        {
            using var image = Image.Load<Rgba32>(originalArt);

            var imageSize = Math.Min(image.Width, image.Height);
            // Logo footprint as a fraction of the cover. 28% reads clearly even
            // at the 100-150px thumbnails most clients use for queue rows.
            var badgeSize = (int)(imageSize * 0.28);
            var padding   = (int)(imageSize * 0.03);

            using var badge = CloneOctoLogo(badgeSize, badgeSize);
            if (badge is null) return originalArt;

            // Top-left placement: most album covers concentrate visual content
            // and text along the center/bottom (artist name, track titles,
            // overlay UI from clients), so top-left is consistently the
            // "quietest" region. Also matches Western reading-order so it's the
            // first thing the eye picks up — exactly what a source indicator
            // wants.
            var badgeX = padding;
            var badgeY = padding;

            image.Mutate(ctx => ctx.DrawImage(badge, new Point(badgeX, badgeY), 1f));

            using var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder { Quality = 90 });
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to composite Octo badge onto cover art");
            return originalArt;
        }
    }

    /// <summary>
    /// Returns a 600x600 placeholder JPEG with the Octo logo centered on a black
    /// background. Used when iTunes lookup whiffs so we never 404 a cover-art
    /// request — Subsonic clients drop entries whose cover fetch fails.
    /// </summary>
    /// <param name="branded">
    /// Whether to stamp the Octo logo. True for external tracks, where the badge
    /// says where the track came from. **False for anything in the user's own
    /// library**: a local file that simply has no embedded art, or whose art could
    /// not be read in time, is not Octo's, and branding it reads as Octo claiming
    /// a song the user already owned.
    /// </param>
    public byte[] GetPlaceholderCover(bool branded = true)
    {
        const int Size = 600;

        try
        {
            using var image = new Image<Rgba32>(Size, Size, new Rgba32(0, 0, 0, 255));

            if (branded)
            {
                var logoSize = (int)(Size * 0.55);
                using var sized = CloneOctoLogo(logoSize, logoSize);
                if (sized is not null)
                {
                    var x = (Size - logoSize) / 2;
                    var y = (Size - logoSize) / 2;
                    image.Mutate(ctx => ctx.DrawImage(sized, new Point(x, y), 1f));
                }
            }

            using var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder { Quality = 85 });
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render Octo placeholder cover");
            // Last-ditch: return a tiny solid-black JPEG so we still respond 200.
            using var fallback = new Image<Rgba32>(64, 64, new Rgba32(0, 0, 0, 255));
            using var ms = new MemoryStream();
            fallback.Save(ms, new JpegEncoder { Quality = 70 });
            return ms.ToArray();
        }
    }

    /// <summary>
    /// A radio station's cover: its named cover, with no Octo mark. It's a playlist like any
    /// other, so its cover is just its own design (or the picture someone put in the covers
    /// folder).
    /// </summary>
    public byte[] GetRadioStationCover(string stationName)
    {
        var name = string.IsNullOrWhiteSpace(stationName) ? "Octo Radio" : stationName.Trim();
        return Named(name, null).Cover.Bytes;
    }

    /// <summary>
    /// A cover for something Octo names, such as a mix (#54), with no Octo mark: the logo says
    /// where a result came from, and a mix is the listener's own library.
    ///
    /// In order: a picture in the covers folder named after it; the cover kit's design for
    /// <paramref name="kitName"/> (the genre or decade, or the name itself); the kit's generic
    /// design in a colour of this name's own; a plain gradient with the name; and last a plain
    /// placeholder, never the logo. Replacing a picture in the covers folder shows without a restart.
    /// </summary>
    public byte[] GetNamedCover(string name, string? kitName = null) => Named(name, kitName).Cover.Bytes;

    private (NamedCover Cover, string Key) Named(string name, string? kitName)
    {
        var display = string.IsNullOrWhiteSpace(name) ? "Octo" : name.Trim();
        var lookup = string.IsNullOrWhiteSpace(kitName) ? display : kitName.Trim();
        var custom = FindOverride(display, lookup);
        var key = $"{display}\n{lookup}\n{(custom is null ? 0 : File.GetLastWriteTimeUtc(custom).Ticks)}";
        if (_namedCovers.Count >= 256) _namedCovers.Clear();
        var cover = _namedCovers.GetOrAdd(key, _ => RenderNamed(display, lookup, custom));
        // Nothing could be drawn: a plain placeholder, never the logo. A playlist's cover is its
        // own, whether Octo made the list or the listener did.
        return (cover, key);
    }

    private NamedCover RenderNamed(string display, string lookup, string? custom)
    {
        try
        {
            if (custom is not null && LoadOverride(custom) is { } picture) return new(picture, CoverSource.Override);
            var kit = Kit;
            if (kit.TryResolve(lookup, out var entry) && RenderTemplate(kit, entry) is { } designed)
                return new(designed, CoverSource.Drawn);
            if (kit.GenericEntry(lookup) is { } generic && RenderTemplate(kit, generic) is { } genericCover)
                return new(genericCover, CoverSource.Drawn);
            if (RenderGradientName(display, CoverKit.Generic(lookup)) is { } plain)
                return new(plain, CoverSource.Drawn);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not draw a cover for {Name}", display);
        }
        return new(GetPlaceholderCover(branded: false), CoverSource.Legacy);
    }

    private byte[]? RenderTemplate(CoverKit kit, CoverKit.Entry entry)
    {
        var svg = File.ReadAllText(entry.TemplatePath)
            .Replace("__BG_FROM__", entry.From)
            .Replace("__BG_TO__", entry.To)
            .Replace("__STROKE__", kit.Stroke)
            .Replace("__STROKE_WIDTH__", kit.StrokeWidth.ToString("0.00", CultureInfo.InvariantCulture));
        return SvgTemplateRenderer.Render(svg, CoverSize, _logger);
    }

    /// <summary>
    /// A picture in the covers folder named after the playlist, the genre or decade, or the kit's
    /// file name for it. Only ever inside that folder, whatever the name contains.
    /// </summary>
    private string? FindOverride(string display, string lookup)
    {
        if (_coversDirectory is null || !Directory.Exists(_coversDirectory)) return null;
        var root = System.IO.Path.GetFullPath(_coversDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        foreach (var stem in new[] { SafeName(display), SafeName(lookup), SafeName(CoverKit.Slug(lookup)) }.Distinct())
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp" })
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(_coversDirectory, stem + extension));
            if (path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path)) return path;
        }
        return null;
    }

    private static string SafeName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(['/', '\\']).ToHashSet();
        return new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
    }

    /// <summary>Someone's own picture, cropped to its centre square and sized like every cover.</summary>
    private byte[]? LoadOverride(string path)
    {
        try
        {
            using var image = Image.Load<Rgba32>(path);
            var side = Math.Min(image.Width, image.Height);
            image.Mutate(ctx => ctx
                .Crop(new Rectangle((image.Width - side) / 2, (image.Height - side) / 2, side, side))
                .Resize(CoverSize, CoverSize));
            using var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder { Quality = 90 });
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The cover {Path} could not be read: {M}", path, ex.Message);
            return null;
        }
    }

    /// <summary>The kit's colours and the name in white, for when no template can be drawn.</summary>
    private byte[]? RenderGradientName(string name, (string From, string To) colours)
    {
        if (!Color.TryParse(colours.From, out var from) || !Color.TryParse(colours.To, out var to)) return null;
        using var image = new Image<Rgba32>(CoverSize, CoverSize);
        image.Mutate(ctx => ctx.Fill(new LinearGradientBrush(new PointF(0, 0), new PointF(CoverSize, CoverSize),
            GradientRepetitionMode.None, new ColorStop(0, from), new ColorStop(1, to))));

        if (CoverFonts.Family() is { } family)
        {
            RichTextOptions options;
            var size = 72f;
            FontRectangle measured;
            do
            {
                options = new RichTextOptions(family.CreateFont(size, FontStyle.Bold))
                {
                    Origin = new PointF(CoverSize / 2f, CoverSize / 2f),
                    WrappingLength = 520,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                };
                measured = TextMeasurer.MeasureSize(name, options);
                size -= 4f;
            } while ((measured.Width > 520f || measured.Height > 400f) && size >= 28f);
            image.Mutate(ctx => ctx.DrawText(options, name, Color.White));
        }

        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = 90 });
        return ms.ToArray();
    }
}
