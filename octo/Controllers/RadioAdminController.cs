using Microsoft.AspNetCore.Mvc;
using Octo.Services.Radio;

namespace Octo.Controllers;

/// <summary>What radio has learned from listening, on the dashboard's Radio page.</summary>
[ApiController]
[Route("api/admin/radio-outcomes")]
public sealed class RadioAdminController(RadioOutcomeStore outcomes) : ControllerBase
{
    [HttpGet]
    public IActionResult Stats() => Ok(outcomes.Stats());

    /// <summary>The artists radio plays less because listeners skip them, most skipped first.</summary>
    [HttpGet("skipped-artists")]
    public IActionResult SkippedArtists() => Ok(outcomes.SkippedArtistStats(10)
        .Select(row => new { artist = row.Artist, listeners = row.Listeners, skips = Math.Round(row.Skipped, 1) }));

    [HttpPost("reset")]
    public IActionResult Reset() { outcomes.Forget(); return Ok(outcomes.Stats()); }
}
