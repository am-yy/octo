using System.Reflection;

namespace Octo.Services.Common;

/// <summary>
/// How Octo introduces itself to the open services that ask to be told who is calling: LRCLIB,
/// MusicBrainz and its Cover Art Archive, and AcoustID submissions. A generic user agent is what
/// those services throttle first.
/// </summary>
public static class OctoUserAgent
{
    public static readonly string Version =
        typeof(OctoUserAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] is { Length: > 0 } version ? version : "dev";

    public static readonly string Value = $"Octo/{Version} (+https://github.com/winters27/octo)";
}
