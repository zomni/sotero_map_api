using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Infrastructure;
using SoteroMap.API.Models;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/walking-routes")]
public class WalkingRoutesController : ControllerBase
{
    private readonly AppDbContext _context;

    public WalkingRoutesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll([FromQuery] string? campus, CancellationToken cancellationToken)
    {
        var normalizedCampus = string.IsNullOrWhiteSpace(campus) ? string.Empty : campus.Trim();

        var nodesQuery = _context.WalkingRouteNodes.AsNoTracking().AsQueryable();
        var edgesQuery = _context.WalkingRouteEdges.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(normalizedCampus))
        {
            nodesQuery = nodesQuery.Where(node => node.Campus == normalizedCampus);
            edgesQuery = edgesQuery.Where(edge => edge.Campus == normalizedCampus);
        }

        var nodes = await nodesQuery
            .OrderBy(node => node.ExternalId)
            .Select(node => new
            {
                node.ExternalId,
                node.Campus,
                node.Latitude,
                node.Longitude,
                node.Notes,
                node.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var edges = await edgesQuery
            .OrderBy(edge => edge.ExternalId)
            .Select(edge => new
            {
                edge.ExternalId,
                edge.Campus,
                edge.FromNodeExternalId,
                edge.ToNodeExternalId,
                edge.DistanceMeters,
                edge.Status,
                edge.Notes,
                edge.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(new { nodes, edges });
    }

    [HttpPost("paths")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> CreatePath(CreateWalkingRoutePathRequest request, CancellationToken cancellationToken)
    {
        var campus = string.IsNullOrWhiteSpace(request.Campus) ? "sotero" : request.Campus.Trim();
        var status = NormalizeStatus(request.Status);
        var notes = request.Notes?.Trim() ?? string.Empty;
        var username = User.FindFirstValue(ClaimTypes.Name) ?? "admin";
        var now = DateTime.UtcNow;

        var points = request.Coordinates
            .Where(point => point.Count >= 2)
            .Select(point => new RoutePoint(point[0], point[1]))
            .ToList();

        if (points.Count < 2)
            return BadRequest(new { message = "La ruta debe tener al menos 2 puntos." });

        var createdNodes = new List<WalkingRouteNode>();
        var createdEdges = new List<WalkingRouteEdge>();
        var existingNodes = await _context.WalkingRouteNodes
            .Where(node => node.Campus == campus)
            .ToListAsync(cancellationToken);
        var pathNodes = new List<WalkingRouteNode>();

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var nearbyNode = FindNearbyNode(existingNodes, point.Latitude, point.Longitude);
            if (nearbyNode is not null)
            {
                pathNodes.Add(nearbyNode);
                continue;
            }

            var node = new WalkingRouteNode
            {
                ExternalId = BuildNodeExternalId(),
                Campus = campus,
                Latitude = point.Latitude,
                Longitude = point.Longitude,
                Notes = notes,
                CreatedByUsername = username,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            createdNodes.Add(node);
            existingNodes.Add(node);
            pathNodes.Add(node);
            _context.WalkingRouteNodes.Add(node);
        }

        for (var index = 0; index < pathNodes.Count - 1; index++)
        {
            var from = pathNodes[index];
            var to = pathNodes[index + 1];
            if (from.ExternalId == to.ExternalId)
            {
                continue;
            }

            var edgeExists = await _context.WalkingRouteEdges.AnyAsync(edge =>
                edge.Campus == campus &&
                ((edge.FromNodeExternalId == from.ExternalId && edge.ToNodeExternalId == to.ExternalId) ||
                 (edge.FromNodeExternalId == to.ExternalId && edge.ToNodeExternalId == from.ExternalId)),
                cancellationToken);

            if (edgeExists)
            {
                continue;
            }

            var edge = new WalkingRouteEdge
            {
                ExternalId = BuildEdgeExternalId(),
                Campus = campus,
                FromNodeExternalId = from.ExternalId,
                ToNodeExternalId = to.ExternalId,
                DistanceMeters = CalculateDistanceMeters(from.Latitude, from.Longitude, to.Latitude, to.Longitude),
                Status = status,
                Notes = notes,
                CreatedByUsername = username,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            createdEdges.Add(edge);
            _context.WalkingRouteEdges.Add(edge);
        }

        if (createdEdges.Count == 0)
            return Conflict(new { message = "No se crearon tramos nuevos. Los puntos ya estaban conectados o eran demasiado cercanos." });

        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = string.Empty,
            EntityType = "walking-route",
            EntityId = createdEdges.First().ExternalId,
            ActionType = "route-created",
            Summary = $"Ruta caminable creada con {createdEdges.Count} tramo(s)",
            Details = $"Campus: {campus}; estado: {status}; notas: {notes}",
            ChangedByUsername = username,
            CreatedAtUtc = now
        });

        await _context.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetAll), new { campus }, new
        {
            nodes = createdNodes.Select(ToNodeDto),
            edges = createdEdges.Select(ToEdgeDto)
        });
    }

    [HttpPut("edges/{externalId}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> UpdateEdge(string externalId, UpdateWalkingRouteEdgeRequest request, CancellationToken cancellationToken)
    {
        var normalizedExternalId = Uri.UnescapeDataString(externalId ?? string.Empty).Trim();
        var edge = await _context.WalkingRouteEdges
            .FirstOrDefaultAsync(item => item.ExternalId == normalizedExternalId, cancellationToken);

        if (edge is null)
            return NotFound(new { message = "No existe el tramo de ruta." });

        var now = DateTime.UtcNow;
        edge.Status = NormalizeStatus(request.Status);
        edge.Notes = request.Notes?.Trim() ?? string.Empty;
        edge.UpdatedAtUtc = now;

        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = string.Empty,
            EntityType = "walking-route",
            EntityId = edge.ExternalId,
            ActionType = "route-updated",
            Summary = $"Tramo {edge.ExternalId} actualizado",
            Details = $"Estado: {edge.Status}; notas: {edge.Notes}",
            ChangedByUsername = User.FindFirstValue(ClaimTypes.Name) ?? "admin",
            CreatedAtUtc = now
        });

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(ToEdgeDto(edge));
    }

    [HttpDelete("edges/{externalId}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> DeleteEdge(string externalId, CancellationToken cancellationToken)
    {
        var normalizedExternalId = Uri.UnescapeDataString(externalId ?? string.Empty).Trim();
        var edge = await _context.WalkingRouteEdges
            .FirstOrDefaultAsync(item => item.ExternalId == normalizedExternalId, cancellationToken);

        if (edge is null)
            return NotFound(new { message = "No existe el tramo de ruta." });

        _context.WalkingRouteEdges.Remove(edge);
        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = string.Empty,
            EntityType = "walking-route",
            EntityId = edge.ExternalId,
            ActionType = "route-deleted",
            Summary = $"Tramo {edge.ExternalId} eliminado",
            Details = $"Campus: {edge.Campus}",
            ChangedByUsername = User.FindFirstValue(ClaimTypes.Name) ?? "admin",
            CreatedAtUtc = DateTime.UtcNow
        });

        await _context.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string NormalizeStatus(string? status)
    {
        var normalized = (status ?? "open").Trim().ToLowerInvariant();
        return normalized is "open" or "closed" or "restricted" ? normalized : "open";
    }

    private static string BuildNodeExternalId() => $"WRN-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..32];

    private static string BuildEdgeExternalId() => $"WRE-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..32];

    private static double CalculateDistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadiusMeters = 6371000;
        var dLat = ToRadians(lat2 - lat1);
        var dLng = ToRadians(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
            * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusMeters * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    private static WalkingRouteNode? FindNearbyNode(IEnumerable<WalkingRouteNode> nodes, double latitude, double longitude)
    {
        const double snapDistanceMeters = 8;
        return nodes
            .Select(node => new
            {
                Node = node,
                Distance = CalculateDistanceMeters(latitude, longitude, node.Latitude, node.Longitude)
            })
            .Where(item => item.Distance <= snapDistanceMeters)
            .OrderBy(item => item.Distance)
            .Select(item => item.Node)
            .FirstOrDefault();
    }

    private static object ToNodeDto(WalkingRouteNode node) => new
    {
        node.ExternalId,
        node.Campus,
        node.Latitude,
        node.Longitude,
        node.Notes,
        node.UpdatedAtUtc
    };

    private static object ToEdgeDto(WalkingRouteEdge edge) => new
    {
        edge.ExternalId,
        edge.Campus,
        edge.FromNodeExternalId,
        edge.ToNodeExternalId,
        edge.DistanceMeters,
        edge.Status,
        edge.Notes,
        edge.UpdatedAtUtc
    };

    private sealed record RoutePoint(double Longitude, double Latitude);
}
