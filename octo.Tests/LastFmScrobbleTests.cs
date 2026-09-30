using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Controllers;
using Octo.Middleware;
using Octo.Models.Settings;
using Octo.Services.Admin;
using Octo.Services.LastFm;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// Outside songs reach Last.fm because Navidrome never hears of them. Library songs must not,
/// because Navidrome scrobbles those itself and a second copy would count every play twice.
/// Everything here talks to <see cref="FakeLastFm"/>; no test reaches the real Last.fm.
/// </summary>
public sealed class LastFmScrobbleServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "octo-lastfm-" + Guid.NewGuid());
    private readonly FakeLastFm _lastFm = new();
    private readonly TestOptionsMonitor<LastFmSettings> _settings;
    private readonly SettingsFileWriter _file;
    private readonly LastFmScrobbleService _service;

    public LastFmScrobbleServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _file = new SettingsFileWriter(Path.Combine(_directory, "settings.json"));
        _file.Merge(JsonNode.Parse("""{ "LastFm": { "UserSessions": { "alice": { "SessionKey": "sk-alice", "LastFmUser": "lfm-alice" } } } }""")!.AsObject());
        _settings = TestOptions.Monitor(new LastFmSettings
        {
            ApiKey = FakeLastFm.ApiKey,
            ApiSecret = FakeLastFm.Secret,
            UserSessions = new(StringComparer.OrdinalIgnoreCase)
            {
                ["alice"] = new LastFmUserSession { SessionKey = "sk-alice", LastFmUser = "lfm-alice" },
            },
        });
        _service = new LastFmScrobbleService(new ReviewFixtures.OneClientFactory(_lastFm), _settings, _file,
            NullLogger<LastFmScrobbleService>.Instance)
        {
            RetryDelay = TimeSpan.FromMilliseconds(20),
            RateLimitPause = TimeSpan.FromMilliseconds(300),
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    private static readonly LastFmTrack Song = new("Bladee", "Be Nice 2 Me", "Icedancer", 154);

    /// <summary>
    /// The authspec's own worked example, auth.getSession, with the MD5 worked out by hand
    /// outside this code (md5sum of "api_key..." + "method..." + "token..." + secret). format
    /// is sent but never signed.
    /// </summary>
    [Fact]
    public void Signature_MatchesAHandComputedVector()
    {
        var parameters = new Dictionary<string, string>
        {
            ["token"] = "tok123",
            ["format"] = "json",
            ["method"] = "auth.getSession",
            ["api_key"] = FakeLastFm.ApiKey,
        };

        Assert.Equal("5f50f7c80ec9a4fe05f95ce8ee49b1f8", LastFmScrobbleService.Sign(parameters, FakeLastFm.Secret));
    }

    /// <summary>Sorted by name, spaces kept as they are, whatever order they were added in.</summary>
    [Fact]
    public void Signature_SortsParametersByName()
    {
        var parameters = new Dictionary<string, string>
        {
            ["track"] = "Be Nice 2 Me",
            ["sk"] = "sk-alice",
            ["method"] = "track.updateNowPlaying",
            ["artist"] = "Bladee",
            ["api_key"] = FakeLastFm.ApiKey,
            ["album"] = "Icedancer",
        };

        Assert.Equal("33dc1eb9105f636ce7cde482d248d31b", LastFmScrobbleService.Sign(parameters, FakeLastFm.Secret));
    }

    [Fact]
    public async Task CompletedPlay_IsScrobbledWithEverythingLastFmAsksFor()
    {
        _service.Scrobble("Alice", Song, DateTimeOffset.FromUnixTimeSeconds(1757200000).UtcDateTime);
        await WhenIdle();

        var call = Assert.Single(_lastFm.CallsTo("track.scrobble"));
        Assert.Equal("sk-alice", call["sk"]);
        Assert.Equal(FakeLastFm.ApiKey, call["api_key"]);
        Assert.Equal("Bladee", call["artist[0]"]);
        Assert.Equal("Be Nice 2 Me", call["track[0]"]);
        Assert.Equal("Icedancer", call["album[0]"]);
        Assert.Equal("154", call["duration[0]"]);
        Assert.Equal("1757200000", call["timestamp[0]"]);
        Assert.Equal(LastFmScrobbleService.Sign(call, FakeLastFm.Secret), call["api_sig"]);
    }

    [Fact]
    public async Task ShortTracksAndUsersWithoutASession_SendNothing()
    {
        _service.Scrobble("alice", Song with { DurationSeconds = 29 }, DateTime.UtcNow);
        _service.Scrobble("bob", Song, DateTime.UtcNow);
        _service.NowPlaying("bob", Song);
        await WhenIdle();

        Assert.Empty(_lastFm.Calls);
    }

    /// <summary>Plays that pile up while one call is out go together, at most 50 a call.</summary>
    [Fact]
    public async Task QueuedPlays_GoInBatchesOfFifty()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastFm.Hold = _ => release.Task;
        _service.Scrobble("alice", Song, DateTime.UtcNow);
        await Until(() => _lastFm.Calls.Count == 1);
        for (var i = 0; i < 60; i++)
            _service.Scrobble("alice", Song with { Title = $"Song {i}" }, DateTime.UtcNow.AddMinutes(-i));
        _lastFm.Hold = null;
        release.SetResult();
        await WhenIdle();

        Assert.Equal([1, 50, 10], _lastFm.CallsTo("track.scrobble")
            .Select(call => call.Keys.Count(key => key.StartsWith("artist[", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task UnreachableLastFm_IsRetried()
    {
        _lastFm.Failures.Enqueue(0);
        _lastFm.Failures.Enqueue(16);

        _service.Scrobble("alice", Song, DateTime.UtcNow);
        await WhenIdle();

        var calls = _lastFm.CallsTo("track.scrobble");
        Assert.Equal(3, calls.Count);
        Assert.Single(calls.Select(call => call["timestamp[0]"]).Distinct());
    }

    [Fact]
    public async Task RetriesAreBounded()
    {
        for (var i = 0; i < 10; i++) _lastFm.Failures.Enqueue(0);

        _service.Scrobble("alice", Song, DateTime.UtcNow);
        await WhenIdle();

        Assert.Equal(LastFmScrobbleService.MaxAttempts, _lastFm.CallsTo("track.scrobble").Count);
    }

    /// <summary>Error 29: nothing more goes until the pause is over, then the play still does.</summary>
    [Fact]
    public async Task RateLimit_PausesThenSends()
    {
        _lastFm.Failures.Enqueue(LastFmScrobbleService.ErrorRateLimited);

        _service.Scrobble("alice", Song, DateTime.UtcNow);
        await Until(() => _lastFm.Calls.Count == 1);
        await Task.Delay(100);
        Assert.Single(_lastFm.Calls);
        await WhenIdle();

        Assert.Equal(2, _lastFm.CallsTo("track.scrobble").Count);
        var times = _lastFm.CallTimes;
        Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Error 9 is Last.fm saying the listener revoked Octo. Retrying cannot help: the user
    /// is disconnected, the saved session removed, and the dashboard says why.</summary>
    [Fact]
    public async Task InvalidSession_DisconnectsTheUser()
    {
        _lastFm.Failures.Enqueue(LastFmScrobbleService.ErrorInvalidSession);

        _service.Scrobble("alice", Song, DateTime.UtcNow);
        await WhenIdle();

        Assert.False(_service.IsEnabledFor("alice"));
        Assert.Null(((_file.Load()["LastFm"] as JsonObject)?["UserSessions"] as JsonObject)?["alice"]);
        var alice = Assert.Single(_service.Users([]), user => user.User == "alice");
        Assert.False(alice.Connected);
        Assert.Contains("Connect again", alice.Notice);

        _service.Scrobble("alice", Song, DateTime.UtcNow);
        _service.NowPlaying("alice", Song);
        await WhenIdle();
        Assert.Single(_lastFm.Calls);
    }

    [Fact]
    public async Task InvalidSession_OnNowPlaying_AlsoDisconnects()
    {
        _lastFm.Failures.Enqueue(LastFmScrobbleService.ErrorInvalidSession);

        _service.NowPlaying("alice", Song);
        await WhenIdle();

        Assert.Equal("track.updateNowPlaying", Assert.Single(_lastFm.Calls)["method"]);
        Assert.False(_service.IsEnabledFor("alice"));
    }

    [Fact]
    public async Task SwitchedOff_SendsNothing()
    {
        _settings.Set(new LastFmSettings
        {
            ApiKey = FakeLastFm.ApiKey, ApiSecret = FakeLastFm.Secret, ScrobbleExternalPlays = false,
            UserSessions = _settings.CurrentValue.UserSessions,
        });

        _service.Scrobble("alice", Song, DateTime.UtcNow);
        _service.NowPlaying("alice", Song);
        await WhenIdle();

        Assert.Empty(_lastFm.Calls);
    }

    private Task WhenIdle() => Until(() => _service.Outstanding == 0);

    internal static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }
}

/// <summary>A played song reaching Octo's /rest/scrobble, all the way to what Last.fm is sent.</summary>
public sealed class LastFmScrobbleEndpointTests
{
    private static string RegisterOutsideSong(RadioWebFactory fixture) =>
        fixture.Services.GetRequiredService<ExternalIdRegistry>().Register(new SoulseekRouting
        {
            Kind = RoutingKind.Song, Artist = "Bladee", Title = "Be Nice 2 Me", Album = "Icedancer", Duration = 154,
        });

    private static async Task WhenIdle(RadioWebFactory fixture)
    {
        var service = fixture.Services.GetRequiredService<LastFmScrobbleService>();
        await LastFmScrobbleServiceTests.Until(() => service.Outstanding == 0);
    }

    [Fact]
    public async Task OutsideSong_CompletedPlay_IsScrobbled()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();

        var body = await client.GetStringAsync(
            $"/rest/scrobble?u=bob&t=token&s=salt&f=json&id={id}&submission=true&time=1757200000000");
        await WhenIdle(fixture);

        Assert.Contains("\"status\":\"ok\"", body);
        var call = Assert.Single(fixture.Handler.LastFm.CallsTo("track.scrobble"));
        Assert.Equal("sk-bob", call["sk"]);
        Assert.Equal("Bladee", call["artist[0]"]);
        Assert.Equal("Be Nice 2 Me", call["track[0]"]);
        Assert.Equal("Icedancer", call["album[0]"]);
        Assert.Equal("154", call["duration[0]"]);
        Assert.Equal("1757200000", call["timestamp[0]"]);
        Assert.Empty(fixture.Handler.LastFm.CallsTo("track.updateNowPlaying"));
    }

    [Fact]
    public async Task OutsideSong_StartOfPlay_IsNowPlaying()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();

        await client.GetStringAsync($"/rest/scrobble?u=bob&t=token&s=salt&f=json&id={id}&submission=false");
        await WhenIdle(fixture);

        var call = Assert.Single(fixture.Handler.LastFm.Calls);
        Assert.Equal("track.updateNowPlaying", call["method"]);
        Assert.Equal("Bladee", call["artist"]);
        Assert.Equal("Be Nice 2 Me", call["track"]);
        Assert.Equal("Icedancer", call["album"]);
        Assert.Equal("154", call["duration"]);
    }

    /// <summary>Navidrome scrobbles library songs to Last.fm itself. From Octo too would be twice.</summary>
    [Fact]
    public async Task LibrarySong_NeverReachesLastFm()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        using var client = fixture.CreateClient();

        await client.GetStringAsync("/rest/scrobble?u=bob&t=token&s=salt&f=json&id=one&submission=false");
        await client.GetStringAsync("/rest/scrobble?u=bob&t=token&s=salt&f=json&id=one&submission=true");
        await WhenIdle(fixture);

        Assert.Equal(["one"], fixture.Handler.RelayedScrobbleIds);
        Assert.Empty(fixture.Handler.LastFm.Calls);
    }

    /// <summary>With no relay, a ping is the credential check. Failing it sends nothing, or anyone
    /// could scrobble to a listener's Last.fm by naming them.</summary>
    [Fact]
    public async Task WrongCredentials_SendNothing()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();

        // "bad" fails the fixture's ping; it is given a session so only the check stops it.
        var body = await client.GetStringAsync($"/rest/scrobble?u=bad&f=json&id={id}&submission=true");
        await WhenIdle(fixture);

        Assert.Contains("failed", body);
        Assert.Empty(fixture.Handler.LastFm.Calls);
    }

    [Fact]
    public async Task UserWithoutASession_SendsNothing()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();

        await client.GetStringAsync($"/rest/scrobble?u=alice&t=token&s=salt&f=json&id={id}&submission=false");
        await client.GetStringAsync($"/rest/scrobble?u=alice&t=token&s=salt&f=json&id={id}&submission=true");
        await WhenIdle(fixture);

        Assert.Empty(fixture.Handler.LastFm.Calls);
        // ListenBrainz still has alice's default token, so the play was not lost there.
        Assert.Single(fixture.Handler.ListenBrainzSubmissions);
    }

    [Fact]
    public async Task InvalidSession_DisconnectsAndStopsSending()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();
        fixture.Handler.LastFm.Failures.Enqueue(LastFmScrobbleService.ErrorInvalidSession);

        await client.GetStringAsync($"/rest/scrobble?u=bob&t=token&s=salt&f=json&id={id}&submission=true");
        await WhenIdle(fixture);
        await client.GetStringAsync($"/rest/scrobble?u=bob&t=token&s=salt&f=json&id={id}&submission=true");
        await WhenIdle(fixture);

        Assert.Single(fixture.Handler.LastFm.Calls);
        Assert.False(fixture.Services.GetRequiredService<LastFmScrobbleService>().IsEnabledFor("bob"));
    }

    /// <summary>The client's answer does not wait on Last.fm: a stalled call still gets an ok.</summary>
    [Fact]
    public async Task ScrobbleAnswer_DoesNotWaitForLastFm()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.LastFm.Hold = _ => release.Task;

        var body = await client.GetStringAsync($"/rest/scrobble?u=bob&t=token&s=salt&f=json&id={id}&submission=true")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("\"status\":\"ok\"", body);
        release.SetResult();
        await WhenIdle(fixture);
        Assert.Single(fixture.Handler.LastFm.CallsTo("track.scrobble"));
    }
}

/// <summary>The dashboard's Connect, Finish and Disconnect, and what the admin API shows of them.</summary>
public sealed class LastFmScrobbleAdminTests
{
    [Theory]
    [InlineData("/api/admin/lastfm/scrobble/connect")]
    [InlineData("/api/admin/lastfm/scrobble/finish")]
    [InlineData("/api/admin/lastfm/scrobble/disconnect")]
    public async Task Writes_WithoutTheAdminHeader_AreRefused(string url)
    {
        await using var factory = new ScrobbleAdminFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(url, Json(new { user = "alice" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.LastFm.Calls);
    }

    [Fact]
    public async Task ConnectFinishDisconnect_LinksAndUnlinksAUser()
    {
        await using var factory = new ScrobbleAdminFactory();
        using var client = factory.AdminClient();

        // Connect: a signed auth.getToken, and the page to approve Octo on.
        using var connect = await client.PostAsync("/api/admin/lastfm/scrobble/connect", Json(new { user = "alice" }));
        connect.EnsureSuccessStatusCode();
        var url = JsonDocument.Parse(await connect.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString();
        Assert.Equal($"https://www.last.fm/api/auth/?api_key={FakeLastFm.ApiKey}&token=tok-1", url);
        Assert.Single(factory.LastFm.CallsTo("auth.getToken"));

        // Finish before approving: Last.fm says not yet, and nothing is saved.
        using var early = await client.PostAsync("/api/admin/lastfm/scrobble/finish", Json(new { user = "alice" }));
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.DoesNotContain("sk-alice", File.ReadAllText(factory.SettingsPath));

        factory.LastFm.Approved = true;
        using var finish = await client.PostAsync("/api/admin/lastfm/scrobble/finish", Json(new { user = "alice" }));
        finish.EnsureSuccessStatusCode();
        Assert.Equal("lfm-alice", JsonDocument.Parse(await finish.Content.ReadAsStringAsync())
            .RootElement.GetProperty("lastFmUser").GetString());
        Assert.Equal("tok-1", factory.LastFm.CallsTo("auth.getSession")[^1]["token"]);
        Assert.Contains("sk-alice", File.ReadAllText(factory.SettingsPath));

        // The dashboard shows who alice is on Last.fm; no admin read shows the key.
        var status = "";
        for (var attempt = 0; attempt < 300 && !status.Contains("\"connected\":true"); attempt++)
        {
            status = await Status(client);
            await Task.Delay(10);
        }
        Assert.Contains("\"connected\":true", status);
        Assert.Contains("lfm-alice", status);
        foreach (var read in new[] { "/api/admin/lastfm/scrobble", "/api/admin/settings", "/api/admin/raw-config" })
            Assert.DoesNotContain("sk-alice", await client.GetStringAsync(read));

        using var disconnect = await client.PostAsync("/api/admin/lastfm/scrobble/disconnect", Json(new { user = "alice" }));
        disconnect.EnsureSuccessStatusCode();
        Assert.DoesNotContain("sk-alice", File.ReadAllText(factory.SettingsPath));
        Assert.False(factory.Services.GetRequiredService<LastFmScrobbleService>().IsEnabledFor("alice"));
    }

    /// <summary>The Raw editor writes back what it was shown. The masked session and secret must come
    /// back as what is stored, not as the placeholder.</summary>
    [Fact]
    public async Task RawConfig_RoundTrip_KeepsTheSecretAndSessions()
    {
        await using var factory = new ScrobbleAdminFactory(
            """{ "LastFm": { "ApiSecret": "stored-secret", "UserSessions": { "alice": { "SessionKey": "sk-alice", "LastFmUser": "lfm-alice" } } } }""");
        using var client = factory.AdminClient();

        var shown = await client.GetStringAsync("/api/admin/raw-config");
        Assert.DoesNotContain("stored-secret", shown);
        Assert.DoesNotContain("sk-alice", shown);
        using var put = await client.PutAsync("/api/admin/raw-config", new StringContent(shown, Encoding.UTF8, "application/json"));
        put.EnsureSuccessStatusCode();

        var saved = JsonNode.Parse(File.ReadAllText(factory.SettingsPath))!["LastFm"]!;
        Assert.Equal("stored-secret", (string?)saved["ApiSecret"]);
        Assert.Equal("sk-alice", (string?)saved["UserSessions"]!["alice"]!["SessionKey"]);
    }

    /// <summary>A form save echoes the placeholder for a secret nobody touched. That keeps what is
    /// stored; it is never saved as the secret.</summary>
    [Fact]
    public async Task FormSave_WithThePlaceholder_KeepsTheStoredSecret()
    {
        await using var factory = new ScrobbleAdminFactory("""{ "LastFm": { "ApiSecret": "stored-secret" } }""");
        using var client = factory.AdminClient();

        using var save = await client.PostAsync("/api/admin/settings",
            Json(new { LastFm = new { ApiKey = FakeLastFm.ApiKey, ApiSecret = AdminController.SecretPlaceholder } }));
        save.EnsureSuccessStatusCode();

        Assert.DoesNotContain("stored-secret", await save.Content.ReadAsStringAsync());
        Assert.Equal("stored-secret", (string?)JsonNode.Parse(File.ReadAllText(factory.SettingsPath))!["LastFm"]!["ApiSecret"]);
    }

    private static Task<string> Status(HttpClient client) => client.GetStringAsync("/api/admin/lastfm/scrobble");

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}

/// <summary>Octo with its settings file in a temporary folder, read back as configuration the way
/// /app/config/settings.json is, and Last.fm faked.</summary>
internal sealed class ScrobbleAdminFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "octo-lastfm-admin-" + Guid.NewGuid());
    public FakeLastFm LastFm { get; } = new();
    public string SettingsPath => Path.Combine(_directory, "settings.json");

    public ScrobbleAdminFactory(string settings = "{}")
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, settings);
    }

    public HttpClient AdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(AdminRequestGuard.HeaderName, "1");
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subsonic:Url"] = "http://127.0.0.1:1",
                ["Subsonic:AutoDetectDownloadPath"] = "false",
                ["Soulseek:BaseUrl"] = "http://127.0.0.1:1",
                ["YouTube:ShimUrl"] = "http://127.0.0.1:1",
                ["Library:DownloadPath"] = _directory,
                ["LastFm:ApiKey"] = FakeLastFm.ApiKey,
                ["LastFm:ApiSecret"] = FakeLastFm.Secret,
            });
            configuration.AddJsonFile(SettingsPath, optional: true, reloadOnChange: true);
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new ReviewFixtures.OneClientFactory(LastFm));
            services.RemoveAll<SettingsFileWriter>();
            services.AddSingleton(new SettingsFileWriter(SettingsPath));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_directory, true); } catch { }
    }
}

/// <summary>
/// Last.fm's web service as far as Octo uses it. Refuses a wrong signature with error 13 as the
/// real one does, so every call a test sees was signed correctly.
/// </summary>
internal sealed class FakeLastFm : HttpMessageHandler
{
    public const string ApiKey = "0123456789abcdef0123456789abcdef";
    public const string Secret = "s3cr3t";

    private readonly object _lock = new();
    private readonly List<Dictionary<string, string>> _calls = [];
    private readonly List<DateTime> _times = [];

    /// <summary>What the next calls fail with, in order: a Last.fm error code, or 0 for HTTP 503.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<int> Failures { get; } = new();

    /// <summary>Whether the admin has approved Octo on last.fm yet.</summary>
    public bool Approved { get; set; }

    /// <summary>Holds a call open until the returned task completes.</summary>
    public Func<Dictionary<string, string>, Task>? Hold { get; set; }

    public IReadOnlyList<Dictionary<string, string>> Calls { get { lock (_lock) return _calls.ToList(); } }
    public IReadOnlyList<DateTime> CallTimes { get { lock (_lock) return _times.ToList(); } }

    public IReadOnlyList<Dictionary<string, string>> CallsTo(string method) =>
        Calls.Where(call => call.GetValueOrDefault("method") == method).ToList();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        RespondAsync(request, cancellationToken);

    public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.RequestUri!.Host.Equals("ws.audioscrobbler.com", StringComparison.OrdinalIgnoreCase))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var form = System.Web.HttpUtility.ParseQueryString(body);
        var call = form.AllKeys.Where(key => key is not null).ToDictionary(key => key!, key => form[key] ?? "");
        lock (_lock)
        {
            _calls.Add(call);
            _times.Add(DateTime.UtcNow);
        }
        if (Hold is { } hold) await hold(call);

        if (call.GetValueOrDefault("api_sig") != LastFmScrobbleService.Sign(call, Secret))
            return Error(13, "Invalid method signature supplied");
        if (Failures.TryDequeue(out var failure))
            return failure == 0 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Error(failure, "Refused by the fixture");

        return call.GetValueOrDefault("method") switch
        {
            "auth.getToken" => Ok("""{"token":"tok-1"}"""),
            "auth.getSession" => Approved
                ? Ok("""{"session":{"name":"lfm-alice","key":"sk-alice","subscriber":0}}""")
                : Error(14, "Unauthorized Token - This token has not been authorized"),
            "track.scrobble" => Ok(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["scrobbles"] = new Dictionary<string, object>
                {
                    ["@attr"] = new { accepted = call.Keys.Count(key => key.StartsWith("artist[", StringComparison.Ordinal)), ignored = 0 },
                    ["scrobble"] = Array.Empty<object>(),
                },
            })),
            "track.updateNowPlaying" => Ok("""{"nowplaying":{"ignoredMessage":{"code":"0","#text":""}}}"""),
            _ => Error(3, "Invalid Method - No method with that name in this package"),
        };
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Last.fm answers a refusal with a 4xx and the error in the body.
    private static HttpResponseMessage Error(int code, string message) =>
        new(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = code, message }), Encoding.UTF8, "application/json"),
        };
}
