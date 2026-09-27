using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Octo.Services.Lyrics;

/// <summary>
/// The timed lines of a Lyricsfile, the YAML document LRCLIB answers with beside its LRC. It
/// is the only place LRCLIB keeps word timing (an entry's hasWordSync), under lines[].words[].
///
/// Deliberately narrow rather than a YAML library for one field: it reads the lines block and
/// its text, start_ms, end_ms and words, and nothing else. Anything it does not expect (a
/// block scalar in a line, a key it cannot place) makes it give up with null, and the caller
/// falls back to the LRC, which is never worse than before.
/// </summary>
internal static class LyricsfileReader
{
    private static readonly Regex Item = new(@"^(\s*)-\s+(\w+):\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex Field = new(@"^(\s*)(\w+):\s*(.*)$", RegexOptions.Compiled);

    private sealed class Entry
    {
        public string? Text;
        public long? Start;
        public long? End;
        public List<Entry> Words = [];
    }

    public static IReadOnlyList<LyricLine>? ReadLines(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        try
        {
            var entries = Parse(yaml.Replace("\r\n", "\n").Split('\n'));
            if (entries is null || entries.Count == 0) return null;
            var lines = entries.Where(entry => entry.Start is not null && entry.Text is not null)
                .Select(ToLine).ToList();
            return lines.Count == 0 ? null : lines;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<Entry>? Parse(string[] rows)
    {
        var entries = new List<Entry>();
        var inLines = false;
        int? lineIndent = null, wordIndent = null;
        var inWords = false;
        Entry? line = null, word = null;

        foreach (var raw in rows)
        {
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#')) continue;
            var indent = raw.Length - raw.TrimStart().Length;

            if (indent == 0)
            {
                // A top-level key: the lines block starts or ends here.
                inLines = raw.StartsWith("lines:", StringComparison.Ordinal);
                if (inLines && raw.Trim() is var header && header != "lines:" && header != "lines: []") return null;
                inWords = false;
                continue;
            }
            if (!inLines) continue;

            var item = Item.Match(raw);
            if (item.Success)
            {
                var itemIndent = item.Groups[1].Length;
                lineIndent ??= itemIndent;
                if (itemIndent == lineIndent)
                {
                    line = new Entry();
                    entries.Add(line);
                    word = null;
                    inWords = false;
                    if (!Set(line, item.Groups[2].Value, item.Groups[3].Value)) return null;
                }
                else if (inWords && line is not null && itemIndent > lineIndent)
                {
                    wordIndent ??= itemIndent;
                    word = new Entry();
                    line.Words.Add(word);
                    if (!Set(word, item.Groups[2].Value, item.Groups[3].Value)) return null;
                }
                else return null;
                continue;
            }

            var field = Field.Match(raw);
            if (!field.Success || line is null) return null;
            var fieldIndent = field.Groups[1].Length;
            var key = field.Groups[2].Value;
            if (key == "words" && fieldIndent > lineIndent)
            {
                inWords = true;
                word = null;
                continue;
            }
            // A word's fields sit deeper than its "- "; anything shallower belongs to the line,
            // and ends its words.
            Entry target = line;
            if (inWords && word is not null && wordIndent is { } deeper && fieldIndent > deeper) target = word;
            else inWords = false;
            if (!Set(target, key, field.Groups[3].Value)) return null;
        }
        return entries;
    }

    private static bool Set(Entry entry, string key, string value)
    {
        switch (key)
        {
            case "text":
                var text = Scalar(value);
                if (text is null) return false;
                entry.Text = text;
                return true;
            case "start_ms":
                if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) return false;
                entry.Start = start;
                return true;
            case "end_ms":
                if (value.Trim() is "" or "null" or "~") return true;
                if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)) return false;
                entry.End = end;
                return true;
            default:
                // A key this reader does not use (a voice, say) is skipped, never guessed at.
                return true;
        }
    }

    /// <summary>A one-line scalar: 'single' ('' is a quote), "double" (backslash escapes), or
    /// plain. Null for anything else, a block scalar included.</summary>
    internal static string? Scalar(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return "";
        if (trimmed[0] == '\'')
        {
            if (trimmed.Length < 2 || trimmed[^1] != '\'') return null;
            return trimmed[1..^1].Replace("''", "'");
        }
        if (trimmed[0] == '"')
        {
            if (trimmed.Length < 2 || trimmed[^1] != '"') return null;
            return Regex.Unescape(trimmed[1..^1]);
        }
        if (trimmed[0] is '|' or '>') return null;
        var comment = trimmed.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? trimmed[..comment].TrimEnd() : trimmed;
    }

    /// <summary>A line with its words placed in its text, in order. A word that cannot be
    /// found where the one before it ended leaves the line timed as a whole.</summary>
    private static LyricLine ToLine(Entry entry)
    {
        var text = entry.Text!.Trim();
        var words = new List<LyricWord>();
        var cursor = 0;
        foreach (var word in entry.Words)
        {
            var piece = (word.Text ?? "").Trim();
            if (piece.Length == 0 || word.Start is not { } start) continue;
            var at = text.IndexOf(piece, cursor, StringComparison.Ordinal);
            if (at < 0) { words.Clear(); break; }
            var to = at + piece.Length;
            while (to < text.Length && char.IsWhiteSpace(text[to])) to++;
            words.Add(new LyricWord(start, word.End, at, to));
            cursor = to;
        }

        // A word's end is the next one's start when the file does not say.
        for (var index = 0; index < words.Count - 1; index++)
            if (words[index].EndMs is null) words[index] = words[index] with { EndMs = words[index + 1].StartMs };

        return new LyricLine(entry.Start!.Value, text)
        {
            Words = words,
            EndMs = words.LastOrDefault()?.EndMs ?? entry.End,
        };
    }
}
