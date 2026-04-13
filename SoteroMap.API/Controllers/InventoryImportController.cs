using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.Services;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/inventory-import")]
[Authorize]
public class InventoryImportController : ControllerBase
{
    private readonly ExcelInventoryImportService _importService;
    private readonly AppDbContext _context;

    public InventoryImportController(ExcelInventoryImportService importService, AppDbContext context)
    {
        _importService = importService;
        _context = context;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var status = await _importService.GetStatusAsync(cancellationToken);
        return Ok(status);
    }

    [AllowAnonymous]
    [HttpGet("sync-state")]
    public async Task<IActionResult> GetSyncState(CancellationToken cancellationToken)
    {
        var assembly = typeof(Program).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var backendVersion = informationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";

        var totalItems = await _context.ImportedInventoryItems.CountAsync(cancellationToken);
        var assignedItems = await _context.ImportedInventoryItems.CountAsync(
            i => i.AssignedBuildingExternalId != "",
            cancellationToken);

        var pendingItems = totalItems - assignedItems;

        var latestImportedAtUtc = await _context.ImportedInventoryItems
            .AsNoTracking()
            .OrderByDescending(i => i.ImportedAtUtc)
            .Select(i => (DateTime?)i.ImportedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var latestAssignmentUpdateUtc = await _context.ImportedInventoryItems
            .AsNoTracking()
            .Where(i => i.AssignmentUpdatedAtUtc != null)
            .OrderByDescending(i => i.AssignmentUpdatedAtUtc)
            .Select(i => i.AssignmentUpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var latestAuditChangeUtc = await _context.AuditLogEntries
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var latestChangeUtc = new[]
        {
            latestImportedAtUtc,
            latestAssignmentUpdateUtc,
            latestAuditChangeUtc
        }
        .Where(value => value.HasValue)
        .Select(value => value!.Value)
        .DefaultIfEmpty(DateTime.MinValue)
        .Max();

        return Ok(new
        {
            totalItems,
            assignedItems,
            pendingItems,
            backendVersion,
            latestImportedAtUtc,
            latestAssignmentUpdateUtc,
            latestAuditChangeUtc,
            latestChangeUtc = latestChangeUtc == DateTime.MinValue ? (DateTime?)null : latestChangeUtc,
            revision = latestChangeUtc == DateTime.MinValue ? "empty" : latestChangeUtc.ToString("O")
        });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost("run")]
    public async Task<IActionResult> Run(
        [FromQuery] string? fileName,
        [FromQuery] string? sheetName,
        [FromQuery] bool merge = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _importService.ImportAsync(fileName, sheetName, merge, cancellationToken);
        return Ok(result);
    }

    [AllowAnonymous]
    [HttpGet("items")]
    public async Task<IActionResult> GetItems(
        [FromQuery] string? category,
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromQuery] string? matchedBuildingExternalId,
        [FromQuery] string? assignedBuildingExternalId,
        [FromQuery] string? matchedRoomExternalId,
        CancellationToken cancellationToken)
    {
        var query = _context.ImportedInventoryItems.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(i => i.InferredCategory == category);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(i => i.InferredStatus == status);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.Trim().ToLower();
            query = query.Where(i =>
                (i.SerialNumber != null && i.SerialNumber.ToLower().Contains(searchLower)) ||
                (i.ItemNumber != null && i.ItemNumber.ToLower().Contains(searchLower)) ||
                (i.Lot != null && i.Lot.ToLower().Contains(searchLower)) ||
                (i.Description != null && i.Description.ToLower().Contains(searchLower)) ||
                (i.ResponsibleUser != null && i.ResponsibleUser.ToLower().Contains(searchLower)) ||
                (i.OrganizationalUnit != null && i.OrganizationalUnit.ToLower().Contains(searchLower)) ||
                (i.UnitOrDepartment != null && i.UnitOrDepartment.ToLower().Contains(searchLower)) ||
                (i.Email != null && i.Email.ToLower().Contains(searchLower)) ||
                (i.JobTitle != null && i.JobTitle.ToLower().Contains(searchLower)) ||
                (i.IpAddress != null && i.IpAddress.ToLower().Contains(searchLower)) ||
                (i.MacAddress != null && i.MacAddress.ToLower().Contains(searchLower)) ||
                (i.TicketMda != null && i.TicketMda.ToLower().Contains(searchLower)) ||
                (i.Observation != null && i.Observation.ToLower().Contains(searchLower)));
        }

        if (!string.IsNullOrWhiteSpace(matchedBuildingExternalId))
            query = query.Where(i => i.MatchedBuildingExternalId == matchedBuildingExternalId);

        if (!string.IsNullOrWhiteSpace(assignedBuildingExternalId))
            query = query.Where(i => i.AssignedBuildingExternalId == assignedBuildingExternalId);

        if (!string.IsNullOrWhiteSpace(matchedRoomExternalId))
            query = query.Where(i => i.MatchedRoomExternalId == matchedRoomExternalId);

        var items = await query
            .OrderBy(i => i.RowNumber)
            .Select(i => new
            {
                i.Id,
                i.RowNumber,
                i.ItemNumber,
                i.SerialNumber,
                i.Description,
                i.Lot,
                i.InstallDate,
                i.UnitOrDepartment,
                i.OrganizationalUnit,
                i.ResponsibleUser,
                i.Email,
                i.JobTitle,
                i.IpAddress,
                i.MacAddress,
                i.TicketMda,
                i.Observation,
                i.InferredCategory,
                i.InferredStatus,
                i.InventoryDate,
                i.MatchedBuildingExternalId,
                i.MatchedRoomExternalId,
                i.MatchConfidence,
                i.MatchNotes,
                i.AssignedBuildingExternalId,
                i.AssignedRoomExternalId,
                i.AssignedFloor,
                i.AssignmentNotes,
                i.AssignmentUpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(items);
    }
}
