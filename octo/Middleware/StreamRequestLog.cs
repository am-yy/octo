using System.Diagnostics;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;

namespace Octo.Middleware;

/// <summary>
/// Watches Subsonic stream and download answers. One that ends before its last byte is always
/// logged: a client that hangs up, or Octo cutting the connection, looks the same to the player
/// ("the song stopped after a few seconds"), and without this line nothing on the server says
/// it happened. With Server:LogStreamRequests on, every answer is logged as well.
/// </summary>
public static class StreamRequestLog
{
    /// <summary>Set by whatever cuts a stream on purpose, so the log says why.</summary>
    public const string EndReasonKey = "Octo.StreamEndReason";

    public static IApplicationBuilder UseStreamRequestLog(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsStream(context.Request.Path))
            {
                await next(context);
                return;
            }

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Octo.StreamRequests");
            var every = context.RequestServices.GetRequiredService<IOptionsMonitor<ServerSettings>>()
                .CurrentValue.LogStreamRequests;
            var original = context.Response.Body;
            var counter = new CountingStream(original);
            context.Response.Body = counter;
            var clock = Stopwatch.StartNew();
            try { await next(context); }
            finally
            {
                context.Response.Body = original;
                Report(context, logger, every, counter.Written, clock.Elapsed);
            }
        });

    internal static bool IsStream(PathString path) =>
        path.Equals("/rest/stream") || path.Equals("/rest/stream.view")
        || path.Equals("/rest/download") || path.Equals("/rest/download.view");

    private static void Report(HttpContext context, ILogger logger, bool every, long written, TimeSpan elapsed)
    {
        var request = context.Request;
        var response = context.Response;
        var id = request.Query["id"].ToString();
        var range = request.Headers.Range.ToString();
        var expected = response.ContentLength;
        var reason = context.Items[EndReasonKey] as string;
        var head = HttpMethods.IsHead(request.Method);
        var aborted = context.RequestAborted.IsCancellationRequested;
        var cut = !head && expected is long size && written < size && response.StatusCode is 200 or 206;
        if (aborted || cut || reason is not null)
        {
            logger.LogWarning(
                "Stream {Id} ended early: {Written} of {Expected} bytes after {Seconds:0.0}s, status {Status}, range '{Range}', {Cause}",
                id, written, expected?.ToString() ?? "unknown", elapsed.TotalSeconds, response.StatusCode, range,
                reason ?? (aborted ? "client closed the connection" : "response ended short"));
            return;
        }
        if (every)
            logger.LogInformation(
                "Stream {Method} {Id} range '{Range}': {Status}, {Written} bytes in {Seconds:0.0}s, {Agent}",
                request.Method, id, range, response.StatusCode, written, elapsed.TotalSeconds,
                request.Headers.UserAgent.ToString());
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Written += count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            Written += buffer.Length;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }
}
