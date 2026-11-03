namespace Octo.Models.Settings;

/// <summary>
/// Settings about Octo's own address, as opposed to the upstream servers it talks to.
/// </summary>
public class ServerSettings
{
    /// <summary>
    /// The address Octo is reachable at from outside the local network, for people who
    /// put a reverse proxy or tunnel in front of it (default: empty).
    /// Environment variable: SERVER__PUBLICURL
    ///
    /// Octo cannot work this out on its own. It sees the public hostname on relayed
    /// Subsonic calls, but the admin dashboard is the one place that would display it
    /// and that is exactly the path a sane proxy setup keeps off the public internet,
    /// so the value never arrives where it is needed. It is also not worth detecting:
    /// whoever configured the proxy chose this hostname and already typed it into a
    /// client to test it. This field exists so they only have to remember it once.
    ///
    /// Purely informational. Nothing routes or validates against it; it is displayed
    /// on the dashboard as a second copyable address next to the local one.
    /// </summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>
    /// Log one line for every stream and download request: method, song id, Range header,
    /// status, bytes sent, time taken and client (default: off).
    /// Environment variable: SERVER__LOGSTREAMREQUESTS
    ///
    /// For chasing playback problems. Read on every request, so flipping it in settings.json
    /// takes effect without a restart. Streams that end early are logged either way.
    /// </summary>
    public bool LogStreamRequests { get; set; }
}
