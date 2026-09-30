using System.Text.RegularExpressions;
using Octo.Services.Common;

namespace Octo.Services.LastFm;

/// <summary>
/// Puts right the rows Last.fm's track.search gets from mislabelled scrobbles, before they become
/// outside songs. A search for "Kavinsky Nightcall" answers, among the real song, artist
/// "Kavinsky Nightcall" with title "Nightcall", title "Kavinsky - Nightcall" under the label
/// "Record Makers", and the same under "[unknown]". Taken as they are, those rows reach search
/// results, playback and each listener's Last.fm under the wrong names.
///
/// Only the song's own names are moved about, and only on evidence from the same answer. The
/// row's own artist at the front of its title is always taken out. Moving a song to another
/// artist needs two things: that artist has rows of their own, and the song as repaired is
/// among them, so Radiohead's "Creep - Acoustic" stays Radiohead's even when a band called
/// Creep answers the same search. What cannot be put right is left as it was, except a row
/// with no artist at all. Rows that turn out to be one song then keep the place of the first
/// and the spelling of the most listened.
/// </summary>
public static class LastFmSearchCleanup
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "unknown", "unknownartist", "notavailable", "na",
    };

    // A hyphen, en dash or em dash (U+2013, U+2014), with or without space around it: "A - T", "A-T".
    private static readonly Regex Dash = new(@"\s*[-\u2013\u2014]\s*", RegexOptions.Compiled);

    public static List<LastFmService.SimilarTrack> Clean(IReadOnlyList<LastFmService.SimilarTrack> tracks)
    {
        // Every artist the answer names on its own, keyed loosely, with the spelling of its most
        // listened row.
        var known = new Dictionary<string, (string Name, long Listeners)>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            var key = SongIdentity.Key(track.Artist);
            if (key.Length == 0 || IsPlaceholder(track.Artist)) continue;
            var listeners = track.Listeners ?? 0;
            if (!known.TryGetValue(key, out var current) || listeners > current.Listeners)
                known[key] = (track.Artist.Trim(), listeners);
        }

        var songs = tracks.Where(track => !IsPlaceholder(track.Artist))
            .Select(track => SongIdentity.MatchKey(track.Artist, track.Title))
            .ToHashSet(StringComparer.Ordinal);

        var cleaned = new List<LastFmService.SimilarTrack>(tracks.Count);
        foreach (var track in tracks)
        {
            if (Repair(track, known, songs) is { } repaired) cleaned.Add(repaired);
        }

        // One song, several rows: the first place, the most listened spelling.
        var order = new List<string>();
        var best = new Dictionary<string, LastFmService.SimilarTrack>(StringComparer.Ordinal);
        foreach (var track in cleaned)
        {
            var key = SongIdentity.MatchKey(track.Artist, track.Title);
            if (!best.TryGetValue(key, out var kept))
            {
                order.Add(key);
                best[key] = track;
            }
            else if ((track.Listeners ?? 0) > (kept.Listeners ?? 0))
            {
                best[key] = track;
            }
        }
        return order.Select(key => best[key]).ToList();
    }

    private static LastFmService.SimilarTrack? Repair(LastFmService.SimilarTrack track,
        Dictionary<string, (string Name, long Listeners)> known, HashSet<string> songs)
    {
        var artist = track.Artist.Trim();
        var title = track.Title.Trim();
        var artistKey = SongIdentity.Key(artist);

        // "Artist - Title" in the title: under the artist itself ("Kanye West - Stronger" by Kanye
        // West), under no artist, or under someone else the answer names on their own, as a label
        // uploading the song ("Kavinsky - Nightcall" by Record Makers).
        if (SplitAtArtist(title, (candidate, rest) =>
                SongIdentity.Key(candidate) == artistKey
                || (known.ContainsKey(SongIdentity.Key(candidate)) && songs.Contains(SongIdentity.MatchKey(candidate, rest))))
            is var (named, rest))
        {
            var namedKey = SongIdentity.Key(named);
            return track with
            {
                Artist = known.TryGetValue(namedKey, out var spelled) ? spelled.Name : named,
                Title = rest,
            };
        }

        if (IsPlaceholder(artist))
        {
            // With no artist to go on, "A - T" still names one, at the first spaced dash.
            var spaced = title.IndexOf(" - ", StringComparison.Ordinal);
            return spaced > 0 && spaced + 3 < title.Length
                ? track with { Artist = title[..spaced].Trim(), Title = title[(spaced + 3)..].Trim() }
                : null;
        }

        // The title on the end of the artist: "Kavinsky Nightcall" or "Kavinsky - Nightcall" with
        // title "Nightcall", when "Kavinsky" is an artist in its own right in the same answer.
        var titleKey = SongIdentity.Key(title);
        if (titleKey.Length > 0 && artistKey.Length > titleKey.Length && artistKey.EndsWith(titleKey, StringComparison.Ordinal)
            && artist.EndsWith(title, StringComparison.OrdinalIgnoreCase))
        {
            var prefix = Dash.Replace(artist[..^title.Length], " ").Trim();
            if (known.TryGetValue(SongIdentity.Key(prefix), out var spelled) && songs.Contains(SongIdentity.MatchKey(prefix, title)))
                return track with { Artist = spelled.Name };
        }

        return track;
    }

    /// <summary>The title split at the dash after an artist <paramref name="accept"/> takes, with
    /// the rest as the title, trying each dash in turn so a name with a hyphen in it
    /// ("Jay-Z - 99 Problems") is read whole.</summary>
    private static (string Artist, string Title)? SplitAtArtist(string title, Func<string, string, bool> accept)
    {
        foreach (Match dash in Dash.Matches(title))
        {
            var before = title[..dash.Index].Trim();
            var after = title[(dash.Index + dash.Length)..].Trim();
            if (before.Length == 0 || after.Length == 0) continue;
            if (accept(before, after)) return (before, after);
        }
        return null;
    }

    private static bool IsPlaceholder(string? artist) =>
        Placeholders.Contains(SongIdentity.Key(artist)) || SongIdentity.Key(artist).Length == 0;
}
