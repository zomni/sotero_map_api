using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Infrastructure;
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
    private const string ManualInventorySourceFile = "manual-admin";

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

    public async Task<IActionResult> Locations(
        string? search,
        string? campus,
        string? floor,
        string? sortBy,
        string? sortDirection,
        int page = 1,
        int pageSize = 30)
    {
        pageSize = NormalizePageSize(pageSize);
        page = Math.Max(page, 1);
        sortBy = NormalizeLocationSortBy(sortBy);
        sortDirection = NormalizeSortDirection(sortDirection);

        var buildingsQuery = _context.SyncedBuildings.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.Trim().ToLower();
            buildingsQuery = buildingsQuery.Where(b =>
                ((b.ManualDisplayName != "" ? b.ManualDisplayName : b.DisplayName).ToLower().Contains(searchLower)) ||
                (b.ExternalId != null && b.ExternalId.ToLower().Contains(searchLower)) ||
                (b.ShortName != null && b.ShortName.ToLower().Contains(searchLower)) ||
                (b.RealName != null && b.RealName.ToLower().Contains(searchLower)) ||
                (b.Type != null && b.Type.ToLower().Contains(searchLower)) ||
                (b.ResponsibleArea != null && b.ResponsibleArea.ToLower().Contains(searchLower)));
        }

        if (!string.IsNullOrEmpty(campus))
            buildingsQuery = buildingsQuery.Where(b => (b.ManualCampus != "" ? b.ManualCampus : b.Campus).Contains(campus));

        if (!string.IsNullOrEmpty(floor))
        {
            var floorToken = $"\"{floor}\"";
            buildingsQuery = buildingsQuery.Where(b => (b.ManualFloorsJson != "" ? b.ManualFloorsJson : b.FloorsJson).Contains(floorToken));
        }

        var filteredBuildings = await buildingsQuery.ToListAsync();
        var totalFilteredLocations = filteredBuildings.Count;
        var filteredBuildingSnapshot = filteredBuildings
            .Select(b => new
            {
                b.Id,
                b.ExternalId,
                b.HasInteriorMap
            })
            .ToList();

        var filteredBuildingIds = filteredBuildingSnapshot.Select(b => b.Id).ToList();
        var filteredBuildingExternalIds = filteredBuildingSnapshot.Select(b => b.ExternalId).ToList();

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

        var sortedRows = SortLocationRows(
            filteredBuildings.Select(b => new AdminLocationRowViewModel
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
            }),
            sortBy,
            sortDirection)
            .ToList();

        var locations = sortedRows
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var model = new AdminLocationsViewModel
        {
            Search = search ?? string.Empty,
            Campus = campus ?? string.Empty,
            Floor = floor ?? string.Empty,
            SortBy = sortBy,
            SortDirection = sortDirection,
            Page = page,
            PageSize = pageSize,
            TotalFilteredLocations = totalFilteredLocations,
            Locations = locations,
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
        string? sortBy,
        string? sortDirection,
        string? inconsistencyType,
        bool onlyInconsistencies = false,
        int page = 1,
        int pageSize = 30)
    {
        pageSize = NormalizePageSize(pageSize);
        page = Math.Max(page, 1);
        assignment = string.IsNullOrWhiteSpace(assignment) ? "all" : assignment.Trim().ToLowerInvariant();
        sortBy = NormalizeInventorySortBy(sortBy);
        sortDirection = NormalizeSortDirection(sortDirection);
        inconsistencyType = NormalizeInconsistencyType(inconsistencyType);

        if (onlyInconsistencies && assignment == "all")
        {
            assignment = "inconsistent";
        }

        var inconsistencyFilterActive = onlyInconsistencies || assignment == "inconsistent";

        var inconsistencySnapshot = await AnalyzeInventoryInconsistenciesAsync();
        var inconsistentItemIds = inconsistencySnapshot.ItemIds.ToHashSet();
        var availableCategories = await GetInventoryCategoryOptionsAsync();
        var availableStatuses = await GetInventoryStatusOptionsAsync();

        var query = _context.ImportedInventoryItems.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.Trim().ToLower();
            query = query.Where(i =>
                (i.SerialNumber != null && i.SerialNumber.ToLower().Contains(searchLower)) ||
                (i.ItemNumber != null && i.ItemNumber.ToLower().Contains(searchLower)) ||
                (i.Lot != null && i.Lot.ToLower().Contains(searchLower)) ||
                (i.Description != null && i.Description.ToLower().Contains(searchLower)) ||
                (i.UnitOrDepartment != null && i.UnitOrDepartment.ToLower().Contains(searchLower)) ||
                (i.OrganizationalUnit != null && i.OrganizationalUnit.ToLower().Contains(searchLower)) ||
                (i.ResponsibleUser != null && i.ResponsibleUser.ToLower().Contains(searchLower)) ||
                (i.Email != null && i.Email.ToLower().Contains(searchLower)) ||
                (i.JobTitle != null && i.JobTitle.ToLower().Contains(searchLower)) ||
                (i.IpAddress != null && i.IpAddress.ToLower().Contains(searchLower)) ||
                (i.MacAddress != null && i.MacAddress.ToLower().Contains(searchLower)) ||
                (i.TicketMda != null && i.TicketMda.ToLower().Contains(searchLower)) ||
                (i.Observation != null && i.Observation.ToLower().Contains(searchLower)));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(i => i.InferredCategory == category);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(i => i.InferredStatus == status);
        }

        switch (assignment)
        {
            case "pending":
                query = query.Where(i => i.AssignedBuildingExternalId == "");
                break;
            case "assigned":
                if (!string.IsNullOrWhiteSpace(buildingExternalId))
                {
                    query = query.Where(i => i.AssignedBuildingExternalId == buildingExternalId);
                }

                query = query.Where(i => i.AssignedBuildingExternalId != "");
                break;
            case "suggested":
                if (!string.IsNullOrWhiteSpace(buildingExternalId))
                {
                    query = query.Where(i => i.MatchedBuildingExternalId == buildingExternalId);
                }

                query = query.Where(i => i.AssignedBuildingExternalId == "" && i.MatchedBuildingExternalId != "");
                break;
            case "inconsistent":
                if (!string.IsNullOrWhiteSpace(buildingExternalId))
                {
                    query = query.Where(i =>
                        i.AssignedBuildingExternalId == buildingExternalId ||
                        i.MatchedBuildingExternalId == buildingExternalId);
                }
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
                break;
        }

        if (inconsistencyFilterActive)
        {
            query = inconsistentItemIds.Count == 0
                ? query.Where(i => false)
                : query.Where(i => inconsistentItemIds.Contains(i.Id));
        }

        var inconsistencyTypeIds = string.IsNullOrWhiteSpace(inconsistencyType)
            ? null
            : inconsistencySnapshot.Summaries
                .Where(entry => MatchesInconsistencyType(entry.Value, inconsistencyType))
                .Select(entry => entry.Key)
                .ToHashSet();

        if (inconsistencyTypeIds is not null)
        {
            query = inconsistencyTypeIds.Count == 0
                ? query.Where(i => false)
                : query.Where(i => inconsistencyTypeIds.Contains(i.Id));
        }

        var filteredItems = await query.ToListAsync();
        var totalFilteredItems = filteredItems.Count;
        var sortedItems = SortInventoryItems(filteredItems, sortBy, sortDirection).ToList();
        var items = sortedItems
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var model = new AdminInventoryListViewModel
        {
            Search = search ?? string.Empty,
            Category = category ?? string.Empty,
            Status = status ?? string.Empty,
            AssignmentFilter = assignment,
            BuildingExternalId = buildingExternalId ?? string.Empty,
            SortBy = sortBy,
            SortDirection = sortDirection,
            InconsistencyType = inconsistencyType ?? string.Empty,
            OnlyInconsistencies = inconsistencyFilterActive,
            Page = page,
            PageSize = pageSize,
            TotalFilteredItems = totalFilteredItems,
            Items = items,
            Buildings = await _context.SyncedBuildings
                .AsNoTracking()
                .OrderBy(b => b.ManualDisplayName != "" ? b.ManualDisplayName : b.DisplayName)
                .ToListAsync(),
            Categories = availableCategories,
            Statuses = availableStatuses,
            AvailableInconsistencyTypes = GetInventoryInconsistencyFilterOptions(),
            InconsistencySummaries = items
                .Where(item => inconsistencySnapshot.Summaries.ContainsKey(item.Id))
                .ToDictionary(item => item.Id, item => inconsistencySnapshot.Summaries[item.Id]),
            TotalItems = await _context.ImportedInventoryItems.CountAsync(),
            AssignedItems = await _context.ImportedInventoryItems.CountAsync(i => i.AssignedBuildingExternalId != ""),
            PendingItems = await _context.ImportedInventoryItems.CountAsync(i => i.AssignedBuildingExternalId == ""),
            SuggestedItems = await _context.ImportedInventoryItems.CountAsync(i => i.AssignedBuildingExternalId == "" && i.MatchedBuildingExternalId != ""),
            InconsistentItems = inconsistencySnapshot.ItemIds.Count
        };

        return View("Equipments", model);
    }

    [HttpGet("/admin/inventory/inconsistency/{id:int}")]
    public async Task<IActionResult> InventoryInconsistency(int id, string? returnUrl = null)
    {
        var model = await BuildInventoryInconsistencyDetailViewModelAsync(id, returnUrl);
        if (model is null)
            return NotFound();

        return View(model);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost("/admin/inventory/inconsistency/{id:int}/merge")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MergeInventoryInconsistency(int id, int[]? selectedItemIds, string? returnUrl = null)
    {
        var normalizedReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : (Url.Action(nameof(InventoryInconsistency), new { id }) ?? $"/admin/inventory/inconsistency/{id}");

        var primary = await _context.ImportedInventoryItems.FirstOrDefaultAsync(item => item.Id == id);
        if (primary is null)
            return NotFound();

        var requestedIds = (selectedItemIds ?? Array.Empty<int>())
            .Where(itemId => itemId != id)
            .Distinct()
            .ToList();

        if (requestedIds.Count == 0)
        {
            TempData["ErrorMessage"] = "Selecciona al menos un equipo relacionado para fusionar.";
            return RedirectToAction(nameof(InventoryInconsistency), new { id, returnUrl = normalizedReturnUrl });
        }

        var requestedItems = await _context.ImportedInventoryItems
            .Where(item => requestedIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .ToListAsync();

        var mergePlan = requestedItems
            .Select(item => new InventoryMergePlanEntry
            {
                Item = item,
                MatchingFields = GetInventoryMatchingFields(primary, item)
            })
            .Where(entry => entry.MatchingFields.Count > 0)
            .OrderByDescending(entry => entry.MatchingFields.Count)
            .ThenBy(entry => entry.Item.Id)
            .ToList();

        if (mergePlan.Count == 0)
        {
            TempData["ErrorMessage"] = "No se encontraron coincidencias suficientes entre el equipo base y los equipos seleccionados.";
            return RedirectToAction(nameof(InventoryInconsistency), new { id, returnUrl = normalizedReturnUrl });
        }

        var previousBuildingExternalId = primary.AssignedBuildingExternalId;
        var previousRoomExternalId = primary.AssignedRoomExternalId;
        var previousFloor = primary.AssignedFloor;
        var previousSerialNumber = primary.SerialNumber;
        var previousAssignmentNotes = primary.AssignmentNotes;

        foreach (var entry in mergePlan)
        {
            MergeInventoryItems(primary, entry.Item);
        }

        _context.ImportedInventoryItems.RemoveRange(mergePlan.Select(entry => entry.Item));
        await _context.SaveChangesAsync();

        var actor = User.Identity?.Name ?? "sistema";
        var assignmentChanged = !string.Equals(previousBuildingExternalId ?? string.Empty, primary.AssignedBuildingExternalId ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(previousRoomExternalId ?? string.Empty, primary.AssignedRoomExternalId ?? string.Empty, StringComparison.Ordinal)
            || previousFloor != primary.AssignedFloor
            || !string.Equals(previousSerialNumber ?? string.Empty, primary.SerialNumber ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(previousAssignmentNotes ?? string.Empty, primary.AssignmentNotes ?? string.Empty, StringComparison.Ordinal);

        if (assignmentChanged)
        {
            await _auditLogService.LogInventoryItemChangeAsync(
                primary,
                actor,
                previousBuildingExternalId,
                previousRoomExternalId,
                previousFloor,
                previousSerialNumber,
                previousAssignmentNotes);
        }

        await LogInventoryMergeAsync(primary, mergePlan, actor);

        TempData["SuccessMessage"] = $"Fusion completada. Se integraron {mergePlan.Count} registro(s) en el equipo #{primary.Id}.";
        return RedirectToAction(nameof(InventoryInconsistency), new { id = primary.Id, returnUrl = normalizedReturnUrl });
    }
    [Authorize(Roles = AppRoles.Admin)]
    [HttpGet("/admin/inventory/create")]
    public async Task<IActionResult> CreateInventoryItem()
    {
        var form = new InventoryItemFormModel
        {
            InferredCategory = "other",
            InferredStatus = "active"
        };

        return View(await BuildCreateInventoryItemViewModelAsync(form));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost("/admin/inventory/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateInventoryItem(CreateInventoryItemViewModel model)
    {
        var form = model.Form ?? new InventoryItemFormModel();
        var categories = await GetInventoryCategoryOptionsAsync();
        var statuses = await GetInventoryStatusOptionsAsync();

        var normalizedCategory = string.IsNullOrWhiteSpace(form.InferredCategory)
            ? "other"
            : form.InferredCategory.Trim().ToLowerInvariant();
        var normalizedStatus = string.IsNullOrWhiteSpace(form.InferredStatus)
            ? "active"
            : form.InferredStatus.Trim().ToLowerInvariant();

        if (!categories.Contains(normalizedCategory, StringComparer.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("Form.InferredCategory", "Selecciona una categoria valida de la lista.");
        }

        if (!statuses.Contains(normalizedStatus, StringComparer.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("Form.InferredStatus", "Selecciona un estado valido de la lista.");
        }

        if (string.IsNullOrWhiteSpace(form.SerialNumber) && string.IsNullOrWhiteSpace(form.Description))
        {
            ModelState.AddModelError("Form.SerialNumber", "Ingresa al menos un S/N o una descripcion para identificar el equipo.");
        }

        var assignedBuildingExternalId = form.AssignedBuildingExternalId?.Trim() ?? string.Empty;
        var assignedRoomExternalId = form.AssignedRoomExternalId?.Trim() ?? string.Empty;
        var assignedFloor = form.AssignedFloor;

        SyncedRoom? assignedRoom = null;
        if (!string.IsNullOrWhiteSpace(assignedRoomExternalId))
        {
            assignedRoom = await _context.SyncedRooms
                .AsNoTracking()
                .FirstOrDefaultAsync(room => room.ExternalId == assignedRoomExternalId);

            if (assignedRoom == null)
            {
                ModelState.AddModelError("Form.AssignedRoomExternalId", "La sala seleccionada ya no existe en la sincronizacion actual.");
            }
            else
            {
                assignedRoomExternalId = assignedRoom.ExternalId;
                assignedBuildingExternalId = assignedRoom.BuildingExternalId;
                assignedFloor = assignedRoom.ManualFloor ?? assignedRoom.Floor;
            }
        }

        if (!string.IsNullOrWhiteSpace(assignedBuildingExternalId))
        {
            var buildingExists = await _context.SyncedBuildings
                .AsNoTracking()
                .AnyAsync(building => building.ExternalId == assignedBuildingExternalId);

            if (!buildingExists)
            {
                ModelState.AddModelError("Form.AssignedBuildingExternalId", "El edificio seleccionado ya no existe en la sincronizacion actual.");
            }
        }
        else
        {
            assignedRoomExternalId = string.Empty;
            assignedFloor = null;
        }

        form.InferredCategory = normalizedCategory;
        form.InferredStatus = normalizedStatus;
        form.AssignedBuildingExternalId = assignedBuildingExternalId;
        form.AssignedRoomExternalId = assignedRoomExternalId;
        form.AssignedFloor = assignedFloor;

        if (!ModelState.IsValid)
        {
            return View(await BuildCreateInventoryItemViewModelAsync(form));
        }

        var nextRowNumber = (await _context.ImportedInventoryItems.MaxAsync(i => (int?)i.RowNumber) ?? 0) + 1;
        var item = new ImportedInventoryItem
        {
            RowNumber = nextRowNumber,
            ItemNumber = string.IsNullOrWhiteSpace(form.ItemNumber) ? $"MANUAL-{nextRowNumber:D5}" : form.ItemNumber.Trim(),
            SerialNumber = form.SerialNumber?.Trim() ?? string.Empty,
            Description = form.Description?.Trim() ?? string.Empty,
            Lot = form.Lot?.Trim() ?? string.Empty,
            UnitOrDepartment = form.UnitOrDepartment?.Trim() ?? string.Empty,
            OrganizationalUnit = form.OrganizationalUnit?.Trim() ?? string.Empty,
            ResponsibleUser = form.ResponsibleUser?.Trim() ?? string.Empty,
            Email = form.Email?.Trim() ?? string.Empty,
            JobTitle = form.JobTitle?.Trim() ?? string.Empty,
            IpAddress = form.IpAddress?.Trim() ?? string.Empty,
            MacAddress = form.MacAddress?.Trim() ?? string.Empty,
            AnnexPhone = form.AnnexPhone?.Trim() ?? string.Empty,
            TicketMda = form.TicketMda?.Trim() ?? string.Empty,
            Installer = form.Installer?.Trim() ?? string.Empty,
            Observation = form.Observation?.Trim() ?? string.Empty,
            InferredCategory = normalizedCategory,
            InferredStatus = normalizedStatus,
            AssignedBuildingExternalId = assignedBuildingExternalId,
            AssignedRoomExternalId = assignedRoomExternalId,
            AssignedFloor = assignedFloor,
            AssignmentNotes = form.AssignmentNotes?.Trim() ?? string.Empty,
            SourceFile = ManualInventorySourceFile,
            ImportedAtUtc = DateTime.UtcNow,
            AssignmentUpdatedAtUtc = string.IsNullOrWhiteSpace(assignedBuildingExternalId) ? null : DateTime.UtcNow,
            MatchedSyncedBuildingId = null,
            MatchedSyncedRoomId = null,
            MatchedBuildingExternalId = string.Empty,
            MatchedRoomExternalId = string.Empty,
            MatchConfidence = string.Empty,
            MatchNotes = string.Empty
        };

        _context.ImportedInventoryItems.Add(item);
        await _context.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(item.AssignedBuildingExternalId))
        {
            await _auditLogService.LogInventoryItemChangeAsync(
                item,
                User.Identity?.Name ?? "sistema",
                string.Empty,
                string.Empty,
                null,
                string.Empty,
                string.Empty);
        }

        TempData["SuccessMessage"] = "Equipo creado correctamente.";
        return RedirectToAction(nameof(EditInventoryItem), new { id = item.Id });
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
                .ToListAsync(),
            Categories = await GetInventoryCategoryOptionsAsync(),
            Statuses = await GetInventoryStatusOptionsAsync()
        };

        return View(model);
    }

    [HttpGet("/admin/suggestions/inventory")]
    public async Task<IActionResult> InventorySuggestions(string? query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Json(Array.Empty<string>());
        }

        var trimmed = query.Trim().ToLower();
        var candidates = await _context.ImportedInventoryItems.AsNoTracking()
            .Where(i =>
                (i.SerialNumber != null && i.SerialNumber.ToLower().Contains(trimmed)) ||
                (i.ItemNumber != null && i.ItemNumber.ToLower().Contains(trimmed)) ||
                (i.Description != null && i.Description.ToLower().Contains(trimmed)) ||
                (i.ResponsibleUser != null && i.ResponsibleUser.ToLower().Contains(trimmed)) ||
                (i.Email != null && i.Email.ToLower().Contains(trimmed)) ||
                (i.IpAddress != null && i.IpAddress.ToLower().Contains(trimmed)) ||
                (i.UnitOrDepartment != null && i.UnitOrDepartment.ToLower().Contains(trimmed)) ||
                (i.OrganizationalUnit != null && i.OrganizationalUnit.ToLower().Contains(trimmed)) ||
                (i.AssignedBuildingExternalId != null && i.AssignedBuildingExternalId.ToLower().Contains(trimmed)))
            .Select(i => new
            {
                i.SerialNumber,
                i.ItemNumber,
                i.Description,
                i.ResponsibleUser,
                i.Email,
                i.IpAddress,
                i.UnitOrDepartment,
                i.OrganizationalUnit,
                i.AssignedBuildingExternalId
            })
            .Take(200)
            .ToListAsync();

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfMatch(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var candidate = value.Trim();
            if (!candidate.ToLower().Contains(trimmed))
            {
                return;
            }

            results.Add(candidate);
        }

        foreach (var item in candidates)
        {
            AddIfMatch(item.SerialNumber);
            AddIfMatch(item.ItemNumber);
            AddIfMatch(item.Description);
            AddIfMatch(item.ResponsibleUser);
            AddIfMatch(item.Email);
            AddIfMatch(item.IpAddress);
            AddIfMatch(item.UnitOrDepartment);
            AddIfMatch(item.OrganizationalUnit);
            AddIfMatch(item.AssignedBuildingExternalId);

            if (results.Count >= limit)
            {
                break;
            }
        }

        return Json(results.Take(limit).ToList());
    }

    [HttpGet("/admin/suggestions/inventory-category")]
    public async Task<IActionResult> InventoryCategorySuggestions(string? query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Json(Array.Empty<string>());
        }

        var trimmed = query.Trim().ToLower();
        var categories = await _context.ImportedInventoryItems.AsNoTracking()
            .Where(i => i.InferredCategory != null && i.InferredCategory.ToLower().Contains(trimmed))
            .Select(i => i.InferredCategory!)
            .Distinct()
            .OrderBy(c => c)
            .Take(limit)
            .ToListAsync();

        return Json(categories);
    }

    [HttpGet("/admin/suggestions/inventory-status")]
    public async Task<IActionResult> InventoryStatusSuggestions(string? query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Json(Array.Empty<string>());
        }

        var trimmed = query.Trim().ToLower();
        var statuses = await _context.ImportedInventoryItems.AsNoTracking()
            .Where(i => i.InferredStatus != null && i.InferredStatus.ToLower().Contains(trimmed))
            .Select(i => i.InferredStatus!)
            .Distinct()
            .OrderBy(s => s)
            .Take(limit)
            .ToListAsync();

        return Json(statuses);
    }

    [HttpGet("/admin/suggestions/locations")]
    public async Task<IActionResult> LocationSuggestions(string? query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Json(Array.Empty<string>());
        }

        var trimmed = query.Trim().ToLower();
        var candidates = await _context.SyncedBuildings.AsNoTracking()
            .Where(b =>
                (b.DisplayName != null && b.DisplayName.ToLower().Contains(trimmed)) ||
                (b.ManualDisplayName != null && b.ManualDisplayName.ToLower().Contains(trimmed)) ||
                (b.ExternalId != null && b.ExternalId.ToLower().Contains(trimmed)) ||
                (b.Type != null && b.Type.ToLower().Contains(trimmed)) ||
                (b.Campus != null && b.Campus.ToLower().Contains(trimmed)) ||
                (b.ManualCampus != null && b.ManualCampus.ToLower().Contains(trimmed)) ||
                (b.ResponsibleArea != null && b.ResponsibleArea.ToLower().Contains(trimmed)))
            .Select(b => new
            {
                b.DisplayName,
                b.ManualDisplayName,
                b.ExternalId,
                b.Type,
                b.Campus,
                b.ManualCampus,
                b.ResponsibleArea
            })
            .Take(200)
            .ToListAsync();

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfMatch(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var candidate = value.Trim();
            if (!candidate.ToLower().Contains(trimmed))
            {
                return;
            }

            results.Add(candidate);
        }

        foreach (var item in candidates)
        {
            AddIfMatch(item.ManualDisplayName);
            AddIfMatch(item.DisplayName);
            AddIfMatch(item.ExternalId);
            AddIfMatch(item.Type);
            AddIfMatch(item.ManualCampus);
            AddIfMatch(item.Campus);
            AddIfMatch(item.ResponsibleArea);

            if (results.Count >= limit)
            {
                break;
            }
        }

        return Json(results.Take(limit).ToList());
    }

    [HttpGet("/admin/suggestions/campus")]
    public async Task<IActionResult> CampusSuggestions(string? query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Json(Array.Empty<string>());
        }

        var trimmed = query.Trim().ToLower();
        var campuses = await _context.SyncedBuildings.AsNoTracking()
            .Select(b => string.IsNullOrWhiteSpace(b.ManualCampus) ? b.Campus : b.ManualCampus)
            .Where(c => c != null && c.ToLower().Contains(trimmed))
            .Distinct()
            .OrderBy(c => c)
            .Take(limit)
            .ToListAsync();

        return Json(campuses);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditInventoryItem(
        int id,
        string? serialNumber,
        string? inferredCategory,
        string? inferredStatus,
        string? assignedBuildingExternalId,
        string? assignedRoomExternalId,
        int? assignedFloor,
        string? assignmentNotes)
    {
        var item = await _context.ImportedInventoryItems.FindAsync(id);
        if (item == null)
            return NotFound();

        var categories = await GetInventoryCategoryOptionsAsync();
        var statuses = await GetInventoryStatusOptionsAsync();
        var previousBuildingExternalId = item.AssignedBuildingExternalId;
        var previousRoomExternalId = item.AssignedRoomExternalId;
        var previousFloor = item.AssignedFloor;
        var previousSerialNumber = item.SerialNumber;
        var previousAssignmentNotes = item.AssignmentNotes;

        var normalizedCategory = string.IsNullOrWhiteSpace(inferredCategory)
            ? "other"
            : inferredCategory.Trim().ToLowerInvariant();
        var normalizedStatus = string.IsNullOrWhiteSpace(inferredStatus)
            ? "active"
            : inferredStatus.Trim().ToLowerInvariant();

        if (!categories.Contains(normalizedCategory, StringComparer.OrdinalIgnoreCase))
        {
            normalizedCategory = "other";
        }

        if (!statuses.Contains(normalizedStatus, StringComparer.OrdinalIgnoreCase))
        {
            normalizedStatus = "active";
        }

        var resolvedBuildingExternalId = assignedBuildingExternalId?.Trim() ?? string.Empty;
        var resolvedRoomExternalId = assignedRoomExternalId?.Trim() ?? string.Empty;
        var resolvedFloor = assignedFloor;

        if (!string.IsNullOrWhiteSpace(resolvedRoomExternalId))
        {
            var room = await _context.SyncedRooms
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.ExternalId == resolvedRoomExternalId);

            if (room != null)
            {
                resolvedRoomExternalId = room.ExternalId;
                resolvedBuildingExternalId = room.BuildingExternalId;
                resolvedFloor = room.ManualFloor ?? room.Floor;
            }
            else
            {
                resolvedRoomExternalId = string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedBuildingExternalId))
        {
            resolvedRoomExternalId = string.Empty;
            resolvedFloor = null;
        }

        item.SerialNumber = serialNumber?.Trim() ?? string.Empty;
        item.InferredCategory = normalizedCategory;
        item.InferredStatus = normalizedStatus;
        item.AssignedBuildingExternalId = resolvedBuildingExternalId;
        item.AssignedRoomExternalId = resolvedRoomExternalId;
        item.AssignedFloor = resolvedFloor;
        item.AssignmentNotes = assignmentNotes?.Trim() ?? string.Empty;
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
        TempData["SuccessMessage"] = "Equipo actualizado correctamente.";
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
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteInventoryItem(int id)
    {
        var item = await _context.ImportedInventoryItems.FindAsync(id);
        if (item == null)
            return NotFound();

        var actor = User.Identity?.Name ?? "sistema";
        var itemLabel = !string.IsNullOrWhiteSpace(item.SerialNumber)
            ? $"S/N {item.SerialNumber}"
            : $"fila #{item.RowNumber}";
        var impactedBuildings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(item.AssignedBuildingExternalId))
        {
            impactedBuildings.Add(item.AssignedBuildingExternalId);
        }

        if (!string.IsNullOrWhiteSpace(item.MatchedBuildingExternalId))
        {
            impactedBuildings.Add(item.MatchedBuildingExternalId);
        }

        foreach (var buildingExternalId in impactedBuildings)
        {
            _context.AuditLogEntries.Add(new AuditLogEntry
            {
                BuildingExternalId = buildingExternalId,
                EntityType = "inventory-item",
                EntityId = item.Id.ToString(),
                ActionType = "deleted",
                Summary = $"{itemLabel} eliminado",
                Details = "Equipo eliminado manualmente desde el dashboard.",
                ChangedByUsername = actor,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        _context.ImportedInventoryItems.Remove(item);
        await _context.SaveChangesAsync();

        TempData["SuccessMessage"] = "Equipo eliminado correctamente.";
        return RedirectToAction(nameof(Inventory));
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

    private sealed class InventoryInconsistencySnapshot
    {
        public HashSet<int> ItemIds { get; } = [];
        public Dictionary<int, string> Summaries { get; } = [];
    }

    private sealed class InventoryInconsistencyCandidate
    {
        public int Id { get; init; }
        public string SerialKey { get; init; } = string.Empty;
        public string SerialFamilyKey { get; init; } = string.Empty;
        public string IpKey { get; init; } = string.Empty;
        public string MacKey { get; init; } = string.Empty;
        public string UnitSignature { get; init; } = string.Empty;
    }

    private sealed class InventoryMergePlanEntry
    {
        public ImportedInventoryItem Item { get; init; } = null!;
        public IReadOnlyList<string> MatchingFields { get; init; } = [];
    }

    private async Task<InventoryInconsistencySnapshot> AnalyzeInventoryInconsistenciesAsync()
    {
        var snapshot = new InventoryInconsistencySnapshot();
        var candidates = await _context.ImportedInventoryItems
            .AsNoTracking()
            .Select(item => new InventoryInconsistencyCandidate
            {
                Id = item.Id,
                SerialKey = NormalizeInventoryToken(item.SerialNumber),
                SerialFamilyKey = NormalizeInventorySerialFamily(item.SerialNumber),
                IpKey = NormalizeInventoryToken(item.IpAddress),
                MacKey = NormalizeInventoryToken(item.MacAddress),
                UnitSignature = $"{NormalizeInventoryToken(item.UnitOrDepartment)}|{NormalizeInventoryToken(item.OrganizationalUnit)}"
            })
            .ToListAsync();

        var reasons = new Dictionary<int, HashSet<string>>();

        void AddReason(int id, string reason)
        {
            if (!reasons.TryGetValue(id, out var itemReasons))
            {
                itemReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                reasons[id] = itemReasons;
            }

            itemReasons.Add(reason);
        }

        foreach (var group in candidates.Where(item => item.SerialKey != string.Empty).GroupBy(item => item.SerialKey).Where(group => group.Count() > 1))
        {
            foreach (var item in group)
            {
                AddReason(item.Id, "S/N duplicado");
            }
        }

        foreach (var group in candidates.Where(item => item.SerialFamilyKey != string.Empty).GroupBy(item => item.SerialFamilyKey).Where(group => group.Count() > 1))
        {
            var items = group.ToList();
            if (items.Select(item => item.SerialKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                foreach (var item in items)
                {
                    AddReason(item.Id, "S/N muy parecido");
                }
            }

            if (items.Select(item => item.UnitSignature).Where(signature => signature != "|").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                foreach (var item in items)
                {
                    AddReason(item.Id, "Unidad/org distinta en posibles duplicados");
                }
            }
        }

        foreach (var group in candidates.Where(item => item.IpKey != string.Empty).GroupBy(item => item.IpKey).Where(group => group.Count() > 1))
        {
            foreach (var item in group)
            {
                AddReason(item.Id, "IP repetida");
            }
        }

        foreach (var group in candidates.Where(item => item.MacKey != string.Empty).GroupBy(item => item.MacKey).Where(group => group.Count() > 1))
        {
            foreach (var item in group)
            {
                AddReason(item.Id, "MAC repetida");
            }
        }

        foreach (var entry in reasons)
        {
            snapshot.ItemIds.Add(entry.Key);
            snapshot.Summaries[entry.Key] = string.Join("; ", entry.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        }

        return snapshot;
    }

    private async Task<InventoryInconsistencyDetailViewModel?> BuildInventoryInconsistencyDetailViewModelAsync(int id, string? returnUrl)
    {
        var items = await _context.ImportedInventoryItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();

        var current = items.FirstOrDefault(item => item.Id == id);
        if (current is null)
        {
            return null;
        }

        var candidatesById = items.ToDictionary(
            item => item.Id,
            item => new InventoryInconsistencyCandidate
            {
                Id = item.Id,
                SerialKey = NormalizeInventoryToken(item.SerialNumber),
                SerialFamilyKey = NormalizeInventorySerialFamily(item.SerialNumber),
                IpKey = NormalizeInventoryToken(item.IpAddress),
                MacKey = NormalizeInventoryToken(item.MacAddress),
                UnitSignature = $"{NormalizeInventoryToken(item.UnitOrDepartment)}|{NormalizeInventoryToken(item.OrganizationalUnit)}"
            });

        var currentCandidate = candidatesById[id];
        var reasons = new List<InventoryInconsistencyReasonViewModel>();
        var relatedItemsById = new Dictionary<int, InventoryInconsistencyRelatedItemViewModel>();

        void AddReason(string title, string suggestedAction, IEnumerable<ImportedInventoryItem> relatedItems)
        {
            var related = MapInventoryRelatedItems(current, relatedItems, current.Id);
            if (related.Count == 0)
            {
                return;
            }

            foreach (var relatedItem in related)
            {
                relatedItemsById[relatedItem.Id] = relatedItem;
            }

            reasons.Add(new InventoryInconsistencyReasonViewModel
            {
                Title = title,
                SuggestedAction = suggestedAction,
                RelatedItems = related
            });
        }

        if (!string.IsNullOrWhiteSpace(currentCandidate.SerialKey))
        {
            var exactSerialItems = candidatesById.Values
                .Where(candidate => candidate.SerialKey == currentCandidate.SerialKey)
                .Select(candidate => items.First(item => item.Id == candidate.Id))
                .ToList();

            if (exactSerialItems.Count > 1)
            {
                AddReason(
                    "S/N duplicado",
                    "Compara ambos registros y corrige el serial o elimina el duplicado si representan el mismo equipo.",
                    exactSerialItems);
            }
        }

        if (!string.IsNullOrWhiteSpace(currentCandidate.SerialFamilyKey))
        {
            var familyCandidates = candidatesById.Values
                .Where(candidate => candidate.SerialFamilyKey == currentCandidate.SerialFamilyKey)
                .ToList();
            var familyItems = familyCandidates
                .Select(candidate => items.First(item => item.Id == candidate.Id))
                .ToList();

            if (familyCandidates.Count > 1 && familyCandidates.Select(candidate => candidate.SerialKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                AddReason(
                    "S/N muy parecido",
                    "Revisa si se trata del mismo equipo cargado con un prefijo extra o con una variacion menor del serial.",
                    familyItems);
            }

            if (familyCandidates.Count > 1 && familyCandidates.Select(candidate => candidate.UnitSignature).Where(signature => signature != "|").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                AddReason(
                    "Unidad/org distinta en posibles duplicados",
                    "Confirma cual es la unidad correcta y deja ambos registros consistentes o consolida el duplicado.",
                    familyItems);
            }
        }

        if (!string.IsNullOrWhiteSpace(currentCandidate.IpKey))
        {
            var ipItems = candidatesById.Values
                .Where(candidate => candidate.IpKey == currentCandidate.IpKey)
                .Select(candidate => items.First(item => item.Id == candidate.Id))
                .ToList();

            if (ipItems.Count > 1)
            {
                AddReason(
                    "IP repetida",
                    "Verifica si ambos registros apuntan al mismo equipo o si una IP quedo repetida por error u obsolescencia.",
                    ipItems);
            }
        }

        if (!string.IsNullOrWhiteSpace(currentCandidate.MacKey))
        {
            var macItems = candidatesById.Values
                .Where(candidate => candidate.MacKey == currentCandidate.MacKey)
                .Select(candidate => items.First(item => item.Id == candidate.Id))
                .ToList();

            if (macItems.Count > 1)
            {
                AddReason(
                    "MAC repetida",
                    "Revisa si la MAC pertenece realmente a mas de un registro o si un equipo fue duplicado durante la carga.",
                    macItems);
            }
        }

        var mergeCandidates = relatedItemsById.Values
            .OrderByDescending(item => item.IsMergeRecommended)
            .ThenByDescending(item => item.MatchingFieldCount)
            .ThenBy(item => item.Id)
            .ToList();

        var defaultReturnUrl = Url.Action("Inventory", new { assignment = "inconsistent", page = 1, pageSize = 30 })
            ?? "/admin/inventory?assignment=inconsistent";
        var normalizedReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : defaultReturnUrl;

        return new InventoryInconsistencyDetailViewModel
        {
            Item = current,
            Summary = reasons.Count == 0
                ? "No se detectaron incongruencias activas para este equipo."
                : string.Join("; ", reasons.Select(reason => reason.Title).Distinct(StringComparer.OrdinalIgnoreCase)),
            ReturnUrl = normalizedReturnUrl,
            Reasons = reasons,
            SuggestedActions = BuildInventoryInconsistencyActions(current, normalizedReturnUrl),
            MergeCandidates = mergeCandidates,
            RecommendedMergeCount = mergeCandidates.Count(item => item.IsMergeRecommended)
        };
    }

    private List<InventoryInconsistencyActionViewModel> BuildInventoryInconsistencyActions(ImportedInventoryItem item, string returnUrl)
    {
        var actions = new List<InventoryInconsistencyActionViewModel>
        {
            new()
            {
                Label = "Volver al inventario",
                Url = returnUrl,
                IconClass = "bi bi-arrow-left",
                ButtonClass = "btn btn-outline-secondary"
            },
            new()
            {
                Label = User.IsInRole(AppRoles.Admin) ? "Editar este equipo" : "Ver equipo",
                Url = $"/admin/editinventoryitem/{item.Id}",
                IconClass = "bi bi-pencil-square",
                ButtonClass = "btn btn-primary"
            }
        };

        void AddSearchAction(string label, string? value, string iconClass)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            var url = Url.Action("Inventory", new { search = trimmed, assignment = "all", page = 1, pageSize = 30 })
                ?? $"/admin/inventory?search={Uri.EscapeDataString(trimmed)}";

            actions.Add(new InventoryInconsistencyActionViewModel
            {
                Label = label,
                Url = url,
                IconClass = iconClass,
                ButtonClass = "btn btn-outline-primary"
            });
        }

        AddSearchAction("Filtrar por S/N", item.SerialNumber, "bi bi-upc-scan");
        AddSearchAction("Filtrar por IP", item.IpAddress, "bi bi-diagram-3");
        AddSearchAction("Filtrar por MAC", item.MacAddress, "bi bi-hdd-network");

        return actions;
    }

    private async Task LogInventoryMergeAsync(
        ImportedInventoryItem primary,
        IReadOnlyList<InventoryMergePlanEntry> mergePlan,
        string changedByUsername,
        CancellationToken cancellationToken = default)
    {
        var actor = string.IsNullOrWhiteSpace(changedByUsername) ? "sistema" : changedByUsername.Trim();
        var itemLabel = !string.IsNullOrWhiteSpace(primary.SerialNumber)
            ? $"S/N {primary.SerialNumber}"
            : $"fila #{primary.RowNumber}";

        var summary = $"{itemLabel} fusionado con {mergePlan.Count} registro(s)";
        var details = string.Join(
            "; ",
            mergePlan.Select(entry => $"#{entry.Item.Id}: {string.Join(", ", entry.MatchingFields)}"));

        var impactedBuildings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(primary.AssignedBuildingExternalId))
        {
            impactedBuildings.Add(primary.AssignedBuildingExternalId);
        }
        if (!string.IsNullOrWhiteSpace(primary.MatchedBuildingExternalId))
        {
            impactedBuildings.Add(primary.MatchedBuildingExternalId);
        }

        foreach (var entry in mergePlan)
        {
            if (!string.IsNullOrWhiteSpace(entry.Item.AssignedBuildingExternalId))
            {
                impactedBuildings.Add(entry.Item.AssignedBuildingExternalId);
            }
            if (!string.IsNullOrWhiteSpace(entry.Item.MatchedBuildingExternalId))
            {
                impactedBuildings.Add(entry.Item.MatchedBuildingExternalId);
            }
        }

        if (impactedBuildings.Count == 0)
        {
            impactedBuildings.Add(string.Empty);
        }

        foreach (var buildingExternalId in impactedBuildings)
        {
            _context.AuditLogEntries.Add(new AuditLogEntry
            {
                BuildingExternalId = buildingExternalId,
                EntityType = "inventory-item",
                EntityId = primary.Id.ToString(),
                ActionType = "merged",
                Summary = summary,
                Details = details,
                ChangedByUsername = actor,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static void MergeInventoryItems(ImportedInventoryItem primary, ImportedInventoryItem duplicate)
    {
        primary.ItemNumber = PreferInventoryValue(primary.ItemNumber, duplicate.ItemNumber);
        primary.SerialNumber = PreferInventoryValue(primary.SerialNumber, duplicate.SerialNumber);
        primary.Description = PreferInventoryValue(primary.Description, duplicate.Description);
        primary.Lot = PreferInventoryValue(primary.Lot, duplicate.Lot);
        primary.InstallDate = PreferInventoryValue(primary.InstallDate, duplicate.InstallDate);
        primary.UnitOrDepartment = PreferInventoryValue(primary.UnitOrDepartment, duplicate.UnitOrDepartment);
        primary.OrganizationalUnit = PreferInventoryValue(primary.OrganizationalUnit, duplicate.OrganizationalUnit);
        primary.ResponsibleUser = PreferInventoryValue(primary.ResponsibleUser, duplicate.ResponsibleUser);
        primary.Run = PreferInventoryValue(primary.Run, duplicate.Run);
        primary.Email = PreferInventoryValue(primary.Email, duplicate.Email);
        primary.JobTitle = PreferInventoryValue(primary.JobTitle, duplicate.JobTitle);
        primary.IpAddress = PreferInventoryValue(primary.IpAddress, duplicate.IpAddress);
        primary.MacAddress = PreferInventoryValue(primary.MacAddress, duplicate.MacAddress);
        primary.AnnexPhone = PreferInventoryValue(primary.AnnexPhone, duplicate.AnnexPhone);
        primary.ReplacedEquipment = PreferInventoryValue(primary.ReplacedEquipment, duplicate.ReplacedEquipment);
        primary.TicketMda = PreferInventoryValue(primary.TicketMda, duplicate.TicketMda);
        primary.Installer = PreferInventoryValue(primary.Installer, duplicate.Installer);
        primary.Rut = PreferInventoryValue(primary.Rut, duplicate.Rut);
        primary.InventoryDate = PreferInventoryValue(primary.InventoryDate, duplicate.InventoryDate);
        primary.SourceFile = PreferInventoryValue(primary.SourceFile, duplicate.SourceFile);

        if (IsWeakInventoryCategory(primary.InferredCategory) && !string.IsNullOrWhiteSpace(duplicate.InferredCategory))
        {
            primary.InferredCategory = duplicate.InferredCategory.Trim().ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(primary.InferredStatus) && !string.IsNullOrWhiteSpace(duplicate.InferredStatus))
        {
            primary.InferredStatus = duplicate.InferredStatus.Trim().ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(primary.AssignedBuildingExternalId) && !string.IsNullOrWhiteSpace(duplicate.AssignedBuildingExternalId))
        {
            primary.AssignedBuildingExternalId = duplicate.AssignedBuildingExternalId;
            primary.AssignedRoomExternalId = PreferInventoryValue(primary.AssignedRoomExternalId, duplicate.AssignedRoomExternalId);
            primary.AssignedFloor = primary.AssignedFloor ?? duplicate.AssignedFloor;
            primary.AssignmentUpdatedAtUtc = duplicate.AssignmentUpdatedAtUtc ?? DateTime.UtcNow;
        }

        if (string.IsNullOrWhiteSpace(primary.MatchedBuildingExternalId) && !string.IsNullOrWhiteSpace(duplicate.MatchedBuildingExternalId))
        {
            primary.MatchedBuildingExternalId = duplicate.MatchedBuildingExternalId;
            primary.MatchedRoomExternalId = PreferInventoryValue(primary.MatchedRoomExternalId, duplicate.MatchedRoomExternalId);
            primary.MatchConfidence = PreferInventoryValue(primary.MatchConfidence, duplicate.MatchConfidence);
            primary.MatchNotes = AppendInventoryUnique(primary.MatchNotes, duplicate.MatchNotes);
            primary.MatchedSyncedBuildingId ??= duplicate.MatchedSyncedBuildingId;
            primary.MatchedSyncedRoomId ??= duplicate.MatchedSyncedRoomId;
        }
        else
        {
            primary.MatchNotes = AppendInventoryUnique(primary.MatchNotes, duplicate.MatchNotes);
        }

        primary.AssignmentNotes = AppendInventoryUnique(primary.AssignmentNotes, duplicate.AssignmentNotes);
        primary.Observation = AppendInventoryUnique(primary.Observation, duplicate.Observation);

        if (duplicate.ImportedAtUtc < primary.ImportedAtUtc)
        {
            primary.ImportedAtUtc = duplicate.ImportedAtUtc;
        }

        if (!primary.AssignmentUpdatedAtUtc.HasValue || (duplicate.AssignmentUpdatedAtUtc.HasValue && duplicate.AssignmentUpdatedAtUtc > primary.AssignmentUpdatedAtUtc))
        {
            primary.AssignmentUpdatedAtUtc = duplicate.AssignmentUpdatedAtUtc ?? primary.AssignmentUpdatedAtUtc;
        }
    }

    private static IReadOnlyList<InventoryInconsistencyRelatedItemViewModel> MapInventoryRelatedItems(
        ImportedInventoryItem currentItem,
        IEnumerable<ImportedInventoryItem> items,
        int currentItemId)
    {
        return items
            .Where(item => item.Id != currentItemId)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .Select(item =>
            {
                var matchingFields = GetInventoryMatchingFields(currentItem, item);
                return new InventoryInconsistencyRelatedItemViewModel
                {
                    Id = item.Id,
                    SerialNumber = string.IsNullOrWhiteSpace(item.SerialNumber) ? "Sin S/N" : item.SerialNumber,
                    ItemNumber = item.ItemNumber,
                    Description = item.Description,
                    UnitOrDepartment = item.UnitOrDepartment,
                    OrganizationalUnit = item.OrganizationalUnit,
                    ResponsibleUser = item.ResponsibleUser,
                    Email = item.Email,
                    IpAddress = item.IpAddress,
                    MacAddress = item.MacAddress,
                    AssignmentLabel = FormatInventoryAssignmentLabel(item),
                    MatchingFields = matchingFields,
                    MatchingFieldCount = matchingFields.Count,
                    IsMergeRecommended = matchingFields.Count >= 3
                };
            })
            .Where(item => item.MatchingFieldCount > 0)
            .OrderByDescending(item => item.IsMergeRecommended)
            .ThenByDescending(item => item.MatchingFieldCount)
            .ThenBy(item => item.Id)
            .ToList();
    }

    private static IReadOnlyList<string> GetInventoryMatchingFields(ImportedInventoryItem currentItem, ImportedInventoryItem otherItem)
    {
        var matchingFields = new List<string>();

        var currentSerialKey = NormalizeInventoryToken(currentItem.SerialNumber);
        var otherSerialKey = NormalizeInventoryToken(otherItem.SerialNumber);
        var currentSerialFamily = NormalizeInventorySerialFamily(currentItem.SerialNumber);
        var otherSerialFamily = NormalizeInventorySerialFamily(otherItem.SerialNumber);

        if (!string.IsNullOrWhiteSpace(currentSerialKey) && !string.IsNullOrWhiteSpace(otherSerialKey)
            && (string.Equals(currentSerialKey, otherSerialKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(currentSerialFamily, otherSerialFamily, StringComparison.OrdinalIgnoreCase)))
        {
            matchingFields.Add("S/N");
        }

        void AddIfSame(string label, string? currentValue, string? otherValue)
        {
            var normalizedCurrent = NormalizeInventoryToken(currentValue);
            var normalizedOther = NormalizeInventoryToken(otherValue);
            if (!string.IsNullOrWhiteSpace(normalizedCurrent)
                && string.Equals(normalizedCurrent, normalizedOther, StringComparison.OrdinalIgnoreCase))
            {
                matchingFields.Add(label);
            }
        }

        AddIfSame("IP", currentItem.IpAddress, otherItem.IpAddress);
        AddIfSame("MAC", currentItem.MacAddress, otherItem.MacAddress);
        AddIfSame("Unidad", currentItem.UnitOrDepartment, otherItem.UnitOrDepartment);
        AddIfSame("Org", currentItem.OrganizationalUnit, otherItem.OrganizationalUnit);
        AddIfSame("Usuario", currentItem.ResponsibleUser, otherItem.ResponsibleUser);
        AddIfSame("Email", currentItem.Email, otherItem.Email);

        return matchingFields
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsWeakInventoryCategory(string? category)
    {
        return string.IsNullOrWhiteSpace(category)
            || string.Equals(category.Trim(), "other", StringComparison.OrdinalIgnoreCase);
    }

    private static string PreferInventoryValue(string? currentValue, string? incomingValue)
    {
        return string.IsNullOrWhiteSpace(currentValue)
            ? (incomingValue?.Trim() ?? string.Empty)
            : currentValue.Trim();
    }

    private static string AppendInventoryUnique(string? currentValue, string? incomingValue)
    {
        var current = currentValue?.Trim() ?? string.Empty;
        var incoming = incomingValue?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(incoming))
        {
            return current;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return incoming;
        }

        return current.Contains(incoming, StringComparison.OrdinalIgnoreCase)
            ? current
            : $"{current} | {incoming}";
    }

    private static string FormatInventoryAssignmentLabel(ImportedInventoryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.AssignedBuildingExternalId))
        {
            var parts = new List<string> { item.AssignedBuildingExternalId };
            if (!string.IsNullOrWhiteSpace(item.AssignedRoomExternalId))
            {
                parts.Add(item.AssignedRoomExternalId);
            }
            if (item.AssignedFloor.HasValue)
            {
                parts.Add($"Piso {item.AssignedFloor.Value}");
            }

            return string.Join(" / ", parts);
        }

        if (!string.IsNullOrWhiteSpace(item.MatchedBuildingExternalId))
        {
            return $"Sugerido: {item.MatchedBuildingExternalId}";
        }

        return "Pendiente";
    }

    private static string NormalizeInventorySerialFamily(string? value)
    {
        var normalized = NormalizeInventoryToken(value);
        if (normalized.Length > 5 && normalized.StartsWith('S'))
        {
            return normalized[1..];
        }

        return normalized;
    }

    private static bool IsInventoryPlaceholderToken(string value)
    {
        return value switch
        {
            "ND" => true,
            "NA" => true,
            "NODISPONIBLE" => true,
            "SINDATO" => true,
            "SINDATOS" => true,
            "NOINFORMADO" => true,
            "NOREGISTRA" => true,
            "NULL" => true,
            "NULO" => true,
            "VACIO" => true,
            "NONE" => true,
            _ => false
        };
    }

    private static string NormalizeInventoryToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        var normalized = builder.ToString();
        return IsInventoryPlaceholderToken(normalized) ? string.Empty : normalized;
    }

    private async Task<IReadOnlyList<string>> GetInventoryCategoryOptionsAsync()
    {
        var defaults = new[] { "pc", "printer", "scanner", "other" };
        var values = await _context.ImportedInventoryItems
            .AsNoTracking()
            .Where(item => item.InferredCategory != "")
            .Select(item => item.InferredCategory)
            .ToListAsync();

        return MergeInventoryOptionLists(defaults, values);
    }

    private async Task<IReadOnlyList<string>> GetInventoryStatusOptionsAsync()
    {
        var defaults = new[] { "active", "maintenance", "inactive", "stolen" };
        var values = await _context.ImportedInventoryItems
            .AsNoTracking()
            .Where(item => item.InferredStatus != "")
            .Select(item => item.InferredStatus)
            .ToListAsync();

        return MergeInventoryOptionLists(defaults, values);
    }

    private static IReadOnlyList<string> MergeInventoryOptionLists(IEnumerable<string> defaults, IEnumerable<string> values)
    {
        var results = new List<string>();

        void AddOption(string? rawValue)
        {
            var normalizedValue = rawValue?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                return;
            }

            if (!results.Contains(normalizedValue, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(normalizedValue);
            }
        }

        foreach (var value in defaults)
        {
            AddOption(value);
        }

        foreach (var value in values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            AddOption(value);
        }

        return results;
    }

    private async Task<CreateInventoryItemViewModel> BuildCreateInventoryItemViewModelAsync(InventoryItemFormModel form)
    {
        return new CreateInventoryItemViewModel
        {
            Form = form,
            Buildings = await _context.SyncedBuildings
                .AsNoTracking()
                .OrderBy(building => building.ManualDisplayName != "" ? building.ManualDisplayName : building.DisplayName)
                .ToListAsync(),
            Rooms = await _context.SyncedRooms
                .AsNoTracking()
                .OrderBy(room => room.ManualFloor ?? room.Floor)
                .ThenBy(room => room.ManualName != "" ? room.ManualName : room.Name)
                .ToListAsync(),
            Categories = await GetInventoryCategoryOptionsAsync(),
            Statuses = await GetInventoryStatusOptionsAsync()
        };
    }

    private static string NormalizeSortDirection(string? sortDirection)
    {
        return string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
    }

    private static string NormalizeInventorySortBy(string? sortBy)
    {
        return sortBy?.Trim().ToLowerInvariant() switch
        {
            "equipment" => "equipment",
            "unit" => "unit",
            "user" => "user",
            "ip" => "ip",
            "category" => "category",
            "status" => "status",
            "suggestion" => "suggestion",
            "assignment" => "assignment",
            _ => "row"
        };
    }

    private static string NormalizeLocationSortBy(string? sortBy)
    {
        return sortBy?.Trim().ToLowerInvariant() switch
        {
            "campus" => "campus",
            "type" => "type",
            "floors" => "floors",
            "rooms" => "rooms",
            "assigned" => "assigned",
            "suggested" => "suggested",
            "map" => "map",
            "coordinates" => "coordinates",
            _ => "building"
        };
    }

    private static string NormalizeInconsistencyType(string? inconsistencyType)
    {
        return inconsistencyType?.Trim().ToLowerInvariant() switch
        {
            "serial-duplicate" => "serial-duplicate",
            "serial-similar" => "serial-similar",
            "ip" => "ip",
            "mac" => "mac",
            "unit-org" => "unit-org",
            "multi" => "multi",
            _ => string.Empty
        };
    }

    private static IReadOnlyList<FilterOptionViewModel> GetInventoryInconsistencyFilterOptions()
    {
        return
        [
            new() { Value = string.Empty, Label = "Todas" },
            new() { Value = "serial-duplicate", Label = "S/N duplicado" },
            new() { Value = "serial-similar", Label = "S/N muy parecido" },
            new() { Value = "ip", Label = "IP repetida" },
            new() { Value = "mac", Label = "MAC repetida" },
            new() { Value = "unit-org", Label = "Unidad/org distinta" },
            new() { Value = "multi", Label = "2 o mas tipos" }
        ];
    }

    private static bool MatchesInconsistencyType(string summary, string inconsistencyType)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        return inconsistencyType switch
        {
            "serial-duplicate" => summary.Contains("S/N duplicado", StringComparison.OrdinalIgnoreCase),
            "serial-similar" => summary.Contains("S/N muy parecido", StringComparison.OrdinalIgnoreCase),
            "ip" => summary.Contains("IP repetida", StringComparison.OrdinalIgnoreCase),
            "mac" => summary.Contains("MAC repetida", StringComparison.OrdinalIgnoreCase),
            "unit-org" => summary.Contains("Unidad/org distinta", StringComparison.OrdinalIgnoreCase),
            "multi" => summary.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length >= 2,
            _ => true
        };
    }

    private static IEnumerable<ImportedInventoryItem> SortInventoryItems(
        IEnumerable<ImportedInventoryItem> items,
        string sortBy,
        string sortDirection)
    {
        IOrderedEnumerable<ImportedInventoryItem> ordered = sortBy switch
        {
            "equipment" => sortDirection == "desc"
                ? items.OrderByDescending(item => NormalizeSortableText(item.SerialNumber))
                    .ThenByDescending(item => NormalizeSortableText(item.Description))
                    .ThenByDescending(item => NormalizeSortableText(item.ItemNumber))
                : items.OrderBy(item => NormalizeSortableText(item.SerialNumber))
                    .ThenBy(item => NormalizeSortableText(item.Description))
                    .ThenBy(item => NormalizeSortableText(item.ItemNumber)),
            "unit" => sortDirection == "desc"
                ? items.OrderByDescending(item => NormalizeSortableText(item.UnitOrDepartment))
                    .ThenByDescending(item => NormalizeSortableText(item.OrganizationalUnit))
                : items.OrderBy(item => NormalizeSortableText(item.UnitOrDepartment))
                    .ThenBy(item => NormalizeSortableText(item.OrganizationalUnit)),
            "user" => sortDirection == "desc"
                ? items.OrderByDescending(item => NormalizeSortableText(item.ResponsibleUser))
                    .ThenByDescending(item => NormalizeSortableText(item.Email))
                : items.OrderBy(item => NormalizeSortableText(item.ResponsibleUser))
                    .ThenBy(item => NormalizeSortableText(item.Email)),
            "ip" => sortDirection == "desc"
                ? items.OrderByDescending(item => NormalizeSortableText(item.IpAddress))
                : items.OrderBy(item => NormalizeSortableText(item.IpAddress)),
            "category" => sortDirection == "desc"
                ? items.OrderByDescending(item => NormalizeSortableText(item.InferredCategory))
                : items.OrderBy(item => NormalizeSortableText(item.InferredCategory)),
            "status" => sortDirection == "desc"
                ? items.OrderByDescending(item => NormalizeSortableText(item.InferredStatus))
                : items.OrderBy(item => NormalizeSortableText(item.InferredStatus)),
            "suggestion" => sortDirection == "desc"
                ? items.OrderByDescending(item => NormalizeSortableText(BuildInventorySuggestionLabel(item)))
                : items.OrderBy(item => NormalizeSortableText(BuildInventorySuggestionLabel(item))),
            "assignment" => sortDirection == "desc"
                ? items.OrderByDescending(item => NormalizeSortableText(FormatInventoryAssignmentLabel(item)))
                : items.OrderBy(item => NormalizeSortableText(FormatInventoryAssignmentLabel(item))),
            _ => sortDirection == "desc"
                ? items.OrderByDescending(item => item.Id)
                : items.OrderBy(item => item.Id)
        };

        return ordered.ThenBy(item => item.Id);
    }

    private static IEnumerable<AdminLocationRowViewModel> SortLocationRows(
        IEnumerable<AdminLocationRowViewModel> rows,
        string sortBy,
        string sortDirection)
    {
        IOrderedEnumerable<AdminLocationRowViewModel> ordered = sortBy switch
        {
            "campus" => sortDirection == "desc"
                ? rows.OrderByDescending(row => NormalizeSortableText(row.Campus))
                    .ThenByDescending(row => NormalizeSortableText(row.DisplayName))
                : rows.OrderBy(row => NormalizeSortableText(row.Campus))
                    .ThenBy(row => NormalizeSortableText(row.DisplayName)),
            "type" => sortDirection == "desc"
                ? rows.OrderByDescending(row => NormalizeSortableText(row.Type))
                    .ThenByDescending(row => NormalizeSortableText(row.DisplayName))
                : rows.OrderBy(row => NormalizeSortableText(row.Type))
                    .ThenBy(row => NormalizeSortableText(row.DisplayName)),
            "floors" => sortDirection == "desc"
                ? rows.OrderByDescending(row => NormalizeSortableText(row.AvailableFloors))
                : rows.OrderBy(row => NormalizeSortableText(row.AvailableFloors)),
            "rooms" => sortDirection == "desc"
                ? rows.OrderByDescending(row => row.RoomsCount)
                : rows.OrderBy(row => row.RoomsCount),
            "assigned" => sortDirection == "desc"
                ? rows.OrderByDescending(row => row.AssignedInventoryCount)
                : rows.OrderBy(row => row.AssignedInventoryCount),
            "suggested" => sortDirection == "desc"
                ? rows.OrderByDescending(row => row.SuggestedInventoryCount)
                : rows.OrderBy(row => row.SuggestedInventoryCount),
            "map" => sortDirection == "desc"
                ? rows.OrderByDescending(row => NormalizeSortableText($"{row.MappingStatus} {row.InventoryStatus}"))
                : rows.OrderBy(row => NormalizeSortableText($"{row.MappingStatus} {row.InventoryStatus}")),
            "coordinates" => sortDirection == "desc"
                ? rows.OrderByDescending(row => NormalizeSortableText(row.Coordinates))
                : rows.OrderBy(row => NormalizeSortableText(row.Coordinates)),
            _ => sortDirection == "desc"
                ? rows.OrderByDescending(row => NormalizeSortableText(row.DisplayName))
                : rows.OrderBy(row => NormalizeSortableText(row.DisplayName))
        };

        return ordered.ThenBy(row => NormalizeSortableText(row.ExternalId));
    }

    private static string BuildInventorySuggestionLabel(ImportedInventoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.MatchedBuildingExternalId))
        {
            return string.Empty;
        }

        var parts = new List<string> { item.MatchedBuildingExternalId };
        if (!string.IsNullOrWhiteSpace(item.MatchedRoomExternalId))
        {
            parts.Add(item.MatchedRoomExternalId);
        }
        if (!string.IsNullOrWhiteSpace(item.MatchConfidence))
        {
            parts.Add(item.MatchConfidence);
        }

        return string.Join(" / ", parts);
    }

    private static string NormalizeSortableText(string? value)
    {
        return NormalizeInventoryToken(value);
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
        var requestHost = Request?.Host.Host;
        if (!string.IsNullOrWhiteSpace(requestHost))
        {
            var requestScheme = string.IsNullOrWhiteSpace(Request?.Scheme) ? "http" : Request.Scheme;
            return $"{requestScheme}://{requestHost}:8080";
        }

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
        var environment = HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();
        return SqliteDatabasePathResolver.ResolveDatabasePath(_configuration, environment.ContentRootPath);
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
















