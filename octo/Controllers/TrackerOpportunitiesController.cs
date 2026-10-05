using Microsoft.AspNetCore.Mvc;
using Octo.Services.Trackers;

namespace Octo.Controllers;

[ApiController]
[Route("api/admin/tracker-opportunities")]
public sealed class TrackerOpportunitiesController(TrackerOpportunityService opportunities, SalmonJobService jobs) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(new { opportunities = await opportunities.ListAsync(HttpContext.RequestAborted), jobs = await jobs.ListAsync(HttpContext.RequestAborted) });

    [HttpPost("recheck")]
    public async Task<IActionResult> Recheck([FromQuery] string key) =>
        await opportunities.RecheckAsync(key, HttpContext.RequestAborted)
            ? Accepted() : NotFound();
}
