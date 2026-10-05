using Microsoft.AspNetCore.Mvc;
using Octo.Services.Trackers;

namespace Octo.Controllers;

[ApiController]
[Route("api/admin/salmon")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SalmonController(SalmonJobService jobs) : ControllerBase
{
    private CancellationToken Ct => HttpContext.RequestAborted;
    [HttpPost("context/{target}")]
    public Task<IActionResult> Context(string target) => Run(() => jobs.ContextAsync(target, Ct));
    [HttpPost("source/{infohash}")]
    public Task<IActionResult> Source(string infohash) => Run(() => jobs.SourceAsync(infohash, Ct));
    [HttpPost("jobs")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public Task<IActionResult> Create([FromBody] SalmonSubmission submission) =>
        Run(() => jobs.CreateAsync(Request.Headers["Idempotency-Key"].ToString(), submission, Ct));
    [HttpPut("jobs/{id}/files/{index:int}")]
    [DisableRequestSizeLimit]
    public Task<IActionResult> PutFile(string id, int index) => Run(async () =>
    {
        await jobs.PutFileAsync(id, index, Request.Body, Ct); return new { transferred = true };
    });
    [HttpPost("jobs/{id}/review")]
    public Task<IActionResult> Review(string id) => Run(() => jobs.ReviewAsync(id, Ct));
    public sealed record Approval(string Revision);
    [HttpPost("jobs/{id}/submit")]
    public Task<IActionResult> Submit(string id, [FromBody] Approval approval) => Run(() => jobs.SubmitAsync(id, approval.Revision, Ct));
    [HttpGet("jobs/{id}")]
    public Task<IActionResult> Get(string id) => Run(() => jobs.GetAsync(id, Ct));
    [HttpGet("jobs")]
    public Task<IActionResult> List() => Run(async () => new { jobs = await jobs.ListAsync(Ct) });
    [HttpPost("jobs/{id}/reconcile")]
    public Task<IActionResult> Reconcile(string id) => Run(() => jobs.ReconcileAsync(id, Ct));
    [HttpPost("jobs/{id}/handoff")]
    public Task<IActionResult> Handoff(string id) => Run(() => jobs.HandoffAsync(id, Ct));
    public sealed record ImportSelection(string ReleaseId);
    [HttpPost("jobs/{id}/import-release")]
    public Task<IActionResult> SelectImportRelease(string id, [FromBody] ImportSelection selection) =>
        Run(() => jobs.SelectImportReleaseAsync(id, selection.ReleaseId, Ct));
    [HttpGet("jobs/{id}/torrent")]
    public async Task<IActionResult> Torrent(string id)
    {
        try { return File(await jobs.TorrentAsync(id, Ct), "application/x-bittorrent", id + ".torrent"); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Failure(ex); }
    }
    private async Task<IActionResult> Run(Func<Task<object>> work)
    {
        try { return Ok(await work()); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Failure(ex); }
    }
    // Framework/network/decoder exceptions can contain private paths, passkeys, or remote response bodies.
    private IActionResult Failure(Exception ex) => ex switch
    {
        KeyNotFoundException => NotFound(new { error = "Job not found or tracker response incomplete." }),
        InvalidDataException => BadRequest(new { error = "Payload, evidence, metadata, or tracker response failed validation. See local preparation checks." }),
        InvalidOperationException => Conflict(new { error = "Job cannot advance in its current state. Review or reconcile before retrying." }),
        _ => StatusCode(503, new { error = "Operation pending; inspect job status before retrying." }),
    };
}
