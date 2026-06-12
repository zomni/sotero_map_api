using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Services;

public class AuditLogService
{
    private readonly AppDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditLogService(AppDbContext context, IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task LogSecurityEventAsync(
        string actionType,
        string resource,
        string summary,
        string details,
        string result = "success",
        string severity = "info",
        string entityType = "security",
        string entityId = "",
        string buildingExternalId = "",
        string previousValue = "",
        string newValue = "",
        string? changedByUsername = null,
        CancellationToken cancellationToken = default)
    {
        _context.AuditLogEntries.Add(CreateEntry(
            buildingExternalId,
            entityType,
            entityId,
            actionType,
            resource,
            result,
            severity,
            summary,
            details,
            previousValue,
            newValue,
            string.IsNullOrWhiteSpace(changedByUsername) ? GetCurrentUsername() : changedByUsername.Trim(),
            GetClientIp(),
            GetUserAgent()));

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task LogInventoryItemChangeAsync(
        ImportedInventoryItem item,
        string changedByUsername,
        string? previousBuildingExternalId,
        string? previousRoomExternalId,
        int? previousFloor,
        string? previousSerialNumber,
        string? previousAssignmentNotes,
        CancellationToken cancellationToken = default)
    {
        var actor = string.IsNullOrWhiteSpace(changedByUsername) ? "sistema" : changedByUsername.Trim();
        var currentBuilding = item.AssignedBuildingExternalId ?? string.Empty;
        var previousBuilding = previousBuildingExternalId ?? string.Empty;

        var changes = new List<string>();

        if (!string.Equals(previousSerialNumber ?? string.Empty, item.SerialNumber ?? string.Empty, StringComparison.Ordinal))
        {
            changes.Add($"S/N: '{ValueOrDash(previousSerialNumber)}' -> '{ValueOrDash(item.SerialNumber)}'");
        }

        if (!string.Equals(previousBuilding, currentBuilding, StringComparison.Ordinal))
        {
            changes.Add($"edificio: '{ValueOrDash(previousBuilding)}' -> '{ValueOrDash(currentBuilding)}'");
        }

        if (!string.Equals(previousRoomExternalId ?? string.Empty, item.AssignedRoomExternalId ?? string.Empty, StringComparison.Ordinal))
        {
            changes.Add($"sala: '{ValueOrDash(previousRoomExternalId)}' -> '{ValueOrDash(item.AssignedRoomExternalId)}'");
        }

        if (previousFloor != item.AssignedFloor)
        {
            changes.Add($"piso: '{ValueOrDash(previousFloor?.ToString())}' -> '{ValueOrDash(item.AssignedFloor?.ToString())}'");
        }

        if (!string.Equals(previousAssignmentNotes ?? string.Empty, item.AssignmentNotes ?? string.Empty, StringComparison.Ordinal))
        {
            changes.Add("notas de asignacion actualizadas");
        }

        if (changes.Count == 0)
        {
            changes.Add("sin cambios detectados");
        }

        var summary = BuildInventorySummary(item, previousBuilding, currentBuilding);
        var details = string.Join("; ", changes);

        var impactedBuildings = new HashSet<string>(
            new[] { previousBuilding, currentBuilding }
                .Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);

        if (impactedBuildings.Count == 0)
        {
            impactedBuildings.Add(item.MatchedBuildingExternalId ?? string.Empty);
        }

        foreach (var buildingExternalId in impactedBuildings.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            _context.AuditLogEntries.Add(CreateEntry(
                buildingExternalId,
                "inventory-item",
                item.Id.ToString(),
                ResolveInventoryActionType(previousBuilding, currentBuilding),
                "inventory-item",
                "success",
                "info",
                summary,
                details,
                previousValue: string.Join(" | ", new[]
                {
                    $"building={ValueOrDash(previousBuilding)}",
                    $"room={ValueOrDash(previousRoomExternalId)}",
                    $"floor={ValueOrDash(previousFloor?.ToString())}",
                    $"serial={ValueOrDash(previousSerialNumber)}",
                    $"notes={ValueOrDash(previousAssignmentNotes)}"
                }),
                newValue: string.Join(" | ", new[]
                {
                    $"building={ValueOrDash(currentBuilding)}",
                    $"room={ValueOrDash(item.AssignedRoomExternalId)}",
                    $"floor={ValueOrDash(item.AssignedFloor?.ToString())}",
                    $"serial={ValueOrDash(item.SerialNumber)}",
                    $"notes={ValueOrDash(item.AssignmentNotes)}"
                }),
                actor,
                GetClientIp(),
                GetUserAgent()));
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string BuildInventorySummary(ImportedInventoryItem item, string previousBuilding, string currentBuilding)
    {
        var itemLabel = !string.IsNullOrWhiteSpace(item.SerialNumber)
            ? $"S/N {item.SerialNumber}"
            : $"fila #{item.RowNumber}";

        if (string.IsNullOrWhiteSpace(previousBuilding) && !string.IsNullOrWhiteSpace(currentBuilding))
        {
            return $"{itemLabel} asignado a {currentBuilding}";
        }

        if (!string.IsNullOrWhiteSpace(previousBuilding) && string.IsNullOrWhiteSpace(currentBuilding))
        {
            return $"{itemLabel} quedo sin asignacion";
        }

        if (!string.Equals(previousBuilding, currentBuilding, StringComparison.Ordinal))
        {
            return $"{itemLabel} movido de {ValueOrDash(previousBuilding)} a {ValueOrDash(currentBuilding)}";
        }

        return $"{itemLabel} actualizado en {ValueOrDash(currentBuilding)}";
    }

    private static string ResolveInventoryActionType(string previousBuilding, string currentBuilding)
    {
        if (string.IsNullOrWhiteSpace(previousBuilding) && !string.IsNullOrWhiteSpace(currentBuilding))
        {
            return "assigned";
        }

        if (!string.IsNullOrWhiteSpace(previousBuilding) && string.IsNullOrWhiteSpace(currentBuilding))
        {
            return "unassigned";
        }

        if (!string.Equals(previousBuilding, currentBuilding, StringComparison.Ordinal))
        {
            return "moved";
        }

        return "updated";
    }

    private static string ValueOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private AuditLogEntry CreateEntry(
        string buildingExternalId,
        string entityType,
        string entityId,
        string actionType,
        string resource,
        string result,
        string severity,
        string summary,
        string details,
        string previousValue,
        string newValue,
        string changedByUsername,
        string clientIp,
        string userAgent)
    {
        return new AuditLogEntry
        {
            BuildingExternalId = buildingExternalId ?? string.Empty,
            EntityType = entityType ?? string.Empty,
            EntityId = entityId ?? string.Empty,
            ActionType = actionType ?? string.Empty,
            Resource = resource ?? string.Empty,
            Result = result ?? string.Empty,
            Severity = severity ?? string.Empty,
            Summary = summary ?? string.Empty,
            Details = details ?? string.Empty,
            PreviousValue = previousValue ?? string.Empty,
            NewValue = newValue ?? string.Empty,
            ChangedByUsername = changedByUsername ?? string.Empty,
            ClientIp = clientIp ?? string.Empty,
            UserAgent = userAgent ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private string GetCurrentUsername()
    {
        return _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "sistema";
    }

    private string GetClientIp()
    {
        return _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }

    private string GetUserAgent()
    {
        return _httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString() ?? string.Empty;
    }
}
