using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Services;

public class InventoryReconciliationService
{
    private readonly AppDbContext _context;

    public InventoryReconciliationService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<InventoryReconciliationResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var buildings = await _context.SyncedBuildings.AsNoTracking().ToListAsync(cancellationToken);
        var rooms = await _context.SyncedRooms.AsNoTracking().ToListAsync(cancellationToken);
        var aliases = await _context.InventoryAliasRules.AsNoTracking().Where(a => a.IsEnabled).ToListAsync(cancellationToken);
        var items = await _context.ImportedInventoryItems.ToListAsync(cancellationToken);

        foreach (var item in items)
        {
            ReconcileItem(item, buildings, rooms, aliases);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new InventoryReconciliationResult
        {
            TotalItems = items.Count,
            MatchedBuildings = items.Count(i => i.MatchedSyncedBuildingId.HasValue),
            MatchedRooms = items.Count(i => i.MatchedSyncedRoomId.HasValue),
            UnmatchedItems = items.Count(i => !i.MatchedSyncedBuildingId.HasValue),
            LastRunUtc = DateTime.UtcNow
        };
    }

    public async Task<object> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var total = await _context.ImportedInventoryItems.CountAsync(cancellationToken);
        var matchedBuildings = await _context.ImportedInventoryItems.CountAsync(i => i.MatchedSyncedBuildingId.HasValue, cancellationToken);
        var matchedRooms = await _context.ImportedInventoryItems.CountAsync(i => i.MatchedSyncedRoomId.HasValue, cancellationToken);
        var unmatched = total - matchedBuildings;

        var topPending = await _context.ImportedInventoryItems
            .AsNoTracking()
            .Where(i => i.MatchedSyncedBuildingId == null)
            .GroupBy(i => string.IsNullOrWhiteSpace(i.OrganizationalUnit) ? i.UnitOrDepartment : i.OrganizationalUnit)
            .Select(g => new { key = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count)
            .Take(20)
            .ToListAsync(cancellationToken);

        return new
        {
            total,
            matchedBuildings,
            matchedRooms,
            unmatched,
            topPending
        };
    }

    private static void ReconcileItem(
        ImportedInventoryItem item,
        List<SyncedBuilding> buildings,
        List<SyncedRoom> rooms,
        List<InventoryAliasRule> aliases)
    {
        item.MatchedSyncedBuildingId = null;
        item.MatchedSyncedRoomId = null;
        item.MatchedBuildingExternalId = string.Empty;
        item.MatchedRoomExternalId = string.Empty;
        item.MatchConfidence = "none";
        item.MatchNotes = string.Empty;

        var candidates = new[]
        {
            item.OrganizationalUnit,
            item.UnitOrDepartment,
            item.ResponsibleUser
        }
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .ToList();

        var normalizedCandidates = candidates
            .Select(c => new { Raw = c, Normalized = Normalize(c) })
            .Where(c => !string.IsNullOrWhiteSpace(c.Normalized))
            .ToList();

        foreach (var candidate in normalizedCandidates)
        {
            var alias = aliases.FirstOrDefault(a => a.NormalizedSourceText == candidate.Normalized);
            if (alias is null)
                continue;

            var aliasBuilding = buildings.FirstOrDefault(b => b.ExternalId == alias.TargetBuildingExternalId);
            if (aliasBuilding is null)
                continue;

            item.MatchedSyncedBuildingId = aliasBuilding.Id;
            item.MatchedBuildingExternalId = aliasBuilding.ExternalId;
            item.MatchConfidence = "alias-building";
            item.MatchNotes = $"alias:{candidate.Raw}";

            if (!string.IsNullOrWhiteSpace(alias.TargetRoomExternalId))
            {
                var aliasRoom = rooms.FirstOrDefault(r => r.ExternalId == alias.TargetRoomExternalId);
                if (aliasRoom is not null)
                {
                    item.MatchedSyncedRoomId = aliasRoom.Id;
                    item.MatchedRoomExternalId = aliasRoom.ExternalId;
                    item.MatchConfidence = "alias-room";
                    item.MatchNotes = $"alias:{candidate.Raw}; room";
                }
            }

            return;
        }

        SyncedBuilding? bestBuilding = null;
        string buildingMatchType = string.Empty;

        foreach (var candidate in normalizedCandidates)
        {
            bestBuilding = buildings.FirstOrDefault(b =>
                Matches(candidate.Normalized, Normalize(b.DisplayName)) ||
                Matches(candidate.Normalized, Normalize(b.RealName)) ||
                Matches(candidate.Normalized, Normalize(b.ResponsibleArea)));

            if (bestBuilding is not null)
            {
                buildingMatchType = $"building:{candidate.Raw}";
                break;
            }
        }

        if (bestBuilding is null)
        {
            item.MatchNotes = "No se encontró edificio equivalente con las reglas actuales.";
            return;
        }

        item.MatchedSyncedBuildingId = bestBuilding.Id;
        item.MatchedBuildingExternalId = bestBuilding.ExternalId;
        item.MatchConfidence = "building";
        item.MatchNotes = buildingMatchType;

        var buildingRooms = rooms.Where(r => r.SyncedBuildingId == bestBuilding.Id).ToList();

        foreach (var candidate in normalizedCandidates)
        {
            var room = buildingRooms.FirstOrDefault(r =>
                Matches(candidate.Normalized, Normalize(r.Name)) ||
                Matches(candidate.Normalized, Normalize(r.Unit)) ||
                Matches(candidate.Normalized, Normalize(r.Service)) ||
                Matches(candidate.Normalized, Normalize(r.ResponsibleArea)));

            if (room is null)
            {
                continue;
            }

            item.MatchedSyncedRoomId = room.Id;
            item.MatchedRoomExternalId = room.ExternalId;
            item.MatchConfidence = "room";
            item.MatchNotes = $"{buildingMatchType}; room:{candidate.Raw}";
            return;
        }
    }

    public static string NormalizeText(string? text) => Normalize(text);

    private static bool Matches(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return left == right || left.Contains(right, StringComparison.OrdinalIgnoreCase) || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                builder.Append(char.ToUpperInvariant(ch));
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public sealed class InventoryReconciliationResult
    {
        public int TotalItems { get; set; }
        public int MatchedBuildings { get; set; }
        public int MatchedRooms { get; set; }
        public int UnmatchedItems { get; set; }
        public DateTime LastRunUtc { get; set; }
    }
}
