using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Octo.Models.Settings;
using Octo.Services.Lyrics;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// Word-timed lyrics end to end: KuGou's KRC decoded and parsed, enhanced LRC read and written,
/// the strict rule for which entry is the song, the source order with "prefer word-timed", and
/// the OpenSubsonic cues exactly as the Octo app's ServerLyrics.kt reads them.
/// </summary>
public sealed class LyricsWordTimingTests
{
    // ---- KRC --------------------------------------------------------------------------------

    private static readonly byte[] KrcKey = [0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69];

    /// <summary>A KRC file made the way KuGou makes them: UTF-8 with a byte order mark, zlib,
    /// XORed with the key, behind "krc1".</summary>
    internal static byte[] EncodeKrc(string text)
    {
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(Encoding.UTF8.GetBytes("﻿" + text));
        var body = packed.ToArray();
        for (var index = 0; index < body.Length; index++) body[index] ^= KrcKey[index % KrcKey.Length];
        return [.. "krc1"u8.ToArray(), .. body];
    }

    private const string Krc = """
        [ti:stronger]
        [ar:kanye west]
        [offset:0]
        [language:eyJjb250ZW50IjpbXX0=]
        [456,1724]<0,486,0>Work <486,215,0>it  <701,261,0>make <962,178,0>it
        [1880,3397]<0,584,0>Makes <584,402,0>us <986,352,0>harder
        """;

    [Fact]
    public void Krc_DecodesBackToItsText()
    {
        var decoded = KugouLyricsSource.DecodeKrc(EncodeKrc(Krc));
        Assert.Equal(Krc, decoded);
    }

    [Fact]
    public void Krc_NotAKrcFile_IsNull()
    {
        Assert.Null(KugouLyricsSource.DecodeKrc(Encoding.UTF8.GetBytes("[00:01.00]plain lrc")));
        Assert.Null(KugouLyricsSource.DecodeKrc([.. "krc1"u8.ToArray(), 1, 2, 3, 4]));
    }

    [Fact]
    public void Krc_ParsesLinesAndWordsWithTheirTimes()
    {
        var lines = KugouLyricsSource.ParseKrc(Krc);

        Assert.Equal(2, lines.Count);
        var first = lines[0];
        Assert.Equal(456, first.StartMs);
        // Two spaces after "it" become one, and the line ends without one.
        Assert.Equal("Work it make it", first.Text);
        Assert.Equal([456L, 942, 1157, 1418], first.Words.Select(word => word.StartMs));
        Assert.Equal([942L, 1157, 1418, 1596], first.Words.Select(word => word.EndMs!.Value));
        Assert.Equal(["Work ", "it ", "make ", "it"], first.Words.Select(word => first.Text[word.From..word.To]));
        Assert.Equal(1596, first.EndMs);
    }

    [Fact]
    public void Krc_BecomesEnhancedLrcThatReadsBackTheSame()
    {
        var lrc = LyricsText.WriteLrc(KugouLyricsSource.ParseKrc(Krc));

        Assert.StartsWith("[00:00.45]<00:00.45>Work <00:00.94>it <00:01.15>make <00:01.41>it<00:01.59>", lrc);
        var back = LyricsText.ParseLrc(lrc);
        Assert.Equal("Work it make it", back[0].Text);
        Assert.Equal([450L, 940, 1150, 1410], back[0].Words.Select(word => word.StartMs));
        Assert.Equal(1590, back[0].EndMs);
    }

    // ---- Enhanced LRC -----------------------------------------------------------------------

    [Fact]
    public void EnhancedLrc_WordTagsBecomeWordsAndNeverReachTheText()
    {
        var lines = LyricsText.ParseLrc("[ar:x]\n[00:01.00]<00:01.00>Hello <00:01.50>world<00:02.00>\n[00:03.00]plain line");

        Assert.Equal("Hello world", lines[0].Text);
        Assert.Equal([(1000L, (long?)1500L, 0, 6), (1500L, 2000L, 6, 11)],
            lines[0].Words.Select(word => (word.StartMs, word.EndMs, word.From, word.To)));
        Assert.Equal(2000, lines[0].EndMs);
        Assert.Empty(lines[1].Words);
        Assert.Equal("plain line", lines[1].Text);
    }

    [Fact]
    public void EnhancedLrc_TextBeforeTheFirstTagStartsWithTheLine()
    {
        var line = LyricsText.ParseLrc("[00:05.00]Oh <00:05.40>yeah")[0];
        Assert.Equal("Oh yeah", line.Text);
        Assert.Equal([5000L, 5400], line.Words.Select(word => word.StartMs));
    }

    [Fact]
    public void EnhancedLrc_ALineSungTwiceCarriesItsWordsToBoth()
    {
        var lines = LyricsText.ParseLrc("[00:10.00][00:30.00]<00:10.00>la <00:10.50>la");
        Assert.Equal([10_000L, 10_500], lines[0].Words.Select(word => word.StartMs));
        Assert.Equal([30_000L, 30_500], lines[1].Words.Select(word => word.StartMs));
    }

    [Theory]
    [InlineData("Café naïve 日本")]
    [InlineData("🎵 la la")]
    [InlineData("It's  spaced   out")]
    public void EnhancedLrc_WriteThenReadIsTheSame(string text)
    {
        var pieces = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join(' ', pieces);
        var words = new List<LyricWord>();
        var at = 0;
        for (var index = 0; index < pieces.Length; index++)
        {
            var to = at + pieces[index].Length + (index + 1 < pieces.Length ? 1 : 0);
            words.Add(new LyricWord(61_230 + index * 400, 61_230 + (index + 1) * 400, at, to));
            at = to;
        }
        var line = new LyricLine(61_230, joined) { Words = words, EndMs = words[^1].EndMs };

        var back = LyricsText.ParseLrc(LyricsText.WriteLrc([line]))[0];

        Assert.Equal(joined, back.Text);
        Assert.Equal(words.Select(word => (word.From, word.To)), back.Words.Select(word => (word.From, word.To)));
        Assert.Equal(words.Select(word => word.StartMs), back.Words.Select(word => word.StartMs));
        Assert.Equal(line.EndMs, back.EndMs);
    }

    [Fact]
    public void EnhancedLrc_HasWordTagsOnlyWhenWordsAreTimed()
    {
        Assert.True(LyricsText.HasWordTags("[00:01.00]<00:01.00>a"));
        Assert.False(LyricsText.HasWordTags("[00:01.00]a <b> c"));
        Assert.Equal(LyricsTiming.Word, new LyricsResult("x", "[00:01.00]<00:01.00>a", null, false).Timing);
        Assert.Equal(LyricsTiming.Line, new LyricsResult("x", "[00:01.00]a", null, false).Timing);
        Assert.Equal(LyricsTiming.Plain, new LyricsResult("x", null, "a", false).Timing);
    }

    // ---- LRCLIB word sync -------------------------------------------------------------------

    [Fact]
    public void Lyricsfile_WordsBecomeWordTiming()
    {
        const string yaml = """
            version: '1.0'
            metadata:
              title: 'Song'
              artist: 'Someone'
            lines:
              - text: 'Hello there, it''s me'
                start_ms: 1000
                end_ms: 3000
                words:
                  - text: 'Hello '
                    start_ms: 1000
                    end_ms: 1400
                  - text: "there, "
                    start_ms: 1400
                  - text: 'it''s '
                    start_ms: 2000
                    end_ms: 2300
                  - text: 'me'
                    start_ms: 2300
                    end_ms: 3000
              - text: 'Second line'
                start_ms: 4000
            plain: |
              Hello there, it's me
            """;

        var lines = LyricsfileReader.ReadLines(yaml)!;

        Assert.Equal(2, lines.Count);
        Assert.Equal("Hello there, it's me", lines[0].Text);
        Assert.Equal([1000L, 1400, 2000, 2300], lines[0].Words.Select(word => word.StartMs));
        Assert.Equal(2000, lines[0].Words[1].EndMs);
        Assert.Equal(["Hello ", "there, ", "it's ", "me"], lines[0].Words.Select(word => lines[0].Text[word.From..word.To]));
        Assert.Empty(lines[1].Words);
    }

    [Fact]
    public void Lyricsfile_UnexpectedShape_IsNullSoTheLrcIsUsed()
    {
        Assert.Null(LyricsfileReader.ReadLines("version: '1.0'\nlines:\n  - text: |\n      block\n    start_ms: 1"));
        Assert.Null(LyricsfileReader.ReadLines(null));
    }

    [Fact]
    public void Lrclib_HasWordSync_UsesTheLyricsfileWords()
    {
        var row = JsonDocument.Parse("""
            {"id":7,"trackName":"Song","artistName":"Someone","duration":200,"hasWordSync":true,
             "syncedLyrics":"[00:01.00]Hello me",
             "lyricsfile":"version: '1.0'\nlines:\n  - text: 'Hello me'\n    start_ms: 1000\n    words:\n      - text: 'Hello '\n        start_ms: 1000\n      - text: 'me'\n        start_ms: 1500\n        end_ms: 1900\n"}
            """).RootElement;

        var result = LrclibLyricsSource.Parse(row, null);

        Assert.Equal(LyricsTiming.Word, result.Timing);
        Assert.Equal("lrclib:7", result.CandidateId);
    }

    // ---- Identity ---------------------------------------------------------------------------

    [Theory]
    [InlineData("$UICIDE", "$uicideboy$", "$UICIDE", "$uicideboy$", true)]
    [InlineData("$UICIDE", "$uicideboy$", "Ultimate $uicide", "$uicideboy$", false)]
    [InlineData("$UICIDE", "$uicideboy$", "Black $uicide", "$uicideboy$", false)]
    [InlineData("Headlines", "Drake", "Headlines (Explicit)", "Drake", true)]
    [InlineData("Nightcall", "Kavinsky", "Nightcall (Breakbot Remix)", "Kavinsky", false)]
    [InlineData("Nightcall (Live)", "Kavinsky", "Nightcall", "Kavinsky", false)]
    [InlineData("Teardrop", "Massive Attack", "Teardrop - Remastered 2006", "Massive Attack", true)]
    [InlineData("Shotta Flow", "NLE Choppa", "Shotta Flow 4", "NLE Choppa", false)]
    [InlineData("Peek A Boo", "Lil Yachty", "Peek a Boo", "Lil Yachty、Migos", true)]
    [InlineData("Work", "Rihanna", "Work (feat. Drake)", "Rihanna feat. Drake", true)]
    [InlineData("Stronger", "Kanye West", "Stronger", "Ye (侃爷)", false)]
    [InlineData("Unsteady", "X Ambassadors", "Unsteady", "X Ambassadors", true)]
    [InlineData("Stronger", "Kanye West", "Stronger", "Kelly Clarkson", false)]
    public void Identity_SameTitleSameKindSameArtist(string title, string artist, string gotTitle, string gotArtist, bool same)
        => Assert.Equal(same, LyricsIdentity.SameSong(title, artist, gotTitle, gotArtist,
            gotArtist.Split('、', StringSplitOptions.TrimEntries)));

    [Fact]
    public void Identity_AnotherCreditedArtistCounts()
        => Assert.True(LyricsIdentity.SameArtist("Daft Punk", "Ye (侃爷)、Daft Punk", ["Ye (侃爷)", "Daft Punk"]));

    [Theory]
    [InlineData(169, 170.0, true)]
    [InlineData(169, 173.0, false)]
    [InlineData(null, 400.0, true)]
    public void Identity_LengthWithinThreeSeconds(int? want, double got, bool fits)
        => Assert.Equal(fits, LyricsIdentity.LengthFits(want, got));

    // ---- KuGou source over fake HTTP --------------------------------------------------------

    private sealed class FakeKugou
    {
        public List<string> Calls { get; } = [];
        public string SearchBody { get; set; } = """{"status":200,"candidates":[]}""";
        public string CatalogueBody { get; set; } = """{"status":1,"data":{"info":[]}}""";
        public string HashSearchBody { get; set; } = """{"status":200,"candidates":[]}""";
        public byte[]? Krc { get; set; }
        public string? Lrc { get; set; }
        public HttpStatusCode? FailWith { get; set; }

        public IHttpClientFactory Factory()
        {
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
                {
                    var uri = request.RequestUri!;
                    Calls.Add(uri.ToString());
                    if (FailWith is { } status) return new HttpResponseMessage(status);
                    var body = uri.Host switch
                    {
                        "mobileservice.kugou.com" => CatalogueBody,
                        _ when uri.AbsolutePath == "/search" && uri.Query.Contains("hash=") => HashSearchBody,
                        _ when uri.AbsolutePath == "/search" => SearchBody,
                        _ when uri.Query.Contains("fmt=krc") => Krc is null ? """{"status":404,"content":""}"""
                            : $$"""{"status":200,"content":"{{Convert.ToBase64String(Krc)}}","fmt":"krc","contenttype":0}""",
                        _ when uri.Query.Contains("fmt=lrc") => Lrc is null ? """{"status":404,"content":""}"""
                            : $$"""{"status":200,"content":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(Lrc))}}","fmt":"lrc"}""",
                        _ => "{}",
                    };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
                });
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler.Object));
            return factory.Object;
        }
    }

    private static string Candidate(string id, string song, string singer, long ms) =>
        $$"""{"id":"{{id}}","accesskey":"KEY{{id}}","song":"{{song}}","singer":"{{singer}}","duration":{{ms}},"krctype":2,"score":60}""";

    [Fact]
    public async Task Kugou_OnlyAnotherSongOfTheSameLength_IsAMissAndNothingIsDownloaded()
    {
        var kugou = new FakeKugou
        {
            SearchBody = $$"""{"status":200,"candidates":[{{Candidate("1", "Ultimate $uicide", "$uicideboy$", 170_000)}},{{Candidate("2", "Black $uicide", "$uicideboy$", 170_500)}}]}""",
            CatalogueBody = """{"status":1,"data":{"info":[{"hash":"h1","songname":"Ultimate $uicide (Explicit)","singername":"$uicideboy$","duration":170}]}}""",
            Krc = EncodeKrc(Krc),
        };
        var source = new KugouLyricsSource(kugou.Factory(), NullLogger<KugouLyricsSource>.Instance);

        var lookup = await source.FindAsync(new LyricsQuery("$uicideboy$", "$UICIDE", null, 169), CancellationToken.None);

        Assert.Null(lookup.Result);
        Assert.False(lookup.Transient);
        Assert.DoesNotContain(kugou.Calls, call => call.Contains("/download"));
    }

    [Fact]
    public async Task Kugou_TheSong_GivesWordTimedLyricsWithoutItsCreditsOrTitleLine()
    {
        var krc = """
            [0,1000]<0,500,0>Kanye West <500,500,0>- Stronger
            [1000,900]<0,900,0>Producer：Daft Punk
            [456000,1724]<0,486,0>Work <486,215,0>it
            """;
        var kugou = new FakeKugou
        {
            SearchBody = $$"""{"status":200,"candidates":[{{Candidate("9", "Stronger", "Kanye West", 312_006)}}]}""",
            Krc = EncodeKrc(krc),
        };
        var source = new KugouLyricsSource(kugou.Factory(), NullLogger<KugouLyricsSource>.Instance);

        var lookup = await source.FindAsync(new LyricsQuery("Kanye West", "Stronger", "Graduation", 311), CancellationToken.None);

        var result = lookup.Result!;
        Assert.Equal(LyricsTiming.Word, result.Timing);
        Assert.Equal("kugou:9.KEY9", result.CandidateId);
        Assert.Null(result.Doubt);
        var lines = LyricsText.ParseLrc(result.Synced!);
        Assert.Equal(["Work it"], lines.Select(line => line.Text));
    }

    /// <summary>What the 300-song evaluation found KuGou putting in its lyrics: who sings next,
    /// HTML entities, a credit timed before its line, and a title line naming the song.</summary>
    [Fact]
    public void Kugou_Clean_TakesOutSpeakersEntitiesCreditsAndTheTitleLine()
    {
        var krc = """
            [0,500]<0,500,0>Sunlight On Your Skin (Explicit) - Lil Peep/iLoveMakonnen
            [100,300]<-100,100,0>Written by：Drake
            [500,500]<0,500,0>Lil Peep：
            [1000,2000]<0,400,0>Kanye West：<400,600,0>Real <1000,500,0>friends
            [4000,1000]<0,500,0>I&apos;m <500,500,0>cruising
            """;
        var lines = KugouLyricsSource.Clean(KugouLyricsSource.ParseKrc(krc),
            new LyricsQuery("Lil Peep", "Sunlight On Your Skin", null, 200));
        var lrc = LyricsText.StripCredits(LyricsText.WriteLrc(lines));
        var back = LyricsText.ParseLrc(lrc);

        Assert.Equal(["Real friends", "I'm cruising"], back.Select(line => line.Text));
        Assert.Equal(["Real ", "friends"], back[0].Words.Select(word => back[0].Text[word.From..word.To]));
        Assert.Equal(1400, back[0].Words[0].StartMs);
    }

    [Theory]
    [InlineData("Can't Tell Me Nothing - Ye", "Kanye West", "Can't Tell Me Nothing")]
    [InlineData("Best Friend (Explicit) - Yelawolf (亚拉狼)/Eminem", "Yelawolf • Eminem", "Best Friend")]
    public void Kugou_Clean_FindsTheNameLineAfterTheCredits(string nameLine, string artist, string title)
    {
        var lines = KugouLyricsSource.ParseKrc($"""
            [0,10]<-100,100,0>Lyrics by：Someone
            [10,10]<0,10,0>{nameLine}
            [5000,500]<0,500,0>sung
            """);

        var kept = KugouLyricsSource.Clean(lines, new LyricsQuery(artist, title, null, 200));

        Assert.Equal(["sung"], LyricsText.ParseLrc(LyricsText.StripCredits(LyricsText.WriteLrc(kept))).Select(line => line.Text));
    }

    [Theory]
    [InlineData("[00:01.00][Intro: Drake & PARTYNEXTDOOR]")]
    [InlineData("[00:01.00]Producers：Frank Dukes/Boi-1da")]
    [InlineData("[00:01.00]Writers：Kanye West")]
    [InlineData("[00:01.00]Artist: Skillet")]
    public void StripCredits_DropsHeadingsAndCreditsTheEvaluationFound(string line)
        => Assert.Equal("[00:40.00]sung", LyricsText.StripCredits(line + "\n[00:40.00]sung"));

    [Fact]
    public async Task Kugou_NoKrc_TakesItsLrc()
    {
        var kugou = new FakeKugou
        {
            SearchBody = $$"""{"status":200,"candidates":[{{Candidate("5", "Song", "Someone", 200_000)}}]}""",
            Lrc = "[00:12.00]a line\n[00:15.00]another",
        };
        var source = new KugouLyricsSource(kugou.Factory(), NullLogger<KugouLyricsSource>.Instance);

        var lookup = await source.FindAsync(new LyricsQuery("Someone", "Song", null, 200), CancellationToken.None);

        Assert.Equal(LyricsTiming.Line, lookup.Result!.Timing);
    }

    [Fact]
    public async Task Kugou_NoLyricEntry_FindsTheSongInTheCatalogueAndAsksByItsHash()
    {
        var kugou = new FakeKugou
        {
            CatalogueBody = """{"status":1,"data":{"info":[{"hash":"abc","songname":"Headlines (Explicit)","singername":"Drake","album_name":"Take Care","duration":236}]}}""",
            HashSearchBody = $$"""{"status":200,"candidates":[{{Candidate("3", "Headlines", "Drake", 235_000)}}]}""",
            Krc = EncodeKrc(Krc),
        };
        var source = new KugouLyricsSource(kugou.Factory(), NullLogger<KugouLyricsSource>.Instance);

        var lookup = await source.FindAsync(new LyricsQuery("Drake", "Headlines", null, 235), CancellationToken.None);

        Assert.Equal(LyricsTiming.Word, lookup.Result!.Timing);
        Assert.Contains(kugou.Calls, call => call.Contains("hash=abc"));
    }

    [Fact]
    public async Task Kugou_FailingOver_StopsAskingForAWhile()
    {
        var kugou = new FakeKugou { FailWith = HttpStatusCode.InternalServerError };
        var source = new KugouLyricsSource(kugou.Factory(), NullLogger<KugouLyricsSource>.Instance);
        var query = new LyricsQuery("A", "B", null, 100);

        for (var i = 0; i < 5; i++) Assert.True((await source.FindAsync(query, CancellationToken.None)).Transient);
        var before = kugou.Calls.Count;
        var after = await source.FindAsync(query, CancellationToken.None);

        Assert.True(after.Transient);
        Assert.Equal(before, kugou.Calls.Count);
    }

    [Fact]
    public async Task Kugou_RateLimited_CoolsDownWithoutAnotherRequest()
    {
        var kugou = new FakeKugou { FailWith = HttpStatusCode.TooManyRequests };
        var source = new KugouLyricsSource(kugou.Factory(), NullLogger<KugouLyricsSource>.Instance);

        Assert.True((await source.FindAsync(new LyricsQuery("A", "B", null, 100), CancellationToken.None)).Transient);
        Assert.True((await source.FindAsync(new LyricsQuery("C", "D", null, 100), CancellationToken.None)).Transient);
        Assert.Single(kugou.Calls);
    }

    // ---- Source order and "prefer word-timed" -----------------------------------------------

    private sealed class FakeSource(string key, Func<LyricsLookup> answer) : ILyricsSource
    {
        public string Key => key;
        public int Calls { get; private set; }
        public Func<CancellationToken, Task>? Before { get; init; }

        public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct)
        {
            Calls++;
            if (Before is not null) await Before(ct);
            return answer();
        }
    }

    private static LyricsLookup Found(string source, string? synced, string? plain = null) =>
        new(new LyricsResult(source, synced, plain, false), false);

    private static LyricsService Service(string order, bool preferWords, params ILyricsSource[] sources) =>
        new(sources, TestOptions.Monitor(new MetadataSettings { LyricsSources = order, PreferWordTimedLyrics = preferWords }),
            NullLogger<LyricsService>.Instance);

    private static readonly LyricsQuery Query = new("Artist", "Song", null, 200);

    [Fact]
    public async Task Order_PreferWordTimed_ALaterWordTimedAnswerBeatsAnEarlierLineTimedOne()
    {
        var lines = new FakeSource("lrclib", () => Found("LRCLIB", "[00:01.00]line"));
        var words = new FakeSource("kugou", () => Found("KuGou", "[00:01.00]<00:01.00>word"));

        var lookup = await Service("lrclib,kugou", true, lines, words).FindAsync(Query, CancellationToken.None);

        Assert.Equal("KuGou", lookup.Result!.Source);
    }

    [Fact]
    public async Task Order_PreferWordTimedOff_TheFirstTimedAnswerWins()
    {
        var lines = new FakeSource("lrclib", () => Found("LRCLIB", "[00:01.00]line"));
        var words = new FakeSource("kugou", () => Found("KuGou", "[00:01.00]<00:01.00>word"));

        var lookup = await Service("lrclib,kugou", false, lines, words).FindAsync(Query, CancellationToken.None);

        Assert.Equal("LRCLIB", lookup.Result!.Source);
        Assert.Equal(0, words.Calls);
    }

    [Fact]
    public async Task Order_WordTimedFirst_NothingLaterIsAsked()
    {
        var words = new FakeSource("kugou", () => Found("KuGou", "[00:01.00]<00:01.00>word"));
        var lines = new FakeSource("lrclib", () => Found("LRCLIB", "[00:01.00]line"));

        var lookup = await Service("kugou,lrclib", true, words, lines).FindAsync(Query, CancellationToken.None);

        Assert.Equal("KuGou", lookup.Result!.Source);
        Assert.Equal(0, lines.Calls);
    }

    [Fact]
    public async Task Order_NoWordTimingAnywhere_KeepsTheLineTimedOne()
    {
        var lines = new FakeSource("kugou", () => Found("KuGou", "[00:01.00]line"));
        var plain = new FakeSource("lrclib", () => Found("LRCLIB", null, "words"));

        var lookup = await Service("kugou,lrclib", true, lines, plain).FindAsync(Query, CancellationToken.None);

        Assert.Equal("KuGou", lookup.Result!.Source);
    }

    [Fact]
    public async Task Order_KugouLeftOut_IsNeverAsked()
    {
        var kugou = new FakeSource("kugou", () => Found("KuGou", "[00:01.00]<00:01.00>word"));
        var lrclib = new FakeSource("lrclib", () => LyricsLookup.Miss);

        await Service("lrclib", true, kugou, lrclib).FindAsync(Query, CancellationToken.None);

        Assert.Equal(0, kugou.Calls);
    }

    [Fact]
    public async Task Order_OutOfTime_ReturnsTheBestFoundSoFar()
    {
        using var budget = new CancellationTokenSource();
        var lines = new FakeSource("lrclib", () => Found("LRCLIB", "[00:01.00]line"));
        var slow = new FakeSource("kugou", () => LyricsLookup.Failed)
        {
            Before = async ct =>
            {
                budget.Cancel();
                await Task.Yield();
            },
        };

        var lookup = await Service("lrclib,kugou", true, lines, slow).FindAsync(Query, budget.Token);

        Assert.Equal("LRCLIB", lookup.Result!.Source);
    }

    // ---- The structured lyrics the app parses -----------------------------------------------

    private static readonly Octo.Services.Subsonic.SubsonicResponseBuilder Builder =
        new(new ExternalIdRegistry(), Microsoft.Extensions.Options.Options.Create(new SubsonicSettings()));

    private static JsonElement Structured(LyricsResult result, bool enhanced)
    {
        var json = (JsonResult)Builder.CreateLyricsListResponse("json", result, "A", "T", enhanced);
        var text = JsonSerializer.Serialize(json.Value);
        return JsonDocument.Parse(text).RootElement.GetProperty("subsonic-response").GetProperty("lyricsList")
            .GetProperty("structuredLyrics")[0];
    }

    /// <summary>The app's own example: "Café naïve 日本", whose cues are bytes 0..5, 6..12,
    /// 13..15 and 16..18, both ends included.</summary>
    private static readonly LyricsResult Cafe = new("KuGou",
        "[00:01.00]<00:01.00>Café <00:01.50>naïve <00:02.50>日<00:03.00>本<00:03.50>", null, false);

    [Fact]
    public void Cues_UseInclusiveUtf8ByteOffsetsIntoTheCueLineValue()
    {
        var lyrics = Structured(Cafe, enhanced: true);

        Assert.Equal("main", lyrics.GetProperty("kind").GetString());
        var cueLine = lyrics.GetProperty("cueLine")[0];
        Assert.Equal(0, cueLine.GetProperty("index").GetInt32());
        Assert.Equal("Café naïve 日本", cueLine.GetProperty("value").GetString());
        Assert.Equal(1000, cueLine.GetProperty("start").GetInt64());
        Assert.Equal(3500, cueLine.GetProperty("end").GetInt64());
        var cues = cueLine.GetProperty("cue").EnumerateArray().ToList();
        Assert.Equal([(0, 5), (6, 12), (13, 15), (16, 18)],
            cues.Select(cue => (cue.GetProperty("byteStart").GetInt32(), cue.GetProperty("byteEnd").GetInt32())));
        Assert.Equal(["Café ", "naïve ", "日", "本"], cues.Select(cue => cue.GetProperty("value").GetString()));
        Assert.Equal([1000L, 1500, 2500, 3000], cues.Select(cue => cue.GetProperty("start").GetInt64()));
        Assert.Equal([1500L, 2500, 3000, 3500], cues.Select(cue => cue.GetProperty("end").GetInt64()));
    }

    [Fact]
    public void Cues_ReadByTheAppsRules_GiveTheWords()
    {
        var cueLine = Structured(Cafe, enhanced: true).GetProperty("cueLine")[0];
        var value = cueLine.GetProperty("value").GetString()!;

        var words = cueLine.GetProperty("cue").EnumerateArray()
            .Select(cue => AppCueWords.CharRange(value, cue.GetProperty("byteStart").GetInt32(), cue.GetProperty("byteEnd").GetInt32()))
            .ToList();

        Assert.All(words, range => Assert.NotNull(range));
        Assert.Equal([(0, 5), (5, 11), (11, 12), (12, 13)], words.Select(range => (range!.Value.From, range.Value.To)));
        Assert.Equal(["Café ", "naïve ", "日", "本"], words.Select(range => value[range!.Value.From..range.Value.To]));
    }

    [Fact]
    public void Cues_AnEmojiTakesFourBytesAndTwoChars()
    {
        var lyrics = Structured(new LyricsResult("x", "[00:00.00]<00:00.00>🎵 <00:00.50>la<00:00.90>", null, false), true);
        var cueLine = lyrics.GetProperty("cueLine")[0];
        var value = cueLine.GetProperty("value").GetString()!;
        var cues = cueLine.GetProperty("cue").EnumerateArray().ToList();

        Assert.Equal([(0, 4), (5, 6)], cues.Select(cue => (cue.GetProperty("byteStart").GetInt32(), cue.GetProperty("byteEnd").GetInt32())));
        Assert.Equal((0, 3), AppCueWords.CharRange(value, 0, 4));
    }

    /// <summary>A strict client that did not ask for word timing gets byte for byte what it
    /// got before this change: no kind, no cues, no word tags in the text.</summary>
    [Fact]
    public void NotEnhanced_IsExactlyTheOldShape()
    {
        var json = (JsonResult)Builder.CreateLyricsListResponse("json",
            new LyricsResult("LRCLIB", "[00:01.50]first\n[00:03.00]second", null, false), "A", "T");
        Assert.Equal(
            """{"subsonic-response":{"status":"ok","version":"1.16.1","lyricsList":{"structuredLyrics":[{"lang":"xxx","synced":true,"displayArtist":"A","displayTitle":"T","offset":0,"line":[{"start":1500,"value":"first"},{"start":3000,"value":"second"}]}]}}}""",
            JsonSerializer.Serialize(json.Value));

        var words = Structured(Cafe, enhanced: false);
        Assert.False(words.TryGetProperty("cueLine", out _));
        Assert.False(words.TryGetProperty("kind", out _));
        Assert.Equal("Café naïve 日本", words.GetProperty("line")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void NotEnhancedXml_HasNoCues_EnhancedXmlHasThem()
    {
        var plain = ((ContentResult)Builder.CreateLyricsListResponse("xml", Cafe, "A", "T")).Content!;
        var rich = ((ContentResult)Builder.CreateLyricsListResponse("xml", Cafe, "A", "T", enhanced: true)).Content!;

        Assert.DoesNotContain("cueLine", plain);
        Assert.Contains("<line start=\"1000\">Café naïve 日本</line>", plain);
        Assert.Contains("kind=\"main\"", rich);
        Assert.Contains("byteStart=\"6\" byteEnd=\"12\"", rich);
    }

    [Fact]
    public void LegacyGetLyrics_IsPlainTextWithoutTimes()
    {
        var json = (JsonResult)Builder.CreateLyricsResponse("json", Cafe, "A", "T");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json.Value));
        Assert.Equal("Café naïve 日本", doc.RootElement.GetProperty("subsonic-response").GetProperty("lyrics").GetProperty("value").GetString());
    }

    /// <summary>The Octo app's ServerLyrics.kt Utf8Positions, ported line for line, so these
    /// tests hold the server to what the app actually does with a cue.</summary>
    private static class AppCueWords
    {
        public static (int From, int To)? CharRange(string text, int byteStart, int byteEnd)
        {
            var starts = new List<int>();
            var index = 0;
            while (index < text.Length)
            {
                var point = char.ConvertToUtf32(text, index);
                var bytes = point < 0x80 ? 1 : point < 0x800 ? 2 : point < 0x10000 ? 3 : 4;
                for (var i = 0; i < bytes; i++) starts.Add(index);
                index += char.IsSurrogatePair(text, index) ? 2 : 1;
            }
            if (byteStart < 0 || byteEnd < byteStart || byteEnd >= starts.Count) return null;
            var from = starts[byteStart];
            var last = starts[byteEnd];
            return (from, last + (char.IsSurrogatePair(text, last) ? 2 : 1));
        }
    }
}
