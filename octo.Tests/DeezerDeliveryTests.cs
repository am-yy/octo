using Microsoft.AspNetCore.Http;
using Octo.Services.Deezer;

namespace Octo.Tests;

public sealed class DeezerDeliveryTests
{
    [Theory]
    [InlineData("", 0, "FLAC", null, 0)]
    [InlineData("", 64, "FLAC", "mp3", 64)]
    [InlineData("raw", 64, "FLAC", null, 0)]
    [InlineData("mp3", 0, "FLAC", "mp3", 128)]
    [InlineData("opus", 0, "FLAC", "opus", 128)]
    [InlineData("mp3", 100, "FLAC", "mp3", 96)]
    [InlineData("mp3", 320, "MP3_320", null, 0)]
    public void ProfilesHonorExplicitRawAndBitrateCaps(string format, int cap, string quality, string? codec, int rate)
    {
        var profile = DeezerDeliveryRequest.Parse(new Dictionary<string, string>
            { ["format"] = format, ["maxBitRate"] = cap.ToString() }, quality);
        Assert.Equal(codec, profile.Codec);
        Assert.Equal(rate, profile.BitrateKbps);
    }

    [Theory]
    [InlineData("format", "flac")]
    [InlineData("maxBitRate", "-1")]
    [InlineData("maxBitRate", "NaN")]
    [InlineData("maxBitRate", "513")]
    [InlineData("timeOffset", "NaN")]
    [InlineData("timeOffset", "-1")]
    public void InvalidParametersFailBeforeSourceWork(string key, string value)
    {
        Assert.Throws<ArgumentException>(() => DeezerDeliveryRequest.Parse(
            new Dictionary<string, string> { [key] = value }, "FLAC"));
    }

    [Fact]
    public void DownloadsAlwaysUseOriginalAndOffsetPreventsLossyPassthrough()
    {
        var parameters = new Dictionary<string, string>
            { ["format"] = "mp3", ["maxBitRate"] = "320", ["timeOffset"] = "12.5" };
        Assert.Equal("mp3", DeezerDeliveryRequest.Parse(parameters, "MP3_320").Codec);
        Assert.Null(DeezerDeliveryRequest.Parse(parameters, "FLAC", download: true).Codec);
    }

    [Theory]
    [InlineData("bytes=0-0", 0, 1, true, false)]
    [InlineData("bytes=2047-2050", 2047, 4, true, false)]
    [InlineData("bytes=4090-", 4090, 6, true, false)]
    [InlineData("bytes=-12", 4084, 12, true, false)]
    [InlineData("bytes=-99999", 0, 4096, true, false)]
    [InlineData("bytes=4096-", 0, 0, true, true)]
    [InlineData("bytes=-0", 0, 0, true, true)]
    [InlineData("bytes=0-2,8-9", 0, 4096, false, false)]
    public void RangeBoundaries(string header, long start, long length, bool partial, bool invalid)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Range = header;
        Assert.Equal((start, (long?)length, partial, invalid), DeezerDeliveryService.GetRange(context.Request, 4096, "source"));
    }

    [Fact]
    public void IfRangeMismatchReturnsWholeRepresentation()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Range = "bytes=80-";
        context.Request.Headers.IfRange = "\"old-source\"";
        Assert.Equal((0L, (long?)100L, false, false), DeezerDeliveryService.GetRange(context.Request, 100, "source"));
        context.Request.Headers.IfRange = "\"source\"";
        Assert.Equal((80L, (long?)20L, true, false), DeezerDeliveryService.GetRange(context.Request, 100, "source"));
    }

    [Fact]
    public void IfRangeDateRequiresMatchingKnownModificationTime()
    {
        var context = new DefaultHttpContext();
        var modified = DateTimeOffset.Parse("2026-10-01T10:00:00.123Z");
        context.Request.Headers.Range = "bytes=80-";
        context.Request.Headers.IfRange = "Thu, 01 Oct 2026 10:00:00 GMT";
        Assert.Equal((80L, (long?)20L, true, false), DeezerDeliveryService.GetRange(context.Request, 100, "source", modified));
        Assert.Equal((0L, (long?)100L, false, false), DeezerDeliveryService.GetRange(context.Request, 100, "source"));
        Assert.Equal((0L, (long?)100L, false, false), DeezerDeliveryService.GetRange(context.Request, 100, "source", modified.AddSeconds(1)));
        context.Request.Headers.IfRange = "W/\"source\"";
        Assert.Equal((0L, (long?)100L, false, false), DeezerDeliveryService.GetRange(context.Request, 100, "source", modified));
    }

    [Fact]
    public async Task FlushedAudioArrivesBeforeValidationButFinalByteWaits()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var validation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copy = DeezerDeliveryService.CopyValidatedAsync(new MemoryStream([1, 2, 3, 4]), context.Response,
            validation.Task, 4, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, ((MemoryStream)context.Response.Body).ToArray());
        Assert.False(copy.IsCompleted);
        validation.SetResult();
        await copy;
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task FailedValidationNeverCompletesPromisedResponse()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var validation = Task.FromException(new InvalidDataException("bad source"));
        await Assert.ThrowsAsync<InvalidDataException>(() => DeezerDeliveryService.CopyValidatedAsync(
            new MemoryStream([1, 2, 3, 4]), context.Response, validation, 4, CancellationToken.None));
        Assert.Equal(3, context.Response.Body.Length);
    }
}
