using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Deezer;
using Octo.Services.Metadata;
using Octo.Services.Soulseek;

namespace Octo.Tests;

// Real HTTP and process deadlines run apart from unrelated CPU-heavy regressions.
[CollectionDefinition(nameof(ProgressiveDelivery), DisableParallelization = true)]
public sealed class ProgressiveDelivery;

[Collection(nameof(ProgressiveDelivery))]
public sealed class ProgressiveDeliveryIntegrationTests
{
    [Theory]
    [InlineData("FLAC", "raw", "audio/flac")]
    [InlineData("MP3_320", "raw", "audio/mpeg")]
    [InlineData("FLAC", "mp3", "audio/mpeg")]
    [InlineData("FLAC", "opus", "audio/ogg")]
    public async Task PlayableBytesArriveWhileSourceRemainsUnpublished(string quality, string format, string mime)
    {
        await using var fixture = await Fixture.CreateAsync(quality);
        using var response = await fixture.Client.GetAsync("/stream?format=" + format, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mime, response.Content.Headers.ContentType!.MediaType);
        if (format != "raw")
        {
            Assert.Null(response.Content.Headers.ContentLength);
            Assert.Empty(response.Headers.AcceptRanges);
        }
        await using var body = await response.Content.ReadAsStreamAsync();
        var initial = new byte[8192];
        await body.ReadExactlyAsync(initial).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.False(fixture.Release.Task.IsCompleted);
        Assert.True(format switch
        {
            "opus" => initial.AsSpan(0, 4).SequenceEqual("OggS"u8),
            "raw" when quality == "FLAC" => initial.AsSpan(0, 4).SequenceEqual("fLaC"u8),
            _ => initial.AsSpan(0, 3).SequenceEqual("ID3"u8) || initial[0] == 0xff,
        });
        fixture.Release.TrySetResult();
        using var output = new MemoryStream();
        output.Write(initial);
        await body.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(File.Exists(fixture.SourcePath));
        var target = Path.Combine(fixture.Root, "delivered." + (format == "opus" ? "ogg" : format == "mp3" || quality == "MP3_320" ? "mp3" : "flac"));
        await File.WriteAllBytesAsync(target, output.ToArray());
        await ValidateAudioAsync(target);
        if (format != "raw")
        {
            using var head = await fixture.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/stream?format=" + format));
            Assert.Equal(output.Length, head.Content.Headers.ContentLength);
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/stream?format=" + format);
            rangeRequest.Headers.Range = new RangeHeaderValue(8, 39);
            using var range = await fixture.Client.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal(output.ToArray()[8..40], await range.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task ColdHeadsStartNeitherFillNorEncoderAndInternalEndpointRejectsUnknownCapabilities()
    {
        await using var fixture = await Fixture.CreateAsync("FLAC");
        using var raw = await fixture.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/stream?format=raw"));
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Equal(fixture.Audio.LongLength, raw.Content.Headers.ContentLength);
        using var encoded = await fixture.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/stream?format=opus"));
        Assert.Null(encoded.Content.Headers.ContentLength);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(0, fixture.BodyReads);
        using var denied = await fixture.Client.GetAsync("/internal/deezer-source/" + new string('0', 64));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task SharedEncoderSurvivesReaderCancellationAndDistinctProfileOverloadReturns429()
    {
        await using var fixture = await Fixture.CreateAsync("FLAC", encoders: 1);
        using var first = await fixture.Client.GetAsync("/stream?format=mp3", HttpCompletionOption.ResponseHeadersRead);
        using var second = await fixture.Client.GetAsync("/stream?format=mp3", HttpCompletionOption.ResponseHeadersRead);
        using var third = await fixture.Client.GetAsync("/stream?format=opus", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        first.Dispose();
        var body = await second.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[8192]).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, ".staging"), "encode-*.tmp"));
        fixture.Release.TrySetResult();
        await body.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "encoded")));
    }

    [Fact]
    public async Task OffsetEncodingUsesAlignedAheadReadsBeforeSourceCompletion()
    {
        await using var fixture = await Fixture.CreateAsync("FLAC");
        using var response = await fixture.Client.GetAsync("/stream?format=opus&timeOffset=20", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[4096]).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(fixture.Release.Task.IsCompleted);
        Assert.False(File.Exists(fixture.SourcePath));
        fixture.Release.TrySetResult();
        await body.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("opus")]
    public async Task CacheDisabledDeliveryKeepsNoSourceOrEncodedCopy(string format)
    {
        await using var fixture = await Fixture.CreateAsync("FLAC", cacheEnabled: false);
        using var response = await fixture.Client.GetAsync("/stream?format=" + format, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[8192]).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(fixture.Root, ".staging"), "*.tmp"));
        fixture.Release.TrySetResult();
        await body.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "encoded")));
        await WaitForNoTemporaryFilesAsync(fixture.Root);
    }

    [Theory]
    [InlineData("raw", false)]
    [InlineData("raw", true)]
    [InlineData("opus", false)]
    public async Task CacheDisabledCorruptTailNeverCompletesOrPublishes(string format, bool download)
    {
        await using var fixture = await Fixture.CreateAsync("FLAC", cacheEnabled: false, corruptAudio: true);
        var query = "/stream?format=" + format + (download ? "&download=true" : "");
        using var response = await fixture.Client.GetAsync(query, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[8192]).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        fixture.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(45)));
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "encoded")));
        await WaitForNoTemporaryFilesAsync(fixture.Root);
    }

    [Fact]
    public async Task HostShutdownStopsActiveEncoderAndRejectsNewDelivery()
    {
        await using var fixture = await Fixture.CreateAsync("FLAC");
        using var response = await fixture.Client.GetAsync("/stream?format=opus", HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[8192]).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(fixture.Root, ".staging"), "encode-*.tmp"));
        var copy = body.CopyToAsync(Stream.Null);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.App.StopAsync(deadline.Token);
        await Assert.ThrowsAnyAsync<IOException>(() => copy.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitForNoTemporaryFilesAsync(fixture.Root);

        var context = new DefaultHttpContext();
        var result = await fixture.Delivery.ServeAsync(context, new Song { Id = "stopping" },
            parameters: new Dictionary<string, string> { ["format"] = "raw" });
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task HostShutdownCancelsRawAndAheadRangeReaders()
    {
        await using var fixture = await Fixture.CreateAsync("FLAC");
        using var raw = await fixture.Client.GetAsync("/stream?format=raw", HttpCompletionOption.ResponseHeadersRead);
        var rawBody = await raw.Content.ReadAsStreamAsync();
        await rawBody.ReadExactlyAsync(new byte[8192]).AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        using var ahead = await fixture.Client.GetAsync("/stream?format=opus&timeOffset=20", HttpCompletionOption.ResponseHeadersRead);
        var aheadBody = await ahead.Content.ReadAsStreamAsync();
        await aheadBody.ReadExactlyAsync(new byte[4096]).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        var rawCopy = rawBody.CopyToAsync(Stream.Null);
        var aheadCopy = aheadBody.CopyToAsync(Stream.Null);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.App.StopAsync(deadline.Token);
        await Assert.ThrowsAnyAsync<IOException>(() => rawCopy.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<IOException>(() => aheadCopy.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitForNoTemporaryFilesAsync(fixture.Root);
    }

    [Fact]
    public async Task LaterRangeValidatorMismatchFailsSharedSourceAndPreventsPublication()
    {
        await using var fixture = await Fixture.CreateAsync("FLAC");
        fixture.ChangeRangeValidator = true;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/stream?format=raw");
        request.Headers.Range = new RangeHeaderValue(150000, 150100);
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, fixture.SourceStartRequests);
        Assert.False(File.Exists(fixture.SourcePath));
        fixture.Release.TrySetResult();
        await WaitForNoTemporaryFilesAsync(fixture.Root);
        Assert.False(File.Exists(fixture.SourcePath));
    }

    [Fact]
    public async Task EncoderKeysUseAcquiredValidatorAndShareWorkAcrossProbeChange()
    {
        await using var fixture = await Fixture.CreateAsync("FLAC");
        fixture.ChangeValidatorAfterFirstHead = true;
        using var first = await fixture.Client.GetAsync("/stream?format=opus", HttpCompletionOption.ResponseHeadersRead);
        var firstBody = await first.Content.ReadAsStreamAsync();
        var firstPrefix = new byte[8192];
        await firstBody.ReadExactlyAsync(firstPrefix).AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        using var second = await fixture.Client.GetAsync("/stream?format=opus", HttpCompletionOption.ResponseHeadersRead);
        var secondBody = await second.Content.ReadAsStreamAsync();
        var secondPrefix = new byte[8192];
        await secondBody.ReadExactlyAsync(secondPrefix).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, ".staging"), "encode-*.tmp"));
        Assert.Equal(1, fixture.SourceStartRequests);

        fixture.Release.TrySetResult();
        var outputs = new[] { (firstBody, firstPrefix), (secondBody, secondPrefix) };
        foreach (var (body, prefix) in outputs)
        {
            using var completed = new MemoryStream();
            completed.Write(prefix);
            await body.CopyToAsync(completed).WaitAsync(TimeSpan.FromSeconds(15));
        }

        var sourceFingerprint = SourceFingerprint("source-v2", fixture.Audio.LongLength);
        var encodeIdentity = $"{sourceFingerprint}\0opus\0{128}\0{0d.ToString("R", CultureInfo.InvariantCulture)}\0progressive-v2";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encodeIdentity))).ToLowerInvariant();
        Assert.True(File.Exists(Path.Combine(fixture.Root, "encoded", key + ".opus")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "encoded"), "*.opus"));

        using var head = await fixture.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/stream?format=opus"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(new FileInfo(Path.Combine(fixture.Root, "encoded", key + ".opus")).Length,
            head.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task RawValidatorsUseAcquiredSourceDescriptor()
    {
        await using var fixture = await Fixture.CreateAsync("FLAC");
        fixture.ChangeValidatorAfterFirstHead = true;
        using var response = await fixture.Client.GetAsync("/stream?format=raw", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal('"' + SourceFingerprint("source-v2", fixture.Audio.LongLength) + '"',
            response.Headers.ETag!.Tag);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T12:00:00Z"), response.Content.Headers.LastModified);
        fixture.Release.TrySetResult();
        await response.Content.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ColdRawSuffixUsesSameRepresentationAnd416StartsNoFill()
    {
        await using var fixture = await Fixture.CreateAsync("FLAC");
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, "/stream?format=raw");
        invalidRequest.Headers.Range = new RangeHeaderValue(fixture.Audio.LongLength, null);
        using var invalid = await fixture.Client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
        Assert.Equal(fixture.Audio.LongLength, invalid.Content.Headers.ContentRange!.Length);
        Assert.Equal(0, fixture.BodyReads);
        using var suffixRequest = new HttpRequestMessage(HttpMethod.Get, "/stream?format=raw");
        suffixRequest.Headers.Range = new RangeHeaderValue(null, 301);
        using var suffix = await fixture.Client.SendAsync(suffixRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.PartialContent, suffix.StatusCode);
        var body = await suffix.Content.ReadAsStreamAsync();
        var initial = new byte[300];
        await body.ReadExactlyAsync(initial).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(fixture.Audio[^301..^1], initial);
        Assert.False(File.Exists(fixture.SourcePath));
        fixture.Release.TrySetResult();
        var final = new byte[1];
        await body.ReadExactlyAsync(final).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(fixture.Audio[^1], final[0]);
    }

    [Fact]
    public async Task MatchingLibrarySourceEncodesWithoutADeezerIdentityOrTransfer()
    {
        await using var fixture = await Fixture.CreateAsync("FLAC");
        using var response = await fixture.Client.GetAsync("/stream?format=opus&library=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/ogg", response.Content.Headers.ContentType!.MediaType);
        var output = await response.Content.ReadAsByteArrayAsync();
        Assert.True(output.AsSpan(0, 4).SequenceEqual("OggS"u8));
        Assert.Equal(0, fixture.BodyReads);
        using var head = await fixture.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/stream?format=opus&library=true"));
        Assert.Equal(output.LongLength, head.Content.Headers.ContentLength);
    }

    private static async Task ValidateAudioAsync(string path)
    {
        await RunFfmpegAsync(["-v", "error", "-xerror", "-i", path, "-f", "null", "-"]);
    }

    private static string SourceFingerprint(string validator, long length)
    {
        var identity = $"1234567890\0FLAC\0FLAC\0https://cdn.test/source\0\"{validator}\"\0{length.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static async Task WaitForNoTemporaryFilesAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var files = Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).ToArray();
            if (files.Length == 0) return;
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task RunFfmpegAsync(string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("ffmpeg")
            { UseShellExecute = false, RedirectStandardError = true } };
        foreach (var value in arguments) process.StartInfo.ArgumentList.Add(value);
        Assert.True(process.Start());
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.ExitCode == 0, await errors);
    }

    private sealed class Fixture : HttpMessageHandler, IHttpClientFactory, IAsyncDisposable
    {
        private const string TrackId = "1234567890";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "octo-progressive-http-" + Guid.NewGuid().ToString("N"));
        public byte[] Audio = [];
        public string LibraryPath = "";
        private byte[] _encrypted = [];
        public int BodyReads;
        public int SourceStartRequests;
        public int HeadProbes;
        public bool ChangeValidatorAfterFirstHead;
        public bool ChangeRangeValidator;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SourcePath => Path.Combine(Root, TrackId + (_quality == "FLAC" ? ".flac" : ".mp3"));
        public WebApplication App => _app;
        public DeezerDeliveryService Delivery => _delivery;
        private string _quality = "";
        public HttpClient Client = null!;
        private WebApplication _app = null!;
        private DeezerAudioCache _cache = null!;
        private DeezerDeliveryService _delivery = null!;
        private DeezerResolver _resolver = null!;
        private DeezerMetadataService _catalog = null!;
        private ExternalIdRegistry _ids = null!;

        public static async Task<Fixture> CreateAsync(string quality, int encoders = 4, bool cacheEnabled = true,
            bool corruptAudio = false)
        {
            var fixture = new Fixture { _quality = quality };
            Directory.CreateDirectory(fixture.Root);
            var audioPath = Path.Combine(fixture.Root, "input." + (quality == "FLAC" ? "flac" : "mp3"));
            fixture.LibraryPath = audioPath;
            var arguments = new List<string> { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=30", "-ac", "2" };
            arguments.AddRange(quality == "FLAC" ? ["-c:a", "flac"] : ["-c:a", "libmp3lame", "-b:a", "320k"]);
            arguments.Add(audioPath);
            await RunFfmpegAsync(arguments.ToArray());
            fixture.Audio = await File.ReadAllBytesAsync(audioPath);
            Assert.True(fixture.Audio.Length > 160000);
            if (corruptAudio)
            {
                Assert.Equal("FLAC", quality);
                fixture.Audio[fixture.Audio.Length * 3 / 4] ^= 0x40;
            }
            fixture._encrypted = DeezerDecryptedStreamTests.EncryptStripes(fixture.Audio, TrackId);
            var options = TestOptions.Monitor(new DeezerSettings
                { Arl = "primary", CacheEnabled = cacheEnabled, CachePath = fixture.Root, CacheQuality = quality, MaxConcurrentTranscodes = encoders });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Deezer:Arl"] = "primary" }).Build();
            fixture._catalog = new DeezerMetadataService(fixture, TestOptions.Monitor(new MetadataSettings()), NullLogger<DeezerMetadataService>.Instance);
            fixture._resolver = new DeezerResolver(fixture, config, NullLogger<DeezerResolver>.Instance, fixture._catalog);
            fixture._ids = new ExternalIdRegistry(Path.Combine(fixture.Root, "ids.json"), NullLogger<ExternalIdRegistry>.Instance);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddControllers();
            builder.Services.AddSingleton<DeezerAudioCache>(sp => fixture._cache = new(fixture._resolver,
                fixture._catalog, fixture._ids, options, NullLogger<DeezerAudioCache>.Instance,
                sp.GetRequiredService<IHostApplicationLifetime>()));
            builder.Services.AddSingleton<DeezerDeliveryService>(sp => new(sp.GetRequiredService<DeezerAudioCache>(),
                options, NullLogger<DeezerDeliveryService>.Instance, sp.GetRequiredService<IServer>(),
                sp.GetRequiredService<IHostApplicationLifetime>()));
            builder.Services.AddHostedService(sp => sp.GetRequiredService<DeezerAudioCache>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<DeezerDeliveryService>());
            fixture._app = builder.Build();
            fixture._app.MapMethods("/internal/deezer-source/{token}", ["GET", "HEAD"],
                (HttpContext context, string token, DeezerDeliveryService delivery) => delivery.ServeSourceAsync(context, token));
            fixture._app.MapMethods("/stream", ["GET", "HEAD"], async (HttpContext context, DeezerDeliveryService delivery) =>
            {
                var song = new Song { Id = "virtual", ExternalId = "virtual", ExternalProvider = "deezer", DeezerId = TrackId, Artist = "Artist", Title = "Title" };
                var library = context.Request.Query.ContainsKey("library");
                if (library) song.DeezerId = null;
                var download = context.Request.Query.ContainsKey("download");
                var result = await delivery.ServeAsync(context, song, download,
                    localPath: library ? fixture.LibraryPath : null);
                await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
            });
            await fixture._app.StartAsync();
            fixture._delivery = fixture._app.Services.GetRequiredService<DeezerDeliveryService>();
            fixture.Client = new HttpClient { BaseAddress = new Uri(fixture._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(20) };
            return fixture;
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Query.Contains("deezer.getUserData"))
                return Task.FromResult(Json(new { results = new { checkForm = "api", USER = new { USER_ID = 7, OPTIONS = new { license_token = "license" } } } }));
            if (uri.Query.Contains("deezer.pageTrack"))
                return Task.FromResult(Json(new { results = new { DATA = new { SNG_ID = TrackId, TRACK_TOKEN = "token" } } }));
            if (uri.Host == "media.deezer.com")
                return Task.FromResult(Json(new { data = new[] { new { media = new[] { new { format = _quality, sources = new[] { new { url = "https://cdn.test/source" } } } } } } }));
            Assert.Equal("cdn.test", uri.Host);
            if (request.Method == HttpMethod.Head)
            {
                var number = Interlocked.Increment(ref HeadProbes);
                var validator = ChangeValidatorAfterFirstHead && number == 1 ? "probe-v1" : "source-v2";
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Content.Headers.ContentLength = _encrypted.Length;
                head.Content.Headers.ContentType = new MediaTypeHeaderValue(_quality == "FLAC" ? "audio/flac" : "audio/mpeg");
                head.Content.Headers.LastModified = number == 1 && ChangeValidatorAfterFirstHead
                    ? DateTimeOffset.Parse("2026-10-01T10:00:00Z")
                    : DateTimeOffset.Parse("2026-10-02T12:00:00Z");
                head.Headers.ETag = new EntityTagHeaderValue($"\"{validator}\"");
                return Task.FromResult(head);
            }
            if (request.Headers.Range is null) Interlocked.Increment(ref SourceStartRequests);
            var from = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            var content = new StreamContent(new HeldStream(_encrypted, from, from == 0 ? Release.Task : Task.CompletedTask, () => Interlocked.Increment(ref BodyReads)));
            content.Headers.ContentLength = _encrypted.Length - from;
            content.Headers.ContentType = new MediaTypeHeaderValue(_quality == "FLAC" ? "audio/flac" : "audio/mpeg");
            if (request.Headers.Range is not null)
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, _encrypted.Length - 1, _encrypted.Length);
            var response = new HttpResponseMessage(request.Headers.Range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = content };
            response.Headers.ETag = new EntityTagHeaderValue(request.Headers.Range is not null && ChangeRangeValidator
                ? "\"source-v3\"" : "\"source-v2\"");
            response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
            return Task.FromResult(response);
        }
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            Client?.Dispose();
            if (_app is not null) await _app.DisposeAsync();
            _cache?.Dispose(); _resolver?.Dispose(); _catalog?.Dispose(); _ids?.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
            base.Dispose();
        }
    }

    private sealed class HeldStream(byte[] bytes, int offset, Task release, Action onRead) : Stream
    {
        private readonly int _start = offset;
        private int _position = offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length - _start;
        public override long Position { get => _position - _start; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position == bytes.Length) return 0;
            if (_position >= 131072) await release.WaitAsync(ct);
            var boundary = _position < 131072 ? 131072 : bytes.Length;
            var read = Math.Min(Math.Min(buffer.Length, boundary - _position), bytes.Length - _position);
            bytes.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            onRead();
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
