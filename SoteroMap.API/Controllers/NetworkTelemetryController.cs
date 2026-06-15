using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Models;
using SoteroMap.API.Services;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/network-telemetry")]
[Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Auditor}")]
public class NetworkTelemetryController : ControllerBase
{
    private readonly NetworkTelemetryService _service;

    public NetworkTelemetryController(NetworkTelemetryService service)
    {
        _service = service;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken = default)
    {
        var model = await _service.GetDashboardAsync(10, cancellationToken);
        return Ok(model);
    }

    [HttpGet("latest")]
    public async Task<IActionResult> Latest([FromQuery] int take = 10, CancellationToken cancellationToken = default)
    {
        var snapshots = await _service.GetRecentSnapshotsAsync(take, cancellationToken);
        return Ok(snapshots);
    }
}
