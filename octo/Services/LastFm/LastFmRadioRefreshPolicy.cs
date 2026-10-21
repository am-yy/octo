using Octo.Models.Radio;
using Octo.Models.Settings;

namespace Octo.Services.LastFm;

public static class LastFmRadioRefreshPolicy
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(15);

    public static bool IsStale(LastFmRadioUserState user, LastFmSettings settings,
        DateTime? nowUtc = null) => !user.LastRefreshSuccessUtc.HasValue
        || user.LastRefreshSuccessUtc < (nowUtc ?? DateTime.UtcNow)
            .AddHours(-settings.EffectiveRefreshIntervalHours);

    public static bool ShouldRefreshAfterPlay(LastFmRadioUserState user, LastFmSettings settings,
        DateTime? nowUtc = null) => RefreshReasonAfterPlay(user, settings, nowUtc) is not null;

    /// <summary>Why a play calls for a rebuild, for the log; null when it does not.</summary>
    public static string? RefreshReasonAfterPlay(LastFmRadioUserState user, LastFmSettings settings,
        DateTime? nowUtc = null)
    {
        if (user.Stations.Count == 0) return "no stations yet";
        if (IsStale(user, settings, nowUtc)) return "stale";
        var threshold = Math.Max(3, settings.EffectiveMinimumPlays / 2);
        if (user.NewPlaysSinceRefresh >= threshold) return $"{user.NewPlaysSinceRefresh} new plays (threshold {threshold})";
        return user.Plays.Count(play => play.LearnedSignal) == settings.EffectiveMinimumPlays
            ? "enough plays to learn from" : null;
    }

    public static bool ShouldSchedulePeriodicRefresh(LastFmRadioUserState user,
        LastFmSettings settings, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (user.Refreshing || !IsStale(user, settings, now)) return false;
        return user.LastRefreshError is null || !user.LastRefreshAttemptUtc.HasValue
            || user.LastRefreshAttemptUtc <= now.Subtract(FailureRetryDelay);
    }

    public static TimeSpan StartupJitter(string username)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant()));
        return TimeSpan.FromMilliseconds(100 + BitConverter.ToUInt16(hash, 0) % 400);
    }
}
