namespace Octo.Services.Subsonic;

/// <summary>
/// The completed plays each listener reported in the last few minutes, so a play a client sends
/// twice is learned from once. Some players post the same completed scrobble again (a retry after
/// a slow answer, a second scrobble at the end of the song), and each copy used to reach Last.fm,
/// ListenBrainz and the radio profile as another play.
///
/// A play is the song and the time the client gave for it, or, without one, the minute it
/// arrived in. Kept per listener, newest first, and bounded both ways, so a flood from one
/// listener pushes out only that listener's oldest plays.
/// </summary>
public sealed class RecentScrobbles
{
    /// <summary>How long a play is remembered. A repeat later than this counts again.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Plays remembered per listener. Far more than anyone finishes in ten minutes.</summary>
    internal const int PerListener = 256;

    /// <summary>Listeners remembered at once.</summary>
    internal const int Listeners = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, Plays> _listeners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True the first time this listener reports this play inside the window, false for a repeat.
    /// </summary>
    /// <param name="time">The client's own time for the play (Subsonic's <c>time</c>), or null.</param>
    public bool FirstReport(string username, string songId, string? time, DateTime nowUtc)
    {
        var play = time is { Length: > 0 }
            ? $"{songId}\n{time}"
            : $"{songId}\nminute {nowUtc.Ticks / TimeSpan.TicksPerMinute}";
        lock (_gate)
        {
            if (!_listeners.TryGetValue(username, out var plays))
            {
                if (_listeners.Count >= Listeners) ForgetQuietestListener();
                _listeners[username] = plays = new Plays();
            }
            plays.LastReportUtc = nowUtc;
            return plays.Add(play, nowUtc);
        }
    }

    /// <summary>Makes room for a new listener by forgetting the one heard from longest ago.</summary>
    private void ForgetQuietestListener()
    {
        var quietest = _listeners.MinBy(pair => pair.Value.LastReportUtc).Key;
        if (quietest is not null) _listeners.Remove(quietest);
    }

    private sealed class Plays
    {
        private readonly LinkedList<(string Play, DateTime AtUtc)> _order = new();
        private readonly Dictionary<string, LinkedListNode<(string Play, DateTime AtUtc)>> _index =
            new(StringComparer.Ordinal);

        public DateTime LastReportUtc { get; set; }

        public bool Add(string play, DateTime nowUtc)
        {
            // Oldest last: drop whatever has aged out of the window before looking.
            while (_order.Last is { } oldest && nowUtc - oldest.Value.AtUtc >= Window)
            {
                _index.Remove(oldest.Value.Play);
                _order.RemoveLast();
            }
            if (_index.ContainsKey(play)) return false;
            _index[play] = _order.AddFirst((play, nowUtc));
            if (_order.Count > PerListener)
            {
                _index.Remove(_order.Last!.Value.Play);
                _order.RemoveLast();
            }
            return true;
        }
    }
}
