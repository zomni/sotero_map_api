using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.Services;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/frontend-static-backup")]
[Authorize(Roles = AppRoles.Admin)]
public class FrontendStaticBackupController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;

    public FrontendStaticBackupController(AppDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    [HttpPost("save")]
    public async Task<IActionResult> Save([FromQuery] string? campus, CancellationToken cancellationToken)
    {
        var normalizedCampus = string.IsNullOrWhiteSpace(campus) ? "sotero" : campus.Trim();
        var dataDirectory = ResolveFrontendDataDirectory();

        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "No se pudo resolver la carpeta src/data del frontend. Configura FrontendDataPath."
            });
        }

        Directory.CreateDirectory(dataDirectory);

        var exportedAt = DateTime.UtcNow;
        var routesPayload = await BuildRoutesPayloadAsync(normalizedCampus, exportedAt, cancellationToken);
        var buildingsPayload = await BuildBuildingsPayloadAsync(normalizedCampus, exportedAt, cancellationToken);

        var routesPath = Path.Combine(dataDirectory, "walking_routes_backup.json");
        var buildingsPath = Path.Combine(dataDirectory, "sotero_buildings_backend_backup.json");

        await WriteJsonAsync(routesPath, routesPayload, cancellationToken);
        await WriteJsonAsync(buildingsPath, buildingsPayload, cancellationToken);

        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = string.Empty,
            EntityType = "frontend-static-backup",
            EntityId = normalizedCampus,
            ActionType = "frontend-backup-saved",
            Summary = "Respaldo estatico del mapa actualizado",
            Details = $"Archivos: {routesPath}; {buildingsPath}",
            ChangedByUsername = User.FindFirstValue(ClaimTypes.Name) ?? "admin",
            CreatedAtUtc = exportedAt
        });

        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            message = "Respaldo estatico guardado correctamente.",
            dataDirectory,
            files = new[] { routesPath, buildingsPath },
            routes = new
            {
                nodes = routesPayload.Nodes.Count,
                edges = routesPayload.Edges.Count
            },
            buildings = new
            {
                synced = buildingsPayload.SyncedBuildings.Count,
                manual = buildingsPayload.ManualBuildings.Count,
                geometry = buildingsPayload.GeometryOverrides.Count
            },
            savedAt = exportedAt
        });
    }

    private async Task<RoutesBackupPayload> BuildRoutesPayloadAsync(
        string campus,
        DateTime exportedAt,
        CancellationToken cancellationToken)
    {
        var edges = await _context.WalkingRouteEdges
            .AsNoTracking()
            .Where(edge => edge.Campus == campus)
            .OrderBy(edge => edge.ExternalId)
            .Select(edge => new RouteEdgeDto(
                edge.ExternalId,
                edge.Campus,
                edge.FromNodeExternalId,
                edge.ToNodeExternalId,
                edge.DistanceMeters,
                edge.Status,
                edge.Notes,
                edge.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        var connectedNodeIds = edges
            .SelectMany(edge => new[] { edge.FromNodeExternalId, edge.ToNodeExternalId })
            .ToHashSet(StringComparer.Ordinal);

        var nodes = await _context.WalkingRouteNodes
            .AsNoTracking()
            .Where(node => node.Campus == campus && connectedNodeIds.Contains(node.ExternalId))
            .OrderBy(node => node.ExternalId)
            .Select(node => new RouteNodeDto(
                node.ExternalId,
                node.Campus,
                node.Latitude,
                node.Longitude,
                node.Notes,
                node.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return new RoutesBackupPayload(nodes, edges, exportedAt);
    }

    private async Task<BuildingsBackupPayload> BuildBuildingsPayloadAsync(
        string campus,
        DateTime exportedAt,
        CancellationToken cancellationToken)
    {
        var syncedRows = await _context.SyncedBuildings
            .AsNoTracking()
            .Where(building => (building.ManualCampus != "" ? building.ManualCampus : building.Campus) == campus)
            .OrderBy(building => building.ManualDisplayName != "" ? building.ManualDisplayName : building.DisplayName)
            .ToListAsync(cancellationToken);
        var syncedBuildings = syncedRows
            .Select(building => new SyncedBuildingDto(
                building.Id,
                building.ExternalId,
                building.ManualCampus != "" ? building.ManualCampus : building.Campus,
                building.ManualDisplayName != "" ? building.ManualDisplayName : building.DisplayName,
                building.ShortName,
                building.RealName,
                building.Type,
                building.ResponsibleArea,
                building.CentroidLatitude,
                building.CentroidLongitude,
                building.HasInteriorMap,
                building.HasInventory,
                building.MappingStatus,
                building.InventoryStatus,
                building.IsDeleted,
                BuildingFloorNormalizer.NormalizeJson(building.ManualFloorsJson != "" ? building.ManualFloorsJson : building.FloorsJson),
                building.SyncedAtUtc))
            .ToList();

        var manualRows = await _context.ManualBuildings
            .AsNoTracking()
            .Where(building => building.Campus == campus)
            .OrderBy(building => building.DisplayName)
            .ToListAsync(cancellationToken);
        var manualBuildings = manualRows
            .Select(building => new ManualBuildingDto(
                building.Id,
                building.ExternalId,
                building.Campus,
                building.DisplayName,
                building.Type,
                building.Notes,
                BuildingFloorNormalizer.NormalizeJson(building.FloorsJson),
                building.GeometryJson,
                building.CentroidLatitude,
                building.CentroidLongitude,
                building.CreatedByUsername,
                building.CreatedAtUtc,
                building.UpdatedAtUtc))
            .ToList();

        var geometryOverrides = await _context.BuildingGeometryOverrides
            .AsNoTracking()
            .OrderBy(item => item.BuildingExternalId)
            .Select(item => new BuildingGeometryOverrideDto(
                item.BuildingExternalId,
                item.GeometryJson,
                item.CentroidLatitude,
                item.CentroidLongitude,
                item.UpdatedByUsername,
                item.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return new BuildingsBackupPayload(campus, syncedBuildings, manualBuildings, geometryOverrides, exportedAt);
    }

    private string? ResolveFrontendDataDirectory()
    {
        var configuredPath = _configuration["FrontendDataPath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var dockerPath = "/app/frontend-data";
        if (Directory.Exists(dockerPath))
        {
            return dockerPath;
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var siblingFrontendData = Path.Combine(current.FullName, "..", "sotero_map", "src", "data");
            if (Directory.Exists(siblingFrontendData))
            {
                return Path.GetFullPath(siblingFrontendData);
            }

            var directFrontendData = Path.Combine(current.FullName, "sotero_map", "src", "data");
            if (Directory.Exists(directFrontendData))
            {
                return Path.GetFullPath(directFrontendData);
            }

            current = current.Parent;
        }

        return null;
    }

    private static async Task WriteJsonAsync(string path, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await System.IO.File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private sealed record RoutesBackupPayload(
        IReadOnlyList<RouteNodeDto> Nodes,
        IReadOnlyList<RouteEdgeDto> Edges,
        DateTime SavedAt);

    private sealed record BuildingsBackupPayload(
        string Campus,
        IReadOnlyList<SyncedBuildingDto> SyncedBuildings,
        IReadOnlyList<ManualBuildingDto> ManualBuildings,
        IReadOnlyList<BuildingGeometryOverrideDto> GeometryOverrides,
        DateTime SavedAt);

    private sealed record RouteNodeDto(
        string ExternalId,
        string Campus,
        double Latitude,
        double Longitude,
        string Notes,
        DateTime UpdatedAtUtc);

    private sealed record RouteEdgeDto(
        string ExternalId,
        string Campus,
        string FromNodeExternalId,
        string ToNodeExternalId,
        double DistanceMeters,
        string Status,
        string Notes,
        DateTime UpdatedAtUtc);

    private sealed record SyncedBuildingDto(
        int Id,
        string ExternalId,
        string Campus,
        string DisplayName,
        string ShortName,
        string RealName,
        string Type,
        string ResponsibleArea,
        double? CentroidLatitude,
        double? CentroidLongitude,
        bool HasInteriorMap,
        bool HasInventory,
        string MappingStatus,
        string InventoryStatus,
        bool IsDeleted,
        string FloorsJson,
        DateTime SyncedAtUtc);

    private sealed record ManualBuildingDto(
        int Id,
        string ExternalId,
        string Campus,
        string DisplayName,
        string Type,
        string Notes,
        string FloorsJson,
        string GeometryJson,
        double? CentroidLatitude,
        double? CentroidLongitude,
        string CreatedByUsername,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    private sealed record BuildingGeometryOverrideDto(
        string BuildingExternalId,
        string GeometryJson,
        double? CentroidLatitude,
        double? CentroidLongitude,
        string UpdatedByUsername,
        DateTime UpdatedAtUtc);
}
