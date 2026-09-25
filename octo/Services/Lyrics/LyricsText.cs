using System.Text.RegularExpressions;

namespace Octo.Services.Lyrics;

public sealed record LyricLine(long StartMs, string Text);

internal static class LyricsText
{
    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    /// <summary>"label: value", where the label is at most three short words.</summary>
    private static readonly Regex CreditLabel = new(@"^\s*([^\s:：]{1,16}(?:\s[^\s:：]{1,16}){0,2})\s*[:：]", RegexOptions.Compiled);

    private static readonly Regex FeatureSegment = new(
        @"\s*[\(\[]\s*(feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Contributor roles NetEase credits at the top of a lyric. Chinese has no word
    /// boundaries, so these match anywhere in the label.</summary>
    private static readonly string[] CjkCredits =
    [
        "作词", "作詞", "作曲", "编曲", "編曲", "制作", "製作", "监制", "監製", "出品", "混音", "母带", "母帶",
        "录音", "錄音", "和声", "和聲", "吉他", "贝斯", "貝斯", "鼓", "弦乐", "弦樂", "版权", "版權", "统筹", "統籌",
        "企划", "企劃", "发行", "發行",
    ];

    /// <summary>The Latin ones match only as whole words, so "Stop: ..." is never taken for "OP".</summary>
    private static readonly Regex LatinCredit = new(
        @"\b(op|sp|lyrics|lyricist|composer|composed|arranger|arranged|producer|produced|written|mixed|mastered|recorded|vocals)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool HasTimestamps(string? text) => text is not null && TimeTag.IsMatch(text);

    /// <summary>The title a lyrics service indexes: no featured-artist credit and no upload
    /// noise, but a "(Live)" or "(Remix)" kept, because those have their own lyrics.</summary>
    public static string QueryTitle(string title, string artist) =>
        FeatureSegment.Replace(Octo.Services.Common.PathHelper.FileTitle(title, artist), "").Trim();

    /// <summary>Timed lines, sorted, with metadata tags such as [ar:] and [offset:] skipped.</summary>
    public static IReadOnlyList<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var tags = TimeTag.Matches(raw);
            if (tags.Count == 0) continue;
            var text = TimeTag.Replace(raw, "").Trim();
            foreach (Match tag in tags)
            {
                var ms = long.Parse(tag.Groups[1].Value) * 60_000 + long.Parse(tag.Groups[2].Value) * 1000;
                if (tag.Groups[3].Success)
                {
                    var fraction = tag.Groups[3].Value;
                    ms += fraction.Length switch
                    {
                        1 => long.Parse(fraction) * 100,
                        2 => long.Parse(fraction) * 10,
                        _ => long.Parse(fraction[..3]),
                    };
                }
                lines.Add(new LyricLine(ms, text));
            }
        }
        return lines.OrderBy(line => line.StartMs).ToList();
    }

    /// <summary>
    /// NetEase opens a lyric with contributor credits, "[00:00.000] 作词 : 周杰伦" and a dozen more,
    /// sometimes as JSON lines, sometimes a mid-song "出品：...". Written through unfiltered, every
    /// NetEase result starts with text that is not the song (#52). A line goes when it is JSON, or
    /// when it reads "label: value" and either its label is a known credit word, or it sits in
    /// the first fifteen seconds under a label that is not Latin text. An English lyric that
    /// happens to contain a colon keeps its place.
    /// </summary>
    public static string StripNeteaseCredits(string lrc)
    {
        var kept = new List<string>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var text = TimeTag.Replace(raw, "").Trim();
            if (text.StartsWith("{\"t\":", StringComparison.Ordinal)) continue;

            var label = CreditLabel.Match(text);
            if (label.Success)
            {
                var name = label.Groups[1].Value;
                var isCredit = CjkCredits.Any(word => name.Contains(word, StringComparison.Ordinal))
                    || LatinCredit.IsMatch(name);
                var first = TimeTag.Match(raw);
                var early = first.Success && long.Parse(first.Groups[1].Value) == 0
                    && long.Parse(first.Groups[2].Value) < 15;
                var nonLatin = name.Any(ch => ch > 0x2E80);
                if (isCredit || (early && nonLatin)) continue;
            }
            kept.Add(raw);
        }
        return string.Join('\n', kept).Trim();
    }
}
