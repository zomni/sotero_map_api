using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Models;
using SoteroMap.API.Services;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/activity-log")]
[Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Auditor}")]
public class AuditLogController : ControllerBase
{
    private readonly AuditLogService _auditLogService;

    public AuditLogController(AuditLogService auditLogService)
    {
        _auditLogService = auditLogService;
    }

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] AuditLogQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _auditLogService.QueryAsync(request, cancellationToken);
        return Ok(result);
    }

    [AllowAnonymous]
    [HttpGet("/api/activity-log/building")]
    public async Task<IActionResult> GetBuildingHistory(
        [FromQuery] string buildingExternalId,
        [FromQuery] int take = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(buildingExternalId))
        {
            return Ok(Array.Empty<object>());
        }

        take = Math.Clamp(take, 1, 20);

        var result = await _auditLogService.QueryAsync(new AuditLogQueryRequest
        {
            BuildingExternalId = buildingExternalId,
            Page = 1,
            PageSize = take
        }, cancellationToken);

        return Ok(result.Items);
    }
}
