using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Infrastructure;
using SoteroMap.API.Models;
using SoteroMap.API.Services;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/manual-buildings")]
public class ManualBuildingsController : ControllerBase
{
    private readonly AppDbContext _context;

    public ManualBuildingsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll([FromQuery] string? campus, CancellationToken cancellationToken)
    {
        var manualBuildings = await _context.ManualBuildings
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var manualIds = manualBuildings.Select(building => building.ExternalId).ToList();
        var syncedBuildingsById = await _context.SyncedBuildings
            .AsNoTracking()
            .Where(building => manualIds.Contains(building.ExternalId))
            .ToDictionaryAsync(building => building.ExternalId, cancellationToken);

        var buildings = manualBuildings
            .Select(building =>
            {
                syncedBuildingsById.TryGetValue(building.ExternalId, out var syncedBuilding);
                return new
                {
                    building.Id,
                    building.ExternalId,
                    Campus = syncedBuilding?.EffectiveCampus ?? building.Campus,
                    DisplayName = syncedBuilding?.EffectiveDisplayName ?? building.DisplayName,
                    Type = syncedBuilding?.Type ?? building.Type,
                    Notes = syncedBuilding?.Notes ?? building.Notes,
                    FloorsJson = BuildingFloorNormalizer.NormalizeJson(syncedBuilding?.EffectiveFloorsJson ?? building.FloorsJson),
                    building.GeometryJson,
                    building.CentroidLatitude,
                    building.CentroidLongitude,
                    building.CreatedByUsername,
                    building.CreatedAtUtc,
                    building.UpdatedAtUtc,
                    IsDeleted = syncedBuilding?.IsDeleted ?? false
                };
            })
            .Where(building => !building.IsDeleted)
            .Where(building => string.IsNullOrWhiteSpace(campus) || building.Campus == campus)
            .OrderBy(building => building.DisplayName)
            .ToList();

        return Ok(buildings);
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Create(CreateManualBuildingRequest request, CancellationToken cancellationToken)
    {
        var externalId = (request.ExternalId ?? string.Empty).Trim();
        var displayName = (request.DisplayName ?? string.Empty).Trim();
        var campus = string.IsNullOrWhiteSpace(request.Campus) ? "sotero" : request.Campus.Trim();
        var type = string.IsNullOrWhiteSpace(request.Type) ? "manual" : request.Type.Trim();

        if (string.IsNullOrWhiteSpace(externalId))
            return BadRequest(new { message = "El ID del edificio es obligatorio." });

        if (string.IsNullOrWhiteSpace(displayName))
            return BadRequest(new { message = "El nombre del edificio es obligatorio." });

        if (request.Coordinates.Count < 3)
            return BadRequest(new { message = "El poligono debe tener al menos 3 puntos." });

        var existsInManual = await _context.ManualBuildings.AnyAsync(
            building => building.ExternalId == externalId,
            cancellationToken);
        var existsInSynced = await _context.SyncedBuildings.AnyAsync(
            building => building.ExternalId == externalId,
            cancellationToken);

        if (existsInManual || existsInSynced)
            return Conflict(new { message = $"Ya existe un edificio con ID {externalId}." });

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

        var now = DateTime.UtcNow;
        var building = new ManualBuilding
        {
            ExternalId = externalId,
            Campus = campus,
            DisplayName = displayName,
            Type = type,
            Notes = request.Notes?.Trim() ?? string.Empty,
            FloorsJson = BuildingFloorNormalizer.NormalizeCsv(request.FloorsCsv),
            GeometryJson = JsonSerializer.Serialize(geometry),
            CentroidLatitude = centroidLatitude,
            CentroidLongitude = centroidLongitude,
            CreatedByUsername = User.FindFirstValue(ClaimTypes.Name) ?? "admin",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var syncedBuilding = new SyncedBuilding
        {
            ExternalId = externalId,
            Campus = campus,
            Slug = externalId.ToLowerInvariant().Replace("_", "-"),
            DisplayName = displayName,
            ShortName = externalId,
            RealName = displayName,
            Type = type,
            ResponsibleArea = string.Empty,
            Notes = request.Notes?.Trim() ?? string.Empty,
            SourceId = "manual",
            CentroidLatitude = centroidLatitude,
            CentroidLongitude = centroidLongitude,
            HasInteriorMap = false,
            HasInventory = false,
            MappingStatus = "manual",
            InventoryStatus = string.Empty,
            OperationalNotes = string.Empty,
            TechnicalNotes = string.Empty,
            LastUpdate = now.ToString("O"),
            FloorsJson = building.FloorsJson,
            FloorSummariesJson = "[]",
            TagsJson = "[]",
            ContactsJson = "[]",
            SyncedAtUtc = now
        };

        _context.ManualBuildings.Add(building);
        _context.SyncedBuildings.Add(syncedBuilding);
        await _context.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetAll), new { id = building.Id }, new
        {
            building.Id,
            building.ExternalId,
            building.Campus,
            building.DisplayName,
            building.Type,
            building.Notes,
            building.FloorsJson,
            building.GeometryJson,
            building.CentroidLatitude,
            building.CentroidLongitude,
            building.CreatedByUsername,
            building.CreatedAtUtc,
            building.UpdatedAtUtc
        });
    }

    [HttpDelete("{externalId}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Delete(string externalId, CancellationToken cancellationToken)
    {
        var normalizedExternalId = Uri.UnescapeDataString(externalId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedExternalId))
            return BadRequest(new { message = "El ID del edificio es obligatorio." });

        var manualBuilding = await _context.ManualBuildings
            .FirstOrDefaultAsync(building => building.ExternalId == normalizedExternalId, cancellationToken);

        if (manualBuilding is null)
            return NotFound(new { message = "Solo se pueden eliminar edificios creados manualmente desde el mapa." });

        var assignedCount = await _context.ImportedInventoryItems
            .CountAsync(item => item.AssignedBuildingExternalId == normalizedExternalId, cancellationToken);

        if (assignedCount > 0)
        {
            return Conflict(new
            {
                message = $"No se puede eliminar el edificio porque tiene {assignedCount} equipo(s) asignado(s). Reasigna o desasigna esos equipos antes de eliminarlo."
            });
        }

        var syncedBuilding = await _context.SyncedBuildings
            .FirstOrDefaultAsync(
                building => building.ExternalId == normalizedExternalId && building.SourceId == "manual",
                cancellationToken);

        _context.ManualBuildings.Remove(manualBuilding);
        if (syncedBuilding is not null)
        {
            _context.SyncedBuildings.Remove(syncedBuilding);
        }

        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = normalizedExternalId,
            EntityType = "manual-building",
            EntityId = normalizedExternalId,
            ActionType = "delete-building",
            Summary = $"Edificio manual eliminado del mapa: {manualBuilding.DisplayName}",
            Details = "El edificio manual fue eliminado desde el mapa.",
            ChangedByUsername = User.FindFirstValue(ClaimTypes.Name) ?? "sistema",
            CreatedAtUtc = DateTime.UtcNow
        });

        await _context.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
