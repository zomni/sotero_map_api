using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/activity-log")]
[Authorize]
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
                x.Summary,
                x.Details,
                x.ChangedByUsername,
                x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(items);
    }
}
