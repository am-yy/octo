using Octo.Models.Domain;
using Octo.Services.Deezer;
using static Octo.Tests.DeezerAudioCacheTests;

namespace Octo.Tests;

public sealed class SourceAdmissionTests
{
    private static Song Track(int id) => new() { Id = $"dz-{id}", DeezerId = id.ToString(), Artist = "Artist", Title = "Title" };

    [Fact]
    public async Task TwoHundredHeldSourcesReserveSixteenSlotsAndJoinAtCapacity()
    {
        using var fixture = new Fixture { HoldCdn = true };
        var requests = Enumerable.Range(10000, 200).Select(id => fixture.Cache.OpenProgressiveAsync(Track(id))).ToArray();
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref fixture.ActiveCdnRequests) == 4);
            foreach (var request in requests.Skip(16))
                await Assert.ThrowsAsync<DeezerAdmissionException>(() => request);
            Assert.Equal(16, Directory.GetFiles(Path.Combine(fixture.Root, ".staging")).Length);
            Assert.Equal(4, fixture.ActiveCdnRequests);
            using var canceledJoin = new CancellationTokenSource();
            var joined = fixture.Cache.OpenProgressiveAsync(Track(10000), cancellationToken: canceledJoin.Token);
            canceledJoin.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joined);
            await Assert.ThrowsAsync<DeezerAdmissionException>(() => fixture.Cache.OpenProgressiveAsync(Track(20000)));
            // The same source still owns admission despite its joining reader leaving.
            joined = fixture.Cache.OpenProgressiveAsync(Track(10000));
            fixture.ReleaseCdn.Release(200);
            var leases = await Task.WhenAll(requests.Take(16)).WaitAsync(TimeSpan.FromSeconds(15));
            await using var duplicate = await joined.WaitAsync(TimeSpan.FromSeconds(15));
            foreach (var lease in leases)
            {
                await lease!.Completion.WaitAsync(TimeSpan.FromSeconds(15));
                await lease.DisposeAsync();
            }
            await using var next = await fixture.Cache.OpenProgressiveAsync(Track(20000));
            await next!.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, fixture.CdnRequestIds.Count(id => id == "10000"));
        }
        finally
        {
            await fixture.Cache.StopAsync(CancellationToken.None);
            foreach (var request in requests)
                try { if (await request is { } lease) await lease.DisposeAsync(); } catch { }
        }
    }

    [Fact]
    public async Task DeliveryReturns429BeforeHeadersWhileHeadInvalidRangesAndCompletedCopiesStillWork()
    {
        using var fixture = new Fixture { HoldCdn = true };
        var requests = Enumerable.Range(10000, 16).Select(id => fixture.Cache.OpenProgressiveAsync(Track(id))).ToArray();
        using var delivery = new DeezerDeliveryService(fixture.Cache, fixture.Settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezerDeliveryService>.Instance,
            Moq.Mock.Of<Microsoft.AspNetCore.Hosting.Server.IServer>());
        try
        {
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Method = "GET";
            var rejected = await delivery.ServeAsync(context, Track(20000));
            Assert.Equal(429, Assert.IsType<Microsoft.AspNetCore.Mvc.StatusCodeResult>(rejected).StatusCode);
            Assert.Equal("5", context.Response.Headers.RetryAfter);
            Assert.False(context.Response.HasStarted);
            context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Method = "HEAD";
            await delivery.ServeAsync(context, Track(20000));
            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal(fixture.Payload.Length, context.Response.ContentLength);
            context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Headers.Range = "bytes=999999-";
            await delivery.ServeAsync(context, Track(20000));
            Assert.Equal(416, context.Response.StatusCode);
            Assert.Equal(16, Directory.GetFiles(Path.Combine(fixture.Root, ".staging")).Length);
            await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "99.flac"), fixture.Payload);
            context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Method = "GET";
            context.Response.Body = new MemoryStream();
            await delivery.ServeAsync(context, Track(99));
            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal(fixture.Payload, ((MemoryStream)context.Response.Body).ToArray());
        }
        finally
        {
            await fixture.Cache.StopAsync(CancellationToken.None);
            await delivery.StopAsync(CancellationToken.None);
            foreach (var request in requests) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
    }

    [Fact]
    public async Task BackgroundAdmissionLeavesOneDownloadWindowForPlaybackAndShutdownReleasesEverything()
    {
        using var fixture = new Fixture { HoldCdn = true };
        var background = Enumerable.Range(10000, 12).Select(id =>
            fixture.Cache.OpenProgressiveAsync(Track(id), DeezerCachePriority.Speculative)).ToArray();
        var foreground = new List<Task<DeezerProgressiveLease?>>();
        try
        {
            await fixture.TwoCdnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<DeezerAdmissionException>(() =>
                fixture.Cache.OpenProgressiveAsync(Track(30000), DeezerCachePriority.Pinned));
            foreground.AddRange(Enumerable.Range(20000, 4).Select(id => fixture.Cache.OpenProgressiveAsync(Track(id))));
            await Assert.ThrowsAsync<DeezerAdmissionException>(() => fixture.Cache.OpenProgressiveAsync(Track(30000)));
            Assert.Equal(16, Directory.GetFiles(Path.Combine(fixture.Root, ".staging")).Length);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await fixture.Cache.StopAsync(deadline.Token);
            foreach (var request in background.Concat(foreground))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(0, fixture.ActiveCdnRequests);
            Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, ".staging")));
            await Assert.ThrowsAsync<DeezerStoppingException>(() => fixture.Cache.OpenProgressiveAsync(Track(30000)));
            await Assert.ThrowsAsync<DeezerStoppingException>(() => fixture.Cache.ProbeAsync(Track(30000)));
        }
        finally { await fixture.Cache.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task TemporaryPromotionDoesNotBorrowTheForegroundAdmissionReservation()
    {
        using var fixture = new Fixture { HoldCdn = true };
        var background = Enumerable.Range(10000, 12).Select(id =>
            fixture.Cache.OpenProgressiveAsync(Track(id), DeezerCachePriority.Speculative)).ToArray();
        using var cancellation = new CancellationTokenSource();
        var promoted = Enumerable.Range(10000, 4).Select(id =>
            fixture.Cache.OpenProgressiveAsync(Track(id), cancellationToken: cancellation.Token)).ToArray();
        try
        {
            await Assert.ThrowsAsync<DeezerAdmissionException>(() =>
                fixture.Cache.OpenProgressiveAsync(Track(30000), DeezerCachePriority.Speculative));
            cancellation.Cancel();
            foreach (var request in promoted) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(12, Directory.GetFiles(Path.Combine(fixture.Root, ".staging")).Length);
        }
        finally
        {
            await fixture.Cache.StopAsync(CancellationToken.None);
            foreach (var request in background) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
    }

    [Fact]
    public async Task CanceledPromotionMakesUnusedBackgroundTransferPreemptibleAgain()
    {
        using var fixture = new Fixture { HoldCdn = true };
        var background = fixture.Cache.PrewarmAsync([Track(42)], 1);
        var work = new List<Task> { background };
        try
        {
            await fixture.Started("42").Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var canceled = new CancellationTokenSource();
            var joined = fixture.Cache.EnsureAsync(Track(42), cancellationToken: canceled.Token);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joined);
            foreach (var id in new[] { 45, 46, 47 }) work.Add(fixture.Cache.EnsureAsync(Track(id)));
            await Task.WhenAll(new[] { "45", "46", "47" }.Select(id => fixture.Started(id).Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
            work.Add(fixture.Cache.EnsureAsync(Track(48)));
            await fixture.Started("48").Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            fixture.ReleaseCdn.Release(32);
            await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(15));
            await fixture.Cache.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PinOverflowKeepsDurablePinsAndRetriesUnattemptedIdsFirst()
    {
        using var fixture = new Fixture { HoldCdn = true, CorruptCdn = true };
        for (var id = 1; id <= 24; id++) await fixture.Cache.PinAsync(Track(id), "heart:user");
        var queue = PrivateField<System.Threading.Channels.Channel<string>>(fixture.Cache, "_pinQueue");
        Assert.Equal(16, queue.Reader.Count);
        await fixture.Cache.StartAsync(CancellationToken.None);
        try
        {
            await fixture.TwoCdnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, fixture.ActiveCdnRequests);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(fixture.Root, ".staging")).Length);
            fixture.ReleaseCdn.Release(64);
            var queued = PrivateField<System.Collections.Concurrent.ConcurrentDictionary<string, byte>>(fixture.Cache, "_queuedPins");
            await WaitUntilAsync(() => queued.IsEmpty);
            var firstAttempts = fixture.CdnRequestIds.ToHashSet();
            Assert.InRange(firstAttempts.Count, 16, 18);
            var state = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "cache-index.json"));
            using var document = System.Text.Json.JsonDocument.Parse(state);
            Assert.Equal(24, document.RootElement.GetProperty("tracks").EnumerateObject().Count());
            await RetryPinsAsync(fixture.Cache);
            await WaitUntilAsync(() => queued.IsEmpty);
            Assert.Equal(24, fixture.CdnRequestIds.Distinct().Count());
            // Failed attempts are delayed; overflow that never attempted is immediately eligible.
            Assert.All(fixture.CdnRequestIds.GroupBy(id => id), group => Assert.Single(group));
        }
        finally { await fixture.Cache.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task PinAdmissionRefusalDoesNotDelayRetryAfterForegroundReleasesCapacity()
    {
        using var fixture = new Fixture { HoldCdn = true };
        await fixture.Cache.StartAsync(CancellationToken.None);
        var work = Enumerable.Range(10000, 16).Select(id => fixture.Cache.EnsureAsync(Track(id))).ToArray();
        try
        {
            await fixture.TwoCdnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Cache.PinAsync(Track(42), "heart:user");
            var queued = PrivateField<System.Collections.Concurrent.ConcurrentDictionary<string, byte>>(fixture.Cache, "_queuedPins");
            await WaitUntilAsync(() => queued.IsEmpty);
            var attempts = PrivateField<System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>>(fixture.Cache, "_pinAttempts");
            Assert.False(attempts.ContainsKey("42"));
            fixture.ReleaseCdn.Release(64);
            await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(15));
            await RetryPinsAsync(fixture.Cache);
            await fixture.Started("42").Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => queued.IsEmpty);
            Assert.True(File.Exists(Path.Combine(fixture.Root, "42.flac")));
        }
        finally { await fixture.Cache.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RestartPreservesCompletedSourceAndPinsAndRestoresMissingFillAfterOrphanCleanup()
    {
        using var fixture = new Fixture { HoldCdn = true };
        await fixture.Cache.PinAsync(Track(42), "heart:user");
        await fixture.Cache.StartAsync(CancellationToken.None);
        await fixture.CdnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Cache.StopAsync(CancellationToken.None);
        var completed = Path.Combine(fixture.Root, "99.flac");
        await File.WriteAllBytesAsync(completed, fixture.Payload);
        var staging = Path.Combine(fixture.Root, ".staging");
        await File.WriteAllTextAsync(Path.Combine(staging, "42.FLAC.abrupt.tmp"), "partial source");
        await File.WriteAllTextAsync(Path.Combine(staging, "encode-abrupt.tmp"), "partial encode");
        using var restarted = new DeezerAudioCache(fixture.Resolver, fixture.Catalog, fixture.Ids,
            fixture.Settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezerAudioCache>.Instance);
        await restarted.StartAsync(CancellationToken.None);
        try
        {
            fixture.ReleaseCdn.Release(8);
            await WaitUntilAsync(() => File.Exists(Path.Combine(fixture.Root, "42.flac")));
            Assert.True(File.Exists(completed));
            Assert.DoesNotContain(Directory.GetFiles(staging), path => path.Contains("abrupt"));
            using var state = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "cache-index.json")));
            Assert.True(state.RootElement.GetProperty("tracks").GetProperty("42").GetProperty("pins").TryGetProperty("heart:user", out _));
        }
        finally { await restarted.StopAsync(CancellationToken.None); }
    }

    private static T PrivateField<T>(DeezerAudioCache cache, string name) => (T)typeof(DeezerAudioCache)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cache)!;

    private static Task RetryPinsAsync(DeezerAudioCache cache) => (Task)typeof(DeezerAudioCache)
        .GetMethod("RetryMissingPinsAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(cache, [CancellationToken.None])!;

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task ShutdownWakesReadersAndTemporarySourceDisappearsAfterLastLease()
    {
        using var fixture = new Fixture { HoldAfterBytes = 20 };
        fixture.Settings.Set(new() { Arl = "primary", CacheEnabled = false, CachePath = fixture.Root });
        var lease = (await fixture.Cache.OpenProgressiveAsync(Track(42)))!;
        await fixture.CdnHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var received = new MemoryStream();
        var read = lease.Stream.CopyToAsync(received);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await fixture.Cache.StopAsync(deadline.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.Completion);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, ".staging")));
        await lease.DisposeAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, ".staging")));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.flac"));
    }
}
