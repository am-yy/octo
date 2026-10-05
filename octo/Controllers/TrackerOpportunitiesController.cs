using Microsoft.AspNetCore.Mvc;
using Octo.Services.Trackers;

namespace Octo.Controllers;

[ApiController]
[Route("api/admin/tracker-opportunities")]
public sealed class TrackerOpportunitiesController(TrackerOpportunityService opportunities, SalmonJobService jobs) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(new { opportunities = (await opportunities.ListAsync(HttpContext.RequestAborted)).Select(r => new {
            r.Key, r.Artist, r.Album, r.AlbumType, r.Saved, r.IdentityStatus, r.IdentityVersion, r.IdentityChanged,
            r.IdentityDiagnostic, r.ParentSearchComplete, r.ParentsInspected, r.Acquisition, r.ReconciliationError,
            r.DeezerAvailability, r.DeezerCheckedUtc, r.DeezerProofExpiresUtc,
            manifest = r.Manifest is null ? null : new { r.Manifest.AlbumId, r.Manifest.Revision, r.Manifest.AlbumType, r.Manifest.DeclaredCount, r.Manifest.Complete },
            r.SourceLinks, r.Sources, r.Red, r.Ops
        }), jobs = await jobs.ListAsync(HttpContext.RequestAborted) });

    [HttpPost("recheck")]
    public async Task<IActionResult> Recheck([FromQuery] string key) =>
        await opportunities.RecheckAsync(key, HttpContext.RequestAborted)
            ? Accepted() : NotFound();
}
