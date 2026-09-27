using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Octo.Services.Lyrics;

/// <summary>
/// KuGou Music: the one source here with word timing for most songs, in its own KRC format, so
/// it drives the Octo app's word-by-word lyrics. Its API is undocumented, keyless and
/// unlicensed, the same standing as NetEase's, and it can change or vanish without notice; so
/// every failure is a quiet miss or a cool-down, never an exception, and dropping "kugou" from
/// LYRICS_SOURCES switches it off.
///
/// Three public endpoints, all unsigned:
///   lyrics.kugou.com/search     lyric entries for "artist - title" (and a length, or a song hash)
///   mobileservice.kugou.com     the song catalogue, asked only when the lyric search has no
///                               entry for this song, for the song's hash
///   lyrics.kugou.com/download   one entry's lyric: KRC (fmt=krc) or LRC (fmt=lrc), base64
/// KRC is "krc1", then zlib data XORed with a fixed 16-byte key, giving text with a
/// [lineStart,lineLength] tag per line and a &lt;offset,length,0&gt; tag per word.
/// </summary>
public sealed class KugouLyricsSource : ILyricsSource
{
    public const string ClientName = "kugou";

    private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);

    /// <summary>After this many failures in a row KuGou is left alone for a while, so a dead or
    /// changed API costs playback nothing but one quick check every few minutes.</summary>
    private const int FailuresBeforeBreak = 5;
    private static readonly TimeSpan BreakLength = TimeSpan.FromMinutes(5);

    /// <summary>The key every KRC file is XORed with, public since 2012.</summary>
    private static readonly byte[] KrcKey = [0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69];

    private static readonly Regex KrcLine = new(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled);
    /// <summary>A word's offset can be negative (a credit timed before its line).</summary>
    private static readonly Regex KrcWord = new(@"<(-?\d+),(-?\d+),-?\d+>", RegexOptions.Compiled);

    /// <summary>"Singer：", KuGou's own way of saying who sings next, always with a full-width
    /// colon, which an English lyric never uses.</summary>
    private static readonly Regex Speaker = new(@"^([^：]{1,60})：\s*", RegexOptions.Compiled);

    /// <summary>What KuGou shows for a track with no words: "pure music, please enjoy".</summary>
    private const string InstrumentalMark = "纯音乐";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<KugouLyricsSource> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;
    private DateTime _coolDownUntilUtc = DateTime.MinValue;
    private int _failures;

    public KugouLyricsSource(IHttpClientFactory http, ILogger<KugouLyricsSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Key => "kugou";

    /// <summary>Until when the service asked to be left alone, for a caller that would rather
    /// wait than give up.</summary>
    internal DateTime CoolDownUntilUtc => _coolDownUntilUtc;

    public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct) =>
        await FindInAsync(query, await SearchAsync(query, ct), ct);

    /// <summary>The lyrics of the best entry in a search already made that is this song.</summary>
    internal async Task<LyricsLookup> FindInAsync(LyricsQuery query, LyricsSearch search, CancellationToken ct)
    {
        if (search.Transient && search.Candidates.Count == 0) return LyricsLookup.Failed;

        var transient = search.Transient;
        foreach (var candidate in search.Candidates
                     .Where(candidate => IsThisSong(candidate, query))
                     .OrderBy(candidate => Distance(candidate, query))
                     .Take(2))
        {
            var lookup = await FetchAsync(candidate.Id, query, ct);
            if (lookup.Transient) { transient = true; continue; }
            if (lookup.Result is not { } result) continue;
            return new LyricsLookup(result with
            {
                CandidateId = candidate.CandidateId,
                Doubt = LyricsIdentity.Doubt(query.DurationSeconds, candidate.DurationSeconds),
            }, false);
        }
        return transient ? LyricsLookup.Failed : LyricsLookup.Miss;
    }

    internal static bool IsThisSong(LyricsCandidate candidate, LyricsQuery query) =>
        LyricsIdentity.SameSong(query.Title, query.Artist, candidate.Title, candidate.Artist, Credits(candidate.Artist))
        && LyricsIdentity.LengthFits(query.DurationSeconds, candidate.DurationSeconds);

    private static double Distance(LyricsCandidate candidate, LyricsQuery query) =>
        query.DurationSeconds is > 0 && candidate.DurationSeconds is > 0
            ? Math.Abs(candidate.DurationSeconds.Value - query.DurationSeconds.Value) : 0;

    /// <summary>KuGou joins a song's artists with "、".</summary>
    private static IReadOnlyList<string> Credits(string artist) =>
        artist.Split(['、', ',', '&', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The lyric entries for "artist - title". When none of them is this song, the song
    /// catalogue is asked too, and the lyric entries for each catalogue song that IS this song
    /// are added under that song's name and album.
    /// </summary>
    public async Task<LyricsSearch> SearchAsync(LyricsQuery query, CancellationToken ct)
    {
        var keyword = $"{query.Artist} - {query.Title}";
        var url = "https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=" + Uri.EscapeDataString(keyword);
        if (query.DurationSeconds is > 0 and <= 3600) url += $"&duration={query.DurationSeconds * 1000}";

        var direct = await GetJsonAsync(url, ct);
        if (direct.Transient) return LyricsSearch.Failed;
        var candidates = new List<LyricsCandidate>();
        using (direct.Json) candidates.AddRange(ReadLyricEntries(direct.Json?.RootElement, null));
        if (candidates.Any(candidate => IsThisSong(candidate, query))) return new LyricsSearch(Distinct(candidates), false);

        var catalogue = await GetJsonAsync("https://mobileservice.kugou.com/api/v3/search/song?format=json&page=1&pagesize=10&showtype=1&keyword="
            + Uri.EscapeDataString($"{query.Artist} {query.Title}"), ct);
        if (catalogue.Transient) return new LyricsSearch(Distinct(candidates), true);

        List<Song> songs;
        using (catalogue.Json) songs = ReadSongs(catalogue.Json?.RootElement);
        // The catalogue names some artists its own way ("Ye (侃爷)" for Kanye West), so a song
        // with the right title and length is asked about even when its artist reads otherwise;
        // its lyric entries then carry the lyric's own credit, and the identity check decides.
        var transient = false;
        var found = new List<LyricsCandidate>();
        foreach (var song in songs
                     .Where(song => LyricsIdentity.SameTitle(query.Title, song.Title)
                         && LyricsIdentity.LengthFits(query.DurationSeconds, song.Seconds))
                     .OrderByDescending(song => LyricsIdentity.SameArtist(query.Artist, song.Artist, Credits(song.Artist)))
                     .Take(2))
        {
            var byHash = await GetJsonAsync("https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&hash="
                + Uri.EscapeDataString(song.Hash) + (song.Seconds > 0 ? $"&duration={song.Seconds * 1000}" : ""), ct);
            if (byHash.Transient) { transient = true; continue; }
            // A few per song, so the entries of the likelier song (named artist first) are not
            // crowded out of the list by the other's.
            var named = LyricsIdentity.SameArtist(query.Artist, song.Artist, Credits(song.Artist));
            using (byHash.Json) found.AddRange(ReadLyricEntries(byHash.Json?.RootElement, song, named).Take(4));
        }
        // In front: these may be the song, the entries before them were not, and the list is cut
        // to a dozen.
        candidates.InsertRange(0, found);
        return new LyricsSearch(Distinct(candidates), transient);
    }

    private static List<LyricsCandidate> Distinct(List<LyricsCandidate> candidates) =>
        candidates.DistinctBy(candidate => candidate.Id).Take(12).ToList();

    private sealed record Song(string Hash, string Title, string Artist, string? Album, int Seconds);

    private static List<Song> ReadSongs(JsonElement? root)
    {
        if (root is not { } element || !element.TryGetProperty("data", out var data)
            || !data.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array) return [];
        return info.EnumerateArray()
            .Select(song => new Song(Str(song, "hash") ?? "", Str(song, "songname") ?? "", Str(song, "singername") ?? "",
                Str(song, "album_name"), song.TryGetProperty("duration", out var d) && d.TryGetInt32(out var s) ? s : 0))
            .Where(song => song.Hash.Length > 0)
            .ToList();
    }

    /// <summary>Lyric entries, titled after the catalogue song they were found through when
    /// there is one, and credited to its artist when that artist is the one asked for;
    /// otherwise to the lyric's own singer, so the identity check still judges the artist.</summary>
    private static IEnumerable<LyricsCandidate> ReadLyricEntries(JsonElement? root, Song? song, bool songArtist = true)
    {
        if (root is not { } element || !element.TryGetProperty("candidates", out var list)
            || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in list.EnumerateArray())
        {
            var id = Str(entry, "id");
            var key = Str(entry, "accesskey");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key)) continue;
            int? seconds = entry.TryGetProperty("duration", out var d) && d.TryGetInt64(out var ms) && ms > 0
                ? (int)Math.Round(ms / 1000.0) : null;
            yield return new LyricsCandidate("kugou", $"{id}.{key}",
                song?.Title ?? Str(entry, "song") ?? "",
                (songArtist ? song?.Artist : null) ?? Str(entry, "singer") ?? "",
                song?.Album, seconds ?? (song?.Seconds > 0 ? song.Seconds : null));
        }
    }

    public Task<LyricsLookup> FetchAsync(string id, CancellationToken ct) => FetchAsync(id, null, ct);

    /// <summary>
    /// One entry's lyric: its KRC, word-timed, or its LRC when it has no KRC. The song, when
    /// known, lets a first line that only names it ("Artist - Title") be dropped.
    /// </summary>
    internal async Task<LyricsLookup> FetchAsync(string id, LyricsQuery? song, CancellationToken ct)
    {
        var dot = id.IndexOf('.');
        if (dot <= 0 || dot == id.Length - 1) return LyricsLookup.Miss;
        var download = $"https://lyrics.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(id[..dot])}"
            + $"&accesskey={Uri.EscapeDataString(id[(dot + 1)..])}&charset=utf8&fmt=";

        var krc = await GetJsonAsync(download + "krc", ct);
        if (krc.Transient) return LyricsLookup.Failed;
        string? text;
        using (krc.Json) text = Content(krc.Json?.RootElement);
        var lines = text is null ? null : ParseKrc(text);

        if (lines is null || lines.Count == 0)
        {
            var lrc = await GetJsonAsync(download + "lrc", ct);
            if (lrc.Transient) return LyricsLookup.Failed;
            using (lrc.Json) text = Content(lrc.Json?.RootElement);
            if (string.IsNullOrWhiteSpace(text)) return LyricsLookup.Miss;
            text = System.Net.WebUtility.HtmlDecode(text);
            lines = LyricsText.HasTimestamps(text) ? LyricsText.ParseLrc(text).ToList() : null;
            if (lines is null) return Plain(text);
        }

        return ToResult(Clean(lines, song));
    }

    private static LyricsLookup Plain(string text)
    {
        var clean = text.Replace("\r\n", "\n").Trim();
        if (clean.Contains(InstrumentalMark, StringComparison.Ordinal) && clean.Length < 40)
            return new LyricsLookup(new LyricsResult("KuGou", null, null, true), false);
        return new LyricsLookup(new LyricsResult("KuGou", null, clean, false), false);
    }

    private static LyricsLookup ToResult(IReadOnlyList<LyricLine> lines)
    {
        var sung = lines.Where(line => line.Text.Length > 0).ToList();
        if (sung.Count == 0) return LyricsLookup.Miss;
        if (sung.Count <= 2 && sung.All(line => line.Text.Contains(InstrumentalMark, StringComparison.Ordinal)))
            return new LyricsLookup(new LyricsResult("KuGou", null, null, true), false);
        var synced = LyricsText.StripCredits(LyricsText.WriteLrc(lines));
        return string.IsNullOrWhiteSpace(synced)
            ? LyricsLookup.Miss
            : new LyricsLookup(new LyricsResult("KuGou", synced, null, false), false);
    }

    /// <summary>
    /// KuGou's own furniture, taken out of the lines:
    /// - a first line that only names the song, "Artist - Title" or "Title (Explicit) - Artist";
    /// - who sings next, "Drake：" as a line of its own (dropped) or ahead of the words
    ///   ("Kanye West：Real friends", where the words stay). A label that is a contributor role
    ///   ("Written by：Drake") takes its whole line with it.
    /// </summary>
    internal static IReadOnlyList<LyricLine> Clean(IReadOnlyList<LyricLine> lines, LyricsQuery? song)
    {
        var kept = new List<LyricLine>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            // The name line sits among the first few, before or after the credits; it is only
            // looked for until the first line with words in it is kept.
            if (song is not null && index < 6 && !kept.Any(sung => sung.Text.Length > 0) && NamesSong(line.Text, song)) continue;

            var speaker = Speaker.Match(line.Text);
            if (!speaker.Success)
            {
                kept.Add(line);
                continue;
            }
            if (LyricsText.IsCreditLabel(speaker.Groups[1].Value)) continue;
            var cut = speaker.Length;
            if (cut >= line.Text.Length) continue;
            var words = line.Words
                .Where(word => word.To > cut)
                .Select(word => word with { From = Math.Max(word.From, cut) - cut, To = word.To - cut })
                .Where(word => word.To > word.From)
                .ToList();
            kept.Add(line with { Text = line.Text[cut..], Words = words });
        }
        return kept;
    }

    private static bool NamesSong(string text, LyricsQuery song)
    {
        var dash = text.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash <= 0) return false;
        var before = LyricsIdentity.TitleKey(text[..dash]);
        var after = text[(dash + 3)..];
        var title = LyricsIdentity.TitleKey(song.Title);
        if (title.Length == 0) return false;
        // "Artist - Title", or "Title - whoever", the title whole
        if (LyricsIdentity.TitleKey(after) == title || before == title) return true;
        // "Title (Explicit) - Artist/Guest"
        return before.StartsWith(title, StringComparison.Ordinal)
            && after.Split(['/', '、', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(name => LyricsIdentity.LeadArtist(name) == LyricsIdentity.LeadArtist(song.Artist));
    }

    /// <summary>The base64 lyric in a download answer, decoded: KRC when it carries the KRC
    /// header, otherwise the text as it came (an LRC or plain lyric).</summary>
    private static string? Content(JsonElement? root)
    {
        if (root is not { } element || Str(element, "content") is not { Length: > 0 } content) return null;
        try
        {
            var bytes = Convert.FromBase64String(content);
            return DecodeKrc(bytes) ?? Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>KRC to text: past the "krc1" header, XOR with the key, then inflate. Null when
    /// the bytes are not a KRC file or do not inflate.</summary>
    internal static string? DecodeKrc(byte[] data)
    {
        if (data.Length <= 4 || data[0] != 'k' || data[1] != 'r' || data[2] != 'c' || data[3] != '1') return null;
        var body = new byte[data.Length - 4];
        for (var index = 0; index < body.Length; index++) body[index] = (byte)(data[index + 4] ^ KrcKey[index % KrcKey.Length]);
        try
        {
            using var inflate = new ZLibStream(new MemoryStream(body), CompressionMode.Decompress);
            using var reader = new StreamReader(inflate, Encoding.UTF8);
            return reader.ReadToEnd().TrimStart('﻿');
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// KRC text as timed lines with timed words. A line is "[start,length]" in milliseconds from
    /// the song's start, and each word "&lt;offset,length,0&gt;text" with the offset from the
    /// line's start. Tag lines such as [ti:] and [language:] are skipped. Words are joined as
    /// KuGou spaced them, with runs of spaces made one.
    /// </summary>
    internal static List<LyricLine> ParseKrc(string krc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in krc.Replace("\r\n", "\n").Split('\n'))
        {
            var match = KrcLine.Match(raw.Trim());
            if (!match.Success) continue;
            var start = long.Parse(match.Groups[1].Value);
            var length = long.Parse(match.Groups[2].Value);
            var body = match.Groups[3].Value;

            var tags = KrcWord.Matches(body);
            if (tags.Count == 0)
            {
                lines.Add(new LyricLine(start, System.Net.WebUtility.HtmlDecode(body).Trim()) { EndMs = length > 0 ? start + length : null });
                continue;
            }

            var text = new StringBuilder();
            var words = new List<LyricWord>();
            for (var index = 0; index < tags.Count; index++)
            {
                var from = tags[index].Index + tags[index].Length;
                var to = index + 1 < tags.Count ? tags[index + 1].Index : body.Length;
                var piece = Regex.Replace(System.Net.WebUtility.HtmlDecode(body[from..to]), @"\s+", " ");
                if (text.Length == 0 || text[^1] == ' ') piece = piece.TrimStart();
                if (piece.Length == 0) continue;

                var wordStart = start + long.Parse(tags[index].Groups[1].Value);
                var wordLength = long.Parse(tags[index].Groups[2].Value);
                var at = text.Length;
                text.Append(piece);
                if (!string.IsNullOrWhiteSpace(piece))
                    words.Add(new LyricWord(wordStart, wordLength > 0 ? wordStart + wordLength : null, at, text.Length));
            }

            var line = text.ToString().TrimEnd();
            var placed = words
                .Select(word => word with { To = Math.Min(word.To, line.Length) })
                .Where(word => word.To > word.From)
                .ToList();
            lines.Add(new LyricLine(start, line)
            {
                Words = placed,
                EndMs = placed.LastOrDefault()?.EndMs ?? (length > 0 ? start + length : null),
            });
        }
        return lines.OrderBy(line => line.StartMs).ToList();
    }

    private sealed record Answer(JsonDocument? Json, bool Transient) : IDisposable
    {
        public void Dispose() => Json?.Dispose();
    }

    /// <summary>
    /// One request, one at a time with a short gap. A 429 or 503 cools KuGou down for as long
    /// as it asks; five failures in a row (timeouts, refused connections, pages that are not
    /// JSON) open a five-minute break, during which every lookup is an instant "not now".
    /// </summary>
    private async Task<Answer> GetJsonAsync(string url, CancellationToken ct)
    {
        if (DateTime.UtcNow < _coolDownUntilUtc) return new(null, true);

        await _gate.WaitAsync(ct);
        try
        {
            var gap = _lastCallUtc + MinimumGap - DateTime.UtcNow;
            if (gap > TimeSpan.Zero) await Task.Delay(gap, ct);
            _lastCallUtc = DateTime.UtcNow;

            using var response = await _http.CreateClient(ClientName).GetAsync(url, ct);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? DefaultCooldown;
                _coolDownUntilUtc = DateTime.UtcNow + wait;
                _logger.LogInformation("KuGou asked Octo to wait {Seconds:0}s", wait.TotalSeconds);
                return new(null, true);
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return Succeeded(new(null, false));
            if (!response.IsSuccessStatusCode) return Failure($"HTTP {(int)response.StatusCode}");

            var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            return Succeeded(new(json, false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Answer Succeeded(Answer answer)
    {
        Interlocked.Exchange(ref _failures, 0);
        return answer;
    }

    private Answer Failure(string why)
    {
        _logger.LogDebug("KuGou request failed: {M}", why);
        if (Interlocked.Increment(ref _failures) >= FailuresBeforeBreak)
        {
            Interlocked.Exchange(ref _failures, 0);
            _coolDownUntilUtc = DateTime.UtcNow + BreakLength;
            _logger.LogWarning("KuGou failed {N} times in a row; not asking it for {Minutes} minutes",
                FailuresBeforeBreak, BreakLength.TotalMinutes);
        }
        return new(null, true);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
