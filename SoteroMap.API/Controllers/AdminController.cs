using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.Services;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Controllers;

[Authorize]
public class AdminController : Controller
{
    private readonly AppDbContext _context;
    private readonly AuditLogService _auditLogService;

    public AdminController(AppDbContext context, AuditLogService auditLogService)
    {
        _context = context;
        _auditLogService = auditLogService;
    }

    public async Task<IActionResult> Index()
    {
        var model = new AdminDashboardViewModel
        {
            SyncedBuildings = await _context.SyncedBuildings.CountAsync(),
            SyncedRooms = await _context.SyncedRooms.CountAsync(),
            TotalImportedItems = await _context.ImportedInventoryItems.CountAsync(),
            AssignedItems = await _context.ImportedInventoryItems.CountAsync(i => i.AssignedBuildingExternalId != ""),
            PendingAssignmentItems = await _context.ImportedInventoryItems.CountAsync(i => i.AssignedBuildingExternalId == ""),
            SuggestedItems = await _context.ImportedInventoryItems.CountAsync(i => i.AssignedBuildingExternalId == "" && i.MatchedBuildingExternalId != ""),
            StolenItems = await _context.ImportedInventoryItems.CountAsync(i => i.InferredStatus == "stolen"),
            DistinctImportedCategories = await _context.ImportedInventoryItems
                .Where(i => i.InferredCategory != "")
                .Select(i => i.InferredCategory)
                .Distinct()
                .CountAsync(),
            CategoryBreakdown = await _context.ImportedInventoryItems
                .AsNoTracking()
                .GroupBy(i => i.InferredCategory == "" ? "sin-categoria" : i.InferredCategory)
                .Select(g => new DashboardCategorySummaryViewModel
                {
                    Category = g.Key,
                    Count = g.Count()
                })
                .OrderByDescending(g => g.Count)
                .Take(6)
                .ToListAsync(),
            RecentItems = await _context.ImportedInventoryItems
                .AsNoTracking()
                .OrderByDescending(i => i.ImportedAtUtc)
                .ThenByDescending(i => i.Id)
                .Take(8)
                .Select(i => new DashboardInventoryPreviewViewModel
                {
                    Id = i.Id,
                    Description = i.Description,
                    ResponsibleUser = i.ResponsibleUser,
                    UnitOrDepartment = i.UnitOrDepartment,
                    AssignedBuildingExternalId = i.AssignedBuildingExternalId,
                    InferredStatus = i.InferredStatus
                })
                .ToListAsync(),
            RecentActivity = await _context.AuditLogEntries
                .AsNoTracking()
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(8)
                .Select(x => new ActivityLogListItemViewModel
                {
                    Id = x.Id,
                    BuildingExternalId = x.BuildingExternalId,
                    Summary = x.Summary,
                    Details = x.Details,
                    ChangedByUsername = x.ChangedByUsername,
                    ActionType = x.ActionType,
                    CreatedAtUtc = x.CreatedAtUtc
                })
                .ToListAsync()
        };

        return View(model);
    }

    [HttpGet("/admin/activity")]
    public async Task<IActionResult> Activity(string? buildingExternalId, string? changedByUsername)
    {
        var query = _context.AuditLogEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(buildingExternalId))
        {
            query = query.Where(x => x.BuildingExternalId == buildingExternalId);
        }

        if (!string.IsNullOrWhiteSpace(changedByUsername))
        {
            query = query.Where(x => x.ChangedByUsername.Contains(changedByUsername));
        }

        var model = new AdminActivityViewModel
        {
            BuildingExternalId = buildingExternalId ?? string.Empty,
            ChangedByUsername = changedByUsername ?? string.Empty,
            Items = await query
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(250)
                .Select(x => new ActivityLogListItemViewModel
                {
                    Id = x.Id,
                    BuildingExternalId = x.BuildingExternalId,
                    Summary = x.Summary,
                    Details = x.Details,
                    ChangedByUsername = x.ChangedByUsername,
                    ActionType = x.ActionType,
                    CreatedAtUtc = x.CreatedAtUtc
                })
                .ToListAsync()
        };

        return View(model);
    }

    public async Task<IActionResult> Locations(string? search, string? campus, string? floor)
    {
        var buildingsQuery = _context.SyncedBuildings.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            buildingsQuery = buildingsQuery.Where(b =>
                (b.ManualDisplayName != "" ? b.ManualDisplayName : b.DisplayName).Contains(search) ||
                b.ExternalId.Contains(search) ||
                b.ShortName.Contains(search) ||
                b.RealName.Contains(search) ||
                b.Type.Contains(search) ||
                b.ResponsibleArea.Contains(search));
        }

        if (!string.IsNullOrEmpty(campus))
            buildingsQuery = buildingsQuery.Where(b => (b.ManualCampus != "" ? b.ManualCampus : b.Campus).Contains(campus));

        if (!string.IsNullOrEmpty(floor))
        {
            var floorToken = $"\"{floor}\"";
            buildingsQuery = buildingsQuery.Where(b => (b.ManualFloorsJson != "" ? b.ManualFloorsJson : b.FloorsJson).Contains(floorToken));
        }

        var buildings = await buildingsQuery
            .OrderBy(b => b.ManualDisplayName != "" ? b.ManualDisplayName : b.DisplayName)
            .ToListAsync();

        var buildingIds = buildings.Select(b => b.Id).ToList();
        var buildingExternalIds = buildings.Select(b => b.ExternalId).ToList();

        var roomsByBuilding = await _context.SyncedRooms
            .AsNoTracking()
            .Where(r => buildingIds.Contains(r.SyncedBuildingId))
            .GroupBy(r => r.BuildingExternalId)
            .Select(g => new
            {
                BuildingExternalId = g.Key,
                Count = g.Count()
            })
            .ToDictionaryAsync(x => x.BuildingExternalId, x => x.Count);

        var assignedInventoryByBuilding = await _context.ImportedInventoryItems
            .AsNoTracking()
            .Where(i => i.AssignedBuildingExternalId != "" && buildingExternalIds.Contains(i.AssignedBuildingExternalId))
            .GroupBy(i => i.AssignedBuildingExternalId)
            .Select(g => new
            {
                BuildingExternalId = g.Key,
                Count = g.Count()
            })
            .ToDictionaryAsync(x => x.BuildingExternalId, x => x.Count);

        var suggestedInventoryByBuilding = await _context.ImportedInventoryItems
            .AsNoTracking()
            .Where(i => i.AssignedBuildingExternalId == "" && i.MatchedBuildingExternalId != "" && buildingExternalIds.Contains(i.MatchedBuildingExternalId))
            .GroupBy(i => i.MatchedBuildingExternalId)
            .Select(g => new
            {
                BuildingExternalId = g.Key,
                Count = g.Count()
            })
            .ToDictionaryAsync(x => x.BuildingExternalId, x => x.Count);

        var model = new AdminLocationsViewModel
        {
            Search = search ?? string.Empty,
            Campus = campus ?? string.Empty,
            Floor = floor ?? string.Empty,
            Locations = buildings.Select(b => new AdminLocationRowViewModel
            {
                ExternalId = b.ExternalId,
                DisplayName = b.EffectiveDisplayName,
                Campus = b.EffectiveCampus,
                HasManualOverride = !string.IsNullOrWhiteSpace(b.ManualCampus) ||
                                    !string.IsNullOrWhiteSpace(b.ManualDisplayName) ||
                                    !string.IsNullOrWhiteSpace(b.ManualFloorsJson),
                Type = b.Type,
                HasInteriorMap = b.HasInteriorMap,
                MappingStatus = b.MappingStatus,
                InventoryStatus = b.InventoryStatus,
                AvailableFloors = FormatFloors(b.EffectiveFloorsJson),
                RoomsCount = roomsByBuilding.GetValueOrDefault(b.ExternalId, 0),
                AssignedInventoryCount = assignedInventoryByBuilding.GetValueOrDefault(b.ExternalId, 0),
                SuggestedInventoryCount = suggestedInventoryByBuilding.GetValueOrDefault(b.ExternalId, 0),
                Coordinates = b.CentroidLatitude.HasValue && b.CentroidLongitude.HasValue
                    ? $"{b.CentroidLatitude.Value:F4}, {b.CentroidLongitude.Value:F4}"
                    : "-"
            }).ToList(),
            TotalBuildings = buildings.Count,
            BuildingsWithInteriorMap = buildings.Count(b => b.HasInteriorMap),
            TotalRooms = roomsByBuilding.Values.Sum(),
            AssignedInventoryItems = assignedInventoryByBuilding.Values.Sum()
        };

        return View(model);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpGet("/admin/editsyncedbuilding/{externalId}")]
    public async Task<IActionResult> EditSyncedBuilding(string externalId)
    {
        var building = await _context.SyncedBuildings
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.ExternalId == externalId);
        if (building is null)
            return NotFound();

        var model = new EditSyncedBuildingViewModel
        {
            Building = building,
            Rooms = await _context.SyncedRooms
                .AsNoTracking()
                .Where(r => r.BuildingExternalId == externalId)
                .OrderBy(r => r.ManualFloor ?? r.Floor)
                .ThenBy(r => r.ManualName != "" ? r.ManualName : r.Name)
                .ToListAsync()
        };

        return View(model);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost("/admin/editsyncedbuilding/{externalId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditSyncedBuilding(
        string externalId,
        string? manualCampus,
        string? manualDisplayName,
        string? manualFloorsCsv)
    {
        var building = await _context.SyncedBuildings.FirstOrDefaultAsync(b => b.ExternalId == externalId);
        if (building is null)
            return NotFound();

        var previousCampus = building.EffectiveCampus;
        var previousDisplayName = building.EffectiveDisplayName;
        var previousFloors = building.EffectiveFloorsJson;

        building.ManualCampus = manualCampus?.Trim() ?? string.Empty;
        building.ManualDisplayName = manualDisplayName?.Trim() ?? string.Empty;
        building.ManualFloorsJson = NormalizeFloorsCsv(manualFloorsCsv);

        await _context.SaveChangesAsync();
        await LogBuildingOverrideAsync(building, previousCampus, previousDisplayName, previousFloors);

        TempData["SuccessMessage"] = "Override del edificio guardado correctamente.";
        return RedirectToAction(nameof(EditSyncedBuilding), new { externalId });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpGet("/admin/editsyncedroom/{externalId}")]
    public async Task<IActionResult> EditSyncedRoom(string externalId)
    {
        var room = await _context.SyncedRooms.FirstOrDefaultAsync(r => r.ExternalId == externalId);
        if (room is null)
            return NotFound();

        var building = await _context.SyncedBuildings
            .AsNoTracking()
            .FirstAsync(b => b.ExternalId == room.BuildingExternalId);

        return View(new EditSyncedRoomViewModel
        {
            Room = room,
            Building = building
        });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost("/admin/editsyncedroom/{externalId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditSyncedRoom(string externalId, string? manualName, int? manualFloor)
    {
        var room = await _context.SyncedRooms.FirstOrDefaultAsync(r => r.ExternalId == externalId);
        if (room is null)
            return NotFound();

        var previousName = room.EffectiveName;
        var previousFloor = room.EffectiveFloor;

        room.ManualName = manualName?.Trim() ?? string.Empty;
        room.ManualFloor = manualFloor;

        await _context.SaveChangesAsync();
        await LogRoomOverrideAsync(room, previousName, previousFloor);

        TempData["SuccessMessage"] = "Override de la sala guardado correctamente.";
        return RedirectToAction(nameof(EditSyncedRoom), new { externalId });
    }

    [Authorize(Roles = AppRoles.Admin)]
    public IActionResult CreateLocation() => View(new Location());

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLocation(Location location)
    {
        if (!ModelState.IsValid)
            return View(location);

        location.CreatedAt = DateTime.UtcNow;
        _context.Locations.Add(location);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Locations));
    }

    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> EditLocation(int id)
    {
        var location = await _context.Locations.FindAsync(id);
        if (location == null)
            return NotFound();

        return View(location);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditLocation(int id, Location location)
    {
        if (id != location.Id)
            return BadRequest();

        if (!ModelState.IsValid)
            return View(location);

        _context.Entry(location).State = EntityState.Modified;
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Locations));
    }

    public IActionResult Equipments()
    {
        return RedirectToAction(nameof(Inventory));
    }

    public async Task<IActionResult> Inventory(
        string? search,
        string? category,
        string? status,
        string? assignment,
        string? buildingExternalId)
    {
        var query = _context.ImportedInventoryItems.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(i =>
                i.Description.Contains(search) ||
                i.ItemNumber.Contains(search) ||
                i.SerialNumber.Contains(search) ||
                i.UnitOrDepartment.Contains(search) ||
                i.OrganizationalUnit.Contains(search) ||
                i.ResponsibleUser.Contains(search) ||
                i.Email.Contains(search) ||
                i.IpAddress.Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(i => i.InferredCategory == category);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(i => i.InferredStatus == status);

        switch (assignment)
        {
            case "assigned":
                if (!string.IsNullOrWhiteSpace(buildingExternalId))
                    query = query.Where(i => i.AssignedBuildingExternalId == buildingExternalId);

                query = query.Where(i => i.AssignedBuildingExternalId != "");
                break;
            case "suggested":
                if (!string.IsNullOrWhiteSpace(buildingExternalId))
                    query = query.Where(i => i.MatchedBuildingExternalId == buildingExternalId);

                query = query.Where(i => i.AssignedBuildingExternalId == "" && i.MatchedBuildingExternalId != "");
                break;
            case "all":
                if (!string.IsNullOrWhiteSpace(buildingExternalId))
                {
                    query = query.Where(i =>
                        i.AssignedBuildingExternalId == buildingExternalId ||
                        i.MatchedBuildingExternalId == buildingExternalId);
                }

                break;
            default:
                assignment = "all";
                if (!string.IsNullOrWhiteSpace(buildingExternalId))
                {
                    query = query.Where(i =>
                        i.AssignedBuildingExternalId == buildingExternalId ||
                        i.MatchedBuildingExternalId == buildingExternalId);
                }
                break;
        }

        var model = new AdminInventoryListViewModel
        {
            Search = search ?? string.Empty,
            Category = category ?? string.Empty,
            Status = status ?? string.Empty,
            AssignmentFilter = assignment ?? "all",
            BuildingExternalId = buildingExternalId ?? string.Empty,
            Items = await query
                .OrderBy(i => i.AssignedBuildingExternalId == "" ? 0 : 1)
                .ThenBy(i => i.RowNumber)
                .Take(250)
                .ToListAsync(),
            Buildings = await _context.SyncedBuildings
                .AsNoTracking()
                .OrderBy(b => b.ManualDisplayName != "" ? b.ManualDisplayName : b.DisplayName)
                .ToListAsync(),
            TotalItems = await _context.ImportedInventoryItems.CountAsync(),
            AssignedItems = await _context.ImportedInventoryItems.CountAsync(i => i.AssignedBuildingExternalId != ""),
            PendingItems = await _context.ImportedInventoryItems.CountAsync(i => i.AssignedBuildingExternalId == ""),
            SuggestedItems = await _context.ImportedInventoryItems.CountAsync(i => i.AssignedBuildingExternalId == "" && i.MatchedBuildingExternalId != "")
        };

        return View("Equipments", model);
    }

    public async Task<IActionResult> EditInventoryItem(int id)
    {
        var item = await _context.ImportedInventoryItems.FindAsync(id);
        if (item == null)
            return NotFound();

        var model = new EditInventoryItemViewModel
        {
            Item = item,
            Buildings = await _context.SyncedBuildings
                .AsNoTracking()
                .OrderBy(b => b.ManualDisplayName != "" ? b.ManualDisplayName : b.DisplayName)
                .ToListAsync(),
            Rooms = await _context.SyncedRooms
                .AsNoTracking()
                .OrderBy(r => r.ManualFloor ?? r.Floor)
                .ThenBy(r => r.ManualName != "" ? r.ManualName : r.Name)
                .ToListAsync()
        };

        return View(model);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditInventoryItem(
        int id,
        string? serialNumber,
        string? assignedBuildingExternalId,
        string? assignedRoomExternalId,
        int? assignedFloor,
        string? assignmentNotes)
    {
        var item = await _context.ImportedInventoryItems.FindAsync(id);
        if (item == null)
            return NotFound();

        var previousBuildingExternalId = item.AssignedBuildingExternalId;
        var previousRoomExternalId = item.AssignedRoomExternalId;
        var previousFloor = item.AssignedFloor;
        var previousSerialNumber = item.SerialNumber;
        var previousAssignmentNotes = item.AssignmentNotes;

        item.SerialNumber = serialNumber?.Trim() ?? string.Empty;
        item.AssignedBuildingExternalId = assignedBuildingExternalId?.Trim() ?? string.Empty;
        item.AssignedRoomExternalId = assignedRoomExternalId?.Trim() ?? string.Empty;
        item.AssignedFloor = assignedFloor;
        item.AssignmentNotes = assignmentNotes?.Trim() ?? string.Empty;
        item.AssignmentUpdatedAtUtc = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(item.AssignedBuildingExternalId))
        {
            item.AssignedRoomExternalId = string.Empty;
            item.AssignedFloor = null;
        }

        await _context.SaveChangesAsync();
        await _auditLogService.LogInventoryItemChangeAsync(
            item,
            User.Identity?.Name ?? "sistema",
            previousBuildingExternalId,
            previousRoomExternalId,
            previousFloor,
            previousSerialNumber,
            previousAssignmentNotes);
        TempData["SuccessMessage"] = "Asignacion guardada correctamente.";
        return RedirectToAction(nameof(EditInventoryItem), new { id });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearInventoryAssignment(int id)
    {
        var item = await _context.ImportedInventoryItems.FindAsync(id);
        if (item == null)
            return NotFound();

        var previousBuildingExternalId = item.AssignedBuildingExternalId;
        var previousRoomExternalId = item.AssignedRoomExternalId;
        var previousFloor = item.AssignedFloor;
        var previousSerialNumber = item.SerialNumber;
        var previousAssignmentNotes = item.AssignmentNotes;

        item.AssignedBuildingExternalId = string.Empty;
        item.AssignedRoomExternalId = string.Empty;
        item.AssignedFloor = null;
        item.AssignmentNotes = string.Empty;
        item.AssignmentUpdatedAtUtc = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        await _auditLogService.LogInventoryItemChangeAsync(
            item,
            User.Identity?.Name ?? "sistema",
            previousBuildingExternalId,
            previousRoomExternalId,
            previousFloor,
            previousSerialNumber,
            previousAssignmentNotes);
        TempData["SuccessMessage"] = "Asignacion limpiada. El equipo quedo pendiente.";
        return RedirectToAction(nameof(EditInventoryItem), new { id });
    }

    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> CreateEquipment()
    {
        ViewBag.Locations = await _context.Locations.Where(l => l.IsActive).ToListAsync();
        return View(new Equipment());
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateEquipment(Equipment equipment)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.Locations = await _context.Locations.Where(l => l.IsActive).ToListAsync();
            return View(equipment);
        }

        equipment.CreatedAt = DateTime.UtcNow;
        _context.Equipments.Add(equipment);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Inventory));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEquipment(int id)
    {
        var equipment = await _context.Equipments.FindAsync(id);
        if (equipment != null)
        {
            _context.Equipments.Remove(equipment);
            await _context.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Inventory));
    }

    private static string FormatFloors(string floorsJson)
    {
        if (string.IsNullOrWhiteSpace(floorsJson))
            return "-";

        var trimmed = floorsJson
            .Replace("[", string.Empty)
            .Replace("]", string.Empty)
            .Replace("\"", string.Empty)
            .Trim();

        return string.IsNullOrWhiteSpace(trimmed) ? "-" : trimmed;
    }

    private static string NormalizeFloorsCsv(string? manualFloorsCsv)
    {
        var tokens = (manualFloorsCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => int.TryParse(value, out _))
            .Select(int.Parse)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        return tokens.Count == 0 ? string.Empty : System.Text.Json.JsonSerializer.Serialize(tokens);
    }

    private async Task LogBuildingOverrideAsync(
        SyncedBuilding building,
        string previousCampus,
        string previousDisplayName,
        string previousFloorsJson)
    {
        var changes = new List<string>();

        if (!string.Equals(previousCampus, building.EffectiveCampus, StringComparison.Ordinal))
            changes.Add($"campus: '{previousCampus}' -> '{building.EffectiveCampus}'");

        if (!string.Equals(previousDisplayName, building.EffectiveDisplayName, StringComparison.Ordinal))
            changes.Add($"edificio: '{previousDisplayName}' -> '{building.EffectiveDisplayName}'");

        if (!string.Equals(previousFloorsJson, building.EffectiveFloorsJson, StringComparison.Ordinal))
            changes.Add("pisos actualizados");

        if (changes.Count == 0)
            changes.Add("override revisado sin cambios");

        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = building.ExternalId,
            EntityType = "synced-building",
            EntityId = building.ExternalId,
            ActionType = "override-building",
            Summary = $"Override manual en edificio {building.EffectiveDisplayName}",
            Details = string.Join("; ", changes),
            ChangedByUsername = User.Identity?.Name ?? "sistema",
            CreatedAtUtc = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
    }

    private async Task LogRoomOverrideAsync(SyncedRoom room, string previousName, int previousFloor)
    {
        var changes = new List<string>();

        if (!string.Equals(previousName, room.EffectiveName, StringComparison.Ordinal))
            changes.Add($"sala: '{previousName}' -> '{room.EffectiveName}'");

        if (previousFloor != room.EffectiveFloor)
            changes.Add($"piso: '{previousFloor}' -> '{room.EffectiveFloor}'");

        if (changes.Count == 0)
            changes.Add("override revisado sin cambios");

        _context.AuditLogEntries.Add(new AuditLogEntry
        {
            BuildingExternalId = room.BuildingExternalId,
            EntityType = "synced-room",
            EntityId = room.ExternalId,
            ActionType = "override-room",
            Summary = $"Override manual en sala {room.EffectiveName}",
            Details = string.Join("; ", changes),
            ChangedByUsername = User.Identity?.Name ?? "sistema",
            CreatedAtUtc = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
    }
}
