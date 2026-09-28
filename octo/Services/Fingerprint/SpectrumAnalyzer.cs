using System.Diagnostics;
using static System.FormattableString;

namespace Octo.Services.Fingerprint;

public enum SpectrumVerdict
{
    /// <summary>
    /// No opinion: ffmpeg is missing, timed out or could not decode the file, the file is too
    /// short or too quiet, its sample rate is under 44.1 kHz, or its spectrum has no clear cliff.
    /// Treated exactly like a genuine file by every caller.
    /// </summary>
    Unknown,

    /// <summary>The audio reaches the top of the band, or stops at a cliff no lossy encoder uses.</summary>
    Genuine,

    /// <summary>
    /// The spectrum stops dead at a frequency a lossy encoder cuts at and stays at the floor
    /// above it: a lossless file made from an MP3 or an AAC. Still the right song, only not
    /// lossless.
    /// </summary>
    LikelyLossy,
}

/// <summary>What the spectrum of a file showed, and why.</summary>
/// <param name="CutoffHz">Where the audio stops, when a cliff was found.</param>
/// <param name="Estimate">For a likely lossy file, the bitrate its cutoff is typical of,
/// in words ("about 128 kbps MP3").</param>
public sealed record SpectrumReport(SpectrumVerdict Verdict, int SampleRate, double? CutoffHz, string Reason,
    string? Estimate = null)
{
    public bool IsLikelyLossy => Verdict == SpectrumVerdict.LikelyLossy;

    public static SpectrumReport Unknown(string reason, int sampleRate = 0) =>
        new(SpectrumVerdict.Unknown, sampleRate, null, reason);

    /// <summary>One line for a log or a download record.</summary>
    public string Describe() => Verdict switch
    {
        SpectrumVerdict.LikelyLossy => Invariant($"likely transcoded from {Estimate} (cutoff {CutoffHz / 1000:0.0} kHz)"),
        SpectrumVerdict.Genuine => $"genuine ({Reason})",
        _ => $"no opinion ({Reason})",
    };
}

/// <summary>
/// Tells a lossless file made from a lossy one ("fake FLAC") by its spectrum.
///
/// A lossy encoder throws away everything above a frequency it picks from its bitrate: about
/// 16 to 17 kHz at 128 kbps, 18.5 at 192, 19.5 to 20.5 at 256 and 320. Converting the result to
/// FLAC keeps that hole. So a few windows of the file are decoded to mono, the power spectrum is
/// averaged over every frame that is not silent, and the highest frequency where the level
/// falls off a cliff and stays at the floor all the way up is the cutoff.
///
/// The cliff is what makes the verdict, not a lack of treble. An old recording, a lo-fi mix or a
/// quiet acoustic track can have almost nothing above 15 kHz and still be genuine; its spectrum
/// slopes down, it does not stop. So a drop has to be steep (tens of dB inside about a
/// kilohertz) and the band above it has to stay flat, and a spectrum that merely fades is
/// Unknown. Unknown is always preferred to a wrong LikelyLossy, because a wrong one makes Octo
/// throw away a perfectly good download to go looking for another.
///
/// Process handling is modelled on AudioFingerprinter: a missing ffmpeg, a timeout or a file
/// that will not decode is no opinion, never a failed download.
/// </summary>
public sealed class SpectrumAnalyzer
{
    /// <summary>Extensions that promise lossless audio. ALAC usually arrives as .m4a, which
    /// also holds AAC, so it is not in the list: an m4a is never assumed to be lossless.</summary>
    private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".aiff", ".aif", ".alac", ".ape", ".wv",
    };

    /// <summary>How many windows are decoded, spread through the track, and how long each is.
    /// Four windows of six seconds reach past a quiet intro and a fade without decoding the
    /// whole file.</summary>
    internal const int Windows = 4;
    internal const double WindowSeconds = 6;

    /// <summary>Frames quieter than this (RMS, dB below full scale) are skipped as silence.</summary>
    private const double SilenceDb = -65;

    /// <summary>Fewer non-silent frames than this (about two seconds) is too little to judge.</summary>
    internal const int MinActiveFrames = 40;

    /// <summary>How far the level must fall across a cliff, in dB, measured between the half
    /// kilohertz below it and the half kilohertz above it. A gentle roll-off at 24 dB per octave
    /// loses about 2 dB over that distance; an encoder's cutoff loses 40 or more.</summary>
    internal const double MinCliffDb = 30;

    /// <summary>How far anything above the cliff may rise over the level just past it. More than
    /// this and there is audio above the "cliff", so it was a dip, not a cutoff.</summary>
    private const double MaxTailRiseDb = 8;

    /// <summary>
    /// A cliff at or above this is the top of a genuine recording's band, not an encoder's
    /// cutoff. MP3 at 320 kbps stops at about 20.4 kHz; a CD master's anti-alias filter, and a
    /// good resampler bringing a 48 kHz master down to 44.1, stop at 21 kHz or above.
    /// </summary>
    internal const double GenuineCutoffHz = 20700;

    /// <summary>With no cliff, the top of the band must still carry audio within this many dB of
    /// the midrange for the file to count as genuine rather than unknown. A 16-bit file's own
    /// noise floor sits about 70 dB under loud music, so this stays well clear of it.</summary>
    private const double TopBandContentDb = 45;

    private readonly ILogger<SpectrumAnalyzer> _logger;

    /// <summary>Latched so a misbuilt image costs one log line, not a spawned process per file.</summary>
    private volatile bool _binaryMissing;

    public SpectrumAnalyzer(ILogger<SpectrumAnalyzer> logger) => _logger = logger;

    /// <summary>Whether a file's extension says it is lossless, which is the claim this checks.</summary>
    public static bool ClaimsLossless(string? path) =>
        !string.IsNullOrEmpty(path) && LosslessExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Decode a few windows of the file and judge its spectrum. Never throws; every failure is
    /// Unknown. The timeout covers the whole analysis, every window included.
    /// </summary>
    public async Task<SpectrumReport> AnalyzeAsync(string path, int timeoutSeconds)
    {
        if (_binaryMissing) return SpectrumReport.Unknown("ffmpeg is not available");

        int sampleRate;
        double seconds;
        try
        {
            using var file = TagLib.File.Create(path);
            sampleRate = file.Properties.AudioSampleRate;
            seconds = file.Properties.Duration.TotalSeconds;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("could not read the format of {Path}: {M}", path, ex.Message);
            return SpectrumReport.Unknown("the file's format could not be read");
        }

        // Below 44.1 kHz the band ends before the cutoffs this looks for, so there is nothing
        // to tell apart.
        if (sampleRate < 44100) return SpectrumReport.Unknown($"a sample rate of {sampleRate} Hz is under 44.1 kHz", sampleRate);
        if (seconds < 3) return SpectrumReport.Unknown("too short", sampleRate);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        var windows = new List<float[]>();
        try
        {
            foreach (var start in WindowStarts(seconds))
            {
                var samples = await DecodeAsync(path, start, Math.Min(WindowSeconds, seconds), sampleRate, cts.Token);
                if (samples is null) return SpectrumReport.Unknown("ffmpeg could not decode the file", sampleRate);
                windows.Add(samples);
            }
        }
        catch (FfmpegMissingException)
        {
            return SpectrumReport.Unknown("ffmpeg is not available");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("the spectrum check of {Path} took longer than {Timeout}s; no opinion", path, timeoutSeconds);
            return SpectrumReport.Unknown("the check timed out", sampleRate);
        }

        try
        {
            return Judge(windows, sampleRate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("the spectrum check of {Path} failed: {M}", path, ex.Message);
            return SpectrumReport.Unknown("the check failed", sampleRate);
        }
    }

    /// <summary>Where each window starts: spread through the track, clear of the very start
    /// and end, which are the likeliest to be silence or a fade.</summary>
    internal static IReadOnlyList<double> WindowStarts(double seconds)
    {
        if (seconds <= WindowSeconds) return [0];
        var starts = new List<double>();
        for (var i = 0; i < Windows; i++)
        {
            var at = seconds * (0.15 + 0.2 * i);
            starts.Add(Math.Max(0, Math.Min(at, seconds - WindowSeconds)));
        }
        return starts.Distinct().ToList();
    }

    private sealed class FfmpegMissingException : Exception;

    /// <summary>One window as mono 32-bit float samples at the file's own rate, or null when
    /// ffmpeg ran and could not decode it.</summary>
    private async Task<float[]?> DecodeAsync(string path, double start, double length, int sampleRate, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var arg in new[]
                 {
                     "-nostdin", "-hide_banner", "-v", "error",
                     "-ss", start.ToString("0.###", inv), "-t", length.ToString("0.###", inv),
                     "-i", path, "-map", "0:a:0", "-ac", "1", "-ar", sampleRate.ToString(inv),
                     "-f", "f32le", "-acodec", "pcm_f32le", "-",
                 })
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("ffmpeg did not start");
        }
        catch (Exception ex)
        {
            _binaryMissing = true;
            _logger.LogWarning(
                "ffmpeg is not in this image, so lossless downloads cannot be checked for transcoding: {M}", ex.Message);
            throw new FfmpegMissingException();
        }

        try
        {
            using var buffer = new MemoryStream();
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
            var stderr = await errorTask;
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || buffer.Length < 4)
            {
                _logger.LogDebug("ffmpeg could not decode {Path} for the spectrum check (exit {Code}): {Err}",
                    path, process.ExitCode, stderr.Trim());
                return null;
            }

            var bytes = buffer.GetBuffer();
            var samples = new float[buffer.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
            return samples;
        }
        catch
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
            }
            throw;
        }
    }

    // ---- the spectrum -------------------------------------------------------------------

    /// <summary>The FFT length for a sample rate: about 11 Hz a bin whatever the rate.</summary>
    internal static int FrameSize(int sampleRate) => sampleRate switch
    {
        <= 48000 => 4096,
        <= 96000 => 8192,
        _ => 16384,
    };

    /// <summary>
    /// The averaged power spectrum of every non-silent frame, in dB, smoothed across about
    /// 200 Hz so a single tone or a noisy bin cannot make or hide a cliff. Null when too few
    /// frames had sound in them.
    /// </summary>
    internal static double[]? AverageSpectrum(IReadOnlyList<float[]> windows, int sampleRate, out int activeFrames)
    {
        var size = FrameSize(sampleRate);
        var hop = size / 2;
        var window = BlackmanHarris(size);
        var power = new double[size / 2 + 1];
        var re = new double[size];
        var im = new double[size];
        var silence = Math.Pow(10, SilenceDb / 10);
        activeFrames = 0;

        foreach (var samples in windows)
        {
            for (var start = 0; start + size <= samples.Length; start += hop)
            {
                double energy = 0;
                for (var i = 0; i < size; i++)
                {
                    var sample = samples[start + i];
                    energy += sample * sample;
                    re[i] = sample * window[i];
                    im[i] = 0;
                }
                if (energy / size < silence) continue;

                Fft(re, im);
                for (var k = 0; k < power.Length; k++) power[k] += re[k] * re[k] + im[k] * im[k];
                activeFrames++;
            }
        }
        if (activeFrames < MinActiveFrames) return null;

        var binHz = (double)sampleRate / size;
        var half = Math.Max(1, (int)Math.Round(100 / binHz));
        var db = new double[power.Length];
        for (var k = 0; k < power.Length; k++)
        {
            var from = Math.Max(0, k - half);
            var to = Math.Min(power.Length - 1, k + half);
            double sum = 0;
            for (var j = from; j <= to; j++) sum += power[j];
            db[k] = 10 * Math.Log10(sum / (to - from + 1) / activeFrames + 1e-30);
        }
        return db;
    }

    /// <summary>
    /// The verdict on decoded windows, separate from the process handling so it can be driven
    /// directly.
    /// </summary>
    internal static SpectrumReport Judge(IReadOnlyList<float[]> windows, int sampleRate)
    {
        if (sampleRate < 44100) return SpectrumReport.Unknown($"a sample rate of {sampleRate} Hz is under 44.1 kHz", sampleRate);
        var db = AverageSpectrum(windows, sampleRate, out _);
        if (db is null) return SpectrumReport.Unknown("too little sound to judge", sampleRate);

        var binHz = (double)sampleRate / FrameSize(sampleRate);
        var nyquist = sampleRate / 2.0;
        double Mean(double fromHz, double toHz)
        {
            var from = Math.Clamp((int)Math.Round(fromHz / binHz), 0, db.Length - 1);
            var to = Math.Clamp((int)Math.Round(toHz / binHz), from, db.Length - 1);
            double sum = 0;
            for (var k = from; k <= to; k++) sum += db[k];
            return sum / (to - from + 1);
        }
        double Max(double fromHz, double toHz)
        {
            var from = Math.Clamp((int)Math.Round(fromHz / binHz), 0, db.Length - 1);
            var to = Math.Clamp((int)Math.Round(toHz / binHz), from, db.Length - 1);
            var max = double.MinValue;
            for (var k = from; k <= to; k++) max = Math.Max(max, db[k]);
            return max;
        }

        var mid = Mean(2000, 8000);

        // From the top down: the highest frequency where the half kilohertz below is far above
        // the half kilohertz above, and nothing higher comes back up.
        const double gap = 150, reach = 650;
        var top = nyquist - reach - 50;
        for (var f = top; f >= 10000; f -= binHz)
        {
            var below = Mean(f - reach, f - gap);
            var above = Mean(f + gap, f + reach);
            if (below - above < MinCliffDb) continue;
            if (Max(f + gap, nyquist - 100) - above > MaxTailRiseDb) continue;

            // The cutoff is where the level crosses halfway down the cliff, the highest such
            // point, so a ragged shelf just below it does not pull the estimate down.
            var halfway = (below + above) / 2;
            var cutoff = f;
            for (var g = f + reach; g >= f - reach; g -= binHz)
                if (Mean(g, g) >= halfway) { cutoff = g; break; }

            if (cutoff >= GenuineCutoffHz)
                return new SpectrumReport(SpectrumVerdict.Genuine, sampleRate, Math.Round(cutoff),
                    Invariant($"full band to {cutoff / 1000:0.0} kHz"));
            // The whole depth, for the log: the scan stops at the first 30 dB it finds.
            var depth = Mean(cutoff - 1200, cutoff - 400) - Mean(cutoff + 400, Math.Min(cutoff + 1200, nyquist - 100));
            return new SpectrumReport(SpectrumVerdict.LikelyLossy, sampleRate, Math.Round(cutoff),
                Invariant($"a {depth:0} dB cliff at {cutoff / 1000:0.0} kHz"), EstimateFor(cutoff));
        }

        // No cliff. Audio right up to the top of the CD band is genuine; a spectrum that fades
        // out before it may be a band-limited recording or a lossy file with a soft cutoff, and
        // that is no opinion.
        var high = Mean(20000, Math.Min(21000, nyquist - 200));
        if (mid - high <= TopBandContentDb)
            return new SpectrumReport(SpectrumVerdict.Genuine, sampleRate, null, "audio up to the top of the band");
        return SpectrumReport.Unknown("no clear cutoff", sampleRate);
    }

    /// <summary>The bitrate a cutoff is typical of, for a person reading a log. Encoders pick
    /// their low-pass from the bitrate, so the cutoff gives the bitrate away roughly.</summary>
    internal static string EstimateFor(double cutoffHz) => cutoffHz switch
    {
        < 15500 => "a lossy file under 128 kbps",
        < 17300 => "about 128 kbps MP3",
        < 18200 => "about 160 kbps",
        < 19400 => "about 192 kbps",
        _ => "about 256-320 kbps",
    };

    /// <summary>
    /// The 4-term Blackman-Harris window. Its side lobes sit about 92 dB down, so the loud
    /// midrange does not leak into the empty band above a cutoff and fill in the cliff.
    /// </summary>
    internal static double[] BlackmanHarris(int size)
    {
        var window = new double[size];
        for (var i = 0; i < size; i++)
        {
            var x = 2 * Math.PI * i / (size - 1);
            window[i] = 0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x) - 0.01168 * Math.Cos(3 * x);
        }
        return window;
    }

    /// <summary>In-place iterative radix-2 FFT. The length must be a power of two.</summary>
    internal static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("the length must be a power of two", nameof(re));

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var stepRe = Math.Cos(angle);
            var stepIm = Math.Sin(angle);
            for (var i = 0; i < n; i += length)
            {
                double wRe = 1, wIm = 0;
                for (var k = 0; k < length / 2; k++)
                {
                    var a = i + k;
                    var b = a + length / 2;
                    var tRe = re[b] * wRe - im[b] * wIm;
                    var tIm = re[b] * wIm + im[b] * wRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    var next = wRe * stepRe - wIm * stepIm;
                    wIm = wRe * stepIm + wIm * stepRe;
                    wRe = next;
                }
            }
        }
    }
}
