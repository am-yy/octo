using Microsoft.Extensions.Options;
using Octo.Models.Radio;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.LastFm;
using Octo.Services.Metadata;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Services.Library;

/// <summary>
/// "Keep these?": outside songs a listener keeps coming back to but has never added, as a
/// read-only playlist made like "Popular right now". A heart keeps a song, as anywhere; one star
/// says no, and the song never comes back. A song the library has, or the listener hearted, is
/// left out. Its plays are the ones radio learns from, so it is empty while radio does not learn.
/// </summary>
public sealed class KeepThesePlaylistService : PopularPlaylistService
{
    public new const string Key = "keep:these";
    public new const string Kind = "keep";
    public new const string Name = "Keep these?";

    /// <summary>Plays, on days, within days, that make a song one to ask about.</summary>
    internal const int MinimumPlays = 3;
    internal const int MinimumDays = 2;
    internal static readonly TimeSpan PlayedWithin = TimeSpan.FromDays(90);
    internal new const int MaxSongs = 50;

    private readonly LastFmRadioStateStore _plays;
    private readonly ExternalSaveStore? _saves;

    public KeepThesePlaylistService(TopSongsService topSongs, SubsonicResponseBuilder responses,
        ExternalIdRegistry registry, IServiceScopeFactory scopes, IOptionsMonitor<GeneratedPlaylistSettings> settings,
        IOptionsMonitor<SubsonicSettings> subsonic, ILogger<PopularPlaylistService> logger,
        LastFmRadioStateStore plays, ExternalSaveStore? saves = null)
        : base(topSongs, responses, registry, scopes, settings, subsonic, logger)
    {
        _plays = plays;
        _saves = saves;
    }

    public override bool On => Settings.CurrentValue.KeepThese;
    protected override string ListKey => Key;
    public override string ListKind => Kind;
    public override string ListName => Name;
    protected override bool ListsOwned => false;

    public new static string PlaylistId(string username) => GeneratedPlaylistService.PlaylistId(username, Key);

    protected override Task<(IReadOnlyList<TopSongsService.TopSong> Rows, string Source)> RowsAsync(
        string username, CancellationToken ct)
    {
        var user = _plays.GetUser(username);
        var kept = (_saves?.GetHearts(username) ?? [])
            .Select(heart => LastFmRadioSeedNormalizer.TrackKey(heart.Song.Artist ?? "", heart.Song.Title ?? ""))
            .Concat((user.RadioBans ?? []).Select(ban => ban.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = Pick(user.Plays, kept, Clock())
            .Select((pick, index) => new TopSongsService.TopSong(index + 1,
                TopSongsService.CatalogSong(Registry, new DeezerMetadataService.ChartTrack(
                    "", pick.Play.Title, pick.Play.Artist, null, pick.Play.Album, null, null, pick.Play.Duration, null)),
                pick.Plays, null))
            .ToList();
        return Task.FromResult<(IReadOnlyList<TopSongsService.TopSong>, string)>((rows, "their plays"));
    }

    /// <summary>
    /// The outside songs to ask about: played <see cref="MinimumPlays"/> times or more on
    /// <see cref="MinimumDays"/> days or more, the last within <see cref="PlayedWithin"/>, never
    /// hearted or rated one star (<paramref name="excluded"/>, by track key). Most played first,
    /// then most lately. A play from a random library pick at first sign-in is not the listener's.
    /// </summary>
    internal static IReadOnlyList<(LastFmRadioPlay Play, int Plays)> Pick(IEnumerable<LastFmRadioPlay> plays,
        IReadOnlySet<string> excluded, DateTime now) =>
        plays.Where(play => !play.IsLocal && play.Source != "bootstrap-random"
                && play.Artist.Length > 0 && play.Title.Length > 0)
            .GroupBy(play => LastFmRadioSeedNormalizer.TrackKey(play.Artist, play.Title), StringComparer.OrdinalIgnoreCase)
            .Where(group => !excluded.Contains(group.Key) && !group.Any(play => play.Hearted))
            .Select(group => (Latest: group.MaxBy(play => play.PlayedAtUtc)!, Count: group.Count(),
                Days: group.Select(play => play.PlayedAtUtc.Date).Distinct().Count()))
            .Where(item => item.Count >= MinimumPlays && item.Days >= MinimumDays
                && now - item.Latest.PlayedAtUtc <= PlayedWithin)
            .OrderByDescending(item => item.Count).ThenByDescending(item => item.Latest.PlayedAtUtc)
            .Take(MaxSongs)
            .Select(item => (item.Latest, item.Count))
            .ToList();
}
