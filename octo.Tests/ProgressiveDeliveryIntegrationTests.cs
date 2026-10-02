using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Deezer;
using Octo.Services.Metadata;
using Octo.Services.Soulseek;

namespace Octo.Tests;

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
        if (format == "raw") Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        fixture.Release.TrySetResult();
        await body.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "encoded")));
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
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SourcePath => Path.Combine(Root, TrackId + (_quality == "FLAC" ? ".flac" : ".mp3"));
        private string _quality = "";
        public HttpClient Client = null!;
        private WebApplication _app = null!;
        private DeezerAudioCache _cache = null!;
        private DeezerResolver _resolver = null!;
        private DeezerMetadataService _catalog = null!;
        private ExternalIdRegistry _ids = null!;

        public static async Task<Fixture> CreateAsync(string quality, int encoders = 4, bool cacheEnabled = true)
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
            fixture._encrypted = DeezerDecryptedStreamTests.EncryptStripes(fixture.Audio, TrackId);
            var options = TestOptions.Monitor(new DeezerSettings
                { Arl = "primary", CacheEnabled = cacheEnabled, CachePath = fixture.Root, CacheQuality = quality, MaxConcurrentTranscodes = encoders });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Deezer:Arl"] = "primary" }).Build();
            fixture._catalog = new DeezerMetadataService(fixture, TestOptions.Monitor(new MetadataSettings()), NullLogger<DeezerMetadataService>.Instance);
            fixture._resolver = new DeezerResolver(fixture, config, NullLogger<DeezerResolver>.Instance, fixture._catalog);
            fixture._ids = new ExternalIdRegistry(Path.Combine(fixture.Root, "ids.json"), NullLogger<ExternalIdRegistry>.Instance);
            fixture._cache = new DeezerAudioCache(fixture._resolver, fixture._catalog, fixture._ids, options, NullLogger<DeezerAudioCache>.Instance);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddControllers();
            builder.Services.AddSingleton(fixture._cache);
            builder.Services.AddSingleton<DeezerDeliveryService>(sp => new(fixture._cache, options, NullLogger<DeezerDeliveryService>.Instance, sp.GetRequiredService<IServer>()));
            fixture._app = builder.Build();
            fixture._app.MapMethods("/internal/deezer-source/{token}", ["GET", "HEAD"],
                (HttpContext context, string token, DeezerDeliveryService delivery) => delivery.ServeSourceAsync(context, token));
            fixture._app.MapMethods("/stream", ["GET", "HEAD"], async (HttpContext context, DeezerDeliveryService delivery) =>
            {
                var song = new Song { Id = "virtual", ExternalId = "virtual", ExternalProvider = "deezer", DeezerId = TrackId, Artist = "Artist", Title = "Title" };
                var library = context.Request.Query.ContainsKey("library");
                if (library) song.DeezerId = null;
                var result = await delivery.ServeAsync(context, song, localPath: library ? fixture.LibraryPath : null);
                await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
            });
            await fixture._app.StartAsync();
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
            var from = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            var content = new StreamContent(new HeldStream(_encrypted, from, from == 0 ? Release.Task : Task.CompletedTask, () => Interlocked.Increment(ref BodyReads)));
            content.Headers.ContentLength = _encrypted.Length - from;
            content.Headers.ContentType = new MediaTypeHeaderValue(_quality == "FLAC" ? "audio/flac" : "audio/mpeg");
            if (request.Headers.Range is not null)
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, _encrypted.Length - 1, _encrypted.Length);
            var response = new HttpResponseMessage(request.Headers.Range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = content };
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-source\"");
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
