using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Models;
using SoteroMap.API.Services;
using SoteroMap.API.ViewModels;

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

    [AllowAnonymous]
    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest([FromBody] NetworkTelemetryIngestRequest request, CancellationToken cancellationToken = default)
    {
        if (!CanIngest())
        {
            return Unauthorized();
        }

        var actor = User.Identity?.IsAuthenticated == true
            ? (User.Identity?.Name ?? "usuario")
            : "collector";

        var result = await _service.IngestAsync(request, actor, cancellationToken);
        return Ok(result);
    }

    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Auditor}")]
    [HttpGet("snapshots/{snapshotId:int}/observations")]
    public async Task<IActionResult> Observations(int snapshotId, [FromQuery] int take = 25, [FromQuery] string? type = null, CancellationToken cancellationToken = default)
    {
        var observations = await _service.GetObservationsAsync(snapshotId, take, type, cancellationToken);
        return Ok(observations);
    }

    private bool CanIngest()
    {
        var apiKey = _service.IngestApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (Request.Headers.TryGetValue("X-Network-Telemetry-Key", out var providedKey))
            {
                return string.Equals(providedKey.ToString(), apiKey, StringComparison.Ordinal);
            }

            return false;
        }

        return User.Identity?.IsAuthenticated == true &&
               (User.IsInRole(AppRoles.Admin) || User.IsInRole(AppRoles.Auditor));
    }
}
