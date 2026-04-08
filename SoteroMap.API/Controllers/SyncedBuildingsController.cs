using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/synced-buildings")]
[Authorize]
public class SyncedBuildingsController : ControllerBase
{
    private readonly AppDbContext _context;

    public SyncedBuildingsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll([FromQuery] string? campus, CancellationToken cancellationToken)
    {
        var query = _context.SyncedBuildings.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(campus))
        {
            query = query.Where(b => (b.ManualCampus != "" ? b.ManualCampus : b.Campus) == campus);
        }

        var buildings = await query
            .OrderBy(b => b.ManualDisplayName != "" ? b.ManualDisplayName : b.DisplayName)
            .Select(b => new
            {
                b.Id,
                b.ExternalId,
                Campus = b.ManualCampus != "" ? b.ManualCampus : b.Campus,
                DisplayName = b.ManualDisplayName != "" ? b.ManualDisplayName : b.DisplayName,
                b.ShortName,
                b.RealName,
                b.Type,
                b.ResponsibleArea,
                b.CentroidLatitude,
                b.CentroidLongitude,
                b.HasInteriorMap,
                b.HasInventory,
                b.MappingStatus,
                b.InventoryStatus,
                FloorsJson = b.ManualFloorsJson != "" ? b.ManualFloorsJson : b.FloorsJson,
                b.SyncedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(buildings);
    }
}
