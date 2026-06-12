using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/activity-log")]
[Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Auditor}")]
public class AuditLogController : ControllerBase
{
    private readonly AppDbContext _context;

    public AuditLogController(AppDbContext context)
    {
        _context = context;
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

        var items = await _context.AuditLogEntries
            .AsNoTracking()
            .Where(x => x.BuildingExternalId == buildingExternalId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.BuildingExternalId,
                x.EntityType,
                x.EntityId,
                x.ActionType,
                x.Resource,
                x.Result,
                x.Severity,
                x.Summary,
                x.Details,
                x.PreviousValue,
                x.NewValue,
                x.ChangedByUsername,
                x.ClientIp,
                x.UserAgent,
                x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(items);
    }
}
