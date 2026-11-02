using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Orthanc.Core;

namespace Orthanc.Server.Controllers;

[ApiController]
public class HealthController : Controller
{
    private readonly OrthancContext _context;
    private readonly ILogger<HealthController> _logger;

    public HealthController(OrthancContext context, ILogger<HealthController> logger)
    {
        _context = context;
        _logger  = logger;
    }

    [HttpGet("/health")]
    public ActionResult Health()
    {
        return Ok(new { status = "alive" });
    }

    [HttpGet("/health/ready")]
    public async Task<ActionResult> HealthReady(CancellationToken ct)
    {
        try
        {
            // Also validates that the manually installed schema exists.
            await _context.Customers
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .Take(1)
                .ToListAsync(ct);

            return Ok(new { status = "ready" });
        }
        catch
        {
            return StatusCode(503);
        }
    }
}