using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.Services;
using SoteroMap.API.ViewModels;
using System.Text.Json;

namespace SoteroMap.API.Controllers;

[Authorize]
public class AdminController : Controller
{
    private readonly AppDbContext _context;
    private readonly AuditLogService _auditLogService;
    private readonly IConfiguration _configuration;

    public AdminController(AppDbContext context, AuditLogService auditLogService, IConfiguration configuration)
    {
        _context = context;
        _auditLogService = auditLogService;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        var databaseFileInfo = GetDatabaseFileInfo();
        var databaseBackups = GetDatabaseBackupFiles();

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
            DatabaseFileName = databaseFileInfo?.Name ?? "soteromap.db",
            DatabaseFileSizeBytes = databaseFileInfo?.Length ?? 0,
            DatabaseLastWriteUtc = databaseFileInfo?.LastWriteTimeUtc,
            FrontendMapUrl = ResolveFrontendMapUrl(),
            DatabaseBackups = databaseBackups
                .Select(file => new DatabaseBackupViewModel
                {
                    FileName = file.Name,
                    SizeBytes = file.Length,
                    LastWriteUtc = file.LastWriteTimeUtc
                })
                .ToList(),
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

    [Authorize(Roles = AppRoles.Admin)]
    [HttpGet("/admin/database/download")]
    public IActionResult DownloadDatabase()
    {
        var databasePath = GetDatabaseFilePath();
        if (!System.IO.File.Exists(databasePath))
            return NotFound("No se encontro la base de datos.");

        return DownloadDatabaseFile(databasePath, $"soteromap-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpGet("/admin/database/backups/{fileName}")]
    public IActionResult DownloadDatabaseBackup(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return NotFound();

        var backupPath = Path.Combine(GetDatabaseBackupDirectory(), Path.GetFileName(fileName));
        if (!System.IO.File.Exists(backupPath))
            return NotFound("No se encontro el respaldo solicitado.");

        return DownloadDatabaseFile(backupPath, Path.GetFileName(backupPath));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost("/admin/database/backups/{fileName}/restore")]
    [ValidateAntiForgeryToken]
    public IActionResult RestoreDatabaseBackup(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            TempData["ErrorMessage"] = "No se indico ningun respaldo para restaurar.";
            return RedirectToAction(nameof(Index));
        }

        var backupPath = Path.Combine(GetDatabaseBackupDirectory(), Path.GetFileName(fileName));
        if (!System.IO.File.Exists(backupPath))
        {
            TempData["ErrorMessage"] = "No se encontro el respaldo seleccionado.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            ValidateSqliteFile(backupPath);
            RestoreDatabaseFromFile(backupPath);
            TempData["SuccessMessage"] = $"Respaldo restaurado correctamente: {Path.GetFileName(backupPath)}";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"No se pudo restaurar el respaldo: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost("/admin/database/backups/{fileName}/delete")]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteDatabaseBackup(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            TempData["ErrorMessage"] = "No se indico ningun respaldo para eliminar.";
            return RedirectToAction(nameof(Index));
        }

        var backupPath = Path.Combine(GetDatabaseBackupDirectory(), Path.GetFileName(fileName));
        if (!System.IO.File.Exists(backupPath))
        {
            TempData["ErrorMessage"] = "No se encontro el respaldo seleccionado.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            System.IO.File.Delete(backupPath);
            TempData["SuccessMessage"] = $"Respaldo eliminado correctamente: {Path.GetFileName(backupPath)}";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"No se pudo eliminar el respaldo: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost("/admin/database/upload")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> UploadDatabase(IFormFile? databaseFile)
    {
        if (databaseFile is null || databaseFile.Length == 0)
        {
            TempData["ErrorMessage"] = "Selecciona un archivo .db para restaurar.";
            return RedirectToAction(nameof(Index));
        }

        var extension = Path.GetExtension(databaseFile.FileName);
        if (!new[] { ".db", ".sqlite", ".sqlite3" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "Formato no valido. Sube un archivo .db, .sqlite o .sqlite3.";
            return RedirectToAction(nameof(Index));
        }

        var databasePath = GetDatabaseFilePath();
        var databaseDirectory = Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(databaseDirectory);

        var tempPath = Path.Combine(databaseDirectory, $"upload-{Guid.NewGuid():N}{extension}");

        try
        {
            await using (var stream = System.IO.File.Create(tempPath))
            {
                await databaseFile.CopyToAsync(stream);
            }

            ValidateSqliteFile(tempPath);
            RestoreDatabaseFromFile(tempPath);
            TempData["SuccessMessage"] = "Base de datos restaurada correctamente. Si tienes otra sesion abierta, recarga la pagina.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"No se pudo restaurar la base de datos: {ex.Message}";
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }

        return RedirectToAction(nameof(Index));
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

    public async Task<IActionResult> Locations(string? search, string? campus, string? floor, int page = 1, int pageSize = 30)
    {
        pageSize = NormalizePageSize(pageSize);
        page = Math.Max(page, 1);

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

        var totalFilteredLocations = await buildingsQuery.CountAsync();
        var filteredBuildingSnapshot = await buildingsQuery
            .Select(b => new
            {
                b.Id,
                b.ExternalId,
                b.HasInteriorMap
            })
            .ToListAsync();

        var filteredBuildingIds = filteredBuildingSnapshot.Select(b => b.Id).ToList();
        var filteredBuildingExternalIds = filteredBuildingSnapshot.Select(b => b.ExternalId).ToList();

        var buildings = await buildingsQuery
            .OrderBy(b => b.ManualDisplayName != "" ? b.ManualDisplayName : b.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var buildingIds = buildings.Select(b => b.Id).ToList();
        var buildingExternalIds = buildings.Select(b => b.ExternalId).ToList();

        var roomsByBuilding = await _context.SyncedRooms
            .AsNoTracking()
            .Where(r => filteredBuildingIds.Contains(r.SyncedBuildingId))
            .GroupBy(r => r.BuildingExternalId)
            .Select(g => new
            {
                BuildingExternalId = g.Key,
                Count = g.Count()
            })
            .ToDictionaryAsync(x => x.BuildingExternalId, x => x.Count);

        var assignedInventoryByBuilding = await _context.ImportedInventoryItems
            .AsNoTracking()
            .Where(i => i.AssignedBuildingExternalId != "" && filteredBuildingExternalIds.Contains(i.AssignedBuildingExternalId))
            .GroupBy(i => i.AssignedBuildingExternalId)
            .Select(g => new
            {
                BuildingExternalId = g.Key,
                Count = g.Count()
            })
            .ToDictionaryAsync(x => x.BuildingExternalId, x => x.Count);

        var suggestedInventoryByBuilding = await _context.ImportedInventoryItems
            .AsNoTracking()
            .Where(i => i.AssignedBuildingExternalId == "" && i.MatchedBuildingExternalId != "" && filteredBuildingExternalIds.Contains(i.MatchedBuildingExternalId))
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
            Page = page,
            PageSize = pageSize,
            TotalFilteredLocations = totalFilteredLocations,
            Locations = buildings.Select(b => new AdminLocationRowViewModel
            {
                ExternalId = b.ExternalId,
                DisplayName = b.EffectiveDisplayName,
                Campus = b.EffectiveCampus,
                DefaultMapFloor = GetPrimaryFloor(b.EffectiveFloorsJson),
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
            TotalBuildings = totalFilteredLocations,
            BuildingsWithInteriorMap = filteredBuildingSnapshot.Count(b => b.HasInteriorMap),
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
        string? buildingExternalId,
        int page = 1,
        int pageSize = 30)
    {
        pageSize = NormalizePageSize(pageSize);
        page = Math.Max(page, 1);

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
                i.IpAddress.Contains(search) ||
                i.MacAddress.Contains(search));
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

        var totalFilteredItems = await query.CountAsync();

        var model = new AdminInventoryListViewModel
        {
            Search = search ?? string.Empty,
            Category = category ?? string.Empty,
            Status = status ?? string.Empty,
            AssignmentFilter = assignment ?? "all",
            BuildingExternalId = buildingExternalId ?? string.Empty,
            Page = page,
            PageSize = pageSize,
            TotalFilteredItems = totalFilteredItems,
            Items = await query
                .OrderBy(i => i.AssignedBuildingExternalId == "" ? 0 : 1)
                .ThenBy(i => i.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
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

    private static int GetPrimaryFloor(string floorsJson)
    {
        if (string.IsNullOrWhiteSpace(floorsJson))
            return 0;

        try
        {
            var floors = JsonSerializer.Deserialize<List<int>>(floorsJson);
            if (floors is { Count: > 0 })
                return floors.Min();
        }
        catch
        {
        }

        var firstToken = floorsJson
            .Replace("[", string.Empty)
            .Replace("]", string.Empty)
            .Replace("\"", string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return int.TryParse(firstToken, out var parsedFloor) ? parsedFloor : 0;
    }

    private static int NormalizePageSize(int pageSize)
    {
        return pageSize switch
        {
            30 or 50 or 100 or 200 or 500 => pageSize,
            _ => 30
        };
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

    private string ResolveFrontendMapUrl()
    {
        var configured = _configuration["FrontendAppUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var allowedOrigins = _configuration["AllowedOrigins"];
        if (!string.IsNullOrWhiteSpace(allowedOrigins))
        {
            var frontendOrigin = allowedOrigins
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(origin => origin.Contains("8080", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(frontendOrigin))
                return frontendOrigin;
        }

        return "http://localhost:8080";
    }

    private string GetDatabaseFilePath()
    {
        var connectionString = _configuration.GetConnectionString("Default")
            ?? _configuration["ConnectionStrings:Default"]
            ?? throw new InvalidOperationException("No se encontro la cadena de conexion SQLite.");

        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException("No se encontro la ruta del archivo SQLite.");

        return Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dataSource));
    }

    private FileInfo? GetDatabaseFileInfo()
    {
        var databasePath = GetDatabaseFilePath();
        return System.IO.File.Exists(databasePath) ? new FileInfo(databasePath) : null;
    }

    private IReadOnlyList<FileInfo> GetDatabaseBackupFiles()
    {
        var backupDirectory = GetDatabaseBackupDirectory();
        if (!Directory.Exists(backupDirectory))
            return [];

        return new DirectoryInfo(backupDirectory)
            .GetFiles("*.db", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(15)
            .ToList();
    }

    private string GetDatabaseBackupDirectory()
    {
        var databasePath = GetDatabaseFilePath();
        var databaseDirectory = Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory;
        return Path.Combine(databaseDirectory, "backups");
    }

    private FileContentResult DownloadDatabaseFile(string sourcePath, string downloadFileName)
    {
        var bytes = System.IO.File.ReadAllBytes(sourcePath);
        return File(bytes, "application/octet-stream", downloadFileName);
    }

    private void RestoreDatabaseFromFile(string sourcePath)
    {
        var databasePath = GetDatabaseFilePath();
        var databaseDirectory = Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(databaseDirectory);

        _context.ChangeTracker.Clear();
        _context.Database.CloseConnection();
        SqliteConnection.ClearAllPools();

        if (System.IO.File.Exists(databasePath))
        {
            var backupDirectory = GetDatabaseBackupDirectory();
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, $"soteromap-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
            System.IO.File.Copy(databasePath, backupPath, overwrite: true);
        }

        System.IO.File.Copy(sourcePath, databasePath, overwrite: true);
        SqliteConnection.ClearAllPools();
    }

    private static void ValidateSqliteFile(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master LIMIT 1;";
        command.ExecuteScalar();
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
