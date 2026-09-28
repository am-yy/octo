using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Services.Fingerprint;

namespace Octo.Tests;

/// <summary>
/// Real audio, made by ffmpeg when the tests run: stereo pink noise as the music, the same noise
/// through an MP3 encoder and back to FLAC as the fake, and the cases that must never be called
/// fake. Made once for the class; each file is twelve seconds.
/// </summary>
public sealed class SpectrumAudioFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"octo-spectrum-{Guid.NewGuid():N}");

    public SpectrumAudioFixture()
    {
        if (!FfmpegFactAttribute.IsAvailable) return;
        System.IO.Directory.CreateDirectory(Directory);

        // Two noise sources with different seeds, so the stereo is real stereo: an encoder given
        // identical channels spends its bits on one and cuts far higher than it does for music.
        const string noise = "anoisesrc=color=pink:duration=12:sample_rate=44100:amplitude=0.3";
        Run($"-filter_complex {noise}:seed=1[a];{noise}:seed=2[b];[a][b]amerge=inputs=2 -c:a flac -sample_fmt s16 genuine.flac");
        foreach (var bitrate in new[] { 128, 320 })
        {
            Run($"-i genuine.flac -c:a libmp3lame -b:a {bitrate}k mp3_{bitrate}.mp3");
            Run($"-i mp3_{bitrate}.mp3 -c:a flac -sample_fmt s16 mp3_{bitrate}.flac");
        }
        // A genuinely band-limited recording: four poles at 5 kHz, 24 dB an octave, so there is
        // almost nothing above 15 kHz and no cliff anywhere.
        Run("-i genuine.flac -af lowpass=f=5000,lowpass=f=5000 -c:a flac -sample_fmt s16 gentle.flac");
        Run("-f lavfi -i anullsrc=r=44100:cl=stereo -t 12 -c:a flac -sample_fmt s16 silence.flac");
        Run("-f lavfi -i anoisesrc=color=pink:duration=12:sample_rate=22050:amplitude=0.3 -c:a flac -sample_fmt s16 low_rate.flac");
    }

    public string this[string name] => Path.Combine(Directory, name);

    private void Run(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-y -nostdin -hide_banner -v error " + arguments)
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Directory,
        })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg {arguments}: {error}");
    }

    public void Dispose()
    {
        try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
        catch { /* a temp folder */ }
    }
}

/// <summary>
/// The fake-lossless check. A wrong LikelyLossy makes Octo throw away a good download and go
/// looking for another, so the cases that must NOT be called fake matter as much as the ones
/// that must.
/// </summary>
public class SpectrumAnalyzerTests(SpectrumAudioFixture audio, Xunit.Abstractions.ITestOutputHelper output)
    : IClassFixture<SpectrumAudioFixture>
{
    private static readonly SpectrumAnalyzer Analyzer = new(NullLogger<SpectrumAnalyzer>.Instance);

    private async Task<SpectrumReport> Analyze(string name)
    {
        var report = await Analyzer.AnalyzeAsync(audio[name], 30);
        output.WriteLine($"{name}: {report.Verdict}, cutoff {report.CutoffHz?.ToString() ?? "none"} Hz, {report.Reason}");
        return report;
    }

    [FfmpegFact]
    public async Task FullBandLosslessIsGenuine()
    {
        var report = await Analyze("genuine.flac");
        Assert.Equal(SpectrumVerdict.Genuine, report.Verdict);
    }

    [FfmpegFact]
    public async Task A128kMp3MadeIntoFlacIsLikelyLossy()
    {
        var report = await Analyze("mp3_128.flac");
        Assert.Equal(SpectrumVerdict.LikelyLossy, report.Verdict);
        // The encoder's own low-pass for 128 kbps stereo is 17 kHz.
        Assert.InRange(report.CutoffHz!.Value, 16000, 17300);
        Assert.Equal("about 128 kbps MP3", report.Estimate);
    }

    [FfmpegFact]
    public async Task A320kMp3MadeIntoFlacIsLikelyLossy()
    {
        var report = await Analyze("mp3_320.flac");
        Assert.Equal(SpectrumVerdict.LikelyLossy, report.Verdict);
        Assert.InRange(report.CutoffHz!.Value, 19600, SpectrumAnalyzer.GenuineCutoffHz);
        Assert.Equal("about 256-320 kbps", report.Estimate);
    }

    /// <summary>The false positive this is built to avoid: little treble is not a cutoff.</summary>
    [FfmpegFact]
    public async Task AGentleRollOffIsNeverCalledLossy()
    {
        var report = await Analyze("gentle.flac");
        Assert.NotEqual(SpectrumVerdict.LikelyLossy, report.Verdict);
    }

    [FfmpegFact]
    public async Task SilenceIsUnknown()
    {
        var report = await Analyze("silence.flac");
        Assert.Equal(SpectrumVerdict.Unknown, report.Verdict);
    }

    [FfmpegFact]
    public async Task ASampleRateUnder44kIsUnknown()
    {
        var report = await Analyze("low_rate.flac");
        Assert.Equal(SpectrumVerdict.Unknown, report.Verdict);
        Assert.Equal(22050, report.SampleRate);
    }

    [Fact]
    public async Task AFileThatIsNotThereIsNoOpinionNotAnException()
    {
        var report = await Analyzer.AnalyzeAsync(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.flac"), 5);
        Assert.Equal(SpectrumVerdict.Unknown, report.Verdict);
    }

    [Theory]
    [InlineData("song.flac", true)]
    [InlineData("SONG.FLAC", true)]
    [InlineData("song.wav", true)]
    [InlineData("song.aiff", true)]
    [InlineData("song.ape", true)]
    [InlineData("song.mp3", false)]
    [InlineData("song.m4a", false)]
    [InlineData("", false)]
    public void OnlyALosslessExtensionMakesTheClaim(string path, bool claims) =>
        Assert.Equal(claims, SpectrumAnalyzer.ClaimsLossless(path));

    [Theory]
    [InlineData(14000, "a lossy file under 128 kbps")]
    [InlineData(16000, "about 128 kbps MP3")]
    [InlineData(17000, "about 128 kbps MP3")]
    [InlineData(17600, "about 160 kbps")]
    [InlineData(19000, "about 192 kbps")]
    [InlineData(19700, "about 256-320 kbps")]
    [InlineData(20400, "about 256-320 kbps")]
    public void TheCutoffGivesTheBitrateAway(double cutoff, string estimate) =>
        Assert.Equal(estimate, SpectrumAnalyzer.EstimateFor(cutoff));

    [Fact]
    public void WindowsAreSpreadThroughTheTrackAndStayInsideIt()
    {
        var starts = SpectrumAnalyzer.WindowStarts(200);
        Assert.Equal(SpectrumAnalyzer.Windows, starts.Count);
        Assert.All(starts, start => Assert.InRange(start, 0, 200 - SpectrumAnalyzer.WindowSeconds));
        Assert.Equal([0], SpectrumAnalyzer.WindowStarts(4));
    }

    [Fact]
    public void TheFftPutsATonesEnergyInItsBin()
    {
        const int size = 1024;
        var re = new double[size];
        var im = new double[size];
        for (var i = 0; i < size; i++) re[i] = Math.Sin(2 * Math.PI * 37 * i / size);

        SpectrumAnalyzer.Fft(re, im);

        var magnitude = Enumerable.Range(0, size / 2).Select(k => Math.Sqrt(re[k] * re[k] + im[k] * im[k])).ToArray();
        Assert.Equal(37, Array.IndexOf(magnitude, magnitude.Max()));
        Assert.Equal(size / 2.0, magnitude[37], 6);
        Assert.True(magnitude.Where((_, k) => k != 37).All(value => value < 1e-6));
    }

    [Fact]
    public void AFrameThatIsNotAPowerOfTwoIsRefused() =>
        Assert.Throws<ArgumentException>(() => SpectrumAnalyzer.Fft(new double[1000], new double[1000]));

    /// <summary>Synthetic samples straight into the judge: a spectrum with nothing in it is not a
    /// verdict about the file.</summary>
    [Fact]
    public void TooLittleSoundIsUnknown()
    {
        var quiet = new float[44100 * 5];
        Assert.Equal(SpectrumVerdict.Unknown, SpectrumAnalyzer.Judge([quiet], 44100).Verdict);
    }
}

/// <summary>
/// What a likely transcode changes where lossless is trusted: which Soulseek copy is kept, and
/// whether a better-quality replacement is accepted. Never whether the song is downloaded.
/// </summary>
public class TranscodeDecisionTests
{
    private static readonly string Held = Path.Combine(Path.GetTempPath(), "music", "Artist", "Song.flac");
    private static readonly string Other = Path.Combine(Path.GetTempPath(), "music", "Peer", "Song.flac");

    [Fact]
    public void TheFirstTranscodeIsHeldBack() =>
        Assert.Equal(Octo.Services.Soulseek.SoulseekDownloadService.ReserveChoice.Hold,
            Octo.Services.Soulseek.SoulseekDownloadService.WeighTranscode(null, null, Other, 16900));

    /// <summary>The higher cutoff was made from the higher bitrate, so it replaces the one held.</summary>
    [Fact]
    public void AHigherCutoffReplacesTheOneHeld() =>
        Assert.Equal(Octo.Services.Soulseek.SoulseekDownloadService.ReserveChoice.Hold,
            Octo.Services.Soulseek.SoulseekDownloadService.WeighTranscode(Held, 16900, Other, 20350));

    [Theory]
    [InlineData(16900)]
    [InlineData(15000)]
    public void ANoBetterTranscodeIsDiscarded(double cutoff) =>
        Assert.Equal(Octo.Services.Soulseek.SoulseekDownloadService.ReserveChoice.DiscardNew,
            Octo.Services.Soulseek.SoulseekDownloadService.WeighTranscode(Held, 16900, Other, cutoff));

    /// <summary>The resolver can find the file already held back for a later peer offering the
    /// same rip. Deleting it as a new, worse copy would lose the only copy there is.</summary>
    [Fact]
    public void TheCopyAlreadyHeldIsNeverDiscardedAsANewOne() =>
        Assert.Equal(Octo.Services.Soulseek.SoulseekDownloadService.ReserveChoice.AlreadyHeld,
            Octo.Services.Soulseek.SoulseekDownloadService.WeighTranscode(Held, 16900, Held, 16900));

    [Fact]
    public void ABetterQualityReplacementThatIsATranscodeIsRefused()
    {
        var fake = new SpectrumReport(SpectrumVerdict.LikelyLossy, 44100, 16929, "a cliff", "about 128 kbps MP3");

        Assert.Equal("is likely transcoded from about 128 kbps MP3 (cutoff 16.9 kHz)",
            Octo.Services.Library.LibraryActionExecutor.NotReallyLossless(fake));
    }

    [Fact]
    public void ABetterQualityReplacementTheCheckCannotJudgeIsAccepted()
    {
        Assert.Null(Octo.Services.Library.LibraryActionExecutor.NotReallyLossless(SpectrumReport.Unknown("not checked")));
        Assert.Null(Octo.Services.Library.LibraryActionExecutor.NotReallyLossless(
            new SpectrumReport(SpectrumVerdict.Genuine, 44100, null, "audio up to the top of the band")));
    }
}
