using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Octo.Services.Lyrics;

/// <summary>
/// One timed word, as a character range [From, To) in its line's Text. The range takes the
/// space after the word too, except at the end of the line, which is how enhanced LRC and the
/// OpenSubsonic cues both place a word. EndMs is null when nothing says when the word ends.
/// </summary>
public sealed record LyricWord(long StartMs, long? EndMs, int From, int To);

public sealed record LyricLine(long StartMs, string Text)
{
    /// <summary>Empty when the line is timed as a whole.</summary>
    public IReadOnlyList<LyricWord> Words { get; init; } = [];

    /// <summary>When the last word ends, when anything says so.</summary>
    public long? EndMs { get; init; }
}

internal static class LyricsText
{
    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    /// <summary>A word's time inside a line, &lt;01:23.45&gt;, as enhanced LRC writes it.</summary>
    private static readonly Regex WordTag = new(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled);

    /// <summary>"label: value", where the label is at most three short words.</summary>
    private static readonly Regex CreditLabel = new(@"^\s*([^\s:：]{1,16}(?:\s[^\s:：]{1,16}){0,2})\s*[:：]", RegexOptions.Compiled);

    private static readonly Regex FeatureSegment = new(
        @"\s*[\(\[]\s*(feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Contributor roles NetEase and KuGou credit at the top of a lyric. Chinese has no
    /// word boundaries, so these match anywhere in the label.</summary>
    private static readonly string[] CjkCredits =
    [
        "作词", "作詞", "作曲", "编曲", "編曲", "制作", "製作", "监制", "監製", "出品", "混音", "母带", "母帶",
        "录音", "錄音", "和声", "和聲", "吉他", "贝斯", "貝斯", "鼓", "弦乐", "弦樂", "版权", "版權", "统筹", "統籌",
        "企划", "企劃", "发行", "發行",
    ];

    /// <summary>The rights notices KuGou puts over a lyric ("TME owns the copyright of this
    /// translation", "not to be covered without permission"). No colon, so matched by phrase,
    /// and only near the start, where a notice sits and a lyric line saying so would not.</summary>
    private static readonly string[] CjkNotices = ["著作权", "著作權", "未经", "未經", "不得翻唱", "版权所有", "版權所有"];

    /// <summary>The Latin ones match only as whole words, so "Stop: ..." is never taken for "OP".</summary>
    private static readonly Regex LatinCredit = new(
        @"\b(op|sp|lyrics|lyricist|composers?|composed|arrangers?|arranged|producers?|produced|writers?|written|mixed|mastered|recorded|vocals|engineers?|engineered|publishers?|published|samples?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Labels that are credits only near the top: "Artist: X" and "Track: Y" open a
    /// lyric some sites keep, and a sung line may start with either word later on.</summary>
    private static readonly Regex EarlyLatinCredit = new(@"^\s*(artist|album|track|title)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SectionHeading = new(@"^\[[^\]]*\]$", RegexOptions.Compiled);

    /// <summary>Whether a "label: value" line's label names a contributor role.</summary>
    public static bool IsCreditLabel(string name) =>
        CjkCredits.Any(word => name.Contains(word, StringComparison.Ordinal)) || LatinCredit.IsMatch(name);

    public static bool HasTimestamps(string? text) => text is not null && TimeTag.IsMatch(text);

    /// <summary>Whether a lyric times its words, not only its lines.</summary>
    public static bool HasWordTags(string? text) => text is not null && WordTag.IsMatch(text);

    /// <summary>The title a lyrics service indexes: no featured-artist credit and no upload
    /// noise, but a "(Live)" or "(Remix)" kept, because those have their own lyrics.</summary>
    public static string QueryTitle(string title, string artist) =>
        FeatureSegment.Replace(Octo.Services.Common.PathHelper.FileTitle(title, artist), "").Trim();

    /// <summary>
    /// Timed lines, sorted, with metadata tags such as [ar:] and [offset:] skipped. A line with
    /// &lt;mm:ss.xx&gt; word tags gets its words: each tag starts the text up to the next one, a
    /// tag with nothing after it ends the word before it, and the tags never reach the text.
    /// A line sung at several times carries its words to each, moved by the same amount.
    /// </summary>
    public static IReadOnlyList<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var tags = TimeTag.Matches(raw);
            if (tags.Count == 0) continue;
            var first = Millis(tags[0]);
            var line = ParseWords(first, TimeTag.Replace(raw, ""));
            foreach (Match tag in tags)
            {
                var shift = Millis(tag) - first;
                lines.Add(shift == 0 ? line : Shift(line, shift));
            }
        }
        return lines.OrderBy(line => line.StartMs).ToList();
    }

    private static LyricLine ParseWords(long start, string body)
    {
        var stamps = WordTag.Matches(body);
        if (stamps.Count == 0) return new LyricLine(start, body.Trim());

        // Each piece is the text after a stamp, up to the next one. Text before the first stamp
        // starts with the line.
        var pieces = new List<(long At, string Text)>();
        var lead = body[..stamps[0].Index];
        if (!string.IsNullOrWhiteSpace(lead)) pieces.Add((start, lead));
        for (var index = 0; index < stamps.Count; index++)
        {
            var from = stamps[index].Index + stamps[index].Length;
            var to = index + 1 < stamps.Count ? stamps[index + 1].Index : body.Length;
            pieces.Add((Millis(stamps[index]), body[from..to]));
        }

        var text = new StringBuilder();
        var words = new List<LyricWord>();
        for (var index = 0; index < pieces.Count; index++)
        {
            var (at, piece) = pieces[index];
            if (piece.Length == 0) continue;
            var from = text.Length;
            text.Append(piece);
            if (string.IsNullOrWhiteSpace(piece)) continue;
            long? end = index + 1 < pieces.Count ? pieces[index + 1].At : null;
            words.Add(new LyricWord(at, end, from, text.Length));
        }

        // Spaces around the line go, and the words move with the text.
        var full = text.ToString();
        var cut = full.Length - full.TrimStart().Length;
        var trimmed = full.Trim();
        var placed = words
            .Select(word => word with
            {
                From = Math.Clamp(word.From - cut, 0, trimmed.Length),
                To = Math.Clamp(word.To - cut, 0, trimmed.Length),
            })
            .Where(word => word.To > word.From)
            .ToList();
        return new LyricLine(start, trimmed) { Words = placed, EndMs = placed.LastOrDefault()?.EndMs };
    }

    private static LyricLine Shift(LyricLine line, long by) => line with
    {
        StartMs = line.StartMs + by,
        EndMs = line.EndMs + by,
        Words = line.Words.Select(word => word with { StartMs = word.StartMs + by, EndMs = word.EndMs + by }).ToList(),
    };

    private static long Millis(Match tag)
    {
        var ms = long.Parse(tag.Groups[1].Value) * 60_000 + long.Parse(tag.Groups[2].Value) * 1000;
        if (!tag.Groups[3].Success) return ms;
        var fraction = tag.Groups[3].Value;
        return ms + fraction.Length switch
        {
            1 => long.Parse(fraction) * 100,
            2 => long.Parse(fraction) * 10,
            _ => long.Parse(fraction[..3]),
        };
    }

    /// <summary>
    /// Lines as LRC: [mm:ss.xx] before each, and when a line has words, &lt;mm:ss.xx&gt; before
    /// each word and one after the last when its end is known. The standard line tags stay, so
    /// a player that knows nothing of word timing still shows every line at its time.
    /// </summary>
    public static string WriteLrc(IEnumerable<LyricLine> lines)
    {
        var lrc = new StringBuilder();
        foreach (var line in lines)
        {
            lrc.Append('[').Append(Stamp(line.StartMs)).Append(']');
            if (line.Words.Count == 0)
            {
                lrc.Append(line.Text).Append('\n');
                continue;
            }

            lrc.Append(line.Text[..line.Words[0].From]);
            for (var index = 0; index < line.Words.Count; index++)
            {
                var word = line.Words[index];
                var to = index + 1 < line.Words.Count ? line.Words[index + 1].From : line.Text.Length;
                lrc.Append('<').Append(Stamp(word.StartMs)).Append('>').Append(line.Text[word.From..Math.Max(word.From, to)]);
            }
            if (line.Words[^1].EndMs is { } end) lrc.Append('<').Append(Stamp(end)).Append('>');
            lrc.Append('\n');
        }
        return lrc.ToString().TrimEnd('\n');
    }

    /// <summary>mm:ss.xx, in hundredths as most players write it; minutes run past 99.</summary>
    private static string Stamp(long ms)
    {
        ms = Math.Max(0, ms);
        return string.Create(CultureInfo.InvariantCulture,
            $"{ms / 60_000:00}:{ms / 1000 % 60:00}.{ms % 1000 / 10:00}");
    }

    /// <summary>A line's text with every word tag taken out, for a client that asked for lines.</summary>
    public static string StripWordTags(string text) => WordTag.Replace(text, "");

    /// <summary>The words as untimed text, one line each, for the legacy getLyrics call.</summary>
    public static string PlainText(LyricsResult result) => result.HasSynced
        ? string.Join('\n', ParseLrc(result.Synced!).Select(line => line.Text))
        : (result.Plain ?? "").Replace("\r\n", "\n").Trim();

    /// <summary>A lyric's first lines with words in them, for a choice list.</summary>
    public static IReadOnlyList<string> Preview(LyricsResult result, int count = 2)
    {
        var lines = result.HasSynced
            ? ParseLrc(result.Synced!).Select(line => line.Text)
            : (result.Plain ?? "").Replace("\r\n", "\n").Split('\n').Select(line => line.Trim());
        return lines.Where(line => line.Length > 0).Take(count).ToList();
    }

    /// <summary>
    /// NetEase and KuGou open a lyric with contributor credits, "[00:00.000] 作词 : 周杰伦" and a
    /// dozen more, sometimes as JSON lines, sometimes a mid-song "出品：...". Written through
    /// unfiltered, every such result starts with text that is not the song (#52). A line goes
    /// when it is JSON, when it is a rights notice in the first half minute, or when it reads
    /// "label: value" and either its label is a known credit word, or it sits in the first
    /// fifteen seconds under a label that is not Latin text. An English lyric that happens to
    /// contain a colon keeps its place.
    /// </summary>
    public static string StripCredits(string lrc)
    {
        var kept = new List<string>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var text = StripWordTags(TimeTag.Replace(raw, "")).Trim();
            if (text.StartsWith("{\"t\":", StringComparison.Ordinal)) continue;

            var first = TimeTag.Match(raw);
            var at = first.Success ? Millis(first) : long.MaxValue;
            if (at < 30_000 && CjkNotices.Any(notice => text.Contains(notice, StringComparison.Ordinal))) continue;

            // A section heading, "[Intro: Drake]", is never sung.
            if (SectionHeading.IsMatch(text)) continue;

            var label = CreditLabel.Match(text);
            if (label.Success)
            {
                var name = label.Groups[1].Value;
                var nonLatin = name.Any(ch => ch > 0x2E80);
                if (IsCreditLabel(name) || (at < 15_000 && nonLatin)
                    || (at < 30_000 && EarlyLatinCredit.IsMatch(name))) continue;
            }
            kept.Add(raw);
        }
        return string.Join('\n', kept).Trim();
    }

    /// <summary>Kept under its first name for the NetEase source and its tests.</summary>
    public static string StripNeteaseCredits(string lrc) => StripCredits(lrc);
}
