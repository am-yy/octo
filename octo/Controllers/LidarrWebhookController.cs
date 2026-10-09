using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Local;
using Octo.Services.Subsonic;

namespace Octo.Controllers;

/// <summary>
/// Lidarr's Connect > Webhook. An import makes Navidrome scan and the saved-song reconciler check
/// every few seconds, so a hearted song swaps to its library copy as soon as it lands instead of on
/// the next minute's poll. That includes albums Octo stopped waiting for after ImportTimeoutSeconds
/// or a restart. Off until Lidarr:WebhookSecret is set; Lidarr sends it as the Basic auth password.
/// </summary>
[ApiController]
[Route("api/lidarr/webhook")]
public class LidarrWebhookController(
    IOptionsMonitor<LidarrSettings> settings,
    ILocalLibraryService library,
    ExternalSaveReconciler reconciler,
    ILogger<LidarrWebhookController> logger) : ControllerBase
{
    [HttpPost, HttpPut]
    public async Task<IActionResult> Receive()
    {
        var secret = settings.CurrentValue.WebhookSecret;
        if (string.IsNullOrEmpty(secret)) return NotFound();
        if (!HasSecret(Request.Headers.Authorization.ToString(), secret)) return Unauthorized();

        string? eventType;
        try
        {
            using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: HttpContext.RequestAborted);
            eventType = document.RootElement.TryGetProperty("eventType", out var value)
                        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return BadRequest(); }

        // Download is an import, upgrades included. Test and every other event only need a 200.
        if (string.Equals(eventType, "Download", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Lidarr imported a release; asking Navidrome to scan");
            await library.TriggerLibraryScanAsync(force: true);
            reconciler.Wake();
        }
        return Ok();
    }

    /// <summary>Whether a Basic auth header carries the secret as its password. Lidarr requires a
    /// username too, so it is ignored.</summary>
    internal static bool HasSecret(string header, string secret)
    {
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
        catch (FormatException) { return false; }
        var colon = decoded.IndexOf(':');
        if (colon < 0) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(decoded[(colon + 1)..]), Encoding.UTF8.GetBytes(secret));
    }
}
