using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Models;
using SoteroMap.API.Services;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/backups")]
[Authorize(Roles = AppRoles.Admin)]
public class BackupsController : ControllerBase
{
    private readonly DatabaseBackupService _backupService;

    public BackupsController(DatabaseBackupService backupService)
    {
        _backupService = backupService;
    }

    [HttpGet("latest")]
    public async Task<IActionResult> Latest([FromQuery] int take = 10, CancellationToken cancellationToken = default)
    {
        var backups = await _backupService.GetLatestBackupsAsync(take, cancellationToken);
        return Ok(backups.Select(MapBackup));
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken = default)
    {
        var backup = await _backupService.CreateBackupAsync(User.Identity?.Name ?? "admin", "manual-api-trigger", cancellationToken);
        return Ok(MapBackup(backup));
    }

    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup(CancellationToken cancellationToken = default)
    {
        var removed = await _backupService.CleanupExpiredBackupsAsync(cancellationToken);
        return Ok(new { removed });
    }

    private static object MapBackup(BackupHistory backup)
    {
        return new
        {
            backup.Id,
            backup.CreatedAtUtc,
            backup.Status,
            backup.FilePath,
            backup.SizeBytes,
            backup.Hash,
            backup.ErrorMessage,
            backup.CreatedByUsername,
            backup.Reason
        };
    }
}
