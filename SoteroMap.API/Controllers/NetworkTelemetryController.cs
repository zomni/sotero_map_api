using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Models;
using SoteroMap.API.Services;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/network-telemetry")]
[Authorize]
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
        var model = await _service.GetDashboardAsync(10, null, cancellationToken);
        return Ok(model);
    }

    [HttpGet("latest")]
    public async Task<IActionResult> Latest([FromQuery] int take = 10, CancellationToken cancellationToken = default)
    {
        var snapshots = await _service.GetRecentSnapshotsAsync(take, cancellationToken);
        return Ok(snapshots);
    }

    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Auditor}")]
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

    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Auditor}")]
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

    [HttpGet("snapshots/{snapshotId:int}/devices")]
    public async Task<IActionResult> Devices(
        int snapshotId,
        [FromQuery] string? search = null,
        [FromQuery] string? riskLevel = null,
        [FromQuery] string? buildingExternalId = null,
        [FromQuery] string? subnetCidr = null,
        [FromQuery] string? onlineState = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetObservationPageAsync(
            snapshotId,
            new NetworkTelemetryObservationQueryRequest
            {
                Search = search ?? string.Empty,
                RiskLevel = riskLevel ?? string.Empty,
                BuildingExternalId = buildingExternalId ?? string.Empty,
                SubnetCidr = subnetCidr ?? string.Empty,
                OnlineState = onlineState ?? string.Empty,
                ObservationType = "device",
                SortBy = sortBy ?? "risk",
                SortDirection = sortDirection ?? "desc",
                Page = page,
                PageSize = pageSize
            },
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("snapshots")]
    public async Task<IActionResult> Snapshots(
        [FromQuery] string? search = null,
        [FromQuery] string? triggerType = null,
        [FromQuery] string? weekday = null,
        [FromQuery] string? timeSlot = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetSnapshotPageAsync(
            new NetworkTelemetrySnapshotQueryRequest
            {
                Search = search ?? string.Empty,
                TriggerType = triggerType ?? string.Empty,
                Weekday = weekday ?? string.Empty,
                TimeSlot = timeSlot ?? string.Empty,
                SortBy = sortBy ?? "observedAt",
                SortDirection = sortDirection ?? "desc",
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
