using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/synced-equipments")]
[Authorize]
public class SyncedEquipmentsController : ControllerBase
{
    private readonly AppDbContext _context;

    public SyncedEquipmentsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? buildingExternalId,
        [FromQuery] string? roomExternalId,
        [FromQuery] string? type,
        [FromQuery] int? floor,
        CancellationToken cancellationToken)
    {
        var query = _context.SyncedEquipments.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(buildingExternalId))
        {
            query = query.Where(e => e.BuildingExternalId == buildingExternalId);
        }

        if (!string.IsNullOrWhiteSpace(roomExternalId))
        {
            query = query.Where(e => e.RoomExternalId == roomExternalId);
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(e => e.Type == type);
        }

        if (floor.HasValue)
        {
            query = query.Where(e => e.Floor == floor.Value);
        }

        var equipments = await query
            .OrderBy(e => e.BuildingExternalId)
            .ThenBy(e => e.RoomExternalId)
            .ThenBy(e => e.Name)
            .Select(e => new
            {
                e.Id,
                e.ExternalId,
                e.BuildingExternalId,
                e.RoomExternalId,
                e.Floor,
                e.Type,
                e.Subtype,
                e.Name,
                e.InventoryCode,
                e.SerialNumber,
                e.Brand,
                e.Model,
                e.IpAddress,
                e.AssignedTo,
                e.Status,
                e.NetworkStatus,
                e.Source,
                e.SyncedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(equipments);
    }
}
