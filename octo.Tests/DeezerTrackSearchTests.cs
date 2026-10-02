using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Octo.Models.Settings;
using Octo.Services.Metadata;

namespace Octo.Tests;

public sealed class DeezerTrackSearchTests
{
    private const string Artist = "The Sundays";
    private const string Title = "You're Not the Only One I Know";
    private const string EmptyRest = """{"data":[],"total":0,"next":null}""";
    private static readonly string EmptyWeb = WebResponse([]);

    [Fact]
    public async Task RestSearchUsesLimit100AndFindsSixthCandidate()
    {
        var rows = Enumerable.Range(1, 5).Select(i => RestTrack(100 + i, $"Other {i}", Artist))
            .Append(RestTrack(106, Title, Artist));
        using var rig = new Rig((request, _) => Task.FromResult(request.Path switch
        {
            "/search" => Json(RestResponse(rows)),
            "/track/106" => Json(TrackDetail(106, Title, Artist)),
            _ => Json(EmptyRest),
        }));

        var result = await rig.Service.EnrichTrackAsync(Artist, Title, includeYear: false);

        Assert.Equal("106", result?.DeezerId);
        var search = Assert.Single(rig.Calls, call => call.Path == "/search");
        Assert.Equal("100", Query(search.Uri, "limit"));
        Assert.DoesNotContain(rig.Calls, call => call.Host == "auth.deezer.com");
    }

    [Fact]
    public async Task RestSearchFollowsSameOriginNextPage()
    {
        var initialPages = 0;
        using var rig = new Rig((request, _) => Task.FromResult(request.Path switch
        {
            "/search" when Query(request.Uri, "index") == "100" => Json(RestResponse([RestTrack(2476993601, Title, Artist)])),
            "/search" when Interlocked.Increment(ref initialPages) == 1 => Json(RestResponse([], total: 101,
                $"https://api.deezer.com/search?q={Uri.EscapeDataString(Query(request.Uri, "q")!)}&limit=100&index=100")),
            "/search" => Json(EmptyRest),
            "/track/2476993601" => Json(TrackDetail(2476993601, Title, Artist)),
            _ => Json(EmptyRest),
        }));

        var result = await rig.Service.EnrichTrackAsync(Artist, Title, includeYear: false);

        Assert.Equal("2476993601", result?.DeezerId);
        Assert.Equal(3, rig.Calls.Count(call => call.Path == "/search"));
        Assert.DoesNotContain(rig.Calls, call => call.Path == "/artist/1058/top");
    }

    [Fact]
    public async Task MaliciousNextHostIsNotRequestedAndWebSearchCanRecover()
    {
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" when request.Path == "/search" => Json(RestResponse([], total: 100, "https://evil.invalid/steal")),
            "api.deezer.com" when request.Path == "/track/2476993601" => Json(TrackDetail(2476993601, Title, Artist)),
            "auth.deezer.com" => Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }),
            "pipe.deezer.com" => Json(WebResponse([WebTrack("2476993601", Title, Artist)])),
            _ => Json(EmptyRest),
        }));

        var result = await rig.Service.EnrichTrackAsync(Artist, Title, includeYear: false);

        Assert.Equal("2476993601", result?.DeezerId);
        Assert.DoesNotContain(rig.Calls, call => call.Host == "evil.invalid");
        var web = Assert.Single(rig.Calls, call => call.Host == "pipe.deezer.com");
        AssertWebIdentityQuery(web);
    }

    [Theory]
    [InlineData("basic", "[]")]
    [InlineData("basic", "{\"data\":[null]}")]
    [InlineData("basic", "{\"total\":0}")]
    [InlineData("basic", "{\"data\":[],\"total\":3,\"next\":null}")]
    [InlineData("basic", "429")]
    [InlineData("basic", "quota")]
    [InlineData("full", "[]")]
    [InlineData("full", "{\"data\":[null]}")]
    [InlineData("full", "{\"total\":0}")]
    [InlineData("full", "{\"data\":[],\"total\":3,\"next\":null}")]
    [InlineData("full", "429")]
    [InlineData("full", "quota")]
    [InlineData("alternative", "[]")]
    [InlineData("alternative", "{\"data\":[null]}")]
    [InlineData("alternative", "{\"total\":0}")]
    [InlineData("alternative", "{\"data\":[],\"total\":3,\"next\":null}")]
    [InlineData("alternative", "429")]
    [InlineData("alternative", "quota")]
    public async Task RestFailureDoesNotPoisonBasicFullOrAlternativeRetry(string mode, string failure)
    {
        var firstPhase = true;
        var restSearches = 0;
        using var rig = new Rig((request, _) =>
        {
            if (request.Host == "api.deezer.com" && request.Path == "/search")
            {
                Interlocked.Increment(ref restSearches);
                if (!firstPhase) return Task.FromResult(Json(RestResponse([RestTrack(42, "Track", "Artist")])));
                return Task.FromResult(RestFailure(failure));
            }
            if (request.Host == "api.deezer.com" && request.Path == "/track/42")
                return Task.FromResult(Json(TrackDetail(42, "Track", "Artist")));
            if (request.Host == "api.deezer.com" && request.Path == "/album/7")
                return Task.FromResult(Json(AlbumDetail(7)));
            if (request.Host == "auth.deezer.com")
                return Task.FromResult(Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }));
            if (request.Host == "pipe.deezer.com")
                return Task.FromResult(Json(EmptyWeb));
            return Task.FromResult(Json(EmptyRest));
        });

        var first = await Lookup(rig.Service, mode, "Artist", "Track");
        var callsBeforeRetry = rig.Calls.Count;
        firstPhase = false;
        var recovered = await Lookup(rig.Service, mode, "Artist", "Track");

        Assert.Null(first);
        Assert.NotNull(recovered);
        Assert.True(rig.Calls.Count > callsBeforeRetry, $"{mode}, {failure}: retry must reach upstream");
        Assert.True(restSearches >= 2, $"{mode}, {failure}: recovery must issue another REST search");
    }

    [Fact]
    public async Task FullRestPageWithoutPagingMetadataDoesNotNegativeCacheMiss()
    {
        var recovered = false;
        var fullPage = Enumerable.Range(1, 100).Select(i => RestTrack(1000 + i, $"Other {i}", "Artist"));
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" when request.Path == "/search" => Json(recovered
                ? RestResponse([RestTrack(42, "Track", "Artist")])
                : JsonSerializer.Serialize(new { data = fullPage })),
            "api.deezer.com" when request.Path == "/search/album" => Json(EmptyRest),
            "auth.deezer.com" => Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }),
            "pipe.deezer.com" => Json(EmptyWeb),
            _ => Json(EmptyRest),
        }));

        Assert.Null(await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false));
        var beforeRetry = rig.Calls.Count;
        recovered = true;
        Assert.Equal("42", (await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false))?.DeezerId);
        Assert.True(rig.Calls.Count > beforeRetry);
    }

    [Fact]
    public async Task TruncatedAlbumTracklistDoesNotNegativeCacheMiss()
    {
        var recovered = false;
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" when request.Path == "/search/album" => Json(RestResponse([
                new { id = 7, title = "Track", artist = new { id = 8, name = "Artist" } }])),
            "api.deezer.com" when request.Path == "/album/7/tracks" => Json(recovered
                ? RestResponse([RestTrack(42, "Track", "Artist")], total: 1, next: null)
                : RestResponse([RestTrack(41, "Other", "Artist")], total: 2, next: null)),
            "api.deezer.com" when request.Path == "/track/42" => Json(TrackDetail(42, "Track", "Artist")),
            "api.deezer.com" => Json(EmptyRest),
            "auth.deezer.com" => Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }),
            "pipe.deezer.com" => Json(EmptyWeb),
            _ => Json(EmptyRest),
        }));

        Assert.Null(await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false));
        var beforeRetry = rig.Calls.Count;
        recovered = true;
        Assert.Equal("42", (await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false))?.DeezerId);
        Assert.True(rig.Calls.Count > beforeRetry);
    }

    [Fact]
    public async Task PartialTrackMetadataWithoutIdCanRecoverOnImmediateRetry()
    {
        var recovered = false;
        var noIdTrack = new
        {
            title = "Track",
            duration = 230,
            readable = true,
            isrc = "USGF19027706",
            artist = new { id = 1058, name = "Artist" },
            album = new { id = 7, title = "Reading, Writing And Arithmetic" },
        };
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" when request.Path == "/search" => Json(recovered
                ? RestResponse([RestTrack(42, "Track", "Artist")])
                : RestResponse([noIdTrack])),
            _ => Json(EmptyRest),
        }));

        var partial = await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false);
        Assert.NotNull(partial);
        Assert.Null(partial.DeezerId);

        var beforeRetry = rig.Calls.Count;
        recovered = true;
        Assert.Equal("42", (await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false))?.DeezerId);
        Assert.True(rig.Calls.Count > beforeRetry);
    }

    [Theory]
    [InlineData(Artist, Title, Title, "The Sun Days")]
    [InlineData(Artist, Title, "You're Not the Only One I Know (Demo)", Artist)]
    [InlineData("Artist", "Track", "Other Track", "Artist")]
    [InlineData("Artist", "Track", null, null)]
    public async Task WebSearchRejectsWrongArtistTitleOrRecordingVersion(
        string requestedArtist, string requestedTitle, string? candidateTitle, string? candidateArtist)
    {
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" when request.Path == "/search" => Json(EmptyRest),
            "api.deezer.com" when request.Path == "/search/album" => Json(EmptyRest),
            "auth.deezer.com" => Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }),
            "pipe.deezer.com" => Json(WebResponse([WebTrack("42", candidateTitle, candidateArtist)])),
            _ => Json(EmptyRest),
        }));

        Assert.Null(await rig.Service.EnrichTrackAsync(requestedArtist, requestedTitle, includeYear: false));
        Assert.Single(rig.Calls, call => call.Host == "pipe.deezer.com");
    }

    [Fact]
    public async Task RestTitleVersionFieldParticipatesInRecordingIdentity()
    {
        var row = new
        {
            id = 42,
            title = "Track",
            title_version = "Demo",
            duration = 230,
            readable = true,
            artist = new { id = 8, name = "Artist" },
            album = new { id = 7, title = "Release" },
        };
        using var rig = new Rig((request, _) => Task.FromResult(request.Path switch
        {
            "/search" => Json(RestResponse([row])),
            "/search/album" => Json(EmptyRest),
            _ => Json(EmptyRest),
        }));

        Assert.Null(await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false));
        Assert.DoesNotContain(rig.Calls, call => call.Path == "/track/42");
    }

    [Fact]
    public async Task IncompleteWebCandidateHydratesAndReturnsOnlyWhenFullIdentityAgrees()
    {
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" when request.Path == "/search" => Json(EmptyRest),
            "api.deezer.com" when request.Path == "/track/2476993601" => Json(TrackDetail(2476993601, Title, Artist)),
            "auth.deezer.com" => Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }),
            "pipe.deezer.com" => Json(WebResponse([WebTrack("2476993601", null, Artist)])),
            _ => Json(EmptyRest),
        }));

        var result = await rig.Service.EnrichTrackAsync(Artist, Title, includeYear: false);

        Assert.Equal("2476993601", result?.DeezerId);
        Assert.Single(rig.Calls, call => call.Path == "/track/2476993601");
    }

    [Fact]
    public async Task CapturedDemoRestHitCanRecoverOriginalFromWebCatalog()
    {
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" when request.Path == "/search" => Json(RestResponse([new
            {
                id = 3996036921,
                title = "You're Not the Only One I Know",
                title_version = "Demo",
                duration = 231,
                readable = true,
                isrc = "USUM72603409",
                artist = new { id = 1058, name = Artist },
                album = new { id = 700, title = "Demo" },
            }])),
            "api.deezer.com" when request.Path == "/track/2476993601" => Json(TrackDetail(2476993601, Title, Artist)),
            "auth.deezer.com" => Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }),
            "pipe.deezer.com" => Json(WebResponse([WebTrack("2476993601", Title, Artist)])),
            _ => Json(EmptyRest),
        }));

        var result = await rig.Service.EnrichTrackAsync(Artist, Title, includeYear: false);

        Assert.Equal("2476993601", result?.DeezerId);
        Assert.Contains(rig.Calls, call => call.Host == "pipe.deezer.com");
    }

    [Fact]
    public async Task ConflictingHydratedTrackIsRejected()
    {
        var validDetail = false;
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" when request.Path == "/search" => Json(EmptyRest),
            "api.deezer.com" when request.Path == "/track/42" => Json(validDetail
                ? TrackDetail(42, "Track", "Artist")
                : TrackDetail(42, "Track (Live)", "Artist")),
            "auth.deezer.com" => Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }),
            "pipe.deezer.com" => Json(WebResponse([WebTrack("42", "Track", "Artist")])),
            _ => Json(EmptyRest),
        }));

        Assert.Null(await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false));
        var beforeRetry = rig.Calls.Count;
        validDetail = true;
        Assert.Equal("42", (await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false))?.DeezerId);
        Assert.True(rig.Calls.Count > beforeRetry);
    }

    [Fact]
    public async Task GraphQlErrorAndPartialNullAreRetryable()
    {
        var returnCandidate = false;
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" when request.Path == "/search" => Json(EmptyRest),
            "api.deezer.com" when request.Path == "/track/42" => Json(TrackDetail(42, "Track", "Artist")),
            "auth.deezer.com" => Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }),
            "pipe.deezer.com" => Json(returnCandidate
                ? WebResponse([WebTrack("42", "Track", "Artist")])
                : """{"data":{"instantSearch":null},"errors":[{"message":"temporary"}]}"""),
            _ => Json(EmptyRest),
        }));

        Assert.Null(await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false));
        var beforeRetry = rig.Calls.Count;
        returnCandidate = true;
        Assert.Equal("42", (await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false))?.DeezerId);
        Assert.True(rig.Calls.Count > beforeRetry);
    }

    [Fact]
    public async Task TrueEmptyFromEveryEndpointIsNegativeCached()
    {
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" => Json(EmptyRest),
            "auth.deezer.com" => Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }),
            "pipe.deezer.com" => Json(EmptyWeb),
            _ => Json(EmptyRest),
        }));

        Assert.Null(await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false));
        var afterFirst = rig.Calls.Count;
        Assert.Null(await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false));

        Assert.Equal(afterFirst, rig.Calls.Count);
        Assert.InRange(rig.Calls.Count(call => call.Host == "pipe.deezer.com"), 1, 1);
    }

    [Fact]
    public async Task MatchingAlbumFallbackRunsAfterWebMissWithinBudget()
    {
        using var rig = new Rig((request, _) => Task.FromResult(request.Host switch
        {
            "api.deezer.com" when request.Path == "/search/album" => Json(RestResponse([new { id = 7, title = "Track", artist = new { id = 8, name = "Artist" } }])),
            "api.deezer.com" when request.Path == "/album/7/tracks" => Json(RestResponse([RestTrack(42, "Track", "Artist")], total: 1, next: null)),
            "api.deezer.com" when request.Path == "/track/42" => Json(TrackDetail(42, "Track", "Artist")),
            "api.deezer.com" => Json(EmptyRest),
            "auth.deezer.com" => Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) }),
            "pipe.deezer.com" => Json(EmptyWeb),
            _ => Json(EmptyRest),
        }));

        var result = await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false);

        Assert.Equal("42", result?.DeezerId);
        Assert.Contains(rig.Calls, call => call.Path == "/search/album");
        Assert.Contains(rig.Calls, call => call.Path == "/album/7/tracks");
        Assert.True(rig.Calls.Count <= 7, $"Expected discovery budget ≤7, got {rig.Calls.Count}");
    }

    [Fact]
    public async Task BasicAndFullCallersShareOneConcurrentDiscovery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(async (request, ct) =>
        {
            if (request.Host == "api.deezer.com" && request.Path == "/search")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Json(EmptyRest);
            }
            if (request.Host == "api.deezer.com" && request.Path == "/track/42") return Json(TrackDetail(42, "Track", "Artist"));
            if (request.Host == "api.deezer.com" && request.Path == "/album/7") return Json(AlbumDetail(7));
            if (request.Host == "auth.deezer.com") return Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) });
            if (request.Host == "pipe.deezer.com") return Json(WebResponse([WebTrack("42", "Track", "Artist")]));
            return Json(EmptyRest);
        });

        var basic = rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var full = rig.Service.EnrichTrackFullAsync("artist", "Track");
        release.TrySetResult();
        await Task.WhenAll(basic, full);

        Assert.Equal("42", (await basic)?.DeezerId);
        Assert.NotNull(await full);
        Assert.Single(rig.Calls, call => call.Host == "pipe.deezer.com");
        Assert.InRange(rig.Calls.Count(call => call.Path == "/search"), 1, 3);
    }

    [Fact]
    public async Task StopCancelsAndAwaitsActiveLookupThenRejectsNewWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(async (request, ct) =>
        {
            if (request.Host == "api.deezer.com" && request.Path == "/search")
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException)
                {
                    sendCancelled.TrySetResult();
                    throw;
                }
            }
            return Json(EmptyRest);
        });

        var lookup = rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var shutdownDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await rig.Service.StopAsync(shutdownDeadline.Token);
        Assert.Null(await lookup.WaitAsync(TimeSpan.FromSeconds(2)));
        await sendCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var sendsAfterStop = rig.Calls.Count;
        Assert.Null(await rig.Service.EnrichTrackAsync("Other Artist", "Other Track", includeYear: false));
        Assert.Equal(sendsAfterStop, rig.Calls.Count);
    }

    [Fact]
    public async Task CancelledSharedWaiterDoesNotCancelOtherCaller()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(async (request, ct) =>
        {
            if (request.Host == "api.deezer.com" && request.Path == "/search")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Json(RestResponse([RestTrack(42, "Track", "Artist")]));
            }
            return Json(EmptyRest);
        });

        using var cancellation = new CancellationTokenSource();
        var cancelled = rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false, ct: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var remaining = rig.Service.EnrichTrackAsync("artist", "Track", includeYear: false);
        cancellation.Cancel();
        release.TrySetResult();
        var cancelledResult = await cancelled.WaitAsync(TimeSpan.FromSeconds(2));
        var remainingResult = await remaining.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(cancelledResult);
        Assert.Equal("42", remainingResult?.DeezerId);
        Assert.InRange(rig.Calls.Count(call => call.Path == "/search"), 1, 3);
    }

    [Fact]
    public async Task DiscoveryStopsAtFiveSecondsAndSevenSends()
    {
        using var rig = new Rig(async (request, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), ct);
            if (request.Host == "auth.deezer.com")
                return Json(new { jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10)) });
            if (request.Host == "pipe.deezer.com") return Json(EmptyWeb);
            return Json(EmptyRest);
        });
        var timer = Stopwatch.StartNew();

        var result = await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false);

        timer.Stop();
        Assert.Null(result);
        Assert.InRange(rig.Calls.Count, 1, 7);
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(5.5));
    }

    [Fact]
    public async Task ExpiredJwtRefreshesOnceThenSearchesWithFreshToken()
    {
        var authCalls = 0;
        var graphCalls = 0;
        using var rig = new Rig((request, _) =>
        {
            if (request.Host == "auth.deezer.com")
            {
                var jwt = TestJwt(DateTimeOffset.UtcNow.AddMinutes(10));
                return Task.FromResult(Json(new { jwt = $"{jwt[..^1]}{Interlocked.Increment(ref authCalls)}" }));
            }
            if (request.Host == "pipe.deezer.com")
            {
                var attempt = Interlocked.Increment(ref graphCalls);
                return Task.FromResult(Json(attempt == 1
                    ? """{"data":{"instantSearch":null},"errors":[{"message":"JWT token expired","extensions":{"type":"JwtTokenExpiredError"}}]}"""
                    : WebResponse([WebTrack("42", "Track", "Artist")])));
            }
            if (request.Host == "api.deezer.com" && request.Path == "/track/42") return Task.FromResult(Json(TrackDetail(42, "Track", "Artist")));
            return Task.FromResult(Json(EmptyRest));
        });

        var result = await rig.Service.EnrichTrackAsync("Artist", "Track", includeYear: false);

        Assert.Equal("42", result?.DeezerId);
        Assert.Equal(2, authCalls);
        Assert.Equal(2, graphCalls);
        var tokens = rig.Calls.Where(call => call.Host == "pipe.deezer.com")
            .Select(call => call.Authorization).ToArray();
        Assert.Equal(2, tokens.Length);
        Assert.NotEqual(tokens[0], tokens[1]);
    }

    private static async Task<object?> Lookup(DeezerMetadataService service, string mode, string artist, string title) => mode switch
    {
        "basic" => await service.EnrichTrackAsync(artist, title, includeYear: false),
        "full" => await service.EnrichTrackFullAsync(artist, title),
        "alternative" => await service.FindAlternativeTrackIdAsync(artist, title, new HashSet<string>(), default),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static HttpResponseMessage RestFailure(string failure) => failure switch
    {
        "429" => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
        "quota" => Json("""{"error":{"code":4,"message":"Quota limit exceeded"}}"""),
        _ => Json(failure),
    };

    private static string RestResponse(IEnumerable<object> rows, int? total = null, string? next = null) =>
        JsonSerializer.Serialize(new { data = rows, total, next });

    private static object RestTrack(long id, string title, string artist) => new
    {
        id,
        title,
        duration = 230,
        readable = true,
        isrc = "USGF19027706",
        artist = new { id = 1058, name = artist },
        album = new { id = 7, title = "Reading, Writing And Arithmetic" },
    };

    private static object TrackDetail(long id, string title, string artist) => new
    {
        id,
        title,
        duration = 230,
        readable = true,
        track_position = 4,
        disk_number = 1,
        isrc = "USGF19027706",
        artist = new { id = 1058, name = artist },
        album = new { id = 7, title = "Reading, Writing And Arithmetic", cover_xl = "https://cdn.example/cover.jpg" },
    };

    private static object AlbumDetail(long id) => new
    {
        id,
        release_date = "1993-01-01",
        nb_tracks = 12,
        label = "Test Label",
        artist = new { id = 1058, name = Artist },
        genres = new { data = new[] { new { name = "Alternative" } } },
    };

    private static object WebTrack(string id, string? title, string? artist) => new
    {
        id,
        title,
        duration = 230,
        ISRC = "USGF19027706",
        contributors = new { edges = new[] { new { roles = new[] { "MAIN" }, node = new { id = "artist-1058", name = artist } } } },
        album = new { id = "album-7", displayTitle = "Reading, Writing And Arithmetic" },
    };

    private static string WebResponse(IEnumerable<object> nodes) => JsonSerializer.Serialize(new
    {
        data = new
        {
            instantSearch = new
            {
                results = new
                {
                    tracks = new { edges = nodes.Select(node => new { node }).ToArray(), pageInfo = new { hasNextPage = false, endCursor = (string?)null } },
                },
            },
        },
    });

    private static void AssertWebIdentityQuery(RecordedCall call)
    {
        using var body = JsonDocument.Parse(call.Body!);
        var query = body.RootElement.GetProperty("query").GetString()!;
        Assert.Contains("instantSearch", query, StringComparison.Ordinal);
        Assert.Contains("title", query, StringComparison.Ordinal);
        Assert.Contains("contributors", query, StringComparison.Ordinal);
        Assert.Contains("roles", query, StringComparison.Ordinal);
        Assert.Contains("id", query, StringComparison.Ordinal);
    }

    private static string? Query(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == name)
            .Select(pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')))
            .FirstOrDefault();

    private static HttpResponseMessage Json(object value) => Json(JsonSerializer.Serialize(value));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string TestJwt(DateTimeOffset expiry)
    {
        static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { exp = expiry.ToUnixTimeSeconds() })));
        return $"{header}.{payload}.test-signature";
    }

    private sealed record RecordedCall(string Host, string Path, Uri Uri, string Method, string? Body, string? Authorization);

    private sealed class Rig : IDisposable
    {
        private readonly RecordingHandler _handler;
        public DeezerMetadataService Service { get; }
        public IReadOnlyList<RecordedCall> Calls => _handler.Calls;

        public Rig(Func<RecordedCall, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _handler = new RecordingHandler(respond);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(item => item.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(_handler, disposeHandler: false));
            Service = new DeezerMetadataService(factory.Object,
                TestOptions.Monitor(new MetadataSettings { Language = "en" }),
                NullLogger<DeezerMetadataService>.Instance);
        }

        public void Dispose()
        {
            Service.Dispose();
            _handler.Dispose();
        }
    }

    private sealed class RecordingHandler(Func<RecordedCall, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedCall> _calls = new();
        public IReadOnlyList<RecordedCall> Calls => _calls.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var call = new RecordedCall(request.RequestUri!.Host, request.RequestUri.AbsolutePath,
                request.RequestUri, request.Method.Method, body, request.Headers.Authorization?.ToString());
            _calls.Enqueue(call);
            return await respond(call, cancellationToken);
        }
    }
}
