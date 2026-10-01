namespace Octo.Models.Settings;

/// <summary>
/// Push notifications for the download lifecycle. Subsonic has no notification
/// mechanism and Navidrome's event stream only reaches its own web UI, so without
/// this nothing tells the user a starred track landed, settled for a lossy source,
/// or failed — the SearchWaitSeconds bug went unnoticed for exactly that reason.
///
/// A transport is enabled simply by its URL being non-empty. Every value is read
/// through IOptionsMonitor at send time, so changes apply without a restart.
/// </summary>
public class NotificationSettings
{
    /// <summary>
    /// Full ntfy topic URL, e.g. "https://ntfy.sh/my-octo-topic". Non-empty enables
    /// the ntfy sink. Subscribe to the same topic in the ntfy app to receive pushes.
    /// Environment variable: NOTIFICATIONS__NTFYURL
    /// </summary>
    public string NtfyUrl { get; set; } = "";

    /// <summary>
    /// Optional ntfy access token, sent as "Authorization: Bearer". Only needed on
    /// servers with access control.
    /// Environment variable: NOTIFICATIONS__NTFYTOKEN
    /// </summary>
    public string NtfyToken { get; set; } = "";

    /// <summary>
    /// Discord webhook URL. Non-empty enables the Discord sink. The URL embeds the
    /// webhook token, so the whole URL is treated as a secret and masked in the
    /// config-sources view.
    /// Environment variable: NOTIFICATIONS__DISCORDWEBHOOKURL
    /// </summary>
    public string DiscordWebhookUrl { get; set; } = "";

    /// <summary>
    /// A transfer actually began, saying up front whether it found lossless or is
    /// settling for MP3. Off by default: it doubles volume for information
    /// DownloadCompleted mostly repeats, and LosslessFallback (on by default)
    /// already covers the settling case.
    /// </summary>
    public bool NotifyDownloadStarted { get; set; } = false;

    /// <summary>A track landed: format, source, size, album art.</summary>
    public bool NotifyDownloadCompleted { get; set; } = true;

    /// <summary>
    /// Soulseek came up empty and Octo settled for a Deezer MP3. On by default:
    /// silent quality loss is the failure mode this whole feature exists to expose.
    /// </summary>
    public bool NotifyLosslessFallback { get; set; } = true;

    /// <summary>Both sources failed for a starred track. Stars only — a shed
    /// play-triggered acquisition is a hint, not an unmet promise.</summary>
    public bool NotifyDownloadFailed { get; set; } = true;

    /// <summary>One summary per album walk (tracks fetched, how many lossless)
    /// instead of a ping per track.</summary>
    public bool NotifyAlbumCompleted { get; set; } = true;
}
