using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Common;

namespace Octo.Services.Deezer;

/// <summary>One delivery path for strict source audio and shared progressive MP3/Opus output.</summary>
public sealed class DeezerDeliveryService : IDisposable
{
    private const string ProfileVersion = "progressive-v1";
    private readonly DeezerAudioCache _cache;
    private readonly IServer _server;
    private readonly ILogger<DeezerDeliveryService> _logger;
    private readonly int _maximumEncoders;
    private readonly object _lock = new();
    private readonly Dictionary<string, EncodeJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceCapability> _capabilities = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private int _running;

    public DeezerDeliveryService(DeezerAudioCache cache, IOptionsMonitor<DeezerSettings> options,
        ILogger<DeezerDeliveryService> logger, IServer server)
    {
        _cache = cache;
        _logger = logger;
        _server = server;
        _maximumEncoders = Math.Max(1, options.CurrentValue.MaxConcurrentTranscodes);
    }

    public async Task<IActionResult> ServeAsync(HttpContext context, Song song, bool download = false,
        string? localPath = null, IReadOnlyDictionary<string, string>? parameters = null)
    {
        parameters ??= context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
        DeezerDeliveryRequest request;
        try { request = DeezerDeliveryRequest.Parse(parameters, _cache.SourceQuality, download); }
        catch (ArgumentException ex) { return new BadRequestObjectResult(new { error = ex.Message }); }
        var head = HttpMethods.IsHead(context.Request.Method);
        try
        {
            if (localPath is not null && request.Codec is null)
                return CompletedFile(localPath, Mime(localPath), song, download);

            var info = localPath is null ? await _cache.ProbeAsync(song, context.RequestAborted) : null;
            if (localPath is null && info is null) return new StatusCodeResult(502);
            var type = localPath is null ? info!.ContentType : Mime(localPath);
            var fingerprint = localPath is null ? info!.Fingerprint : LocalFingerprint(localPath);
            if (request.Codec is null && info?.Path is not null)
            {
                var ready = _cache.OpenRead(song);
                if (ready is not null)
                {
                    context.Response.RegisterForDispose(ready);
                    var result = (FileStreamResult)CompletedStream(ready.Stream, type, fingerprint);
                    result.LastModified = info.ModifiedUtc;
                    if (download) result.FileDownloadName = DownloadName(song, type);
                    return result;
                }
            }
            if (request.Codec is null && head)
            {
                SetHeaders(context.Response, type, song, download, fingerprint, info?.ModifiedUtc);
                if (info!.ExpectedLength is long size) context.Response.ContentLength = size;
                context.Response.Headers.AcceptRanges = "bytes";
                return new EmptyResult();
            }
            if (request.Codec is null && info is not null
                && GetRange(context.Request, info.ExpectedLength, fingerprint, info.ModifiedUtc).Invalid)
            {
                SetHeaders(context.Response, type, song, download, fingerprint);
                context.Response.StatusCode = 416;
                context.Response.Headers.ContentRange = $"bytes */{info.ExpectedLength}";
                context.Response.ContentLength = 0;
                return new EmptyResult();
            }

            var key = request.Codec is null ? "" : EncodeKey(fingerprint, request);
            var completed = request.Codec is null ? null : EncodedPath(key, request.Codec);
            if (completed is not null && _cache.IsEnabled)
            {
                var ready = _cache.OpenEncodedRead(completed);
                if (ready is not null)
                {
                    context.Response.RegisterForDispose(ready);
                    return CompletedStream(ready.Stream, request.ContentType(type), ready.Fingerprint);
                }
            }
            if (head)
            {
                SetHeaders(context.Response, request.ContentType(type), song, false, key);
                context.Response.Headers.Remove("ETag");
                // A cold encoded representation has no exact size or stable byte ranges yet.
                return new EmptyResult();
            }

            if (request.Codec is null)
            {
                if (!_cache.IsEnabled)
                {
                    await ServeDirectAsync(context, song, download, info!);
                    return new EmptyResult();
                }
                await using var lease = await _cache.OpenProgressiveAsync(song,
                    DeezerCachePriority.Playback, context.RequestAborted);
                if (lease is null) return new StatusCodeResult(502);
                await ServeRawAsync(context, lease, song, download, info!.ModifiedUtc);
                return new EmptyResult();
            }

            EncodeJob? job;
            lock (_lock)
            {
                _jobs.TryGetValue(key, out job);
                if (job is null && _running >= _maximumEncoders)
                {
                    context.Response.Headers.RetryAfter = "5";
                    return new StatusCodeResult(429);
                }
                if (job is null)
                {
                    var directory = Path.Combine(_cache.RootPath, ".staging");
                    Directory.CreateDirectory(directory);
                    var staging = Path.Combine(directory, $"encode-{key}.{Guid.NewGuid():N}.tmp");
                    job = new EncodeJob(new ProgressiveFile(staging), completed!, _cache.IsEnabled,
                        _cache.ProtectEncoded(completed!), _cache.ProtectEncoded(staging));
                    _jobs.Add(key, job);
                    _running++;
                    _ = RunEncoderAsync(job, key, song, localPath, request);
                }
                job.Readers++;
            }
            try
            {
                SetHeaders(context.Response, request.ContentType(type), song, false, key);
                context.Response.Headers.Remove("ETag");
                context.Response.StatusCode = 200;
                context.Response.ContentLength = null;
                context.Response.Headers.Remove("Accept-Ranges");
                context.Response.Headers.Remove("Content-Range");
                await using var reader = job.Output.OpenRead();
                await CopyValidatedAsync(reader, context.Response, job.Output.Completion, null, context.RequestAborted);
                return new EmptyResult();
            }
            finally { ReleaseEncodedReader(key, job); }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { return new EmptyResult(); }
        catch (Exception ex)
        {
            _logger.LogWarning("Deezer delivery failed: {Type}", ex.GetType().Name);
            if (context.Response.HasStarted) { context.Abort(); return new EmptyResult(); }
            return new StatusCodeResult(502);
        }
    }

    private async Task ServeDirectAsync(HttpContext context, Song song, bool download, DeezerSourceInfo info)
    {
        SetHeaders(context.Response, info.ContentType, song, download, info.Fingerprint, info.ModifiedUtc);
        var range = GetRange(context.Request, info.ExpectedLength, info.Fingerprint, info.ModifiedUtc);
        if (range.Invalid)
        {
            context.Response.StatusCode = 416;
            context.Response.Headers.ContentRange = $"bytes */{info.ExpectedLength}";
            context.Response.ContentLength = 0;
            return;
        }
        var header = range.Partial ? $"bytes={range.Start}-{range.Start + range.Length - 1}" : null;
        await using var lease = await _cache.OpenDirectAsync(song, header,
            cancellationToken: context.RequestAborted) ?? throw new IOException("Direct Deezer source unavailable.");
        context.Response.ContentType = lease.ContentType;
        context.Response.StatusCode = lease.StatusCode;
        context.Response.ContentLength = lease.ContentLength;
        context.Response.Headers.AcceptRanges = "bytes";
        if (lease.ContentRange is not null) context.Response.Headers.ContentRange = lease.ContentRange;
        await CopyBoundedAsync(lease.Stream, context.Response.Body, lease.ContentLength, context.RequestAborted);
    }

    private async Task ServeRawAsync(HttpContext context, DeezerProgressiveLease lease, Song song, bool download,
        DateTimeOffset? modifiedUtc)
    {
        SetHeaders(context.Response, lease.ContentType, song, download, lease.Fingerprint, modifiedUtc);
        var range = GetRange(context.Request, lease.ExpectedLength, lease.Fingerprint, modifiedUtc);
        if (range.Invalid)
        {
            context.Response.StatusCode = 416;
            context.Response.Headers.ContentRange = $"bytes */{lease.ExpectedLength}";
            context.Response.ContentLength = 0;
            return;
        }
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.ContentLength = range.Length;
        context.Response.StatusCode = range.Partial ? 206 : 200;
        if (range.Partial)
            context.Response.Headers.ContentRange = $"bytes {range.Start}-{range.Start + range.Length - 1}/{lease.ExpectedLength}";
        await using var reader = await lease.OpenRangeAsync(range.Start, range.Length, context.RequestAborted);
        await CopyValidatedAsync(reader, context.Response, lease.Completion, range.Length, context.RequestAborted);
    }

    /// <summary>Only random, short-lived, source-bound capabilities on actual loopback connections work here.</summary>
    public async Task ServeSourceAsync(HttpContext context, string token)
    {
        SourceCapability? capability;
        lock (_lock) _capabilities.TryGetValue(token, out capability);
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote) || capability is null || capability.Expires < DateTime.UtcNow)
        { context.Response.StatusCode = 404; return; }
        var source = capability.Source;
        var range = GetRange(context.Request, source.Length, source.Fingerprint);
        context.Response.ContentType = source.ContentType;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ETag = Quote(source.Fingerprint);
        if (range.Invalid)
        {
            context.Response.StatusCode = 416;
            context.Response.Headers.ContentRange = $"bytes */{source.Length}";
            context.Response.ContentLength = 0;
            return;
        }
        context.Response.StatusCode = range.Partial ? 206 : 200;
        context.Response.ContentLength = range.Length;
        if (range.Partial)
            context.Response.Headers.ContentRange = $"bytes {range.Start}-{range.Start + range.Length - 1}/{source.Length}";
        if (HttpMethods.IsHead(context.Request.Method)) return;
        try
        {
            await using var reader = await source.OpenAsync(range.Start, range.Length, context.RequestAborted);
            await CopyBoundedAsync(reader, context.Response.Body, range.Length, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch { context.Abort(); }
    }

    private async Task RunEncoderAsync(EncodeJob job, string key, Song song, string? localPath, DeezerDeliveryRequest request)
    {
        // Yield before doing work: ServeAsync publishes the job under its lock first.
        await Task.Yield();
        string? token = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _shutdown.Token);
        var ct = linked.Token;
        try
        {
            await using var source = localPath is not null ? SourceAccess.Local(localPath)
                : SourceAccess.Growing(await _cache.OpenProgressiveAsync(song, DeezerCachePriority.Playback, ct)
                    ?? throw new IOException("No source available for transcoding."));
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            lock (_lock) _capabilities.Add(token, new(source, DateTime.UtcNow.AddMinutes(20)));
            var url = LoopbackUrl(token);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("ffmpeg")
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true,
                },
            };
            var arguments = new List<string> { "-nostdin", "-hide_banner", "-v", "error", "-xerror",
                "-protocol_whitelist", "http,tcp", "-rw_timeout", "120000000", "-seekable", "1" };
            if (request.Offset > 0) arguments.AddRange(["-ss", request.Offset.ToString("0.########", CultureInfo.InvariantCulture)]);
            arguments.AddRange(["-i", url, "-map", "0:a:0", "-vn", "-ac", "2", "-map_metadata", "-1", "-codec:a",
                request.Codec == "mp3" ? "libmp3lame" : "libopus", "-b:a", $"{request.BitrateKbps}k"]);
            if (request.Codec == "mp3")
                arguments.AddRange(["-ar", request.BitrateKbps < 64 ? "22050" : "44100", "-write_xing", "0", "-id3v2_version", "3", "-f", "mp3"]);
            else arguments.AddRange(["-application", "audio", "-page_duration", "200000", "-f", "ogg"]);
            arguments.AddRange(["-flush_packets", "1", "pipe:1"]);
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) throw new IOException("ffmpeg did not start.");
            try
            {
                // Drain stderr concurrently to avoid blocking an encoder on its pipe buffer.
                var errors = process.StandardError.ReadToEndAsync(ct);
                var buffer = new byte[16384];
                int read;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) > 0)
                    await job.Output.AppendAsync(buffer.AsMemory(0, read), ct);
                await process.WaitForExitAsync(ct);
                var error = await errors;
                if (process.ExitCode != 0 || job.Output.Length == 0)
                    // ffmpeg diagnostics can contain the capability URL; keep it out of logs and client errors.
                    throw new IOException($"ffmpeg exited with code {process.ExitCode}.");
                await source.Completion.WaitAsync(ct);
                if (job.Persistent)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(job.FinalPath)!);
                    job.Output.Publish(job.FinalPath);
                    await _cache.RegisterEncodedVariantAsync(key, song.DeezerId ?? "", job.FinalPath, DateTime.UtcNow, ct);
                }
                job.Output.Complete();
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
        }
        catch (Exception ex) { job.Output.Fail(ex); }
        finally
        {
            lock (_lock)
            {
                if (token is not null) _capabilities.Remove(token);
                _running--;
                job.Finished = true;
                if (job.Readers == 0) RemoveJob(key, job);
            }
        }
    }

    private string LoopbackUrl(string token)
    {
        var address = _server.Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault(value => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (address is null) throw new InvalidOperationException("Progressive transcoding requires an HTTP listener.");
        var colon = address.LastIndexOf(':');
        if (colon < 7 || !int.TryParse(address[(colon + 1)..].TrimEnd('/'), out var port) || port <= 0)
            throw new InvalidOperationException("HTTP listener has no usable port.");
        return $"http://127.0.0.1:{port}/internal/deezer-source/{token}";
    }

    private void ReleaseEncodedReader(string key, EncodeJob job)
    {
        lock (_lock)
        {
            job.Readers--;
            if (job.Finished && job.Readers == 0) RemoveJob(key, job);
        }
    }

    private void RemoveJob(string key, EncodeJob job)
    {
        _jobs.Remove(key);
        job.Output.Dispose();
        job.FinalProtection.Dispose();
        job.StagingProtection.Dispose();
        if (!job.Persistent || !job.Output.Completion.IsCompletedSuccessfully)
        {
            try { File.Delete(job.Output.Path); } catch { }
        }
    }

    private string EncodedPath(string key, string codec) => Path.Combine(_cache.RootPath, "encoded", key + (codec == "mp3" ? ".mp3" : ".opus"));
    private static string EncodeKey(string fingerprint, DeezerDeliveryRequest request) => Hash($"{fingerprint}\0{request.Codec}\0{request.BitrateKbps}\0{request.Offset.ToString("R", CultureInfo.InvariantCulture)}\0{ProfileVersion}");
    private static string LocalFingerprint(string path)
    {
        var file = new FileInfo(path);
        return Hash($"library\0{Path.GetFullPath(path)}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}");
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Quote(string fingerprint) => $"\"{fingerprint}\"";
    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    { ".mp3" => "audio/mpeg", ".opus" or ".ogg" => "audio/ogg", _ => "audio/flac" };

    private static void SetHeaders(HttpResponse response, string type, Song song, bool download, string fingerprint,
        DateTimeOffset? modifiedUtc = null)
    {
        response.ContentType = type;
        response.Headers.ETag = Quote(fingerprint);
        if (modifiedUtc.HasValue) response.Headers.LastModified = modifiedUtc.Value.ToString("R", CultureInfo.InvariantCulture);
        if (download)
        {
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(DownloadName(song, type));
            response.Headers.ContentDisposition = disposition.ToString();
        }
    }

    private static string DownloadName(Song song, string type) => PathHelper.SanitizeFileName(
        string.IsNullOrWhiteSpace(song.Title) ? song.Id : $"{song.Artist} - {song.Title}") + (type == "audio/mpeg" ? ".mp3" : ".flac");

    private static IActionResult CompletedFile(string path, string type, Song song, bool download) => new PhysicalFileResult(Path.GetFullPath(path), type)
    {
        EnableRangeProcessing = true,
        LastModified = File.GetLastWriteTimeUtc(path),
        EntityTag = new EntityTagHeaderValue(Quote(LocalFingerprint(path))),
        FileDownloadName = download ? PathHelper.SanitizeFileName($"{song.Artist} - {song.Title}") + Path.GetExtension(path) : null,
    };
    private static IActionResult CompletedStream(Stream stream, string type, string key) => new FileStreamResult(stream, type)
    { EnableRangeProcessing = true, EntityTag = new EntityTagHeaderValue(Quote(key)) };

    internal static (long Start, long? Length, bool Partial, bool Invalid) GetRange(HttpRequest request,
        long? total, string fingerprint, DateTimeOffset? modifiedUtc = null)
    {
        var header = request.Headers.Range.ToString();
        var ifRange = request.Headers.IfRange.ToString();
        var matchingDate = modifiedUtc.HasValue
            && DateTimeOffset.TryParseExact(ifRange, "R", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date)
            && date.ToUnixTimeSeconds() == modifiedUtc.Value.ToUnixTimeSeconds();
        if (total is null || header.Length == 0 || ifRange.Length > 0 && ifRange != Quote(fingerprint) && !matchingDate)
            return (0, total, false, false);
        if (!System.Net.Http.Headers.RangeHeaderValue.TryParse(header, out var parsed)
            || parsed.Unit != "bytes" || parsed.Ranges.Count != 1) return (0, total, false, false);
        var part = parsed.Ranges.Single();
        var start = part.From ?? Math.Max(0, total.Value - part.To.GetValueOrDefault());
        var end = part.From.HasValue ? Math.Min(part.To ?? total.Value - 1, total.Value - 1) : total.Value - 1;
        if (start >= total || start > end || !part.From.HasValue && part.To == 0) return (0, 0, true, true);
        return (start, end - start + 1, true, false);
    }

    internal static async Task CopyValidatedAsync(Stream input, HttpResponse response, Task validation, long? length, CancellationToken ct)
    {
        // Keep one byte back even when Content-Length is known: a client cannot declare
        // success before source/encoder validation. Flush every earlier chunk for playback.
        var buffer = new byte[16384];
        int? pending = null;
        var remaining = length;
        while (remaining is null || remaining > 0)
        {
            var count = remaining is long bounded ? (int)Math.Min(buffer.Length, bounded) : buffer.Length;
            var read = await input.ReadAsync(buffer.AsMemory(0, count), ct);
            if (read == 0)
            {
                if (remaining is > 0) throw new EndOfStreamException("Incomplete audio response.");
                break;
            }
            if (pending.HasValue) await response.Body.WriteAsync(new byte[] { (byte)pending.Value }, ct);
            if (read > 1) await response.Body.WriteAsync(buffer.AsMemory(0, read - 1), ct);
            pending = buffer[read - 1];
            if (remaining.HasValue) remaining -= read;
            await response.Body.FlushAsync(ct);
        }
        await validation.WaitAsync(ct);
        if (pending.HasValue) await response.Body.WriteAsync(new byte[] { (byte)pending.Value }, ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long? length, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var remaining = length;
        while (remaining is null || remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, remaining is long bounded ? (int)Math.Min(buffer.Length, bounded) : buffer.Length), ct);
            if (read == 0)
            {
                if (remaining is > 0) throw new EndOfStreamException();
                break;
            }
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            if (remaining.HasValue) remaining -= read;
            await output.FlushAsync(ct);
        }
    }

    public void Dispose() { _shutdown.Cancel(); }

    private sealed class EncodeJob(ProgressiveFile output, string finalPath, bool persistent,
        IDisposable finalProtection, IDisposable stagingProtection)
    {
        public ProgressiveFile Output { get; } = output;
        public string FinalPath { get; } = finalPath;
        public bool Persistent { get; } = persistent;
        public IDisposable FinalProtection { get; } = finalProtection;
        public IDisposable StagingProtection { get; } = stagingProtection;
        public int Readers;
        public bool Finished;
    }

    private sealed record SourceCapability(SourceAccess Source, DateTime Expires);
    private sealed class SourceAccess : IAsyncDisposable
    {
        private DeezerProgressiveLease? _lease;
        private string? _path;
        public long? Length { get; private init; }
        public string ContentType { get; private init; } = "";
        public string Fingerprint { get; private init; } = "";
        public Task Completion => _lease?.Completion ?? Task.CompletedTask;
        public static SourceAccess Growing(DeezerProgressiveLease lease) => new()
        { _lease = lease, Length = lease.ExpectedLength, ContentType = lease.ContentType, Fingerprint = lease.Fingerprint };
        public static SourceAccess Local(string path) => new()
        { _path = path, Length = new FileInfo(path).Length, ContentType = Mime(path), Fingerprint = LocalFingerprint(path) };
        public async Task<Stream> OpenAsync(long offset, long? length, CancellationToken ct)
        {
            if (_lease is not null) return await _lease.OpenRangeAsync(offset, length, ct);
            var stream = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous);
            stream.Position = offset;
            return stream;
        }
        public ValueTask DisposeAsync() => _lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
