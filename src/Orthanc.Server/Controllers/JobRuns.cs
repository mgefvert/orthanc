using Microsoft.AspNetCore.Mvc;
using Orthanc.Core;
using Orthanc.Core.Services;

namespace Orthanc.Server.Controllers;

[ApiController]
public class JobRunsController : Controller
{
    private readonly ILogger<HealthController> _logger;
    private readonly IngestionService _ingestionService;

    public JobRunsController(ILogger<HealthController> logger, IngestionService ingestionService)
    {
        _logger           = logger;
        _ingestionService = ingestionService;
    }

    [HttpPost("/api/v1/jobruns/start")]
    public async Task<ActionResult> Start(StartRequest request, CancellationToken ct)
    {
        var run = await _ingestionService.Start(
            (long)HttpContext.Items["CustomerId"]!,
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", ct);

        _logger.LogInformation("Started run {RunId} for job {JobId}, customer {CustomerId}", run.Id, run.JobId, HttpContext.Items["CustomerId"]);

        return Created($"/api/v1/jobruns/{run.Id}/complete", new
        {
            id = run.Id
        });
    }

    [HttpPost("/api/v1/jobruns/{id:guid}/complete")]
    public async Task<ActionResult> Complete(Guid id, CompleteRequest request, CancellationToken ct)
    {
        var run = await _ingestionService.Complete((long)HttpContext.Items["CustomerId"]!, id, request, ct);

        _logger.LogInformation("Completed run {RunId}: {Outcome}, warnings {Warnings}, errors {Errors}, fatal {Fatal}",
            run.Id, run.Outcome, run.WarningCount, run.ErrorCount, run.FatalCount);

        return Ok(new
        {
            id      = run.Id,
            outcome = run.Outcome.ToString()
        });
    }
}