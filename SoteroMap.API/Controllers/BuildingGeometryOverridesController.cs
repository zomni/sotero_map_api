using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Infrastructure;
using SoteroMap.API.Models;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/building-geometry-overrides")]
public class BuildingGeometryOverridesController : ControllerBase
{
    private readonly AppDbContext _context;

    public BuildingGeometryOverridesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var overrides = await _context.BuildingGeometryOverrides
            .AsNoTracking()
            .OrderBy(item => item.BuildingExternalId)
            .Select(item => new
            {
                item.BuildingExternalId,
                item.GeometryJson,
                item.CentroidLatitude,
                item.CentroidLongitude,
                item.UpdatedByUsername,
                item.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(overrides);
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Save(SaveBuildingGeometryOverrideRequest request, CancellationToken cancellationToken)
    {
        var externalId = (request.BuildingExternalId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(externalId))
            return BadRequest(new { message = "El ID del edificio es obligatorio." });

        var building = await _context.SyncedBuildings
            .FirstOrDefaultAsync(item => item.ExternalId == externalId, cancellationToken);

        if (building is null)
            return NotFound(new { message = $"No existe el edificio {externalId}." });

        var ring = request.Coordinates
            .Where(point => point.Count >= 2)
            .Select(point => new[] { point[0], point[1] })
            .ToList();

        if (ring.Count < 3)
            return BadRequest(new { message = "El poligono debe tener al menos 3 puntos validos." });

        if (ring[0][0] != ring[^1][0] || ring[0][1] != ring[^1][1])
        {
            ring.Add(new[] { ring[0][0], ring[0][1] });
        }

        var centroidLongitude = ring.Take(ring.Count - 1).Average(point => point[0]);
        var centroidLatitude = ring.Take(ring.Count - 1).Average(point => point[1]);
        var geometry = new
        {
            type = "Polygon",
            coordinates = new[] { ring }
        };
        var geometryJson = JsonSerializer.Serialize(geometry);
        var now = DateTime.UtcNow;
        var username = User.FindFirstValue(ClaimTypes.Name) ?? "admin";

        var geometryOverride = await _context.BuildingGeometryOverrides
            .FirstOrDefaultAsync(item => item.BuildingExternalId == externalId, cancellationToken);

        if (geometryOverride is null)
        {
            geometryOverride = new BuildingGeometryOverride
            {
                BuildingExternalId = externalId
            };
            _context.BuildingGeometryOverrides.Add(geometryOverride);
        }

        geometryOverride.GeometryJson = geometryJson;
        geometryOverride.CentroidLatitude = centroidLatitude;
        geometryOverride.CentroidLongitude = centroidLongitude;
        geometryOverride.UpdatedByUsername = username;
        geometryOverride.UpdatedAtUtc = now;

        building.CentroidLatitude = centroidLatitude;
        building.CentroidLongitude = centroidLongitude;

        var manualBuilding = await _context.ManualBuildings
            .FirstOrDefaultAsync(item => item.ExternalId == externalId, cancellationToken);

        if (manualBuilding is not null)
        {
            manualBuilding.GeometryJson = geometryJson;
            manualBuilding.CentroidLatitude = centroidLatitude;
            manualBuilding.CentroidLongitude = centroidLongitude;
            manualBuilding.UpdatedAtUtc = now;
        }

        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = externalId,
            EntityType = "synced-building",
            EntityId = externalId,
            ActionType = "geometry-updated",
            Summary = $"Geometria actualizada en {building.EffectiveDisplayName}",
            Details = $"Poligono actualizado con {ring.Count - 1} puntos.",
            ChangedByUsername = username,
            CreatedAtUtc = now
        });

        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            externalId,
            geometryJson,
            centroidLatitude,
            centroidLongitude,
            updatedAtUtc = now
        });
    }
}
