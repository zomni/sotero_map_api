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
    private readonly NetworkTelemetryLiveScanService _liveScanService;
    private readonly NetworkTelemetryAgentBridgeService _agentBridgeService;

    public NetworkTelemetryController(
        NetworkTelemetryService service,
        NetworkTelemetryLiveScanService liveScanService,
        NetworkTelemetryAgentBridgeService agentBridgeService)
    {
        _service = service;
        _liveScanService = liveScanService;
        _agentBridgeService = agentBridgeService;
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

    [HttpPost("scan")]
    public async Task<IActionResult> Scan([FromBody] NetworkTelemetryLiveScanRequest? request, CancellationToken cancellationToken = default)
    {
        if (!_liveScanService.IsEnabled())
        {
            return BadRequest(new { message = "La telemetria de red esta deshabilitada por configuracion." });
        }

        var actor = User.Identity?.IsAuthenticated == true
            ? (User.Identity?.Name ?? "system")
            : "system";

        if (_agentBridgeService.UseAgentMode())
        {
            var status = await _agentBridgeService.QueueScanAsync(actor, request, cancellationToken);
            return Accepted(status);
        }

        var result = await _liveScanService.ScanAndStoreAsync(actor, request, cancellationToken);
        return Ok(result);
    }

    [HttpGet("agent/status")]
    public async Task<IActionResult> AgentStatus(CancellationToken cancellationToken = default)
    {
        return Ok(await _agentBridgeService.GetStatusAsync(cancellationToken));
    }

    [HttpPost("agent/control")]
    public async Task<IActionResult> AgentControl([FromBody] NetworkTelemetryAgentControlRequest? request, CancellationToken cancellationToken = default)
    {
        var actor = User.Identity?.IsAuthenticated == true
            ? (User.Identity?.Name ?? "system")
            : "system";

        var status = await _agentBridgeService.SendControlAsync(actor, request?.Action ?? "pause", cancellationToken);
        return Ok(status);
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

    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Auditor}")]
    [HttpGet("snapshots/{snapshotId:int}/devices")]
    public async Task<IActionResult> Devices(
        int snapshotId,
        [FromQuery] string? search = null,
        [FromQuery] string? riskLevel = null,
        [FromQuery] string? buildingExternalId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetObservationPageAsync(
            snapshotId,
            new NetworkTelemetryObservationQueryRequest
            {
                Search = search ?? string.Empty,
                RiskLevel = riskLevel ?? string.Empty,
                BuildingExternalId = buildingExternalId ?? string.Empty,
                ObservationType = "device",
                Page = page,
                PageSize = pageSize
            },
            cancellationToken);

        return Ok(result);
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

public class NetworkTelemetryAgentControlRequest
{
    public string Action { get; set; } = string.Empty;
}
