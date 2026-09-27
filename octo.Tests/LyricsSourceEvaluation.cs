using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Services.Lyrics;

namespace Octo.Tests;

/// <summary>
/// A live measurement of the lyrics sources against a sample of a real library, which decides
/// the default source order (docs/lyrics-source-eval.md). It makes real requests, so it only
/// runs when OCTO_LYRICS_EVAL names the sample, a JSON list of {title, artist, album,
/// duration}; otherwise it passes without doing anything. OCTO_LYRICS_EVAL_OUT is where the
/// per-song results go, and OCTO_LYRICS_EVAL_SOURCES can name fewer sources than all four.
///
/// Each service gets at most one request a second, the services run side by side, and every
/// request is a read. Latency is the time spent on the network for one lookup, the pacing
/// excluded, since the pacing is this harness being polite rather than a cost Octo has.
/// </summary>
public sealed class LyricsSourceEvaluation
{
    private sealed record Song(string title, string artist, string? album, int? duration);

    public sealed record Outcome(
        string Source, string Title, string Artist, int? Duration,
        string? NaiveTitle, string? NaiveArtist, double? NaiveDuration, bool? NaiveIsThisSong,
        string? Timing, bool Instrumental, bool Transient, int Attempts, string? CandidateId, string? Doubt,
        string? MatchedTitle, string? MatchedArtist, double? MatchedDuration, int Requests,
        double LatencyMs, IReadOnlyList<string> FirstLines);

    [Fact]
    public async Task MeasureSources()
    {
        var samplePath = Environment.GetEnvironmentVariable("OCTO_LYRICS_EVAL");
        if (string.IsNullOrWhiteSpace(samplePath) || !File.Exists(samplePath)) return;
        var outPath = Environment.GetEnvironmentVariable("OCTO_LYRICS_EVAL_OUT") ?? Path.ChangeExtension(samplePath, ".results.json");
        var limit = int.TryParse(Environment.GetEnvironmentVariable("OCTO_LYRICS_EVAL_LIMIT"), out var n) ? n : int.MaxValue;

        var songs = JsonSerializer.Deserialize<List<Song>>(await File.ReadAllTextAsync(samplePath))!.Take(limit).ToList();

        var kugou = new KugouLyricsSource(new PacedFactory(), NullLogger<KugouLyricsSource>.Instance);
        var lrclib = new LrclibLyricsSource(new PacedFactory(), NullLogger<LrclibLyricsSource>.Instance);
        var netease = new NeteaseLyricsSource(new PacedFactory(), NullLogger<NeteaseLyricsSource>.Instance);
        var ovh = new LyricsOvhLyricsSource(new PacedFactory(), NullLogger<LyricsOvhLyricsSource>.Instance);

        var runs = await Task.WhenAll(
            RunAsync("kugou", songs, () => kugou.CoolDownUntilUtc, async query =>
            {
                var search = await kugou.SearchAsync(query, CancellationToken.None);
                return (search, await kugou.FindInAsync(query, search, CancellationToken.None));
            }),
            RunAsync("lrclib", songs, () => lrclib.CoolDownUntilUtc, async query =>
            {
                var search = await lrclib.SearchAsync(query, CancellationToken.None);
                return (search, await lrclib.FindAsync(query, CancellationToken.None));
            }),
            RunAsync("netease", songs, () => DateTime.MinValue, async query =>
            {
                var search = await netease.SearchAsync(query, CancellationToken.None);
                return (search, await netease.FindInAsync(query, search, CancellationToken.None));
            }),
            // lyrics.ovh names nothing back, so its one answer is the song as asked.
            RunAsync("lyricsovh", songs, () => DateTime.MinValue, async query =>
                (LyricsSearch.Empty, await ovh.FindAsync(query, CancellationToken.None))));

        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(runs.SelectMany(run => run),
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    /// <summary>A lookup that could not be answered is tried twice more, after whatever wait the
    /// service asked for (at least 3 s, at most a minute), and counted as an error either way.</summary>
    private static async Task<List<Outcome>> RunAsync(string source, List<Song> songs, Func<DateTime> coolDown,
        Func<LyricsQuery, Task<(LyricsSearch Search, LyricsLookup Lookup)>> lookUp)
    {
        var outcomes = new List<Outcome>();
        if (!(Environment.GetEnvironmentVariable("OCTO_LYRICS_EVAL_SOURCES") ?? source).Split(',').Contains(source)) return outcomes;
        foreach (var song in songs)
        {
            var query = new LyricsQuery(song.artist, LyricsText.QueryTitle(song.title, song.artist), song.album, song.duration);
            LyricsSearch search = LyricsSearch.Failed;
            LyricsLookup lookup = LyricsLookup.Failed;
            var attempts = 0;
            while (attempts < 3)
            {
                attempts++;
                PacedFactory.Network.Value = new StrongBox();
                try
                {
                    (search, lookup) = await lookUp(query);
                }
                catch (Exception)
                {
                    (search, lookup) = (LyricsSearch.Failed, LyricsLookup.Failed);
                }
                if (!lookup.Transient) break;
                var wait = coolDown() - DateTime.UtcNow;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 3, 60)));
            }
            var naive = search.Candidates.FirstOrDefault();
            var result = lookup.Result;
            var matched = result?.CandidateId is { } chosen
                ? search.Candidates.FirstOrDefault(candidate => candidate.CandidateId == chosen)
                : null;
            outcomes.Add(new Outcome(source, song.title, song.artist, song.duration,
                naive?.Title, naive?.Artist, naive?.DurationSeconds,
                naive is null ? null : LyricsIdentity.SameSong(query.Title, query.Artist, naive.Title, naive.Artist,
                        naive.Artist.Split(['、', ',', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    && LyricsIdentity.LengthFits(query.DurationSeconds, naive.DurationSeconds),
                result?.Timing.ToString(), result?.Instrumental == true, lookup.Transient, attempts, result?.CandidateId, result?.Doubt,
                matched?.Title, matched?.Artist, matched?.DurationSeconds, PacedFactory.Network.Value!.Requests,
                PacedFactory.Network.Value!.Milliseconds,
                result is null ? [] : LyricsText.Preview(result, 3)));
        }
        return outcomes;
    }

    private sealed class StrongBox
    {
        public double Milliseconds;
        public int Requests;
    }

    /// <summary>One request a second per service, with the time on the network added up for
    /// the lookup in progress.</summary>
    private sealed class PacedFactory : IHttpClientFactory
    {
        public static readonly AsyncLocal<StrongBox?> Network = new();
        private readonly PacedHandler _handler = new() { InnerHandler = new HttpClientHandler { UseCookies = false } };

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Octo-lyrics-evaluation/1.0 (+https://github.com/winters27/octo)");
            return client;
        }

        private sealed class PacedHandler : DelegatingHandler
        {
            private readonly SemaphoreSlim _gate = new(1, 1);
            private DateTime _last = DateTime.MinValue;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                await _gate.WaitAsync(ct);
                try
                {
                    var wait = _last + TimeSpan.FromSeconds(1) - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                    _last = DateTime.UtcNow;
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        var response = await base.SendAsync(request, ct);
                        await response.Content.LoadIntoBufferAsync(ct);
                        return response;
                    }
                    finally
                    {
                        if (Network.Value is { } box)
                        {
                            box.Milliseconds += clock.Elapsed.TotalMilliseconds;
                            box.Requests++;
                        }
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
    }
}
