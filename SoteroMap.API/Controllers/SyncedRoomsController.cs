using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/synced-rooms")]
[Authorize]
public class SyncedRoomsController : ControllerBase
{
    private readonly AppDbContext _context;

    public SyncedRoomsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? buildingExternalId,
        [FromQuery] int? floor,
        CancellationToken cancellationToken)
    {
        var query = _context.SyncedRooms.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(buildingExternalId))
        {
            query = query.Where(r => r.BuildingExternalId == buildingExternalId);
        }

        if (floor.HasValue)
        {
            query = query.Where(r => (r.ManualFloor ?? r.Floor) == floor.Value);
        }

        var rooms = await query
            .OrderBy(r => r.BuildingExternalId)
            .ThenBy(r => r.ManualFloor ?? r.Floor)
            .ThenBy(r => r.ManualName != "" ? r.ManualName : r.Name)
            .Select(r => new
            {
                r.Id,
                r.ExternalId,
                r.BuildingExternalId,
                Floor = r.ManualFloor ?? r.Floor,
                Name = r.ManualName != "" ? r.ManualName : r.Name,
                r.ShortName,
                r.Type,
                r.Unit,
                r.Service,
                r.Status,
                r.DevicesCount,
                r.ResponsibleArea,
                r.ResponsiblePerson,
                r.SyncedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(rooms);
    }
}
