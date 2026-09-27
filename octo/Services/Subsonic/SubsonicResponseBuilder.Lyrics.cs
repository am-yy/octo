using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Octo.Services.Lyrics;

namespace Octo.Services.Subsonic;

public partial class SubsonicResponseBuilder
{
    public const string LyricsExtension = "octoLyrics";
    public const int LyricsExtensionVersion = 1;

    /// <summary>
    /// OpenSubsonic getLyricsBySongId (#52). Synced lyrics become timed lines in milliseconds,
    /// plain lyrics untimed lines; nothing, or an instrumental, is an empty but ok list, which
    /// is what stops a client logging "data not found" on every play.
    ///
    /// Word timing goes out only to a client that asked for it with enhanced=true (songLyrics
    /// version 2), as the spec has it: a kind, and a cueLine per timed line whose cues place
    /// each word in the line by UTF-8 bytes, both ends included. A client that did not ask gets
    /// exactly the lines it always got, and word tags never reach the text.
    /// </summary>
    public IActionResult CreateLyricsListResponse(string format, LyricsResult? found,
        string artist, string title, bool enhanced = false)
    {
        IReadOnlyList<LyricLine> timed = found is { HasSynced: true } synced ? LyricsText.ParseLrc(synced.Synced!) : [];
        var lines = found switch
        {
            { HasSynced: true } => timed.Select(line => (Start: (long?)line.StartMs, Text: line.Text)).ToList(),
            { HasPlain: true } plain => plain.Plain!.Replace("\r\n", "\n").Split('\n')
                .Select(line => (Start: (long?)null, Text: line.Trim())).ToList(),
            _ => [],
        };
        var isSynced = found?.HasSynced == true;
        var cues = enhanced ? CueLines(timed) : [];

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var structured = new List<object>();
            if (lines.Count > 0)
            {
                var entry = new Dictionary<string, object>
                {
                    ["lang"] = "xxx",
                    ["synced"] = isSynced,
                    ["displayArtist"] = artist,
                    ["displayTitle"] = title,
                    ["offset"] = 0,
                    ["line"] = lines.Select(line => line.Start is { } start
                        ? new Dictionary<string, object> { ["start"] = start, ["value"] = line.Text }
                        : new Dictionary<string, object> { ["value"] = line.Text }).ToList(),
                };
                if (enhanced)
                {
                    entry["kind"] = "main";
                    if (cues.Count > 0) entry["cueLine"] = cues.Select(CueLineJson).ToList();
                }
                structured.Add(entry);
            }
            return CreateJsonResponse(new Dictionary<string, object>
            {
                ["status"] = "ok",
                ["version"] = SubsonicVersion,
                ["lyricsList"] = new Dictionary<string, object> { ["structuredLyrics"] = structured },
            });
        }

        var ns = XNamespace.Get(SubsonicNamespace);
        var list = new XElement(ns + "lyricsList");
        if (lines.Count > 0)
        {
            var element = new XElement(ns + "structuredLyrics",
                new XAttribute("lang", "xxx"), new XAttribute("synced", isSynced ? "true" : "false"),
                new XAttribute("displayArtist", artist), new XAttribute("displayTitle", title),
                new XAttribute("offset", 0),
                lines.Select(line => line.Start is { } start
                    ? new XElement(ns + "line", new XAttribute("start", start), line.Text)
                    : new XElement(ns + "line", line.Text)));
            if (enhanced)
            {
                element.Add(new XAttribute("kind", "main"));
                element.Add(cues.Select(cue => new XElement(ns + "cueLine",
                    new XAttribute("index", cue.Index), new XAttribute("start", cue.Line.StartMs),
                    cue.Line.EndMs is { } end ? new XAttribute("end", end) : null,
                    new XAttribute("value", cue.Line.Text),
                    cue.Words.Select(word => new XElement(ns + "cue",
                        new XAttribute("start", word.Start),
                        word.End is { } wordEnd ? new XAttribute("end", wordEnd) : null,
                        new XAttribute("byteStart", word.ByteStart), new XAttribute("byteEnd", word.ByteEnd),
                        word.Value)))));
            }
            list.Add(element);
        }
        var document = new XDocument(new XElement(ns + "subsonic-response",
            new XAttribute("status", "ok"), new XAttribute("version", SubsonicVersion), list));
        return new ContentResult { Content = document.ToString(), ContentType = "application/xml" };
    }

    internal sealed record CueWord(long Start, long? End, int ByteStart, int ByteEnd, string Value);

    internal sealed record CueLineOut(int Index, LyricLine Line, IReadOnlyList<CueWord> Words);

    /// <summary>
    /// One cue line for each line with timed words, matched to it by index. A cue's place is
    /// in UTF-8 bytes of the cue line's value, both ends included, so a client finds the word
    /// the same way whatever alphabet it is written in.
    /// </summary>
    internal static List<CueLineOut> CueLines(IReadOnlyList<LyricLine> lines)
    {
        var cueLines = new List<CueLineOut>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Words.Count == 0) continue;
            var words = line.Words
                .Where(word => word.From >= 0 && word.To <= line.Text.Length && word.To > word.From)
                .Select(word => new CueWord(word.StartMs, word.EndMs,
                    Encoding.UTF8.GetByteCount(line.Text.AsSpan(0, word.From)),
                    Encoding.UTF8.GetByteCount(line.Text.AsSpan(0, word.To)) - 1,
                    line.Text[word.From..word.To]))
                .ToList();
            if (words.Count > 0) cueLines.Add(new CueLineOut(index, line, words));
        }
        return cueLines;
    }

    private static Dictionary<string, object> CueLineJson(CueLineOut cue)
    {
        var json = new Dictionary<string, object>
        {
            ["index"] = cue.Index,
            ["start"] = cue.Line.StartMs,
            ["value"] = cue.Line.Text,
            ["cue"] = cue.Words.Select(word =>
            {
                var entry = new Dictionary<string, object>
                {
                    ["start"] = word.Start,
                    ["byteStart"] = word.ByteStart,
                    ["byteEnd"] = word.ByteEnd,
                    ["value"] = word.Value,
                };
                if (word.End is { } end) entry["end"] = end;
                return entry;
            }).ToList(),
        };
        if (cue.Line.EndMs is { } lineEnd) json["end"] = lineEnd;
        return json;
    }

    /// <summary>
    /// The legacy getLyrics answer: one block of untimed text, in the shape Navidrome gives, so
    /// a client that only knows artist and title still gets words. Nothing is an empty value.
    /// </summary>
    public IActionResult CreateLyricsResponse(string format, LyricsResult? found, string artist, string title)
    {
        var text = found is null || found.Instrumental ? "" : LyricsText.PlainText(found);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var lyrics = new Dictionary<string, object> { ["value"] = text };
            if (text.Length > 0)
            {
                lyrics["artist"] = artist;
                lyrics["title"] = title;
            }
            return CreateJsonResponse(new Dictionary<string, object>
            {
                ["status"] = "ok",
                ["version"] = SubsonicVersion,
                ["lyrics"] = lyrics,
            });
        }

        var ns = XNamespace.Get(SubsonicNamespace);
        var element = new XElement(ns + "lyrics", text);
        if (text.Length > 0)
        {
            element.Add(new XAttribute("artist", artist));
            element.Add(new XAttribute("title", title));
        }
        var document = new XDocument(new XElement(ns + "subsonic-response",
            new XAttribute("status", "ok"), new XAttribute("version", SubsonicVersion), element));
        return new ContentResult { Content = document.ToString(), ContentType = "application/xml" };
    }

    /// <summary>getLyricsCandidates: every entry the sources hold for a song, for choosing
    /// between them, and what the song is set to (a candidate id, "none" or "auto").</summary>
    public IActionResult CreateLyricsCandidatesResponse(string songId, string choice,
        IEnumerable<LyricsChoiceCandidate> candidates) =>
        CreateJsonResponse(new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["version"] = SubsonicVersion,
            ["type"] = "octo",
            ["lyricsCandidates"] = new Dictionary<string, object?>
            {
                ["id"] = songId,
                ["choice"] = choice,
                ["candidate"] = candidates.Select(candidate => new Dictionary<string, object?>
                {
                    ["id"] = candidate.Id,
                    ["source"] = candidate.Source,
                    ["title"] = candidate.Title,
                    ["artist"] = candidate.Artist,
                    ["album"] = candidate.Album,
                    ["duration"] = candidate.DurationSeconds,
                    ["kind"] = candidate.Kind,
                    ["sameSong"] = candidate.SameSong,
                    ["chosen"] = candidate.Id == choice,
                    ["preview"] = candidate.Preview,
                }).ToList(),
            },
        });

    public IActionResult CreateLyricsChoiceResponse(string songId, string choice) =>
        CreateJsonResponse(new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["version"] = SubsonicVersion,
            ["type"] = "octo",
            ["lyricsChoice"] = new Dictionary<string, object?> { ["id"] = songId, ["choice"] = choice },
        });
}
