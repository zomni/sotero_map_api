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
        var connectedNodeIds = edges
            .SelectMany(edge => new[] { edge.FromNodeExternalId, edge.ToNodeExternalId })
            .ToHashSet(StringComparer.Ordinal);

        var nodes = await nodesQuery
            .Where(node => connectedNodeIds.Contains(node.ExternalId))
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
        var existingEdges = await _context.WalkingRouteEdges
            .Where(edge => edge.Campus == campus)
            .ToListAsync(cancellationToken);
        var pathNodes = new List<WalkingRouteNode>();

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var disableSnap = request.DisableLastPointSnap && index == points.Count - 1;

            if (!disableSnap)
            {
                var nearbyNode = FindNearbyNode(existingNodes, point.Latitude, point.Longitude);
                if (nearbyNode is not null)
                {
                    pathNodes.Add(nearbyNode);
                    continue;
                }

                var nearbyEdge = FindNearbyEdge(existingEdges, existingNodes, point.Latitude, point.Longitude);
                if (nearbyEdge is not null)
                {
                    var splitNode = CreateNode(campus, nearbyEdge.Latitude, nearbyEdge.Longitude, notes, username, now);
                    createdNodes.Add(splitNode);
                    existingNodes.Add(splitNode);
                    pathNodes.Add(splitNode);
                    _context.WalkingRouteNodes.Add(splitNode);

                    SplitEdge(nearbyEdge.Edge, splitNode, existingNodes, existingEdges, createdEdges, username, now);
                    continue;
                }
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

            if (EdgeExists(existingEdges, campus, from.ExternalId, to.ExternalId))
            {
                continue;
            }

            var edge = CreateEdge(campus, from, to, status, notes, username, now);

            createdEdges.Add(edge);
            existingEdges.Add(edge);
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

    [HttpPut("nodes/{externalId}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> UpdateNode(string externalId, UpdateWalkingRouteNodeRequest request, CancellationToken cancellationToken)
    {
        var normalizedExternalId = Uri.UnescapeDataString(externalId ?? string.Empty).Trim();
        var node = await _context.WalkingRouteNodes
            .FirstOrDefaultAsync(item => item.ExternalId == normalizedExternalId, cancellationToken);

        if (node is null)
            return NotFound(new { message = "No existe el vertice de ruta." });

        var now = DateTime.UtcNow;
        var username = User.FindFirstValue(ClaimTypes.Name) ?? "admin";
        var nodes = await _context.WalkingRouteNodes
            .Where(item => item.Campus == node.Campus)
            .ToListAsync(cancellationToken);
        var edges = await _context.WalkingRouteEdges
            .Where(item => item.Campus == node.Campus)
            .ToListAsync(cancellationToken);

        var nearbyNode = FindNearbyNode(
            nodes.Where(item => item.ExternalId != node.ExternalId),
            request.Latitude,
            request.Longitude);

        if (nearbyNode is not null)
        {
            MergeNodes(node, nearbyNode, edges, nodes, now);
            _context.AuditLogEntries.Add(new AuditLogEntry
            {
                BuildingExternalId = string.Empty,
                EntityType = "walking-route",
                EntityId = nearbyNode.ExternalId,
                ActionType = "route-node-merged",
                Summary = $"Vertice {node.ExternalId} unido con {nearbyNode.ExternalId}",
                Details = $"Campus: {node.Campus}",
                ChangedByUsername = username,
                CreatedAtUtc = now
            });

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(new { merged = true, node = ToNodeDto(nearbyNode) });
        }

        var nearbyEdge = FindNearbyEdge(edges, nodes, request.Latitude, request.Longitude, node.ExternalId);
        if (nearbyEdge is not null)
        {
            node.Latitude = nearbyEdge.Latitude;
            node.Longitude = nearbyEdge.Longitude;
            node.UpdatedAtUtc = now;
            SplitEdge(nearbyEdge.Edge, node, nodes, edges, new List<WalkingRouteEdge>(), username, now);
            RecalculateNodeEdges(node, edges, nodes);

            _context.AuditLogEntries.Add(new AuditLogEntry
            {
                BuildingExternalId = string.Empty,
                EntityType = "walking-route",
                EntityId = node.ExternalId,
                ActionType = "route-node-attached",
                Summary = $"Vertice {node.ExternalId} unido a un tramo existente",
                Details = $"Campus: {node.Campus}",
                ChangedByUsername = username,
                CreatedAtUtc = now
            });

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(new { merged = false, attached = true, node = ToNodeDto(node) });
        }

        node.Latitude = request.Latitude;
        node.Longitude = request.Longitude;
        node.UpdatedAtUtc = now;
        RecalculateNodeEdges(node, edges, nodes);

        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = string.Empty,
            EntityType = "walking-route",
            EntityId = node.ExternalId,
            ActionType = "route-node-moved",
            Summary = $"Vertice {node.ExternalId} movido",
            Details = $"Lat: {node.Latitude}; Lng: {node.Longitude}",
            ChangedByUsername = username,
            CreatedAtUtc = now
        });

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new { merged = false, node = ToNodeDto(node) });
    }

    [HttpPost("nodes/{externalId}/split")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> SplitNode(string externalId, SplitWalkingRouteNodeRequest request, CancellationToken cancellationToken)
    {
        var normalizedExternalId = Uri.UnescapeDataString(externalId ?? string.Empty).Trim();
        var node = await _context.WalkingRouteNodes
            .FirstOrDefaultAsync(item => item.ExternalId == normalizedExternalId, cancellationToken);

        if (node is null)
            return NotFound(new { message = "No existe el vertice de ruta." });

        var connectedEdges = await _context.WalkingRouteEdges
            .Where(edge => edge.Campus == node.Campus &&
                (edge.FromNodeExternalId == node.ExternalId || edge.ToNodeExternalId == node.ExternalId))
            .OrderBy(edge => edge.ExternalId)
            .ToListAsync(cancellationToken);

        if (connectedEdges.Count < 2)
            return BadRequest(new { message = "Este vertice no necesita separarse porque tiene menos de 2 tramos conectados." });

        var now = DateTime.UtcNow;
        var username = User.FindFirstValue(ClaimTypes.Name) ?? "admin";
        var splitNodes = new List<WalkingRouteNode>();
        const double spreadDegrees = 0.000018;

        for (var index = 0; index < connectedEdges.Count; index++)
        {
            var angle = (Math.PI * 2 * index) / connectedEdges.Count;
            var splitNode = CreateNode(
                node.Campus,
                node.Latitude + Math.Sin(angle) * spreadDegrees,
                node.Longitude + Math.Cos(angle) * spreadDegrees,
                node.Notes,
                username,
                now);
            var edge = connectedEdges[index];

            if (edge.FromNodeExternalId == node.ExternalId)
                edge.FromNodeExternalId = splitNode.ExternalId;
            else
                edge.ToNodeExternalId = splitNode.ExternalId;

            edge.UpdatedAtUtc = now;
            splitNodes.Add(splitNode);
            _context.WalkingRouteNodes.Add(splitNode);
        }

        _context.WalkingRouteNodes.Remove(node);

        var allNodes = await _context.WalkingRouteNodes
            .Where(item => item.Campus == node.Campus && item.ExternalId != node.ExternalId)
            .ToListAsync(cancellationToken);
        allNodes.AddRange(splitNodes);
        foreach (var splitNode in splitNodes)
        {
            RecalculateNodeEdges(splitNode, connectedEdges, allNodes);
        }

        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = string.Empty,
            EntityType = "walking-route",
            EntityId = node.ExternalId,
            ActionType = "route-node-split",
            Summary = $"Vertice {node.ExternalId} separado",
            Details = $"Tramos separados: {connectedEdges.Count}",
            ChangedByUsername = username,
            CreatedAtUtc = now
        });

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new { nodes = splitNodes.Select(ToNodeDto), edges = connectedEdges.Select(ToEdgeDto) });
    }

    [HttpPost("restore")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> RestoreNetwork(RestoreWalkingRouteNetworkRequest request, CancellationToken cancellationToken)
    {
        var campus = string.IsNullOrWhiteSpace(request.Campus) ? "sotero" : request.Campus.Trim();
        var username = User.FindFirstValue(ClaimTypes.Name) ?? "admin";
        var now = DateTime.UtcNow;
        var nodeIds = request.Nodes
            .Select(node => node.ExternalId?.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        var existingEdges = await _context.WalkingRouteEdges
            .Where(edge => edge.Campus == campus)
            .ToListAsync(cancellationToken);
        var existingNodes = await _context.WalkingRouteNodes
            .Where(node => node.Campus == campus)
            .ToListAsync(cancellationToken);

        _context.WalkingRouteEdges.RemoveRange(existingEdges);
        _context.WalkingRouteNodes.RemoveRange(existingNodes);

        var restoredNodes = request.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.ExternalId))
            .Select(node => new WalkingRouteNode
            {
                ExternalId = node.ExternalId!.Trim(),
                Campus = campus,
                Latitude = node.Latitude,
                Longitude = node.Longitude,
                Notes = node.Notes?.Trim() ?? string.Empty,
                CreatedByUsername = username,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            })
            .ToList();
        var restoredEdges = request.Edges
            .Where(edge =>
                !string.IsNullOrWhiteSpace(edge.ExternalId) &&
                !string.IsNullOrWhiteSpace(edge.FromNodeExternalId) &&
                !string.IsNullOrWhiteSpace(edge.ToNodeExternalId) &&
                nodeIds.Contains(edge.FromNodeExternalId!.Trim()) &&
                nodeIds.Contains(edge.ToNodeExternalId!.Trim()) &&
                edge.FromNodeExternalId!.Trim() != edge.ToNodeExternalId!.Trim())
            .Select(edge => new WalkingRouteEdge
            {
                ExternalId = edge.ExternalId!.Trim(),
                Campus = campus,
                FromNodeExternalId = edge.FromNodeExternalId!.Trim(),
                ToNodeExternalId = edge.ToNodeExternalId!.Trim(),
                DistanceMeters = edge.DistanceMeters,
                Status = NormalizeStatus(edge.Status),
                Notes = edge.Notes?.Trim() ?? string.Empty,
                CreatedByUsername = username,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            })
            .ToList();

        _context.WalkingRouteNodes.AddRange(restoredNodes);
        _context.WalkingRouteEdges.AddRange(restoredEdges);
        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = string.Empty,
            EntityType = "walking-route",
            EntityId = campus,
            ActionType = "route-network-restored",
            Summary = "Ultima accion de rutas deshecha",
            Details = $"Nodos restaurados: {restoredNodes.Count}; tramos restaurados: {restoredEdges.Count}",
            ChangedByUsername = username,
            CreatedAtUtc = now
        });

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new { nodes = restoredNodes.Select(ToNodeDto), edges = restoredEdges.Select(ToEdgeDto) });
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
        await RemoveOrphanRouteNodesAsync(edge.Campus, edge.ExternalId, cancellationToken);
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

    private async Task RemoveOrphanRouteNodesAsync(string campus, string deletedEdgeExternalId, CancellationToken cancellationToken)
    {
        var remainingEdges = await _context.WalkingRouteEdges
            .Where(edge => edge.Campus == campus && edge.ExternalId != deletedEdgeExternalId)
            .ToListAsync(cancellationToken);
        var connectedSet = remainingEdges
            .SelectMany(edge => new[] { edge.FromNodeExternalId, edge.ToNodeExternalId })
            .ToHashSet(StringComparer.Ordinal);
        var orphanNodes = await _context.WalkingRouteNodes
            .Where(node => node.Campus == campus)
            .ToListAsync(cancellationToken);

        _context.WalkingRouteNodes.RemoveRange(orphanNodes.Where(node => !connectedSet.Contains(node.ExternalId)));
    }

    private static string NormalizeStatus(string? status)
    {
        var normalized = (status ?? "open").Trim().ToLowerInvariant();
        return normalized is "open" or "closed" or "restricted" ? normalized : "open";
    }

    private static string BuildNodeExternalId() => $"WRN-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..32];

    private static string BuildEdgeExternalId() => $"WRE-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..32];

    private static WalkingRouteNode CreateNode(
        string campus,
        double latitude,
        double longitude,
        string notes,
        string username,
        DateTime now) => new()
    {
        ExternalId = BuildNodeExternalId(),
        Campus = campus,
        Latitude = latitude,
        Longitude = longitude,
        Notes = notes,
        CreatedByUsername = username,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static WalkingRouteEdge CreateEdge(
        string campus,
        WalkingRouteNode from,
        WalkingRouteNode to,
        string status,
        string notes,
        string username,
        DateTime now) => new()
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

    private void SplitEdge(
        WalkingRouteEdge edge,
        WalkingRouteNode splitNode,
        List<WalkingRouteNode> nodes,
        List<WalkingRouteEdge> edges,
        List<WalkingRouteEdge> createdEdges,
        string username,
        DateTime now)
    {
        var from = nodes.FirstOrDefault(node => node.ExternalId == edge.FromNodeExternalId);
        var to = nodes.FirstOrDefault(node => node.ExternalId == edge.ToNodeExternalId);
        if (from is null || to is null)
            return;

        _context.WalkingRouteEdges.Remove(edge);
        edges.Remove(edge);

        var first = CreateEdge(edge.Campus, from, splitNode, edge.Status, edge.Notes, username, now);
        var second = CreateEdge(edge.Campus, splitNode, to, edge.Status, edge.Notes, username, now);
        createdEdges.Add(first);
        createdEdges.Add(second);
        edges.Add(first);
        edges.Add(second);
        _context.WalkingRouteEdges.AddRange(first, second);
    }

    private static bool EdgeExists(IEnumerable<WalkingRouteEdge> edges, string campus, string fromExternalId, string toExternalId)
    {
        return edges.Any(edge =>
            edge.Campus == campus &&
            ((edge.FromNodeExternalId == fromExternalId && edge.ToNodeExternalId == toExternalId) ||
             (edge.FromNodeExternalId == toExternalId && edge.ToNodeExternalId == fromExternalId)));
    }

    private void MergeNodes(
        WalkingRouteNode source,
        WalkingRouteNode target,
        List<WalkingRouteEdge> edges,
        List<WalkingRouteNode> nodes,
        DateTime now)
    {
        foreach (var edge in edges.Where(item =>
                     item.FromNodeExternalId == source.ExternalId ||
                     item.ToNodeExternalId == source.ExternalId))
        {
            if (edge.FromNodeExternalId == source.ExternalId)
                edge.FromNodeExternalId = target.ExternalId;

            if (edge.ToNodeExternalId == source.ExternalId)
                edge.ToNodeExternalId = target.ExternalId;

            edge.UpdatedAtUtc = now;
        }

        RemoveInvalidOrDuplicateEdges(edges);
        _context.WalkingRouteNodes.Remove(source);
        target.UpdatedAtUtc = now;
        RecalculateNodeEdges(target, edges, nodes);
    }

    private void RemoveInvalidOrDuplicateEdges(List<WalkingRouteEdge> edges)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in edges.ToList())
        {
            if (edge.FromNodeExternalId == edge.ToNodeExternalId)
            {
                _context.WalkingRouteEdges.Remove(edge);
                edges.Remove(edge);
                continue;
            }

            var ordered = string.CompareOrdinal(edge.FromNodeExternalId, edge.ToNodeExternalId) <= 0
                ? $"{edge.FromNodeExternalId}|{edge.ToNodeExternalId}"
                : $"{edge.ToNodeExternalId}|{edge.FromNodeExternalId}";
            var key = $"{edge.Campus}|{ordered}";

            if (seen.Add(key))
                continue;

            _context.WalkingRouteEdges.Remove(edge);
            edges.Remove(edge);
        }
    }

    private static void RecalculateNodeEdges(WalkingRouteNode node, List<WalkingRouteEdge> edges, List<WalkingRouteNode> nodes)
    {
        var nodesById = nodes.ToDictionary(item => item.ExternalId);
        foreach (var edge in edges.Where(item =>
                     item.FromNodeExternalId == node.ExternalId ||
                     item.ToNodeExternalId == node.ExternalId))
        {
            if (!nodesById.TryGetValue(edge.FromNodeExternalId, out var from) ||
                !nodesById.TryGetValue(edge.ToNodeExternalId, out var to))
            {
                continue;
            }

            edge.DistanceMeters = CalculateDistanceMeters(from.Latitude, from.Longitude, to.Latitude, to.Longitude);
            edge.UpdatedAtUtc = node.UpdatedAtUtc;
        }
    }

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

    private static RouteEdgeSnap? FindNearbyEdge(
        IEnumerable<WalkingRouteEdge> edges,
        IEnumerable<WalkingRouteNode> nodes,
        double latitude,
        double longitude,
        string? excludedNodeExternalId = null)
    {
        const double snapDistanceMeters = 3;
        const double endpointProtectionMeters = 2;
        var nodesById = nodes.ToDictionary(node => node.ExternalId);
        RouteEdgeSnap? best = null;

        foreach (var edge in edges)
        {
            if (!string.IsNullOrWhiteSpace(excludedNodeExternalId) &&
                (edge.FromNodeExternalId == excludedNodeExternalId || edge.ToNodeExternalId == excludedNodeExternalId))
            {
                continue;
            }

            if (!nodesById.TryGetValue(edge.FromNodeExternalId, out var from) ||
                !nodesById.TryGetValue(edge.ToNodeExternalId, out var to))
            {
                continue;
            }

            var projection = ProjectPointOnSegment(latitude, longitude, from.Latitude, from.Longitude, to.Latitude, to.Longitude);
            if (projection.DistanceMeters > snapDistanceMeters ||
                projection.DistanceToStartMeters < endpointProtectionMeters ||
                projection.DistanceToEndMeters < endpointProtectionMeters)
            {
                continue;
            }

            if (best is null || projection.DistanceMeters < best.DistanceMeters)
            {
                best = new RouteEdgeSnap(edge, projection.Latitude, projection.Longitude, projection.DistanceMeters);
            }
        }

        return best;
    }

    private static RouteProjection ProjectPointOnSegment(
        double latitude,
        double longitude,
        double startLatitude,
        double startLongitude,
        double endLatitude,
        double endLongitude)
    {
        const double metersPerDegreeLatitude = 111320;
        var originLatitudeRadians = ToRadians(latitude);
        var metersPerDegreeLongitude = metersPerDegreeLatitude * Math.Cos(originLatitudeRadians);

        var px = longitude * metersPerDegreeLongitude;
        var py = latitude * metersPerDegreeLatitude;
        var ax = startLongitude * metersPerDegreeLongitude;
        var ay = startLatitude * metersPerDegreeLatitude;
        var bx = endLongitude * metersPerDegreeLongitude;
        var by = endLatitude * metersPerDegreeLatitude;
        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSquared, 0, 1);
        var projectedX = ax + t * dx;
        var projectedY = ay + t * dy;
        var projectedLatitude = projectedY / metersPerDegreeLatitude;
        var projectedLongitude = projectedX / metersPerDegreeLongitude;

        return new RouteProjection(
            projectedLatitude,
            projectedLongitude,
            CalculateDistanceMeters(latitude, longitude, projectedLatitude, projectedLongitude),
            CalculateDistanceMeters(startLatitude, startLongitude, projectedLatitude, projectedLongitude),
            CalculateDistanceMeters(endLatitude, endLongitude, projectedLatitude, projectedLongitude));
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
    private sealed record RouteProjection(
        double Latitude,
        double Longitude,
        double DistanceMeters,
        double DistanceToStartMeters,
        double DistanceToEndMeters);
    private sealed record RouteEdgeSnap(WalkingRouteEdge Edge, double Latitude, double Longitude, double DistanceMeters);
}
