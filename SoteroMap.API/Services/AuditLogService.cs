using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Services;

public class AuditLogService
{
    private readonly AppDbContext _context;

    public AuditLogService(AppDbContext context)
    {
        _context = context;
    }

    public async Task LogInventoryItemChangeAsync(
        ImportedInventoryItem item,
        string changedByUsername,
        string previousBuildingExternalId,
        string previousRoomExternalId,
        int? previousFloor,
        string previousSerialNumber,
        string previousAssignmentNotes,
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
            _context.AuditLogEntries.Add(new AuditLogEntry
            {
                BuildingExternalId = buildingExternalId,
                EntityType = "inventory-item",
                EntityId = item.Id.ToString(),
                ActionType = ResolveInventoryActionType(previousBuilding, currentBuilding),
                Summary = summary,
                Details = details,
                ChangedByUsername = actor,
                CreatedAtUtc = DateTime.UtcNow
            });
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
}
