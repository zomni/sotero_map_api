using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.Services;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/health")]
[Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Auditor}")]
public class HealthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly DatabaseBackupService _backupService;
    private readonly IConfiguration _configuration;

    public HealthController(
        AppDbContext context,
        DatabaseBackupService backupService,
        IConfiguration configuration)
    {
        _context = context;
        _backupService = backupService;
        _configuration = configuration;
    }

    [HttpGet("integrity")]
    public async Task<IActionResult> Integrity(CancellationToken cancellationToken)
    {
        var databaseConnected = await _context.Database.CanConnectAsync(cancellationToken);
        var requiredTables = new[]
        {
            "AuthUsers",
            "AuditLogEntries",
            "ImportedInventoryItems",
            "SyncedBuildings",
            "SyncedRooms",
            "BackupHistories",
            "WalkingRouteNodes",
            "WalkingRouteEdges"
        };

        var presentTables = databaseConnected
            ? await _context.Database
                .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type = 'table'")
                .ToListAsync(cancellationToken)
            : [];

        var missingTables = requiredTables
            .Where(table => !presentTables.Contains(table, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var equipmentCount = await _context.ImportedInventoryItems.CountAsync(cancellationToken);
        var activeAdmins = await _context.AuthUsers.CountAsync(user => user.IsActive && user.Role == AppRoles.Admin, cancellationToken);
        var criticalEventsLast7Days = await _context.AuditLogEntries.CountAsync(entry => entry.Severity == "critical" && entry.CreatedAtUtc >= DateTime.UtcNow.AddDays(-7), cancellationToken);

        var latestBackups = await _backupService.GetLatestBackupsAsync(1, cancellationToken);
        var latestBackup = latestBackups.FirstOrDefault();
        var backupHealthy = _backupService.IsEnabled()
            && latestBackup is not null
            && string.Equals(latestBackup.Status, "success", StringComparison.OrdinalIgnoreCase)
            && latestBackup.CreatedAtUtc >= DateTime.UtcNow.AddDays(-2);

        var pdfMaxBytes = _configuration.GetValue<long?>("PdfSettings:MaxUploadBytes") ?? 25_000_000;
        var status = !databaseConnected || missingTables.Count > 0 || activeAdmins == 0 || criticalEventsLast7Days > 0
            ? "Critical"
            : backupHealthy
                ? "OK"
                : "Advertencia";

        return Ok(new
        {
            status,
            databaseConnected,
            requiredTables,
            missingTables,
            equipmentCount,
            activeAdmins,
            latestBackup = latestBackup is null ? null : new
            {
                latestBackup.Id,
                latestBackup.CreatedAtUtc,
                latestBackup.Status,
                latestBackup.FilePath,
                latestBackup.SizeBytes,
                latestBackup.Hash,
                latestBackup.ErrorMessage
            },
            backupHealthy,
            backupEnabled = _backupService.IsEnabled(),
            criticalEventsLast7Days,
            pdfMaxBytes,
            generatedAtUtc = DateTime.UtcNow
        });
    }
}
