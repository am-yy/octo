using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Validation;

namespace Octo.Services.Soulseek;

/// <summary>
/// Validates that slskd is reachable + that the music source is wired correctly.
/// </summary>
public class SoulseekStartupValidator : BaseStartupValidator
{
    private readonly SoulseekSettings _settings;
    private readonly SoulseekClient _client;
    private readonly string _downloadPath;

    public override string ServiceName => "Soulseek";

    public SoulseekStartupValidator(
        IOptions<SoulseekSettings> settings,
        SoulseekClient client,
        HttpClient httpClient,
        IConfiguration configuration)
        : base(httpClient)
    {
        _settings = settings.Value;
        _client = client;
        _downloadPath = configuration["Library:DownloadPath"] ?? "/music";
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine();

        if (!_settings.Enabled)
        {
            WriteStatus("Soulseek (slskd)", "DISABLED", ConsoleColor.DarkGray);
            return ValidationResult.Success("Soulseek disabled");
        }

        if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
        {
            WriteStatus("Soulseek (slskd)", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Set the Soulseek__BaseUrl environment variable (e.g. http://slskd:5030)");
            return ValidationResult.Failure("-1", "Soulseek BaseUrl not set");
        }

        WriteStatus("Soulseek BaseUrl", _settings.BaseUrl, ConsoleColor.Cyan);
        WriteStatus("Search wait", $"{_settings.SearchWaitSeconds}s", ConsoleColor.Cyan);
        WriteStatus("Min file size", $"{_settings.MinFileSizeBytes / (1024 * 1024)} MB", ConsoleColor.Cyan);

        var reachable = await _client.IsReachableAsync(cancellationToken);
        if (!reachable)
        {
            WriteStatus("slskd API", "UNREACHABLE", ConsoleColor.Red);
            WriteDetail("Check that slskd is running and Soulseek__BaseUrl + Username + Password are correct");
            return ValidationResult.Failure("-1", "slskd unreachable");
        }

        WriteStatus("slskd API", "REACHABLE", ConsoleColor.Green);

        // Diagnostic only: Octo finds finished files by watching its own
        // DownloadPath, so slskd writing anywhere else means downloads
        // "succeed" in slskd but never reach the library (issue #17).
        var slskdDownloads = await _client.GetDownloadsDirectoryAsync(cancellationToken);
        if (!string.IsNullOrEmpty(slskdDownloads))
        {
            var normalized = Normalize(slskdDownloads);
            if (normalized == Normalize(_downloadPath) || normalized == "/music")
            {
                WriteStatus("slskd downloads dir", slskdDownloads, ConsoleColor.Green);
            }
            else if (normalized.EndsWith("/app/downloads", StringComparison.OrdinalIgnoreCase))
            {
                WriteStatus("slskd downloads dir", $"{slskdDownloads} (MISCONFIGURED)", ConsoleColor.Yellow);
                WriteDetail("slskd is writing to its internal default, which Octo cannot see.");
                WriteDetail("Set SLSKD_DOWNLOADS_DIR=/music on the slskd container (or point slskd's");
                WriteDetail($"downloads dir at the same directory as Octo's DownloadPath: {_downloadPath}).");
                WriteDetail("If already set, check slskd.yml for a directories.downloads entry: a");
                WriteDetail("yaml value overrides the environment variable.");
            }
            else
            {
                // A path we can't judge from inside this container: different
                // mounts can make distinct strings point at the same host dir.
                WriteStatus("slskd downloads dir", slskdDownloads, ConsoleColor.Cyan);
            }
        }

        return ValidationResult.Success("Soulseek validation passed");
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimEnd('/');
}
